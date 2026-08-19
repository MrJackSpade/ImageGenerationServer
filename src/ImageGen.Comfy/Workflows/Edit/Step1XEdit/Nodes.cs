namespace ImageGen.Comfy.Edit.Step1XEdit;

/// <summary>Step1X-Edit's own node ids (source LoadImage reuses the inherited <c>EditNodes.Source</c>), plus the text
/// encoder literal. The DiT and AE are slot ids on the configuration, resolved to this machine's bound files — a const
/// filename here would bake one person's disk into the application, unreachable from the models page. The text
/// encoder stays a literal: it is not a file but the name of a Hugging Face folder the node loads from its own
/// directory, so there is nothing to bind.</summary>
internal static class Nodes
{
    public const string TextEncoder = "Qwen2.5-VL-7B-Instruct";
    public const string ModelLoader = "1";
    public const string SourceScale = "11";
    public const string Generate = "2";
    public const string Save = "9";
}
