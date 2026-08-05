using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Splits a sigma schedule at a denoise fraction (ComfyUI core) — the img2img tail of the Flux.2 Klein
/// pixelizer's custom-sampler path. Output 0 is the high-sigma head, output 1 the low-sigma tail the img2img runs.
/// One typed record per ComfyUI class type; inputs are declared in the exact order the old anonymous-object inputs
/// were written, so the emitted graph is byte-identical.</summary>
public sealed record SplitSigmasDenoise : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SplitSigmasDenoise;
    [JsonPropertyName("sigmas")]  public required Output<Slot.Sigmas> Sigmas { get; init; }
    [JsonPropertyName("denoise")] public required double Denoise { get; init; }
    public static Output<Slot.Sigmas> HighOut(string id) => new(id, 0);
    public static Output<Slot.Sigmas> LowOut(string id) => new(id, 1);
}

/// <summary>An empty SD3-family latent whose width/height are WIRED from another node's int outputs (e.g.
/// <see cref="GetImageSize"/>). Same ComfyUI class type as the literal-dimension <see cref="EmptyLatent"/> built with
/// <see cref="ComfyNodeTypes.EmptySD3LatentImage"/>, but its dimensions are edges rather than constants, so it is a
/// distinct record — the Qwen pixelizer sizes its generate-fresh latent to the scaled source read via GetImageSize.</summary>
public sealed record EmptySD3LatentFromSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptySD3LatentImage;
    [JsonPropertyName("width")]      public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]     public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>The DreamOmni2 editor driven in PIXEL-ART mode: the same <c>RunningHub DreamOmni2 Editor</c> class type as
/// <see cref="RunningHubDreamOmni2Editor"/>, but with the per-step pixel-manifold projection knobs (<c>pixel_art</c> +
/// grid/palette/ramp + render size + img2img <c>strength</c>) the pipeline runs INSIDE its own diffusion. A distinct
/// record because the input shape differs; inputs are declared in the exact order the old anonymous object wrote them,
/// so the emitted graph is byte-identical.</summary>
public sealed record RunningHubDreamOmni2PixelizeEditor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RunningHubDreamOmni2Editor;
    [JsonPropertyName("pipeline")]            public required Output<Slot.Model> Pipeline { get; init; }
    [JsonPropertyName("src_image")]           public required Output<Slot.Image> SrcImage { get; init; }
    [JsonPropertyName("ref_image")]           public required Output<Slot.Image> RefImage { get; init; }
    [JsonPropertyName("prompt")]              public required string Prompt { get; init; }
    [JsonPropertyName("num_inference_steps")] public required int NumInferenceSteps { get; init; }
    [JsonPropertyName("guidance_scale")]      public required double GuidanceScale { get; init; }
    [JsonPropertyName("seed")]                public required long Seed { get; init; }
    [JsonPropertyName("pixel_art")]           public required bool PixelArt { get; init; }
    [JsonPropertyName("grid_w")]              public required int GridW { get; init; }
    [JsonPropertyName("grid_h")]              public required int GridH { get; init; }
    [JsonPropertyName("palette")]             public required string Palette { get; init; }
    [JsonPropertyName("proj_method")]         public required string ProjMethod { get; init; }
    [JsonPropertyName("virtual_resolution")]  public required int VirtualResolution { get; init; }
    [JsonPropertyName("w_start")]             public required double WStart { get; init; }
    [JsonPropertyName("w_end")]               public required double WEnd { get; init; }
    [JsonPropertyName("proj_start")]          public required double ProjStart { get; init; }
    [JsonPropertyName("proj_end")]            public required double ProjEnd { get; init; }
    [JsonPropertyName("project_every")]       public required int ProjectEvery { get; init; }
    [JsonPropertyName("render_width")]        public required int RenderWidth { get; init; }
    [JsonPropertyName("render_height")]       public required int RenderHeight { get; init; }
    [JsonPropertyName("strength")]            public required double Strength { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
