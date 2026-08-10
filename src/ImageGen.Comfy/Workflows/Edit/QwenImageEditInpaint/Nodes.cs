namespace ImageGen.Comfy.Edit.QwenImageEditInpaint;

/// <summary>QwenImageEditInpaintWorkflow's own node ids (role-named), on top of the inherited edit head
/// (EditNodes.Model/Clip/Vae/Source) and the shared <see cref="QwenReferenceHead"/> ids. Values are graph-local keys;
/// they must not collide with the head's (11/13/14/26/30/70/71/72/2/7 + the 40+2i reference loads).</summary>
internal static class Nodes
{
    public const string MaskLoad = "100";
    public const string MaskSize = "101";
    public const string MaskAsImage = "102";
    public const string MaskScaled = "103";
    public const string MaskBack = "104";
    public const string Grow = "105";
    public const string SoftenAsImage = "106";
    public const string SoftenBlur = "107";
    public const string SoftenBack = "108";
    public const string SoftenComposite = "109";
    public const string InpaintConditioning = "120";
    public const string Sampler = "121";
    public const string Decode = "122";
    public const string Composite = "123";
    public const string Save = "9";
}
