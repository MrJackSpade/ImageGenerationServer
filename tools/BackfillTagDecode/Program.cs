using System.Net;
using System.Text.Json.Nodes;
using ImageGen.Application.Security;
using ImageGen.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

// One-time backfill: HTML-decode every stored booru tag name.
//
// The tag scrape never HTML-decoded the names, so both tag dictionaries spelled apostrophes '&#039;' and '>_<'
// '&gt;_&lt;' ("holding_another&#039;s_foot"). tags.json was escaped TWICE over. The serving vocab and tags.json now
// decode at ingest, so the app speaks the literal name ("holding_another's_foot"). This tool moves the DATA the app
// already stored onto that same spelling.
//
// It is not optional cleanup — it gates the deploy. Bans are matched against the vocab by name (server.py's tag2id),
// so a ban still written '&#039;' silently stops suppressing the moment the vocab decodes. Bookmarks and marks key the
// same way: a mark that no longer matches its prompt segment draws as a dead chip instead of a tag chip.
//
// Two shapes of column:
//   * TEXT   — prompts, in marker form, with tag names inline. Decoded wholesale.
//   * JSON   — opaque blobs (marks maps, composer prefs, a serialized request). Parsed and decoded VALUE BY VALUE, never
//              wholesale: the vocab has 115 '&quot;' names, and decoding one inside a JSON string would inject a raw
//              quote and corrupt the blob.
// and one shape of key:
//   * KEY    — deterministically-encrypted canonical names (marks, bookmarks, bans, artist display). Decoding MERGES
//              keys, and three of these tables are UNIQUE on the name, so a row whose decoded spelling is already taken
//              is DELETED rather than updated — the twin already carries it. The mark tables have no unique constraint
//              and would silently duplicate instead, so they get the same treatment.
//
// dbo.UserLog.Payload is deliberately untouched: it is an append-only audit trail of what was actually submitted at the
// time, and rewriting a record of history is not a migration.
//
// Idempotent and resumable: decoding an already-decoded name is a no-op, so only rows that still carry an entity are
// touched. Safe (and meant) to re-run after the deploy to catch anything the old build wrote while it was still up.
//
// Usage:  dotnet run --project tools/BackfillTagDecode -- [--dry-run] [--conn=<connection string>]
//         (falls back to IMAGEGEN_CONNECTION, then to localhost/ImageGen)

var dryRun = args.Contains("--dry-run");
var connectionString = args.FirstOrDefault(a => a.StartsWith("--conn=", StringComparison.Ordinal))?["--conn=".Length..]
    ?? Environment.GetEnvironmentVariable("IMAGEGEN_CONNECTION")
    ?? "Server=localhost;Database=ImageGen;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

var services = new ServiceCollection().AddInfrastructure(connectionString).BuildServiceProvider();
var cipher = services.GetRequiredService<IUserCipher>();
var ct = CancellationToken.None;

await using var conn = new SqlConnection(connectionString);
await conn.OpenAsync(ct);

Console.WriteLine($"HTML-decoding stored tag names{(dryRun ? "  [DRY RUN - no writes]" : "")}");
Console.WriteLine();

// NOTHING BELOW MAY PRINT A DECRYPTED VALUE. This tool decrypts — it has to, to rewrite the value — and every
// prompt-bearing column in this database is encrypted under a per-user key for the express purpose of keeping the text
// out of consoles and logs. Decrypting is necessary; printing never is. Report counts and ids, not content.
// (NoPlaintextConsoleTests fails the build if a Console call here so much as looks like it emits one.)

var totalChanged = 0;
var totalRemoved = 0;

foreach (var target in Targets.Text)
    totalChanged += await RunTextAsync(target);

foreach (var target in Targets.Keys)
{
    var (changed, removed) = await RunKeyAsync(target);
    totalChanged += changed;
    totalRemoved += removed;
}

Console.WriteLine();
Console.WriteLine(dryRun
    ? $"DRY RUN: {totalChanged} value(s) would be rewritten, {totalRemoved} duplicate row(s) would be removed."
    : $"Rewrote {totalChanged} value(s); removed {totalRemoved} duplicate row(s).");

