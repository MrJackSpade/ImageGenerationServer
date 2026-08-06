namespace ImageGen.Comfy.Edit.QwenImageEdit;

/// <summary>Standard split Qwen-Image-Edit.</summary>
public sealed class QwenImageEditWorkflow : QwenEditBase
{
    public override string Name => "qwen-image-edit";
    protected override bool Aio => false;
}
