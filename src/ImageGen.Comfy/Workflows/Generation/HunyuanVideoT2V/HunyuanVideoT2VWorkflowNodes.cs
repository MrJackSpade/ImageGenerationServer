namespace ImageGen.Comfy.Generation.HunyuanVideoT2V;

/// <summary>Original HunyuanVideo 13B t2v's own node ids beyond the inherited txt2img roles
/// (Model/Clip/Vae/Positive/Guidance/Sampler/Decode/Save reused).</summary>
internal static class HunyuanVideoT2VWorkflowNodes
{
    public const string ModelSampling = "30";
    public const string VideoLatent = "14";
    public const string Scheduler = "55";
    public const string SamplerSelect = "56";
    public const string Noise = "57";
    public const string Guider = "58";
}