if (dryRun)
    return 0;

// Verify: a second pass must find nothing left to do. Anything still carrying an entity means a row was missed.
Console.WriteLine();
Console.WriteLine("Verifying...");
var remaining = 0;
foreach (var target in Targets.Text)
    remaining += await CountTextAsync(target);
foreach (var target in Targets.Keys)
    remaining += await CountKeyAsync(target);

if (remaining > 0)
{
    Console.Error.WriteLine($"BACKFILL INCOMPLETE - {remaining} value(s) still encoded.");
    return 1;
}

Console.WriteLine("Clean: nothing left encoded.");
return 0;

// ---- the transform -------------------------------------------------------------------------------------------

// Unescape to a FIXED POINT. One pass is not enough: tags.json was escaped twice, so the app stored names that decode
// '&amp;#039;' -> '&#039;' -> '\''. Terminates because a decode either shortens the string or leaves it identical.
//
// A fixed point is only safe HERE because WebUtility.HtmlDecode requires the trailing ';': it leaves the real tag
// '&ether' alone, so re-running this tool over already-decoded data is a no-op. DO NOT port this loop to python --
// html.unescape resolves semicolon-less entities, so it reads '&ether' as '&eth'+'er' and writes 'ðer'. The python
// side (build-tags-json.py, tagmodel/server.py) does not decode at all any more, because the scraper decodes at
// capture.
static string Decode(string s)
{
    while (true)
    {
        var next = WebUtility.HtmlDecode(s);
        if (next == s)
            return s;
        s = next;
    }
}

// Decode every string INSIDE a JSON blob — object keys (a marks map is keyed by tag name) and string values alike —
// leaving the JSON structure itself alone. Rebuilt rather than mutated in place: a JsonNode cannot be re-parented.
static JsonNode? DecodeJson(JsonNode? node, ref bool touched)
{
    switch (node)
    {
        case JsonObject obj:
        {
            var result = new JsonObject();
            foreach (var (key, value) in obj.ToList())
            {
                var decoded = Decode(key);
                if (decoded != key)
                    touched = true;
                result[decoded] = DecodeJson(value?.DeepClone(), ref touched);
            }
            return result;
        }
        case JsonArray arr:
        {
            var result = new JsonArray();
            foreach (var item in arr.ToList())
                result.Add(DecodeJson(item?.DeepClone(), ref touched));
            return result;
        }
        case JsonValue val when val.TryGetValue<string>(out var s):
        {
            var decoded = Decode(s);
            if (decoded != s)
                touched = true;
            return JsonValue.Create(decoded);
        }
        default:
            return node?.DeepClone();
    }
}

// The decoded form of a stored column, or null if it was already clean.
static string? Rewrite(string stored, bool isJson)
{
    if (!isJson)
    {
        var decoded = Decode(stored);
        return decoded == stored ? null : decoded;
    }

    var touched = false;
    var node = DecodeJson(JsonNode.Parse(stored), ref touched);
    return touched ? node?.ToJsonString() : null;
}

// ---- text + json columns -------------------------------------------------------------------------------------

async Task<int> RunTextAsync(TextTarget t)
{
    var rows = await ReadTextAsync(t);
    var changed = 0;

    foreach (var (id, userId, values) in rows)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < t.Columns.Length; i++)
        {
            if (values[i] is not { } stored)
                continue;
            var clear = await cipher.DecryptAsync(userId, stored, ct);
            if (Rewrite(clear, t.IsJson) is not { } fixedUp)
                continue;
            updates[t.Columns[i]] = await cipher.EncryptAsync(userId, fixedUp, ct);
        }

        if (updates.Count == 0)
            continue;
        changed += updates.Count;
        if (dryRun)
            continue;

        var sets = string.Join(", ", updates.Keys.Select((c, i) => $"{c} = @v{i}"));
        await using var cmd = new SqlCommand($"UPDATE {t.UpdateTable} SET {sets} WHERE {t.UpdateIdCol} = @id;", conn);
        var n = 0;
        foreach (var value in updates.Values)
            cmd.Parameters.AddWithValue($"@v{n++}", value);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    Console.WriteLine($"  {t.Label,-40} {rows.Count,7} row(s) scanned, {changed,5} value(s) {(dryRun ? "to rewrite" : "rewritten")}");
    return changed;
}

