using System.Data.Common;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class ImageFrameRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A replacement that fails after writing its first new frame must put the original complete set back.
    /// A temporary provider-native trigger rejects index 1, deliberately exercising mid-write rollback.</summary>
    [Fact]
    public async Task Failed_replacement_rolls_back_to_the_previous_complete_frame_set()
    {
        string imageId = Guid.NewGuid().ToString("N");
        byte[][] original = [[1, 2, 3], [4, 5, 6]];
        await fixture.Frames.AddFramesAsync(imageId, original, Ct);

        string trigger = "FailImageFrameInsert_" + Guid.NewGuid().ToString("N");
        string create = TestDatabaseFixture.Provider == ImageGen.Infrastructure.DatabaseProvider.SqlServer
            ? $"CREATE TRIGGER dbo.{trigger} ON dbo.ImageFrame AFTER INSERT AS " +
              $"IF EXISTS (SELECT 1 FROM inserted WHERE ImageId = N'{imageId}' AND FrameIndex = 1) " +
              "BEGIN; THROW 51000, 'deliberate frame replacement failure', 1; END;"
            : $"CREATE TRIGGER dbo.{trigger} BEFORE INSERT ON ImageFrame " +
              $"WHEN NEW.ImageId = '{imageId}' AND NEW.FrameIndex = 1 " +
              "BEGIN SELECT RAISE(ABORT, 'deliberate frame replacement failure'); END;";
        string drop = $"DROP TRIGGER dbo.{trigger};";

        await ExecuteAsync(create);
        try
        {
            _ = await Assert.ThrowsAnyAsync<Exception>(
                () => fixture.Frames.AddFramesAsync(imageId, [[9, 9, 9], [8, 8, 8]], Ct));
        }
        finally
        {
            await ExecuteAsync(drop);
        }

        IReadOnlyList<byte[]> after = await fixture.Frames.GetFramesAsync(imageId, Ct);
        Assert.Equal(original.Length, after.Count);
        Assert.Equal(original[0], after[0]);
        Assert.Equal(original[1], after[1]);

        async Task ExecuteAsync(string sql)
        {
            await using DbConnection connection = await fixture.ConnectionFactory.OpenAsync(Ct);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
