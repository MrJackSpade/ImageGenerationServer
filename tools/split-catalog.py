"""Split workflows.json + requirements.json into configurations/{workflows,models}/<id>.json.

One file per thing, so adding a workflow or a model is dropping a file rather than editing a shipped one.

    python tools/split-catalog.py [--write]

Without --write it reports what it would do and validates, changing nothing.

WHAT IS DROPPED, and why (see the plan's audit):
  requirements: urls, target_folder, sha256   -- parsed by the app, zero consumers
                size_bytes                     -- one decoration, and a fact about the user's file
                resolution                     -- MOVED onto the configuration (PixelSnap consumes it)
                _label, _*_comment             -- never read
  workflows:    visible                        -- DEAD KEY, nothing reads it (see MOVED_TO_UI_VISIBLE)
                _hidden_reason, card.notes     -- notes

MATCH PATTERNS are hand-authored below, from each model's PUBLISHED name. They are deliberately absent for
most slots. Nothing here derives a pattern from the author's filename: a rule tuned until this box's 173
files score well is calibrated to this box and breaks on anyone else's.
"""
import argparse
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "configurations")

# Requirement fields that do not survive the split. Everything else on an entry is either id/kind (kept) or a
# comment key (dropped by the leading-underscore rule).
DROP_REQUIREMENT_FIELDS = {"name", "urls", "target_folder", "sha256", "size_bytes", "resolution"}

# Configuration keys that do not survive.
DROP_CONFIG_FIELDS = {"visible", "_hidden_reason"}

# `visible: false` was never read, so these two have been offered all along despite being marked hidden.
# They move onto the pair the code actually reads.
MOVED_TO_UI_VISIBLE = {"joyai-image-edit", "step1x-edit-v1p2"}

# Hand-authored recognition patterns, keyed by requirement id. Written from the model's published name so they
# recognise the same weights downloaded from anywhere, under any of the usual spellings.
#
# Rules for adding one:
#   - Base it on what the PUBLISHER calls the model, not on a filename you have.
#   - Tokens joined with .* so intervening words (quant tags, uploader suffixes) do not break the match.
#   - Only add one when you are confident. An absent pattern means "the user binds it", which is fine.
#   - Anchored at neither end, matched case-insensitively against the filename with its extension stripped.
# NO LOOKAHEADS. These compile under RegexOptions.NonBacktracking, which does not support them — that is the
# price of making catastrophic backtracking impossible on patterns a user may have written. Where two slots
# cannot be told apart without one (SD 3.5 Large bf16 vs its fp8 sibling; Chroma1-HD vs Chroma1-HD-Flash),
# NEITHER gets a pattern and the user binds them. Guessing wrong is worse than not guessing.
MATCH = {
    # --- Stable Diffusion: mirrored everywhere, renamed everywhere ----------------------------------
    "v1-5-pruned-emaonly-fp16":              [r"v1[-_. ]?5[-_. ]?pruned"],
    "v2-1-768-ema-pruned-fp16":              [r"v2[-_. ]?1[-_. ]?768"],
    "sd-xl-base-1-0":                        [r"sd[-_. ]?xl[-_. ]?base"],
    "sd3-5-large-turbo-bf16":                [r"sd3[-_. ]?5[-_. ]?large[-_. ]?turbo"],
    "sd3-5-large-fp8-scaled":                [r"sd3[-_. ]?5[-_. ]?large[-_. ]?fp8"],
    "sd3-5-medium-incl-clips-t5xxlfp8scaled": [r"sd3[-_. ]?5[-_. ]?medium"],

    # --- Community checkpoints: Civitai renames these aggressively, so the published name is all we
    #     can rely on. This is the case the whole feature exists for.
    "ponydiffusionv6xl-v6startwiththisone":  [r"pony.*diffusion.*v6", r"pony.*xl.*v6"],
    "autismmixsdxl-autismmixconfetti":       [r"autism.*mix.*confetti"],

    # --- Flux ---------------------------------------------------------------------------------------
    "flux1-dev-q4-k-s":                      [r"flux1[-_. ]?dev.*q4[-_. ]?k[-_. ]?s"],
    "flux1-dev-q8-0":                        [r"flux1[-_. ]?dev.*q8"],
    "flux1-schnell-q4-k-s":                  [r"flux1[-_. ]?schnell.*q4"],
    "flux1-krea-dev-fp8":                    [r"flux1[-_. ]?krea"],
    "flux1-dev-kontext-fp8-scaled":          [r"flux1[-_. ]?dev[-_. ]?kontext"],
    "flux1-vae-bf16":                        [r"flux1[-_. ]?vae"],
    "flux2-vae":                             [r"flux2[-_. ]?vae"],
    "ae":                                    [r"^ae$"],

    # --- Chroma (Chroma1-HD has no pattern: Chroma1-HD-Flash contains it) ---------------------------
    "chroma1-base-bf16":                     [r"chroma1[-_. ]?base"],
    "chroma1-flash-bf16":                    [r"chroma1[-_. ]?hd[-_. ]?flash"],
    "chroma1-radiance-x0":                   [r"chroma.*radiance"],

    # --- Qwen ---------------------------------------------------------------------------------------
    "qwen-image-vae":                        [r"qwen[-_. ]?image[-_. ]?vae"],
    "qwen-image-q6-k":                       [r"qwen[-_. ]?image[-_. ]?q6"],
    "qwen-image-edit-2511-q6-k":             [r"qwen[-_. ]?image[-_. ]?edit[-_. ]?2511[-_. ]?q6"],

    # --- Z-Image ------------------------------------------------------------------------------------
    "z-image-bf16":                          [r"z[-_. ]?image[-_. ]?bf16"],
    "z-image-turbo-bf16":                    [r"z[-_. ]?image[-_. ]?turbo[-_. ]?bf16"],
    "z-image-q4-k-m":                        [r"z[-_. ]?image[-_. ]?q4"],
    "z-image-turbo-q4-k-m":                  [r"z[-_. ]?image[-_. ]?turbo[-_. ]?q4"],

    # --- Wan VAEs -----------------------------------------------------------------------------------
    "wan2-2-vae":                            [r"wan2[-_. ]?2[-_. ]?vae"],
    "wan-2-1-vae":                           [r"wan[-_. ]?2[-_. ]?1[-_. ]?vae"],

    # --- Text encoders: these names are near-universal across the ecosystem -------------------------
    "clip-l":                                [r"^clip[-_. ]?l$"],
    "t5xxl-fp8-e4m3fn-scaled":               [r"t5xxl.*fp8"],
}


