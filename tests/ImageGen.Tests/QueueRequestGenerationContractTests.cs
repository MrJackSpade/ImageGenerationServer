namespace ImageGen.Tests;

/// <summary>Client contract for #254: a response may update queue state only if no newer poll or navigation request
/// started while it was in flight.</summary>
public sealed class QueueRequestGenerationContractTests
{
    [Fact]
    public void Queue_drops_responses_from_superseded_requests()
    {
        string queue = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "queue.js"));

        Assert.Contains("pollTimer = null, seq = 0", queue, StringComparison.Ordinal);
        Assert.Contains("const mine = ++seq;", queue, StringComparison.Ordinal);
        Assert.Contains("if (mine !== seq) return;", queue, StringComparison.Ordinal);
        Assert.True(
            queue.IndexOf("if (mine !== seq) return;", StringComparison.Ordinal)
            < queue.IndexOf("page = data.page || p", StringComparison.Ordinal),
            "The stale-response guard must run before any shared queue state is updated.");
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
