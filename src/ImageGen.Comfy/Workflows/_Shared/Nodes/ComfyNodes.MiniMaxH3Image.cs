using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>MiniMax-H3-Image's conditioning+latent node in TEXT→image mode (ComfyUI core, v0.45.0+): the prompt is
/// encoded by the node itself through the H3 Qwen3-VL encoder and the canvas is LITERAL width/height from the aspect
/// map. No pictures are attached. The VAE is still wired, exactly as the official template does — without pictures it
/// is unused, but it keeps both modes on one input contract. Output 0 = positive conditioning, 1 = the 24-channel image
/// latent sized width/16 × height/16.</summary>
public sealed record MiniMaxH3ImageConditioning : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MiniMaxH3ImageConditioning;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}

/// <summary>MiniMax-H3-Image's conditioning+latent node in EDIT mode: the same ComfyUI class as
/// <see cref="MiniMaxH3ImageConditioning"/>, but with pictures attached and the canvas WIRED from a
/// <see cref="GetImageSize"/> on the budget-scaled source, so the output latent matches the edit target exactly. A
/// distinct record (as with the H3 video nodes) because the width/height are edges here and literals there.
///
/// <para>The pictures are the node's <c>COMFY_AUTOGROW_V3</c> <c>images</c> input. On the wire each is a FLAT dotted key
/// <c>images.image_{n}</c> (1-based: <c>image_1</c> is the picture to edit, <c>&lt;Picture 1&gt;</c> in the prompt;
/// the rest are references <c>&lt;Picture 2&gt;</c>…), which ComfyUI re-nests server-side. They are emitted through
/// <see cref="JsonExtensionDataAttribute"/> and built only by <see cref="Pictures"/>, which owns the key format.</para>
/// Output 0 = positive conditioning (text + vision tokens + the pictures' reference latents), 1 = image latent.</summary>
public sealed record MiniMaxH3ImageEditConditioning : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MiniMaxH3ImageConditioning;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }

    /// <summary>The flattened <c>images.image_{n}</c> autogrow map; populate it with <see cref="Pictures"/>. (System.Text.Json
    /// forbids <c>required</c> on extension data, so this one input is initialized rather than required.)</summary>
    [JsonExtensionData] public Dictionary<string, object> PictureInputs { get; init; } = [];

    /// <summary>Build the autogrow map from the ordered pictures: index 0 becomes <c>images.image_1</c>
    /// (<c>&lt;Picture 1&gt;</c>, the edit target), index 1 <c>images.image_2</c>, and so on. Each value is the
    /// <c>[nodeId, index]</c> edge ComfyUI expects.</summary>
    public static Dictionary<string, object> Pictures(IReadOnlyList<Output<Slot.Image>> pictures)
    {
        Dictionary<string, object> map = new(pictures.Count);
        for (int i = 0; i < pictures.Count; i++)
        {
            map[$"images.image_{i + 1}"] = new object[] { pictures[i].NodeId, pictures[i].Index };
        }

        return map;
    }

    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}
