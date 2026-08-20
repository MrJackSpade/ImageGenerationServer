namespace ImageGen.Comfy.Edit.ChronoEdit;

/// <summary>This subclass's own node ids (the shared head's Model/Clip/Vae/Source come from EditWorkflow.Nodes);
/// values are the graph-local keys, preserved exactly so the emitted graph stays byte-identical.</summary>
internal static class Nodes
{
    public const string ModelSampling = "20";
    public const string ScaleRope = "21";
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string ClipVisionLoaderNode = "30";
    public const string ClipVisionEncodeNode = "31";
    public const string PositiveEncode = "13";
    public const string NegativeEncode = "12";
    public const string I2VConditioning = "14";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string LastFrame = "16";
    public const string Save = "9";
}
