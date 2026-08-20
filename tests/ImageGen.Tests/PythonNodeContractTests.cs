namespace ImageGen.Tests;

/// <summary>Source contracts for first-party Python shipped into ComfyUI. The .NET build does not import these packs,
/// so pin the failure/lifetime rules that would otherwise regress without a compiler signal.</summary>
public sealed class PythonNodeContractTests
{
    [Fact]
    public void Gguf_metadata_does_not_swallow_process_control_and_failed_weight_copies_propagate()
    {
        string loader = Read("comfy-nodes", "ComfyUI-GGUF", "loader.py");
        string ops = Read("comfy-nodes", "ComfyUI-GGUF", "ops.py");

        Assert.Contains("except Exception as exc:", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("except:\n", loader, StringComparison.Ordinal);
        Assert.Contains("return super().copy_(*args, **kwargs)", ops, StringComparison.Ordinal);
        Assert.DoesNotContain("ignoring 'copy_'", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void Auxiliary_models_are_locked_and_registered_with_the_comfy_vram_arbiter()
    {
        foreach (string file in new[] { "sketchkeras_node.py", "birefnet_matte.py" })
        {
            string source = Read("comfy-nodes", "ComfyUI-PixelHarness", file);
            Assert.DoesNotContain("_MODEL =", source, StringComparison.Ordinal);
            Assert.Contains("threading.Lock()", source, StringComparison.Ordinal);
            Assert.Contains("ModelPatcher", source, StringComparison.Ordinal);
            Assert.Contains("load_models_gpu([_PATCHER])", source, StringComparison.Ordinal);
            Assert.Contains("unet_offload_device()", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Projection_cadence_is_derived_from_the_current_sigma_schedule()
    {
        string nodes = Read("comfy-nodes", "ComfyUI-PixelHarness", "nodes.py");

        Assert.Contains("if idx % project_every != 0:", nodes, StringComparison.Ordinal);
        Assert.DoesNotContain("state = {\"n\": 0}", nodes, StringComparison.Ordinal);
        Assert.DoesNotContain("state[\"n\"]", nodes, StringComparison.Ordinal);
    }

    [Fact]
    public void Feature_pixelizer_rejects_grid_cells_with_no_source_pixels()
    {
        string pixelizer = Read("comfy-nodes", "ComfyUI-PixelHarness", "pixelize_fp.py");

        Assert.Contains("out_w > W or out_h > H", pixelizer, StringComparison.Ordinal);
        Assert.Contains("rounded grid cells have no source pixels", pixelizer, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_document_generator_avoids_pre_312_f_string_backslash_syntax()
    {
        string generator = Read("tools", "gen-models-doc.py");

        Assert.Contains("detail = detail.replace(\"|\", \"\\\\|\")", generator, StringComparison.Ordinal);
        Assert.Contains("f\"| {label} | {detail} | {links} |\"", generator, StringComparison.Ordinal);
        Assert.DoesNotContain("{detail.replace", generator, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(
        Path.Combine([RepoRoot(), .. parts]));

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
