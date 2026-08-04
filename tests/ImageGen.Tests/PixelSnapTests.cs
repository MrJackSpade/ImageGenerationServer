//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Rendering;
using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>Locks the render-resolution snap (PixelSnap.Compute) to the values verified in the standalone
/// Python model (clean k×k cells, both dims /step, nearest the requested resolution within the model range).</summary>
public sealed class PixelSnapTests
{
    [Theory]
    // vres,  reqW, reqH,  min,  max, step,  expW, expH   (Flux: 256/1440/16)
    [InlineData(384, 1024, 1024, 256, 1440, 16, 1152, 1152)]
    [InlineData(384, 1344,  768, 256, 1440, 16, 1152,  672)]  // 16:9, k capped to 3 by max 1440
    [InlineData(384,  832, 1216, 256, 1440, 16,  768, 1152)]  // portrait 2:3
    [InlineData(256, 1344,  768, 256, 1440, 16, 1280,  720)]  // vres256 -> k5, lands 16:9 exactly
    [InlineData(512, 1344,  768, 256, 1440, 16, 1024,  592)]  // vres512 -> k2 (1536 would exceed max)
    // Qwen-Edit: 928/1664/16
    [InlineData(256, 1024, 1024, 928, 1664, 16, 1024, 1024)]
    [InlineData(384, 1216,  832, 928, 1664, 16, 1536, 1056)]
    // FLUX.2-klein: 64/2048/16 (higher max lets k=3 at vres512/16:9)
    [InlineData(512, 1344,  768,  64, 2048, 16, 1536,  864)]
    // SD1.5 tight: 512/768/16  (k=1 -> gen==grid, no supersample; advisory below-min still proceeds)
    [InlineData(512, 1024, 1024, 512,  768, 16,  512,  512)]
    [InlineData(256, 1344,  768, 512,  768, 16,  768,  432)]
    public void Snaps_to_verified_resolution(int vres, int w, int h, int min, int max, int step, int expW, int expH)
    {
        var (gw, gh) = PixelSnap.Compute(vres, w, h, min, max, step);
        Assert.Equal((expW, expH), (gw, gh));
        Assert.True(gw % step == 0 && gh % step == 0, $"{gw}x{gh} not /{step}");
    }

    [Fact]
    public void Long_edge_is_an_integer_multiple_of_vres_giving_square_cells()
    {
        // gen long edge must be k * round_to_step(vres); each axis divides into whole k×k cells.
        var (w, h) = PixelSnap.Compute(vres: 384, reqW: 1344, reqH: 768, minSide: 256, maxSide: 1440, step: 16);
        int gw = 384; // vres already /16
        int lng = System.Math.Max(w, h);
        Assert.Equal(0, lng % gw);                 // long edge is an exact multiple of the grid long edge
        int k = lng / gw;
        Assert.Equal(0, System.Math.Min(w, h) % k); // short edge divides into k-tall cells too
    }

    /// <summary>Flux-dev envelope (256–1440, /16) for the fail-fast tests below: snapping must never silently fall
    /// back to the model default — if it's on and can't compute, it throws.</summary>
    private static readonly ModelResolution Flux = new() { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 };
    private static ParamValues PV(params (string, object?)[] kv)
    {
        var d = new System.Collections.Generic.Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return new ParamValues(d);
    }

    [Fact]
    public void Snap_off_is_the_only_noop()
    {
        Assert.Null(PixelSnap.Target(PV(("snap_resolution", false)), Flux, 384, 1216, 832));
    }

    [Fact]
    public void Snap_on_without_aspect_throws_rather_than_silently_using_the_default()
    {
        // snap defaults on; no width/height and no source dims -> must FAIL, not return null + render at default.
        Assert.Throws<RenderValidationException>(() => PixelSnap.Target(PV(), Flux, 384, 0, 0));
    }

    [Fact]
    public void Snap_on_with_no_resolution_data_throws()
    {
        Assert.Throws<RenderValidationException>(() => PixelSnap.Target(PV(), (ModelResolution?)null, 384, 1216, 832));
    }

    [Fact]
    public void Snap_on_with_source_dims_snaps_from_the_source_aspect()
    {
        var r = PixelSnap.Target(PV(), Flux, 384, 1216, 832);   // no override -> uses the source dims
        Assert.Equal(((int, int)?)(1152, 768), r);
    }

    [Theory]
    [InlineData(0, 0, 1.00)]    // 0% reference  -> full denoise (generate fresh)
    [InlineData(70, 0, 0.30)]   // 70%           -> denoise 0.3 (the old Flux 'strength' default)
    [InlineData(50, 0, 0.50)]
    [InlineData(100, 0, 0.01)]  // 100% (copy)   -> clamped to 0.01 so the sampler still runs
    public void Reference_pct_maps_to_denoise(int reference, int dflt, double expected)
    {
        Assert.Equal(expected, PixelSnap.Denoise(PV(("reference", reference)), dflt), 3);
    }

    [Fact]
    public void Reference_falls_back_to_the_per_model_default_when_unset()
    {
        Assert.Equal(0.30, PixelSnap.Denoise(PV(), 70), 3);   // Flux default 70 -> denoise 0.3
        Assert.Equal(1.00, PixelSnap.Denoise(PV(), 0), 3);    // QIE/Kontext default 0 -> denoise 1.0
    }
}
