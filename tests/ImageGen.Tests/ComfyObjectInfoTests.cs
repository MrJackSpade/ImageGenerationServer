using ImageGen.Comfy;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Pins the two object_info combo shapes ComfyUI emits side by side today. Presence-gating reads the filename list
/// out of every loader node; if that read comes back empty the caller swallows it and every configuration gated on
/// that loader silently vanishes from the catalog. These are verbatim payload fragments from a live ComfyUI.
/// </summary>
public sealed class ComfyObjectInfoTests
{
    private static JsonElement Key(string json, string node, string key) =>
        JsonDocument.Parse(json).RootElement.GetProperty(node).GetProperty("input").GetProperty("required").GetProperty(key);

    private static string[] Options(JsonElement el) =>
        ComfyClient.ComboOptions(el).Select(e => e.RequireString()).ToArray();

    [Fact]
    public void Classic_combo_lists_its_options_at_slot_zero()
    {
        // V1 node (VAELoader): the names are the first element.
        const string json = """
        {"VAELoader":{"input":{"required":{"vae_name":[["ae.safetensors","flux2-vae.safetensors"]]}}}}
        """;
        Assert.Equal(new[] { "ae.safetensors", "flux2-vae.safetensors" }, Options(Key(json, "VAELoader", "vae_name")));
    }

    [Fact]
    public void V3_combo_lists_its_options_under_the_spec_object()
    {
        // V3 node (UpscaleModelLoader): slot 0 is the literal string "COMBO", the names live in slot 1's "options".
        // Reading slot 0 as an array throws here — the bug this guards.
        const string json = """
        {"UpscaleModelLoader":{"input":{"required":{"model_name":["COMBO",{"multiselect":false,"options":["2x-AnimeSharpV2_RPLKSR_Sharp.pth","4xNomos2_hq_dat2.safetensors"]}]}}}}
        """;
        Assert.Equal(
            new[] { "2x-AnimeSharpV2_RPLKSR_Sharp.pth", "4xNomos2_hq_dat2.safetensors" },
            Options(Key(json, "UpscaleModelLoader", "model_name")));
    }

    [Fact]
    public void Unknown_combo_shapes_yield_nothing_rather_than_throwing()
    {
        foreach (string? json in new[]
        {
            """{"N":{"input":{"required":{"k":[]}}}}""",                        // empty spec
            """{"N":{"input":{"required":{"k":["COMBO"]}}}}""",                 // V3 shape with no options object
            """{"N":{"input":{"required":{"k":["COMBO",{"multiselect":false}]}}}}""",   // options key absent
            """{"N":{"input":{"required":{"k":"INT"}}}}""",                     // not a spec array at all
        })
        {
            Assert.Empty(Options(Key(json, "N", "k")));
        }
    }
}
