namespace ImageGen.Comfy.Edit.AnimaOutpaint;

/// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflow.Nodes).</summary>
internal static class Nodes
{
    public const string ClipSkip = "19";
    public const string Positive = "13";
    public const string Negative = "14";
    public const string Pad = "20";
    public const string WorkingImage = "172";
    public const string WorkingMaskAsImage = "173";
    public const string WorkingMaskImage = "174";
    public const string WorkingMask = "175";
    public const string LlliteApply = "40";
    public const string Encode = "12";
    public const string GrowMaskNode = "30";
    public const string NoiseMask = "31";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