def load(path):
    return json.load(io.open(path, encoding="utf-8"))


def strip_comments(obj):
    """Drop leading-underscore keys recursively — they are notes to the author, never read."""
    if isinstance(obj, dict):
        return {k: strip_comments(v) for k, v in obj.items() if not k.startswith("_")}
    if isinstance(obj, list):
        return [strip_comments(v) for v in obj]
    return obj


def label_for(rid, configs):
    """A human name for the binding UI: the friendly name of a configuration that uses this as its checkpoint."""
    for c in configs:
        if (c.get("requirements") or {}).get("checkpoint") == rid and c.get("friendly_name"):
            return c["friendly_name"]
    return rid.replace("-", " ").title()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true", help="actually write the tree")
    args = ap.parse_args()

    configs = load(os.path.join(ROOT, "workflows.json"))["configurations"]
    reqs = load(os.path.join(ROOT, "requirements.json"))["requirements"]

    # --- validate the hand-authored patterns ----------------------------------------------------
    # Not a fitting exercise: this only catches a pattern that is plainly wrong (matches nothing it should,
    # or reaches into another slot of the same kind). A slot with no pattern is not a failure.
    by_id = {r["id"]: r for r in reqs}
    problems = []
    for rid, pats in MATCH.items():
        if rid not in by_id:
            problems.append(f"MATCH['{rid}'] refers to no requirement")
            continue
        kind = by_id[rid]["kind"]
        own = os.path.splitext(by_id[rid]["name"])[0]
        for p in pats:
            try:
                rx = re.compile(p, re.I)
            except re.error as e:
                problems.append(f"MATCH['{rid}'] pattern /{p}/ does not compile: {e}")
                continue
            for other in reqs:
                if other["id"] == rid or other["kind"] != kind:
                    continue
                if rx.search(os.path.splitext(other["name"])[0]):
                    problems.append(f"MATCH['{rid}'] /{p}/ also matches {other['id']} ({other['name']})")
        if not any(re.search(p, own, re.I) for p in pats):
            problems.append(f"MATCH['{rid}'] matches none of its own known file ({by_id[rid]['name']})")

    matched = sum(1 for r in reqs if r["id"] in MATCH)
    print(f"models:    {len(reqs)}  ({matched} with a hand-authored match pattern, "
          f"{len(reqs) - matched} bound by hand in the UI)")
    print(f"workflows: {len(configs)}")

    if problems:
        print("\nPATTERN PROBLEMS:")
        for p in problems:
            print(f"  {p}")
        sys.exit(1)
    print("patterns validated: each matches its own model and reaches no other slot of its kind")

    # --- build ----------------------------------------------------------------------------------
    models = []
    for r in reqs:
        m = {"id": r["id"], "kind": r["kind"], "label": label_for(r["id"], configs)}
        if r["id"] in MATCH:
            m["match"] = MATCH[r["id"]]
        models.append(m)

    workflows = []
    for c in configs:
        w = strip_comments({k: v for k, v in c.items() if k not in DROP_CONFIG_FIELDS})
        if c["id"] in MOVED_TO_UI_VISIBLE:
            w["ui_visible"] = False
            w["api_visible"] = False

        # The output-resolution envelope moves from the checkpoint's requirement onto the CONFIGURATION. It is
        # real, consumed data -- PixelSnap clamps render size to it, and without it SD 1.5 quietly loses its 768
        # ceiling -- but it is not part of a model's identity, which is all a model file carries now. Copying it
        # per configuration duplicates a few numbers and makes each file self-contained, which is the point of
        # one file per thing. Making it user-adjustable is a ConfigOverride (param.*), not an edit to this file.
        ckpt = (c.get("requirements") or {}).get("checkpoint")
        res = (by_id.get(ckpt) or {}).get("resolution") if ckpt else None
        if res:
            w["resolution"] = res
        workflows.append(w)

    if not args.write:
        print("\n(dry run — pass --write to create the tree)")
        return

    for sub, items in (("models", models), ("workflows", workflows)):
        d = os.path.join(OUT, sub)
        os.makedirs(d, exist_ok=True)
        for item in items:
            path = os.path.join(d, f"{item['id']}.json")
            with io.open(path, "w", encoding="utf-8", newline="\n") as fh:
                json.dump(item, fh, indent=2, ensure_ascii=False)
                fh.write("\n")
        print(f"wrote {len(items):4d} files to configurations/{sub}/")


if __name__ == "__main__":
    main()
