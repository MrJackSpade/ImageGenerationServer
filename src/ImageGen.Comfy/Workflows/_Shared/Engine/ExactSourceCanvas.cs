using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>
/// The opt-in <c>exact_source_resolution</c> edit path: render on the source image's own pixels so the output has the
/// upload's width and height and is pixel-aligned with it. Nothing on this path resamples. A model that needs its
/// canvas on a pixel grid gets the source PADDED on the right and bottom up to the next grid multiple, and the decoded
/// result is CROPPED back to the source size from the top-left corner, so source pixel (x, y) and output pixel (x, y)
/// are the same position. Without the pad, ComfyUI's <c>VAEEncode</c> centre-crops an off-grid image and the output
/// comes back smaller and shifted.
/// <para>The fill is <c>ImagePadForOutpaint</c>'s neutral grey with no feathering. The padded band is at most one grid
/// step minus one pixel wide, exists only so the model sees an on-grid canvas, and never reaches the output.</para>
/// </summary>
[AllowMagicStrings("human-readable exact-source-resolution refusal messages")]
internal static class ExactSourceCanvas
{
    /// <summary>The working canvas for a source of the given size: each side rounded UP to a multiple of
    /// <paramref name="step"/>. Equal to the source when it is already on the grid.</summary>
    public static (int Width, int Height) Padded(int sourceWidth, int sourceHeight, int step)
    {
        _ = Ensure.GreaterThanZero(sourceWidth);
        _ = Ensure.GreaterThanZero(sourceHeight);
        _ = Ensure.GreaterThanZero(step);
        return (RoundUp(sourceWidth, step), RoundUp(sourceHeight, step));
    }

    /// <summary>
    /// The padded working canvas an exact-source request renders on, after refusing every request shape that cannot
    /// honour the contract: a workflow with no exact-source path, a request without a primary source image, and a
    /// fixed reference aspect (which sizes the output independently of the source).
    /// </summary>
    public static (int Width, int Height) Resolve(
        IWorkflow workflow, string configId, int sourceWidth, int sourceHeight, bool hasSource, bool hasFixedAspect)
    {
        if (workflow.ExactSourceStep is not int step)
        {
            throw new RenderValidationException(
                $"Configuration '{configId}' cannot render at the exact source resolution: its workflow '{workflow.Name}' "
                + "has no exact-source path. Remove 'exact_source_resolution' or pick a workflow that supports it.");
        }

        if (!hasSource)
        {
            throw new RenderValidationException("'exact_source_resolution' needs a primary source image.");
        }

        if (hasFixedAspect)
        {
            throw new RenderValidationException(
                "'exact_source_resolution' cannot be combined with a fixed reference aspect; the output takes the source's own size.");
        }

        return Padded(sourceWidth, sourceHeight, step);
    }

    /// <summary>Pad <paramref name="image"/> on the right and bottom from <paramref name="source"/> up to
    /// <paramref name="padded"/>. Emits no node when the source is already on the grid.</summary>
    public static Output<Slot.Image> Pad(
        ComfyWorkflowGraph graph,
        string nodeId,
        Output<Slot.Image> image,
        (int Width, int Height) source,
        (int Width, int Height) padded)
    {
        if (source == padded)
        {
            return image;
        }

        graph[nodeId] = new ImagePadForOutpaint
        {
            Image = image,
            Left = 0,
            Top = 0,
            Right = padded.Width - source.Width,
            Bottom = padded.Height - source.Height,
            Feathering = 0,
        };
        return ImagePadForOutpaint.ImageOut(nodeId);
    }

    /// <summary>Crop <paramref name="image"/> from <paramref name="padded"/> back to <paramref name="source"/>,
    /// anchored at the top-left corner. Emits no node when nothing was padded.</summary>
    public static Output<Slot.Image> Crop(
        ComfyWorkflowGraph graph,
        string nodeId,
        Output<Slot.Image> image,
        (int Width, int Height) source,
        (int Width, int Height) padded)
    {
        if (source == padded)
        {
            return image;
        }

        graph[nodeId] = new ImageCrop
        {
            Image = image,
            Width = source.Width,
            Height = source.Height,
            X = 0,
            Y = 0,
        };
        return ImageCrop.Out(nodeId);
    }

    private static int RoundUp(int value, int step) => (value + step - 1) / step * step;
}
