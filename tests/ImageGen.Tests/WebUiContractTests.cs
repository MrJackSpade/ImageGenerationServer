using ImageGen.Web.ViewModels;
using System.Text.RegularExpressions;

namespace ImageGen.Tests;

/// <summary>Source and presentation-contract boundaries for browser behavior that otherwise has no compiler.</summary>
public sealed class WebUiContractTests
{
    [Fact]
    public void Lightbox_consumes_JSON_and_records_viewing_separately()
    {
        string lightbox = Js("lightbox.js");
        string detail = Js("detail.js");
        string controller = Source("src", "ImageGen.Web", "Controllers", "ImageController.cs");

        Assert.Contains("/detail", lightbox, StringComparison.Ordinal);
        Assert.Contains("await r.json()", lightbox, StringComparison.Ordinal);
        Assert.Contains("renderImageDetail(content, rec)", lightbox, StringComparison.Ordinal);
        Assert.Contains("/view", lightbox, StringComparison.Ordinal);
        Assert.DoesNotContain("/card", lightbox, StringComparison.Ordinal);

        Assert.Contains("function renderImageDetail(root, rec)", detail, StringComparison.Ordinal);
        Assert.Contains("""[HttpGet("/image/{id}/detail")]""", controller, StringComparison.Ordinal);
        Assert.Contains("""[HttpPost("/image/{id}/view")]""", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("""[HttpGet("/image/{id}/card")]""", controller, StringComparison.Ordinal);
        Assert.Contains("Json(vm.ToRecord())", controller, StringComparison.Ordinal);
        // Detail presentation spans several independent repositories. Keep those reads in parallel so opening one
        // lightbox does not add every connection/query delay into its response time.
        Assert.Contains("Task<ImageReadGrant?> visibilityTask", controller, StringComparison.Ordinal);
        Assert.Contains("Task<HistoryNeighbors> neighborsTask", controller, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(controller, @"await\s+Task\.WhenAll\(visibilityTask,\s*detailTask\)").Count);
        Assert.Contains("Task.WhenAll(neighborsTask, isBookmarkedTask, bannedForModelTask, tokensTask)", controller,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_dynamic_image_grid_uses_the_one_shared_card_builder()
    {
        string core = Js("core.js");
        _ = Assert.Single(Regex.Matches(core, @"function\s+buildImageCard\s*\(").Cast<Match>());
        Assert.Contains("r.viewed !== true", core, StringComparison.Ordinal);

        foreach (string file in new[] { "gallery.js", "artist.js", "workflow-detail.js", "recents.js" })
        {
            string source = Js(file);
            Assert.Contains("buildImageCard(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("function cardHtml(", source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("function card(r)", Js("recents.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void Behavioral_explanations_are_tooltips_not_visible_labels()
    {
        string composer = Source("src", "ImageGen.Web", "Views", "Shared", "_Composer.cshtml");
        string gallery = Source("src", "ImageGen.Web", "Views", "Gallery", "Index.cshtml");
        string machine = Js("machine.js");

        Assert.DoesNotContain("(leave blank for a sensible default)", composer, StringComparison.Ordinal);
        Assert.Contains("""title="Leave blank""", composer, StringComparison.Ordinal);
        Assert.DoesNotContain("Search prompts —", gallery, StringComparison.Ordinal);
        Assert.Contains("""title="An image must contain every search word.""", gallery, StringComparison.Ordinal);
        Assert.DoesNotContain("tag.textContent = \"restart to apply\"", machine, StringComparison.Ordinal);
        Assert.DoesNotContain("tag.textContent = \"in the config file\"", machine, StringComparison.Ordinal);
        Assert.Contains("tag.className = \"fld-help\"", machine, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_display_names_come_from_catalog_metadata()
    {
        string edit = Js("edit.js");
        string dto = Source("src", "ImageGen.Comfy", "CatalogJson.cs");
        string descriptor = Source("src", "ImageGen.Application", "Workflows", "WorkflowModels.cs");

        Assert.DoesNotContain("const EDIT_NAME", edit, StringComparison.Ordinal);
        Assert.Contains("m.shortName || m.friendly_name", edit, StringComparison.Ordinal);
        Assert.Contains("shortName: r.shortName || null", edit, StringComparison.Ordinal);
        Assert.Contains("""[JsonPropertyName("short_name")]""", dto, StringComparison.Ordinal);
        Assert.Contains("string? ShortName", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_only_guard_tracks_multiline_response_text_into_HTML_sinks()
    {
        string guard = Source("tools", "check-js-json-only.ps1");

        Assert.Contains("Get-Content $file.FullName -Raw", guard, StringComparison.Ordinal);
        Assert.Contains("RegexOptions]::Singleline", guard, StringComparison.Ordinal);
        Assert.Contains("(?<body>", guard, StringComparison.Ordinal);
        Assert.Contains(@"\k<body>", guard, StringComparison.Ordinal);
        Assert.Contains("innerHTML|outerHTML", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_JSON_record_keeps_rendering_and_action_state_together()
    {
        ImageDetailViewModel vm = new()
        {
            Entry = new ImageDetailView(
                "img-1", "plain prompt", "Model", "workflow", "square",
                new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc),
                new Dictionary<string, string>()),
            IsBookmarked = true,
            MarkerPrompt = "#plain_prompt",
            MarkerNegativePrompt = "blurry",
            OriginalPrompt = "{plain|bright} prompt",
        };

        ImageDetailRecord record = vm.ToRecord();

        Assert.Equal("img-1", record.Id);
        Assert.True(record.Bookmarked);
        Assert.Equal("#plain_prompt", record.MarkerPrompt);
        Assert.Equal("blurry", record.NegativePrompt);
        Assert.Equal("{plain|bright} prompt", record.OriginalPrompt);
        _ = Assert.Single(record.Chips);
    }

    private static string Js(string file) => Source("src", "ImageGen.Web", "wwwroot", "js", file);

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
