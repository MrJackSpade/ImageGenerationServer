namespace ImageGen.Comfy.Edit.WanA14bI2V;

/// <summary>Wan 2.2 I2V-A14B's own node ids, named by role; the MoE experts + samplers ("4"/"5"/"41"/"51"/"3"/"31") are
/// written by Vid (see <see cref="VidNodes"/>), and EditNodes.Source ("10") is the inherited edit-head source-image role
/// reused here.</summary>
internal static class WanA14bI2VWorkflowNodes
{
    public const string Clip = "20";
    public const string Vae = "21";
    public const string Positive = "6";
    public const string Negative = "7";
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string EndFrame = "12";
    public const string Cond = "14";
    public const string Decode = "8";
    public const string Save = "9";
    public const string PadCanvas = "71";
    public const string PadMask = "72";
    public const string PadComposite = "73";
    public const string EndScale = "76";
    public const string EndPadCanvas = "77";
    public const string EndPadComposite = "78";
}
