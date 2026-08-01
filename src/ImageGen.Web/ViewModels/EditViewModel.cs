namespace ImageGen.Web.ViewModels;

public sealed class EditViewModel
{
    public required string ImageId { get; init; }
    public required string InitialPrompt { get; init; }
    /// <summary>Marker-form tag prompt ('#'/'@' re-attached) for the inpaint box; empty for prose/uploaded sources.</summary>
    public string InitialTagPrompt { get; init; } = "";

    /// <summary>The source image's negative prompt, verbatim, for the inpaint/outpaint negative boxes. Empty when the
    /// image was made without one (or the source isn't ours).</summary>
    public string InitialNegativePrompt { get; init; } = "";
}
