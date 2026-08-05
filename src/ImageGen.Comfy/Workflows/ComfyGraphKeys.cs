namespace ImageGen.Comfy;

/// <summary>The ComfyUI graph's structural wire keys: the two members every node object carries
/// (<c>class_type</c>, <c>inputs</c>) and the input-slot names the base-agnostic graph surgery reads and rewrites by
/// key. Written once here — mirroring <see cref="ComfyNodeTypes"/> for class_types — so a node's shape is never
/// spelled from a loose literal that a rename could miss. These are wire identifiers fixed by ComfyUI, not display
/// text.</summary>
internal static class ComfyGraphKeys
{
    /// <summary>A node object's type member: <c>{ "class_type": … }</c>.</summary>
    public const string ClassType = "class_type";

    /// <summary>A node object's inputs member: <c>{ "inputs": … }</c>.</summary>
    public const string Inputs = "inputs";

    /// <summary>The <c>images</c> input slot (e.g. a save node's frames).</summary>
    public const string Images = "images";

    /// <summary>The <c>model</c> input slot (a terminal model-consumer's denoise model).</summary>
    public const string Model = "model";

    /// <summary>The <c>vae</c> input slot (a decode node's VAE).</summary>
    public const string Vae = "vae";
}
