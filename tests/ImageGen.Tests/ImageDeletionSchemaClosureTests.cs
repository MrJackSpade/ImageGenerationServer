using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Tests;

/// <summary>
/// The image-deletion cascade is intentionally application-owned rather than expressed entirely through foreign keys.
/// Keep its schema closure explicit: adding a new image-id column must force a decision about whether deletion owns it.
/// </summary>
[Collection("db")]
public sealed class ImageDeletionSchemaClosureTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly HashSet<string> CascadeOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArtistDisplay.GatewayImageId",
        "HistoryEntry.GatewayImageId",
        "ImageBlob.ImageId",
        "ImageBookmark.GatewayImageId",
        "ImageFrame.ImageId",
        "ImageView.GatewayImageId",
        "JobSlot.ImageId",
        "LoraDisplay.GatewayImageId",
        "TagDisplay.GatewayImageId",
    };

    /// <summary>These columns describe render lineage/input ownership. Deleting a library/history image must not
    /// rewrite an active or retained request merely because that request once consumed the same image id.</summary>
    private static readonly HashSet<string> DocumentedExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "JobSlot.LastFrameImageId",
        "JobSlot.MaskImageId",
        "JobSlot.SourceImageId",
        "JobSlotReference.ImageId",
    };

    [Fact]
    public async Task Every_schema_image_id_column_has_an_explicit_deletion_policy()
    {
        HashSet<string> actual = await ImageIdColumnsAsync();
        HashSet<string> classified = new(CascadeOwned, StringComparer.OrdinalIgnoreCase);
        classified.UnionWith(DocumentedExemptions);

        Assert.True(actual.SetEquals(classified),
            "Every schema image-id column must be classified as cascade-owned or a documented lineage exemption. "
            + $"Unclassified: {List(actual.Except(classified))}. Stale classifications: {List(classified.Except(actual))}.");
    }

    [Fact]
    public async Task Delete_removes_every_cascade_owned_image_reference()
    {
        User user = await fixture.NewUserAsync("image-delete-schema-closure");
        string imageId = await fixture.Blobs.AddAsync(
            new NewImageBlob([1, 2, 3, 4], "image/png", 16, 16, ImageBlobKind.Generated), Ct);
        string jobId = Guid.NewGuid().ToString("N");
        DateTime now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        await using (DbConnection conn = await fixture.ConnectionFactory.OpenAsync(Ct))
        {
            await ExecuteAsync(conn,
                "INSERT INTO dbo.HistoryEntry "
                + "(UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, CreatedAtUtc) "
                + "VALUES (@u, @img, 'prompt', 'model', 'model', 'square', @at);",
                ("@u", user.Id), ("@img", imageId), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.ImageView (UserId, GatewayImageId, ViewedAtUtc) VALUES (@u, @img, @at);",
                ("@u", user.Id), ("@img", imageId), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.ImageBookmark "
                + "(UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, OriginalCreatedAtUtc, SavedAtUtc) "
                + "VALUES (@u, @img, 'prompt', 'model', 'model', 'square', @at, @at);",
                ("@u", user.Id), ("@img", imageId), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.ArtistDisplay (UserId, ArtistName, GatewayImageId, SetAtUtc) "
                + "VALUES (@u, 'artist', @img, @at);",
                ("@u", user.Id), ("@img", imageId), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.TagDisplay (UserId, TagName, GatewayImageId, SetAtUtc) "
                + "VALUES (@u, 'tag', @img, @at);",
                ("@u", user.Id), ("@img", imageId), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.LoraDisplay (UserId, LoraName, GatewayImageId, SetAtUtc) "
                + "VALUES (@u, 'lora', @img, @at);",
                ("@u", user.Id), ("@img", imageId), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.ImageFrame (ImageId, FrameIndex, Bytes) VALUES (@img, 0, @bytes);",
                ("@img", imageId), ("@bytes", new byte[] { 5, 6 }));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.Job "
                + "(JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc) "
                + "VALUES (@job, @u, 'machine', 'model', 'prompt', 1, 1, @at, @at);",
                ("@job", jobId), ("@u", user.Id), ("@at", now));
            await ExecuteAsync(conn,
                "INSERT INTO dbo.JobSlot (JobId, SlotIndex, IsEdit, State, ImageId) "
                + "VALUES (@job, 0, 0, 2, @img);",
                ("@job", jobId), ("@img", imageId));
        }

        Assert.True(await fixture.ImageDeletions.DeleteEverywhereAsync(user.Id, imageId, Ct));

        await using DbConnection verification = await fixture.ConnectionFactory.OpenAsync(Ct);
        foreach (string reference in CascadeOwned.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string[] parts = reference.Split('.');
            int count = await CountAsync(verification, parts[0], parts[1], imageId);
            Assert.True(count == 0, $"Deletion left {count} row(s) in {reference} for image '{imageId}'.");
        }
    }

    private async Task<HashSet<string>> ImageIdColumnsAsync()
    {
        await using DbConnection conn = await fixture.ConnectionFactory.OpenAsync(Ct);
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

        if (TestDatabaseFixture.Provider == DatabaseProvider.SqlServer)
        {
            await using DbCommand cmd = conn.Command(
                "SELECT t.name, c.name FROM sys.tables t "
                + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                + "JOIN sys.columns c ON c.object_id = t.object_id "
                + "WHERE s.name = 'dbo' AND LOWER(c.name) LIKE '%imageid';");
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                _ = result.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
            }

            return result;
        }

        List<string> tables = [];
        await using (DbCommand cmd = conn.Command(
            "SELECT name FROM dbo.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"))
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (string table in tables)
        {
            string quoted = table.Replace("'", "''", StringComparison.Ordinal);
            await using DbCommand cmd = conn.Command($"PRAGMA dbo.table_info('{quoted}');");
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                string column = reader.GetString(1);
                if (column.EndsWith("ImageId", StringComparison.OrdinalIgnoreCase))
                {
                    _ = result.Add($"{table}.{column}");
                }
            }
        }

        return result;
    }

    private static async Task ExecuteAsync(
        DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using DbCommand cmd = conn.Command(sql);
        foreach ((string name, object value) in parameters)
        {
            _ = cmd.AddParam(name, value);
        }

        _ = await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<int> CountAsync(
        DbConnection conn, string table, string column, string imageId)
    {
        await using DbCommand cmd = conn.Command(
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE [{column}] = @img;");
        _ = cmd.AddParam("@img", imageId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string List(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return joined.Length == 0 ? "(none)" : joined;
    }
}
