namespace ImageGen.Comfy.Edit.ChronoEdit;

/// <summary>This subclass's own node ids (the shared head's Model/Clip/Vae/Source come from EditWorkflow.Nodes);
/// values are the graph-local keys, preserved exactly so the emitted graph stays byte-identical. <c>Negative</c> is
/// Wan's quality/motion negative prompt (same default the Wan i2v path uses).</summary>
internal static class Nodes
{
    public const string Negative =
        "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
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
