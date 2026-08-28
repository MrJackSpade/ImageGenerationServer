namespace ImageGen.Tests;

/// <summary>The page-wide live tracker, recovery, and the visible progress panel consume one shared active-job feed.
/// They must not regress to independent /forge/jobs request loops in the same browser window.</summary>
public sealed class ActiveJobsPollingContractTests
{
    [Fact]
    public void Active_job_consumers_share_one_http_reader()
    {
        string root = RepoRoot();
        string js = Path.Combine(root, "src", "ImageGen.Web", "wwwroot", "js");
        string core = File.ReadAllText(Path.Combine(js, "core.js"));
        string compose = File.ReadAllText(Path.Combine(js, "compose.js"));
        string tracker = File.ReadAllText(Path.Combine(js, "tracker.js"));

        Assert.Equal(1, Count(core + compose + tracker, "fetch(`${GATEWAY}/jobs`)"));
        Assert.Contains("async function readActiveJobs(maxAgeMs = 0)", core, StringComparison.Ordinal);
        Assert.Contains("readActiveJobs(2250)", compose, StringComparison.Ordinal);
        Assert.Contains("readActiveJobs(2250)", tracker, StringComparison.Ordinal);
        Assert.Contains("res = await readActiveJobs()", core, StringComparison.Ordinal);
        Assert.Contains("res = await readActiveJobs(500)", core, StringComparison.Ordinal);
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
