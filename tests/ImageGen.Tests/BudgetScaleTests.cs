using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>
/// Locks <see cref="BudgetScale.Snap"/> to ComfyUI's <c>ImageScaleToTotalPixels</c> math
/// (<c>comfy_extras/nodes_post_processing.py</c>): scale to the megapixel budget, then round each side to a multiple
/// of the step with Python's round-half-to-even. This is the value the H3 (and every budget-scaling editor's) ETA
/// signature is built from (#180) — so the recorded/predicted resolution reflects the ~budget render, not the raw
/// upload size. The core invariant: wildly different source sizes at the same budget collapse to ~the same pixel count.
/// </summary>
public sealed class BudgetScaleTests
{
    [Theory]
    // srcW, srcH, mp,  steps,  expW, expH   (H3 i2v uses 1.0 MP / 32)
    [InlineData(514, 300, 1.0, 32, 1344, 768)]     // a small upload → snapped UP to ~1 MP
    [InlineData(1280, 720, 1.0, 32, 1376, 768)]    // a large upload → snapped DOWN to ~1 MP
    [InlineData(1024, 1024, 1.0, 32, 1024, 1024)]  // already ~1 MP square, on-grid → unchanged
    [InlineData(1440, 1799, 1.0, 32, 928, 1152)]   // the 2.59 MP outlier → back to ~1 MP
    public void Snaps_to_the_budget_grid(int srcW, int srcH, double mp, int steps, int expW, int expH)
    {
        (int w, int h) = BudgetScale.Snap(srcW, srcH, mp, steps);
        Assert.Equal((expW, expH), (w, h));
    }

    [Theory]
    [InlineData(514, 300)]
    [InlineData(1280, 720)]
    [InlineData(1440, 1799)]
    [InlineData(3840, 2160)]
    public void Same_budget_collapses_disparate_uploads_to_the_same_pixel_count(int srcW, int srcH)
    {
        // The whole point of #180: a 4 MP and a 0.15 MP upload that render identically at H3's ~1 MP budget must land
        // on ~the same recorded pixel count, so the ETA no longer swings with upload resolution. Every one lands within
        // a step's slack of 1 MP.
        (int w, int h) = BudgetScale.Snap(srcW, srcH, 1.0, 32);
        long px = (long)w * h;
        Assert.InRange(px, 900_000, 1_150_000);
    }

    [Fact]
    public void Rejects_a_degenerate_source() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetScale.Snap(0, 512, 1.0, 32));
}
