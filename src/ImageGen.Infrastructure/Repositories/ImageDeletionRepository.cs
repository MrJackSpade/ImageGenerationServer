using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="IImageDeletionRepository"/>. One transaction covers every table that can name an image id, so a delete
/// either takes all of it or none of it.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class ImageDeletionRepository(IDbConnectionFactory connectionFactory) : IImageDeletionRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    /// <summary>
    /// The whole cascade. Notes on why it is shaped this way:
    /// <list type="bullet">
    /// <item><c>HistoryEntry</c> / <c>ImageBookmark</c> cascade to their mark + category child rows
    /// (FK <c>ON DELETE CASCADE</c>), so those tables are not named here.</item>
    /// <item>The <c>HistoryEntry</c> delete is user-scoped, and its rowcount is the "did this user actually own it"
    /// answer the endpoint turns into 204 vs 404.</item>
    /// <item><c>JobSlot</c> rows are only removed for a FINALIZED job. A live job is a write-through cache of the
    /// in-memory queue, which re-upserts its whole slot set on the next transition, so deleting a live slot here
    /// would simply be undone. Slots stranded by a delete during an in-flight batch are swept at finalization.</item>
    /// <item>The blob goes only once no history entry (of any user) still names it. Image ids are minted GUIDs, so
    /// sharing cannot happen today; the guard keeps the delete correct if that ever changes.</item>
    /// <item><c>ImageView</c> is named here rather than left to accumulate: the whole point of the cascade is that a
    /// deleted image leaves nothing behind that references it.</item>
    /// </list>
    /// <para>The cascade is expressed as ordinary statements the caller orchestrates rather than one multi-statement
    /// T-SQL batch: <c>DECLARE @jobs TABLE</c>, <c>DELETE ... OUTPUT deleted.JobId INTO</c>, <c>@@ROWCOUNT</c> and the
    /// <c>DELETE alias FROM</c> form all lack a SQLite equivalent. The rowcount comes from <c>ExecuteNonQuery</c>, and
    /// the set of touched jobs is read out before the delete instead of captured by it. Identical semantics on either
    /// engine, and the transaction makes it all-or-nothing.</para>
    /// </summary>
    public async Task<bool> DeleteEverywhereAsync(long userId, string gatewayImageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // 1. The user's own references to this image.
        foreach (string? table in new[] { "dbo.ImageBookmark", "dbo.ArtistDisplay", "dbo.ImageView" })
        {
            await using DbCommand cmd = conn.Command(
                $"DELETE FROM {table} WHERE UserId = @userId AND GatewayImageId = @img;", tx);
            cmd.AddParam("@userId", userId);
            cmd.AddParam("@img", gatewayImageId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2. History. The rowcount IS the ownership answer the endpoint turns into 204 vs 404.
        int removed;
        await using (DbCommand cmd = conn.Command(
            "DELETE FROM dbo.HistoryEntry WHERE UserId = @userId AND GatewayImageId = @img;", tx))
        {
            cmd.AddParam("@userId", userId);
            cmd.AddParam("@img", gatewayImageId);
            removed = await cmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Which finalized jobs of this user hold a slot for the image. Read FIRST, because after the delete
        //    below there is nothing left to identify them by.
        List<string> jobIds = new List<string>();
        await using (DbCommand cmd = conn.Command(
            "SELECT DISTINCT s.JobId FROM dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId " +
            "WHERE s.ImageId = @img AND j.UserId = @userId AND j.Status <> 0;", tx))
        {
            cmd.AddParam("@userId", userId);
            cmd.AddParam("@img", gatewayImageId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) jobIds.Add(reader.GetString(0));
        }

        if (jobIds.Count > 0)
        {
            string[] ps = jobIds.Select((_, i) => "@j" + i).ToArray();

            // 4. The slots themselves. Same rows the SELECT above matched: same image, same jobs.
            await using (DbCommand cmd = conn.Command(
                $"DELETE FROM dbo.JobSlot WHERE ImageId = @img AND JobId IN ({string.Join(',', ps)});", tx))
            {
                cmd.AddParam("@img", gatewayImageId);
                for (int i = 0; i < jobIds.Count; i++) cmd.AddParam(ps[i], jobIds[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 5. A job whose last slot just went takes the job row with it. Asking which ones still have slots and
            //    deleting the difference keeps this a plain IN-list delete -- a correlated NOT EXISTS against the
            //    delete target cannot be written the same way on both engines (SQLite can't alias a DELETE target).
            HashSet<string> survivors = new HashSet<string>(StringComparer.Ordinal);
            await using (DbCommand cmd = conn.Command(
                $"SELECT DISTINCT JobId FROM dbo.JobSlot WHERE JobId IN ({string.Join(',', ps)});", tx))
            {
                for (int i = 0; i < jobIds.Count; i++) cmd.AddParam(ps[i], jobIds[i]);
                await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) survivors.Add(reader.GetString(0));
            }

            List<string> orphaned = jobIds.Where(id => !survivors.Contains(id)).ToList();
            if (orphaned.Count > 0)
            {
                string[] ops = orphaned.Select((_, i) => "@o" + i).ToArray();
                await using DbCommand cmd = conn.Command(
                    $"DELETE FROM dbo.Job WHERE JobId IN ({string.Join(',', ops)});", tx);
                for (int i = 0; i < orphaned.Count; i++) cmd.AddParam(ops[i], orphaned[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // 6. The pixels: every frame, then the blob itself once no history entry of ANY user still names it.
        await using (DbCommand cmd = conn.Command("DELETE FROM dbo.ImageFrame WHERE ImageId = @img;", tx))
        {
            cmd.AddParam("@img", gatewayImageId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (DbCommand cmd = conn.Command(
            "DELETE FROM dbo.ImageBlob WHERE ImageId = @img " +
            "  AND NOT EXISTS (SELECT 1 FROM dbo.HistoryEntry h WHERE h.GatewayImageId = @img);", tx))
        {
            cmd.AddParam("@img", gatewayImageId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return removed > 0;
    }
}
