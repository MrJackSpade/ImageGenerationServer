namespace ImageGen.Comfy.Edit.HunyuanVideo15I2V;

/// <summary>Own node ids (the model/clip/vae/source head is the inherited <c>Nodes</c>).</summary>
internal static class Nodes
{
    public const string ModelSampling = "30";
    public const string SourceScale = "51";
    public const string SourceSize = "52";
    public const string ClipVisionLoader = "40";
    public const string ClipVisionEncode = "41";
    public const string Positive = "13";
    public const string Negative = "12";
    public const string ImageToVideo = "53";
    public const string Scheduler = "55";
    public const string SamplerSelect = "56";
    public const string Noise = "57";
    public const string Guider = "58";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
