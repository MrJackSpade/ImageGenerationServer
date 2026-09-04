namespace ImageGen.Tests;

/// <summary>Source contracts for the browser-only media gates. There is no JavaScript test runner in this solution,
/// so these pin the fail-closed branches and detached-preview pruning in the shipped scripts.</summary>
public sealed class EditMediaUiContractTests
{
    [Fact]
    public void Source_media_detection_has_no_failure_to_image_fallback()
    {
        string edit = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "edit.js"));

        Assert.Contains("async function detectSrcMedia(id)", edit, StringComparison.Ordinal);
        Assert.Contains("if (!r.ok) throw new Error", edit, StringComparison.Ordinal);
        Assert.Contains("if (kind === \"image\") return \"image\"", edit, StringComparison.Ordinal);
        Assert.Contains("throw new Error(\"the server returned no recognised media kind\")", edit, StringComparison.Ordinal);
        Assert.Contains("setStatus(friendlyError(e), { error: true })", edit, StringComparison.Ordinal);
        Assert.DoesNotContain("media kind check failed", edit, StringComparison.Ordinal);
    }

    [Fact]
    public void Detached_mask_previews_are_removed_before_redraw_and_registration()
    {
        string mask = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "mask-editor.js"));

        Assert.Contains("if (!p.isConnected) previews.delete(p)", mask, StringComparison.Ordinal);
        Assert.Contains("function drawAllPreviews()", mask, StringComparison.Ordinal);
        Assert.Contains("function drawPreview(previewCv)", mask, StringComparison.Ordinal);
        Assert.Equal(2, Count(mask, "prunePreviews();"));
    }

    [Fact]
    public void Refine_offers_random_artist_for_an_artist_capable_workflow_and_submits_it()
    {
        string root = RepoRoot();
        string edit = File.ReadAllText(Path.Combine(root, "src", "ImageGen.Web", "wwwroot", "js", "edit.js"));
        string view = File.ReadAllText(Path.Combine(root, "src", "ImageGen.Web", "Views", "Edit", "Index.cshtml"));

        Assert.Contains("id=\"editRandomArtist\"", view, StringComparison.Ordinal);
        Assert.Contains("chatBucket === \"redraw\" && supportsEditRandomArtist", edit, StringComparison.Ordinal);
        Assert.Contains("randomArtist: wantsEditRandomArtist(eff)", edit, StringComparison.Ordinal);
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
