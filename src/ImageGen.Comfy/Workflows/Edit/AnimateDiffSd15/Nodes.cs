namespace ImageGen.Comfy.Edit.AnimateDiffSd15;

/// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflow.Nodes).</summary>
internal static class Nodes
{
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string MotionLoad = "20";
    public const string MotionApply = "21";
    public const string EvolvedSampling = "22";
    public const string Positive = "13";
    public const string Negative = "12";
    public const string Latent = "7";
    public const string SparseCtrlLoader = "23";
    public const string SparseCtrlPreprocess = "24";
    public const string ControlNetApply = "25";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
