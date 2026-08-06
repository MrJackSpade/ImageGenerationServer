namespace ImageGen.Comfy.Edit.QwenImageInpaint;

/// <summary>QwenImageInpaintWorkflow's own node ids, atop the inherited edit head and QwenInstantXInpaintBase's nodes.</summary>
internal static class Nodes
{
    public const string MaskLoad = "11";
    public const string PrefillBlur1 = "21";
    public const string PrefillBlur2 = "22";
    public const string PrefillComposite = "23";
}