async Task<int> CountTextAsync(TextTarget t)
{
    var n = 0;
    foreach (var (_, userId, values) in await ReadTextAsync(t))
        for (var i = 0; i < t.Columns.Length; i++)
            if (values[i] is { } stored && Rewrite(await cipher.DecryptAsync(userId, stored, ct), t.IsJson) is not null)
                n++;
    return n;
}

async Task<List<(object Id, long UserId, string?[] Values)>> ReadTextAsync(TextTarget t)
{
    var cols = string.Join(", ", t.Columns.Select(c => t.Alias + c));
    var rows = new List<(object, long, string?[])>();
    await using var cmd = new SqlCommand($"SELECT {t.IdExpr}, {t.UserExpr}, {cols} FROM {t.From};", conn);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        var values = new string?[t.Columns.Length];
        for (var i = 0; i < t.Columns.Length; i++)
            values[i] = reader.IsDBNull(i + 2) ? null : reader.GetString(i + 2);
        rows.Add((reader.GetValue(0), reader.GetInt64(1), values));
    }
    return rows;
}

// ---- deterministic key columns -------------------------------------------------------------------------------

async Task<(int Changed, int Removed)> RunKeyAsync(KeyTarget t)
{
    var rows = await ReadKeyAsync(t);

    // Which (user, scope, kind, ciphertext) slots are taken. A row whose decoded spelling lands on an occupied slot is
    // a duplicate of the row already sitting there, so it is deleted instead of updated — every one of these tables is
    // either UNIQUE on that tuple or would carry a pointless duplicate.
    var occupancy = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var row in rows)
        Occupy(occupancy, Slot(row.UserId, row.Scope, row.Kind, row.Value), +1);

    var changed = 0;
    var removed = 0;

    foreach (var (id, userId, scope, stored, kind) in rows)
    {
        var clear = await cipher.DecryptDeterministicAsync(userId, stored, ct);
        var decoded = Decode(clear);
        if (decoded == clear)
            continue;

        var replacement = await cipher.DeterministicAsync(userId, decoded, ct);
        var oldSlot = Slot(userId, scope, kind, stored);
        var newSlot = Slot(userId, scope, kind, replacement);
        var taken = occupancy.TryGetValue(newSlot, out var n) && n > 0;

        Occupy(occupancy, oldSlot, -1);
        if (!taken)
            Occupy(occupancy, newSlot, +1);

        if (taken)
            removed++;
        else
            changed++;

        if (dryRun)
            continue;

        await using var cmd = taken
            ? new SqlCommand($"DELETE FROM {t.UpdateTable} WHERE Id = @id;", conn)
            : new SqlCommand($"UPDATE {t.UpdateTable} SET {t.ValueCol} = @v WHERE Id = @id;", conn);
        if (!taken)
            cmd.Parameters.AddWithValue("@v", replacement);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    Console.WriteLine($"  {t.Label,-40} {rows.Count,7} row(s) scanned, {changed,5} rewritten, {removed,5} de-duplicated");
    return (changed, removed);
}

async Task<int> CountKeyAsync(KeyTarget t)
{
    var n = 0;
    foreach (var row in await ReadKeyAsync(t))
    {
        var clear = await cipher.DecryptDeterministicAsync(row.UserId, row.Value, ct);
        if (Decode(clear) != clear)
            n++;
    }
    return n;
}

async Task<List<(long Id, long UserId, string Scope, string Value, int Kind)>> ReadKeyAsync(KeyTarget t)
{
    var rows = new List<(long, long, string, string, int)>();
    await using var cmd = new SqlCommand(
        $"SELECT {t.IdExpr}, {t.UserExpr}, CAST({t.ScopeExpr} AS NVARCHAR(128)), {t.ValueExpr}, {t.KindExpr} " +
        $"FROM {t.From} ORDER BY {t.IdExpr};", conn);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                  Convert.ToInt32(reader.GetValue(4))));
    return rows;
}

static string Slot(long userId, string scope, int kind, string value) => $"{userId} {scope} {kind} {value}";

