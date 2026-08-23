namespace ImageGen.Tests;

/// <summary>Source-level browser contracts for small UI behaviors whose regressions do not surface in a .NET
/// compilation. These pin the ordering and rollback branches themselves, not incidental markup.</summary>
public sealed class WebUiRegressionContractTests
{
    [Fact]
    public void Queue_first_paint_does_not_wait_for_the_catalog_or_emit_poll_diagnostics()
    {
        string queue = Js("queue.js");

        Assert.Contains("loadCatalog().then(() =>", queue, StringComparison.Ordinal);
        Assert.Contains("fetchPage(1, false).then(schedulePolling);", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("await loadCatalog()", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("DIAGNOSTIC", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log", queue, StringComparison.Ordinal);
        Assert.Contains("lastSig = null; render(lastRows);", queue, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_workflow_preference_writes_restore_sets_and_visible_controls()
    {
        string list = Js("workflows.js");
        string detail = Js("workflow-detail.js");

        Assert.Contains("if (wasOn) favs.add(id); else favs.delete(id);", list, StringComparison.Ordinal);
        Assert.Contains("if (wasOn) hidden.add(id); else hidden.delete(id);", list, StringComparison.Ordinal);
        Assert.Equal(2, Count(list, "render(); console.error"));

        Assert.Contains("if (wasOn) favs.add(workflow.id); else favs.delete(workflow.id);", detail, StringComparison.Ordinal);
        Assert.Contains("star.classList.toggle(\"on\", wasOn);", detail, StringComparison.Ordinal);
        Assert.Contains("h.classList.toggle(\"on\", wasOn); h.textContent = wasOn ? \"Unhide from picker\" : \"Hide from picker\";", detail, StringComparison.Ordinal);
        Assert.Contains("h.classList.toggle(\"on\", wasOn); h.textContent = wasOn ? \"Unhide from API\" : \"Hide from API\";", detail, StringComparison.Ordinal);
        Assert.Contains("save favorites failed:", detail, StringComparison.Ordinal);
        Assert.Contains("save hidden failed:", detail, StringComparison.Ordinal);
        Assert.Contains("save hidden-api failed:", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_prefers_the_server_slot_aspect_after_prompt_fan_out()
    {
        string compose = Js("compose.js");
        string api = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "ImageGen.Api", "Endpoints", "ForgeApi.cs"));

        Assert.Contains("s.aspect || (meta.slotAspects && meta.slotAspects[s.index])", compose, StringComparison.Ordinal);
        Assert.Contains("s.Gen?.Aspect, s.EffectivePrompt", api, StringComparison.Ordinal);
        Assert.Contains("s.Generate?.Aspect, s.EffectivePrompt", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_shape_picker_supports_source_free_generation_and_conditioning_only_primary_images()
    {
        string edit = Js("edit.js");
        string view = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "ImageGen.Web", "Views", "Edit", "Index.cshtml"));

        Assert.Contains("id=\"editAspect\"", view, StringComparison.Ordinal);
        Assert.Contains("data-aspect=\"reference\"", view, StringComparison.Ordinal);
        Assert.Contains("models.every(supportsReferenceOnly)", edit, StringComparison.Ordinal);
        Assert.Contains("m.supportsReferenceAspectWithSource", edit, StringComparison.Ordinal);
        Assert.Contains("models.every(supportsChosenReferenceAspect)", edit, StringComparison.Ordinal);
        Assert.Contains("models.length === 1", edit, StringComparison.Ordinal);
        Assert.Contains("editRefs.some(r => r.kind === \"image\")", edit, StringComparison.Ordinal);
        Assert.Contains("editCurrent && !eff.supportsReferenceAspectWithSource", edit, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_submission_has_no_pending_job_side_channel_or_dead_batch_entry_point()
    {
        string core = Js("core.js");
        string compose = Js("compose.js");
        string edit = Js("edit.js");

        Assert.DoesNotContain("postPending", core, StringComparison.Ordinal);
        Assert.DoesNotContain("onJob", core, StringComparison.Ordinal);
        Assert.DoesNotContain("postPending", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("startBatch", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("postPending", edit, StringComparison.Ordinal);
    }

    [Fact]
    public void Extended_ranges_are_workflow_owned_not_per_generation_toggles()
    {
        string core = Js("core.js");

        Assert.DoesNotContain("mp-range-toggle", core, StringComparison.Ordinal);
        Assert.DoesNotContain("alternate.label", core, StringComparison.Ordinal);
        Assert.Contains("setBound(\"max\", alternateMax);", core, StringComparison.Ordinal);
        Assert.Contains("inp.rangeWarningRefresh = refreshWarning;", core, StringComparison.Ordinal);
    }

    [Fact]
    public void Untrained_resolution_is_workflow_owned_and_changes_both_size_editors()
    {
        string compose = Js("compose.js");
        string detail = Js("workflow-detail.js");

        Assert.Contains("data.allowUntrainedResolution === true", compose, StringComparison.Ordinal);
        Assert.Contains("el.min = 1; el.step = 1; el.removeAttribute(\"max\")", compose, StringComparison.Ordinal);
        Assert.Contains("other sizes may fail, degrade output, or exhaust memory", compose, StringComparison.Ordinal);
        Assert.Contains("settingsData.allowUntrainedResolution === true", detail, StringComparison.Ordinal);
        Assert.Contains("el.min = 1; el.step = 1", detail, StringComparison.Ordinal);
        Assert.Contains("other sizes may fail, degrade output, or exhaust memory", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Machine_tag_generator_toggle_also_enables_composer_autocomplete()
    {
        string compose = Js("compose.js");

        Assert.Contains("c.tagging || (r.tagGeneratorEnabled ? { tags: true, artists: true } : null)", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_tip_preference_is_configurable_and_hides_the_entire_help_block()
    {
        string compose = Js("compose.js");
        string settings = Js("settings.js");
        string core = Js("core.js");
        string view = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "ImageGen.Web", "Views", "Settings", "Configurations.cshtml"));

        Assert.Contains("id=\"hideWorkflowTips\"", view, StringComparison.Ordinal);
        Assert.Contains("saveHideWorkflowTips(box.checked)", settings, StringComparison.Ordinal);
        Assert.Contains("/api/settings/hide-workflow-tips", core, StringComparison.Ordinal);
        Assert.Contains("hideWorkflowTips = !!(prefs.settings && prefs.settings.hideWorkflowTips)", compose, StringComparison.Ordinal);
        Assert.Contains("if (hideWorkflowTips || !m || !help)", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_composer_restores_a_model_selection_scoped_to_its_own_page_or_tab()
    {
        string compose = Js("compose.js");
        string edit = Js("edit.js");

        // Generate owns composerPrefs and restores its complete selected set independently of the Edit page.
        Assert.Contains("modelIds: selectedModelIds()", compose, StringComparison.Ordinal);
        Assert.Contains("Array.isArray(p.modelIds) ? p.modelIds", compose, StringComparison.Ordinal);

        // The shared Edit-page picker has an independent selection slot for every tab it represents.
        Assert.Contains("const CHAT_BUCKETS = [\"edit\", \"redraw\", \"upscale\", \"effects\", \"animate\", \"video\"]", edit, StringComparison.Ordinal);
        Assert.Contains("modelIdsByMode: selectedEditIdsByMode", edit, StringComparison.Ordinal);
        Assert.Contains("selectedEditIdsByMode[chatBucket] = ids", edit, StringComparison.Ordinal);
        Assert.Contains("(selectedEditIdsByMode[chatBucket] || [])", edit, StringComparison.Ordinal);
        Assert.Contains("p.modelIdsByMode[bucket]", edit, StringComparison.Ordinal);

        // Outpaint has its own picker and persisted id rather than sharing any chat-tab selection.
        Assert.Contains("outpaintWorkflowId: selectedOutpaintId", edit, StringComparison.Ordinal);
        Assert.Contains("savedOutpaintWorkflowId", edit, StringComparison.Ordinal);
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

    private static string Js(string file) => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", file));

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
