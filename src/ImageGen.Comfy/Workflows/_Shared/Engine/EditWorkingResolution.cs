using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// Resolves an edit canvas to an aspect-preserving megapixel budget and emits the exact paired image/mask scaling
/// needed to reach it. Editing models operate on a spatially compressed latent grid, so passing a small upload
/// through unchanged needlessly discards structure before sampling. Normalizing both small and large inputs to the
/// model's working budget gives the VAE a stable spatial grid; masks must take the identical trip or conditioning,
/// latent masking, and paste-back no longer describe the same pixels.
/// </summary>
internal static class EditWorkingResolution
{
    /// <summary>The current native edit budget. EDIT-014 will resolve this from per-workflow quality presets.</summary>
    public const double NativeMegapixels = 1.0;

    /// <summary>FLUX/Qwen/Mage-family edit dimensions are aligned to a 16-pixel model grid.</summary>
    public const int NativeStep = 16;

    /// <summary>
    /// Scale <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/> to <paramref name="megapixels"/>, snap
    /// both sides to <paramref name="step"/>, then apply an optional long-edge safety ceiling. The ceiling is applied
    /// after the MP snap so it remains a ceiling, never an instruction to upscale a smaller canvas.
    /// </summary>
    public static (int Width, int Height) Resolve(
        int sourceWidth,
        int sourceHeight,
        double megapixels = NativeMegapixels,
        int step = NativeStep,
        int maxDimension = 0)
    {
        _ = Ensure.GreaterThanZero(sourceWidth);
        _ = Ensure.GreaterThanZero(sourceHeight);
        _ = Ensure.GreaterThanZero(megapixels);
        _ = Ensure.GreaterThanZero(step);
        if (maxDimension < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension), maxDimension, "Maximum dimension cannot be negative.");
        }

        (int width, int height) = BudgetScale.Snap(sourceWidth, sourceHeight, megapixels, step);
        width = Math.Max(step, width);
        height = Math.Max(step, height);

        int longEdge = Math.Max(width, height);
        if (maxDimension > 0 && longEdge > maxDimension)
        {
            double scaleBy = (double)maxDimension / longEdge;
            width = Math.Max(step, (int)(width * scaleBy) / step * step);
            height = Math.Max(step, (int)(height * scaleBy) / step * step);
        }

        return (width, height);
    }

    /// <summary>Scale an image to <paramref name="target"/> when its current canvas differs.</summary>
    public static Output<Slot.Image> ScaleImage(
        ComfyWorkflowGraph graph,
        string nodeId,
        Output<Slot.Image> image,
        (int Width, int Height) current,
        (int Width, int Height) target)
    {
        if (current == target)
        {
            return image;
        }

        graph[nodeId] = new ImageScale
        {
            Image = image,
            UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
            Width = target.Width,
            Height = target.Height,
            Crop = ComfyWidgets.Crop.Disabled,
        };
        return ImageScale.Out(nodeId);
    }

    /// <summary>
    /// Scale a mask to <paramref name="target"/> through IMAGE space. Nearest-exact preserves binary membership;
    /// workflow-specific grow/blur operations run afterwards when a soft transition is required.
    /// </summary>
    public static Output<Slot.Mask> ScaleMask(
        ComfyWorkflowGraph graph,
        string maskToImageId,
        string scaleId,
        string imageToMaskId,
        Output<Slot.Mask> mask,
        (int Width, int Height) current,
        (int Width, int Height) target)
    {
        if (current == target)
        {
            return mask;
        }

        graph[maskToImageId] = new MaskToImage { Mask = mask };
        graph[scaleId] = new ImageScale
        {
            Image = MaskToImage.Out(maskToImageId),
            UpscaleMethod = ComfyWidgets.Upscale.NearestExact,
            Width = target.Width,
            Height = target.Height,
            Crop = ComfyWidgets.Crop.Disabled,
        };
        graph[imageToMaskId] = new ImageToMask
        {
            Image = ImageScale.Out(scaleId),
            Channel = ComfyWidgets.MaskChannel.Red,
        };
        return ImageToMask.Out(imageToMaskId);
    }

    /// <summary>Scale a paired canvas and mask to one resolved target.</summary>
    public static void ScalePair(
        ComfyWorkflowGraph graph,
        string imageScaleId,
        string maskToImageId,
        string maskScaleId,
        string imageToMaskId,
        (int Width, int Height) current,
        (int Width, int Height) target,
        ref Output<Slot.Image> image,
        ref Output<Slot.Mask> mask)
    {
        image = ScaleImage(graph, imageScaleId, image, current, target);
        mask = ScaleMask(graph, maskToImageId, maskScaleId, imageToMaskId, mask, current, target);
    }
}
