namespace ImageGen.Comfy.Generation.Krea2Refine;

/// <summary>Krea 2 refine's own node ids beyond the inherited txt2img roles
/// (Nodes.Model/Clip/Vae/Positive/Negative/Latent/Sampler/Decode/Save reused).</summary>
internal static class Krea2RefineWorkflowNodes
{
    public const string RefinerModel = "40";
    public const string RefinerSampler = "30";
    public const string RefinerCkAttention = "38";
}
