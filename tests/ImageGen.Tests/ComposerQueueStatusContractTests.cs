namespace ImageGen.Tests;

/// <summary>Client contract for #327: the composer shows progress within the current batch and a separately computed
/// generation-wide remaining count. Both fresh tracking and live recovery must use the same formatter.</summary>
public sealed class ComposerQueueStatusContractTests
{
    [Fact]
    public void Composer_status_keeps_current_batch_progress_and_aggregate_remaining_work()
    {
        string root = RepoRoot();
        string compose = File.ReadAllText(Path.Combine(root, "src", "ImageGen.Web", "wwwroot", "js", "compose.js"));
        string core = File.ReadAllText(Path.Combine(root, "src", "ImageGen.Web", "wwwroot", "js", "core.js"));

        Assert.Contains("function composerCreatingStatus(_recorded, total, job, activeJobs)", compose, StringComparison.Ordinal);
        Assert.Contains(".filter(j => j && j.kind === \"generate\")", compose, StringComparison.Ordinal);
        Assert.Contains("(Number(j.total) || 0) - (Number(j.progress) || 0)", compose, StringComparison.Ordinal);
        Assert.Contains("`Creating ${position}/${batchTotal}`", compose, StringComparison.Ordinal);
        Assert.Contains("`${current} · ${remaining} remaining`", compose, StringComparison.Ordinal);
        Assert.Equal(2, Count(compose, "activeStatus: composerCreatingStatus"));
        Assert.Contains("o.activeStatus(recorded.size, N, job, res.jobs || [])", core, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int at = 0; (at = source.IndexOf(value, at, StringComparison.Ordinal)) >= 0; at += value.Length)
        {
            count++;
        }

        return count;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "configurations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
