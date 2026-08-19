using ImageGen.Comfy;

namespace ImageGen.Tests;

public sealed class RenderModelManifestTests
{
    [Fact]
    public void Resolved_model_files_and_native_quantization_are_captured_portably()
    {
        Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowParamKeys.Loader] = "unet",
        };
        ResolvedRequirements requirements = new()
        {
            Checkpoint = "models/diffusion/qwen_image_edit_2511_int8_convrot.safetensors",
            Vae = @"vae\qwen_image_vae.safetensors",
            TextEncoders = ["text/qwen_2.5_vl_7b_fp8.safetensors"],
        };

        ImageGen.Domain.Entities.RenderModelManifest manifest =
            Assert.IsType<ImageGen.Domain.Entities.RenderModelManifest>(RenderModelManifestBuilder.Build(parameters, requirements));

        Assert.Equal("qwen_image_edit_2511_int8_convrot.safetensors", manifest.Checkpoint);
        Assert.Equal("unet", manifest.Loader);
        Assert.Equal("default", manifest.WeightDtype);
        Assert.Equal("int8-convrot", manifest.Quantization);
        Assert.Equal("qwen_image_vae.safetensors", manifest.Vae);
        Assert.Equal(["qwen_2.5_vl_7b_fp8.safetensors"], manifest.TextEncoders);
    }

    [Fact]
    public void Explicit_loader_dtype_and_gguf_quantization_are_kept_separate()
    {
        Dictionary<string, object?> parameters = new()
        {
            [WorkflowParamKeys.Loader] = "unet_gguf",
            [WorkflowParamKeys.WeightDtype] = "fp8_e4m3fn",
        };
        ResolvedRequirements requirements = new() { Checkpoint = "Qwen-Image-Edit-2511-Q6_K.gguf" };

        ImageGen.Domain.Entities.RenderModelManifest manifest =
            Assert.IsType<ImageGen.Domain.Entities.RenderModelManifest>(RenderModelManifestBuilder.Build(parameters, requirements));

        Assert.Equal("fp8_e4m3fn", manifest.WeightDtype);
        Assert.Equal("q6_k", manifest.Quantization);
    }

    [Fact]
    public void A_model_free_workflow_has_no_manifest() =>
        Assert.Null(RenderModelManifestBuilder.Build(new Dictionary<string, object?>(), new ResolvedRequirements()));
}
