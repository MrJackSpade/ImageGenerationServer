namespace ImageGen.Tests;

/// <summary>Static contracts for the Models page's generated markup and narrow-viewport layout.</summary>
public sealed class ResponsiveModelsPageTests
{
    [Fact]
    public void Model_binding_picker_keeps_its_label_and_full_width_control_on_a_phone()
    {
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "css", "app.css"));
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "models.js"));
        string core = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "core.js"));

        Assert.Contains("@media (max-width:640px)", css, StringComparison.Ordinal);
        Assert.Contains(".mrow{align-items:stretch;flex-direction:column", css, StringComparison.Ordinal);
        Assert.Contains(".mrow-head .listrow-name{white-space:normal;overflow-wrap:anywhere}", css, StringComparison.Ordinal);
        Assert.Contains(".mrow .slot-pick{flex:none;width:100%;max-width:100%}", css, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Choose a file for ${esc(s.label)}\"", script, StringComparison.Ordinal);
        Assert.Contains("label=\"${escapeHtml(label)}\"", core, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("repository root not found above the test bin dir.");
    }
}