static void Occupy(Dictionary<string, int> occupancy, string slot, int delta) =>
    occupancy[slot] = occupancy.TryGetValue(slot, out var n) ? n + delta : delta;

// ---- what gets migrated --------------------------------------------------------------------------------------

internal sealed record TextTarget(
    string Label, string From, string Alias, string IdExpr, string UserExpr,
    string UpdateTable, string UpdateIdCol, string[] Columns, bool IsJson);

internal sealed record KeyTarget(
    string Label, string From, string IdExpr, string UserExpr, string ScopeExpr, string ValueExpr, string KindExpr,
    string UpdateTable, string ValueCol);

internal static class Targets
{
    // Prompt text (marker form, tag names inline) and the opaque JSON blobs. Randomized encryption: no uniqueness to
    // preserve, so every row is a straight rewrite.
    public static readonly TextTarget[] Text =
    [
        new("dbo.HistoryEntry", "dbo.HistoryEntry", "", "Id", "UserId", "dbo.HistoryEntry", "Id",
            ["Prompt", "RawPrompt", "RawNegativePrompt"], false),
        new("dbo.ImageBookmark", "dbo.ImageBookmark", "", "Id", "UserId", "dbo.ImageBookmark", "Id",
            ["Prompt"], false),
        new("dbo.PendingJob", "dbo.PendingJob", "", "Id", "UserId", "dbo.PendingJob", "Id",
            ["Prompt"], false),
        new("dbo.Job", "dbo.Job", "", "JobId", "UserId", "dbo.Job", "JobId",
            ["Prompt"], false),
        // JobSlot's owning user hangs off the job.
        new("dbo.JobSlot (text)", "dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId", "s.", "s.Id", "j.UserId",
            "dbo.JobSlot", "Id", ["EffectivePrompt", "RawPrompt", "RawNegativePrompt"], false),
        // RequestJson is REPLAYED to re-render a slot after a restart, so a stale spelling left here would resurrect
        // itself post-migration. MarksJson is keyed by tag name.
        new("dbo.JobSlot (json)", "dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId", "s.", "s.Id", "j.UserId",
            "dbo.JobSlot", "Id", ["MarksJson", "RequestJson"], true),
        // The composer's draft prompt follows the user across devices; the editor's is the inpaint box.
        new("dbo.AppUser (prefs)", "dbo.AppUser", "", "Id", "Id", "dbo.AppUser", "Id",
            ["ComposerPrefs", "EditPrefs"], true),
    ];

    // Canonical names under deterministic encryption — the keys marks, bookmarks and bans are stored under.
    public static readonly KeyTarget[] Keys =
    [
        new("dbo.HistoryMark.Token", "dbo.HistoryMark m JOIN dbo.HistoryEntry h ON h.Id = m.HistoryEntryId",
            "m.Id", "h.UserId", "m.HistoryEntryId", "m.Token", "m.Kind", "dbo.HistoryMark", "Token"),
        new("dbo.ImageBookmarkMark.Token", "dbo.ImageBookmarkMark m JOIN dbo.ImageBookmark b ON b.Id = m.ImageBookmarkId",
            "m.Id", "b.UserId", "m.ImageBookmarkId", "m.Token", "m.Kind", "dbo.ImageBookmarkMark", "Token"),
        // UNIQUE (UserId, Name, Kind) — no further scope.
        new("dbo.TokenBookmark.Name", "dbo.TokenBookmark", "Id", "UserId", "''", "Name", "Kind",
            "dbo.TokenBookmark", "Name"),
        // UNIQUE (UserId, ModelId, Name, Kind) — a ban is per model.
        new("dbo.BannedToken.Name", "dbo.BannedToken", "Id", "UserId", "ModelId", "Name", "Kind",
            "dbo.BannedToken", "Name"),
        // UNIQUE (UserId, ArtistName) — no kind column; every row is an artist.
        new("dbo.ArtistDisplay.ArtistName", "dbo.ArtistDisplay", "Id", "UserId", "''", "ArtistName", "0",
            "dbo.ArtistDisplay", "ArtistName"),
    ];
}
