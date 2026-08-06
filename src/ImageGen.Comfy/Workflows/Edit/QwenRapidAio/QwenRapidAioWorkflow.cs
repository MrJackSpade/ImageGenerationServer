namespace ImageGen.Comfy.Edit.QwenRapidAio;

/// <summary>Qwen rapid all-in-one checkpoint (bakes its own sampling).</summary>
public sealed class QwenRapidAioWorkflow : QwenEditBase
{
    public override string Name => "qwen-rapid-aio";
    protected override bool Aio => true;
}
