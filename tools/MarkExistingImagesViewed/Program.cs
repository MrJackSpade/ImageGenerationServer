//TODO: CHECK FOR FALLBACKS
using Microsoft.Data.SqlClient;

// One-time backfill: mark every image that already exists as VIEWED.
//
// The grids used to outline whatever was generated while a tab happened to be open. The outline now means "you have
// not opened this yet", which is a per-(user, image) row in dbo.ImageView, and unviewed is the ABSENCE of a row. So on
// the deploy that introduces it, every image ever made would light up at once — a library-wide wall of outlines that
// says nothing and that the user would have to clear by hand.
//
// This gives everyone a clean start: everything that exists before the change counts as seen, and only images
// generated afterwards begin unviewed. It is not optional polish — without it the feature's first impression is
// exactly the noise it exists to remove.
//
// Run it BEFORE the deploy, or immediately after; anything generated in between is simply covered by whichever run
// happens later. It only ever INSERTS rows for images that have none, so it is idempotent, resumable, and safe to
// re-run. It never removes a view, so it cannot un-see something the user has actually looked at.
//
// Nothing here decrypts anything and nothing is printed but counts. Image ids are stored plain; prompts are not
// touched at all.
//
// Usage:  dotnet run --project tools/MarkExistingImagesViewed -- [--dry-run] [--conn=<connection string>]
//         (falls back to IMAGEGEN_CONNECTION, then to localhost/ImageGen)

var dryRun = args.Contains("--dry-run");
var connectionString = args.FirstOrDefault(a => a.StartsWith("--conn=", StringComparison.Ordinal))?["--conn=".Length..]
    ?? Environment.GetEnvironmentVariable("IMAGEGEN_CONNECTION")
    ?? "Server=localhost;Database=ImageGen;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

var ct = CancellationToken.None;
await using var conn = new SqlConnection(connectionString);
await conn.OpenAsync(ct);

Console.WriteLine($"Marking every existing image viewed{(dryRun ? "  [DRY RUN - no writes]" : "")}");

const string CountSql = @"
SELECT COUNT(*)
FROM dbo.HistoryEntry h
WHERE NOT EXISTS (SELECT 1 FROM dbo.ImageView v WHERE v.UserId = h.UserId AND v.GatewayImageId = h.GatewayImageId);";

// Every history row of every user that has no view row yet. Joined on the pair, so a user who has already opened
// some images keeps those original timestamps and only the rest are stamped now.
const string InsertSql = @"
INSERT INTO dbo.ImageView (UserId, GatewayImageId, ViewedAtUtc)
SELECT h.UserId, h.GatewayImageId, SYSUTCDATETIME()
FROM dbo.HistoryEntry h
WHERE NOT EXISTS (SELECT 1 FROM dbo.ImageView v WHERE v.UserId = h.UserId AND v.GatewayImageId = h.GatewayImageId);";

int pending;
await using (var cmd = new SqlCommand(CountSql, conn))
    pending = (int)(await cmd.ExecuteScalarAsync(ct))!;

Console.WriteLine($"  {pending} image(s) have no view record.");

if (pending == 0)
{
    Console.WriteLine("Nothing to do.");
    return;
}

if (dryRun)
{
    Console.WriteLine("Dry run - no rows written.");
    return;
}

int inserted;
await using (var cmd = new SqlCommand(InsertSql, conn))
{
    cmd.CommandTimeout = 0;   // a large library is a large single INSERT; let it finish rather than race a clock
    inserted = await cmd.ExecuteNonQueryAsync(ct);
}

Console.WriteLine($"Marked {inserted} image(s) viewed. Re-run any time; it only fills gaps.");
