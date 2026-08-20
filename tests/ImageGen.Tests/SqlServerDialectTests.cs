using ImageGen.Infrastructure.Database;

namespace ImageGen.Tests;

public sealed class SqlServerDialectTests
{
    /// <summary>HOLDLOCK makes the MERGE key lookup serializable, so concurrent inserts of one new key cannot both
    /// take NOT MATCHED and race on the primary key.</summary>
    [Fact]
    public void Both_job_upserts_hold_the_target_key_range()
    {
        SqlServerDialect dialect = new();

        Assert.Contains("MERGE dbo.Job WITH (HOLDLOCK) AS t", dialect.UpsertJob, StringComparison.Ordinal);
        Assert.Contains("MERGE dbo.JobSlot WITH (HOLDLOCK) AS t", dialect.UpsertJobSlot, StringComparison.Ordinal);
    }
}
