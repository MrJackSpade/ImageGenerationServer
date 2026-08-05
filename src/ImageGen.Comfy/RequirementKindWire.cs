namespace ImageGen.Comfy;

/// <summary>The wire spelling of each <see cref="RequirementKind"/> — the <c>kind</c> string a model's JSON declares
/// and <see cref="WorkflowCatalog"/> parses back to the enum, mirrored by <c>ComfyClient</c>'s enum→loader-input map.
/// One spelling per kind, written once here, so the parse and the reverse lookup can never disagree.</summary>
internal static class RequirementKindWire
{
    public const string Checkpoint = "checkpoint";
    public const string Unet = "unet";
    public const string UnetGguf = "unet_gguf";
    public const string Vae = "vae";
    public const string TextEncoder = "text_encoder";
    public const string MotionModel = "motion_model";
    public const string ControlNet = "controlnet";
    public const string UpscaleModel = "upscale_model";
    public const string Lora = "lora";
    public const string ClipVision = "clip_vision";
    public const string IpAdapter = "ipadapter";
    public const string LatentUpscaleModel = "latent_upscale_model";
    public const string SeedVr2 = "seedvr2";
    public const string CustomNode = "custom_node";
}
