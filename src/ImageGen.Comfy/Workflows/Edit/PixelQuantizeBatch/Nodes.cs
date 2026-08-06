namespace ImageGen.Comfy.Edit.PixelQuantizeBatch;

/// <summary>This workflow's own node ids (source LoadImage is the inherited EditNodes.Source; per-reference LoadImage/ImageBatch ids are computed 100+).</summary>
internal static class Nodes
{
    public const string Matte = "15";
    public const string Quantize = "20";
    public const string Save = "9";
}
