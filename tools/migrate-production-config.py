"""Migrate an appsettings.Production.json from the Forge:* key names to the ones that say what they configure.

There is no compatibility read of the old spelling in the app, so a box whose production config still uses
Forge:* silently falls back to defaults after the deploy that renames them -- which for the catalogue paths
means an empty workflow list. This must run in the SAME step as that deploy, not before it.

    python tools/migrate-production-config.py <path>            # writes <path>.new, changes nothing
    python tools/migrate-production-config.py <path> --apply    # backs up to <path>.bak, then rewrites

Prints key names only. The file holds a connection string and a registration code, and neither is something
this script has any reason to echo.
"""
import io
import json
import os
import re
import sys

# old dotted key -> new dotted key
RENAMES = {
    "Forge:BaseUrl": "ComfyUI:BaseUrl",
    "Forge:GateToken": "ComfyUI:GateToken",
    "Forge:WorkflowsPath": "Catalog:WorkflowsPath",
    "Forge:RequirementsPath": "Catalog:RequirementsPath",
    "Forge:FfmpegPath": "Media:FfmpegPath",
}

# Keys for features that no longer exist. Carrying them forward means an operator reasoning about a setting
# nothing reads.
DROP = {
    "Forge:ApiKey",          # the app-wide API key is gone; auth is a per-user X-Api-Key
    "Forge:ApiKeyUserId",    # its companion
    "Forge:ModelsDir",       # nothing ever read it
    "Tags:ModelUrl",         # the tag model runs in-process; there is no URL
    "Tags:ArtistType",       # restated a constant about the tag data
    "Logging:LogPrompts",    # removed outright, not defaulted off
}


def flatten(obj, prefix=""):
    out = {}
    for k, v in obj.items():
        key = f"{prefix}{k}"
        if isinstance(v, dict):
            out.update(flatten(v, key + ":"))
        else:
            out[key] = v
    return out


def nest(flat):
    root = {}
    for key, value in flat.items():
        parts = key.split(":")
        node = root
        for p in parts[:-1]:
            node = node.setdefault(p, {})
        node[parts[-1]] = value
    return root


def infer_database_provider(connection_string):
    """Return the provider only when the connection string identifies it unambiguously."""
    conn = str(connection_string).strip()
    lower = conn.lower()

    # These are SQL Server-specific connection-string keys. `Data Source` by itself is deliberately absent: both
    # providers own it, so treating every value as either engine would simply move the old guess to another branch.
    sql_server_key = re.search(
        r"(?:^|;)\s*(?:server|initial\s+catalog|integrated\s+security|trusted[_ ]connection|"
        r"user\s+id|uid|attachdbfilename)\s*=", lower)
    sql_server_tcp_source = re.search(r"(?:^|;)\s*data\s+source\s*=\s*tcp:", lower)
    if sql_server_key or sql_server_tcp_source:
        return "SqlServer"

    values = {}
    for part in conn.split(";"):
        if not part.strip():
            continue
        if "=" not in part:
            raise ValueError("connection string contains a segment without '='")
        key, value = part.split("=", 1)
        values[key.strip().lower().replace("_", " ")] = value.strip()

    sqlite_source = values.get("filename") or values.get("data source")
    if sqlite_source:
        sqlite_source = sqlite_source.strip().strip('"').strip("'")
        source = sqlite_source.lower()
        is_sqlite_file = (
            source == ":memory:" or source.startswith("file:") or
            "/" in sqlite_source or "\\" in sqlite_source or
            source.endswith((".db", ".sqlite", ".sqlite3"))
        )
        if is_sqlite_file:
            return "Sqlite"

    raise ValueError(
        "ConnectionStrings:ImageGen does not unambiguously identify SQLite or SQL Server; "
        "set Database:Provider explicitly before migrating")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    path = sys.argv[1]
    apply = "--apply" in sys.argv

    with io.open(path, encoding="utf-8-sig") as source:
        flat = flatten(json.load(source))
    out, renamed, dropped = {}, [], []

    for key, value in flat.items():
        if key in DROP:
            dropped.append(key)
            continue
        if key in RENAMES:
            renamed.append((key, RENAMES[key]))
            out[RENAMES[key]] = value
            continue
        out[key] = value

    # Drop comment keys whose section lost every real key -- otherwise a renamed-away section survives as a lone
    # "Forge": { "_comment": ... }, describing settings that are no longer there.
    orphaned = []
    for key in list(out):
        leaf = key.rsplit(":", 1)[-1]
        if not leaf.startswith("_"):
            continue
        section = key.rsplit(":", 1)[0] + ":" if ":" in key else ""
        siblings = [k for k in out
                    if k != key and k.startswith(section) and not k.rsplit(":", 1)[-1].startswith("_")]
        if section and not siblings:
            orphaned.append(key)
            del out[key]

    # Pin the provider. It used to be inferred from the app's SqlServer default; that default is now Sqlite, so a
    # SQL Server box that says nothing would try to open its connection string as a file path. The app refuses to
    # start on that mismatch rather than creating an empty database, but a named key beats a startup error.
    pinned = False
    if "Database:Provider" not in out:
        conn = str(out.get("ConnectionStrings:ImageGen", ""))
        try:
            out["Database:Provider"] = infer_database_provider(conn)
        except ValueError as error:
            raise SystemExit(f"refusing to write configuration: {error}") from error
        pinned = True

    for old, new in renamed:
        print(f"  renamed  {old}  ->  {new}")
    for key in dropped:
        print(f"  dropped  {key}   (feature no longer exists)")
    for key in orphaned:
        print(f"  dropped  {key}   (comment on a section that no longer has any keys)")
    if pinned:
        print(f"  pinned   Database:Provider = {out['Database:Provider']}   (was relying on the app default)")
    if not (renamed or dropped or orphaned or pinned):
        print("  nothing to do — already migrated")
        return

    text = json.dumps(nest(out), indent=2, ensure_ascii=False) + "\n"
    if apply:
        os.replace(path, path + ".bak")
        with io.open(path, "w", encoding="utf-8", newline="\n") as destination:
            destination.write(text)
        print(f"\nwrote {path}  (previous file kept at {path}.bak)")
    else:
        with io.open(path + ".new", "w", encoding="utf-8", newline="\n") as destination:
            destination.write(text)
        print(f"\nwrote {path}.new  — nothing else changed. Re-run with --apply to swap it in.")


if __name__ == "__main__":
    main()
