namespace ImageGen.Tests;

/// <summary>The LoRA picker receives CivitAI names after its first paint; search must cover both identities and
/// refresh an active query when that asynchronous metadata arrives.</summary>
public sealed class LoraSearchContractTests
{
    [Fact]
    public void Picker_searches_filename_and_Civitai_display_name_and_refreshes_on_metadata()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "loraPicker.js"));

        Assert.Contains("const matchesSearch = (l, q) => l.name.toLowerCase().includes(q)", source, StringComparison.Ordinal);
        Assert.Contains("String(l.displayName || \"\").toLowerCase().includes(q)", source, StringComparison.Ordinal);
        Assert.Contains("all.filter(l => matchesSearch(l, q) && showable(l))", source, StringComparison.Ordinal);
        Assert.Contains("if (changed && search.trim()) render();", source, StringComparison.Ordinal);
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
