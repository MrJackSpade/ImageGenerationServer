using System.Text.RegularExpressions;

namespace ImageGen.Tests;

/// <summary>Static security boundaries that can silently regress without producing a compile error.</summary>
public sealed partial class SecurityHardeningTests
{
    [SkippableFact]
    public void GitHub_actions_are_immutable_and_each_workflow_declares_permissions()
    {
        string root = RepositoryRoot();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"))
        {
            string yaml = File.ReadAllText(file);
            Assert.Contains("\npermissions:\n", yaml.Replace("\r\n", "\n"), StringComparison.Ordinal);
            Assert.DoesNotMatch(MutableActionTag(), yaml);
            Assert.DoesNotMatch(UnpinnedAction(), yaml);
        }
    }

    [SkippableFact]
    public void The_Comfy_gate_cannot_swallow_its_installation_failure()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "comfy-nodes", "imagegen_gate", "__init__.py"));

        Assert.Contains("middlewares.append(_imagegen_gate)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("except Exception", source, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Browser_HTML_sinks_use_the_shared_escaper_and_help_links_are_scheme_checked()
    {
        string js = Path.Combine(RepositoryRoot(), "src", "ImageGen.Web", "wwwroot", "js");
        Assert.Contains("const esc = escapeHtml;", File.ReadAllText(Path.Combine(js, "loraManager.js")), StringComparison.Ordinal);
        Assert.Contains("const esc = escapeHtml;", File.ReadAllText(Path.Combine(js, "loraPicker.js")), StringComparison.Ordinal);

        string compose = File.ReadAllText(Path.Combine(js, "compose.js"));
        Assert.Contains("safeExternalUrl(help.link.url)", compose, StringComparison.Ordinal);
        Assert.Contains("url.protocol === \"http:\" || url.protocol === \"https:\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("encodeURI(help.link.url)", compose, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"uses:\s+[^\s@]+@v\d+(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MutableActionTag();

    [GeneratedRegex(@"uses:\s+[^\s@]+@(?![0-9a-f]{40}(?:\s|#|$))[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnpinnedAction();

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".github", "workflows")))
        {
            directory = directory.Parent;
        }

        Skip.If(directory is null, "not running from a source checkout");
        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
