using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// The render-resolution snap ComfyUI's <c>ImageScaleToTotalPixels</c> node performs: scale a source image to a
/// fixed megapixel BUDGET (aspect preserved), then round each side to a multiple of <c>resolutionSteps</c>. A
/// budget-scaling edit workflow renders at THIS size no matter how large the raw upload was, so the ETA timing
/// signature must be built from it rather than the raw source dims — otherwise a 4&#160;MP upload is credited ~4×
/// the render work of a 1&#160;MP upload even though the graph renders both identically, and the per-model average
/// swings with whatever resolution the user happened to upload (see <see cref="IWorkflow.EtaRenderSize"/>).
/// Mirrors <c>comfy_extras/nodes_post_processing.py</c> exactly (round-half-to-even, as Python's <c>round()</c>).
/// </summary>
internal static class BudgetScale
{
    /// <summary>The post-budget render size for a source of <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>
    /// scaled to <paramref name="megapixels"/> and snapped to a <paramref name="resolutionSteps"/> grid.</summary>
    public static (int Width, int Height) Snap(int sourceWidth, int sourceHeight, double megapixels, int resolutionSteps)
    {
        _ = Ensure.GreaterThanZero(sourceWidth);
        _ = Ensure.GreaterThanZero(sourceHeight);
        _ = Ensure.GreaterThanZero(megapixels);
        _ = Ensure.GreaterThanZero(resolutionSteps);

        double total = megapixels * 1024 * 1024;
        double scaleBy = Math.Sqrt(total / ((double)sourceWidth * sourceHeight));
        int width = (int)(Math.Round(sourceWidth * scaleBy / resolutionSteps, MidpointRounding.ToEven) * resolutionSteps);
        int height = (int)(Math.Round(sourceHeight * scaleBy / resolutionSteps, MidpointRounding.ToEven) * resolutionSteps);
        return (width, height);
    }
}
