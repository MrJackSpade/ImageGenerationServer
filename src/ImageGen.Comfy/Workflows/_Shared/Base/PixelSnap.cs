using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>Snaps the diffusion render resolution onto a clean pixel-art grid. Given the user's virtual resolution
/// and the requested output width/height (a fixed landscape/portrait preset), it picks a gen size whose long edge is
/// an exact integer multiple <c>k</c> of VRES — so every <c>PixelQuantize</c> cell is an exact k×k block — with both
/// dims a multiple of the model's latent <c>step</c> and inside the model's documented side range. Aspect is a soft
/// target (drifts a few percent to hit a clean snap); the min-side bound is ADVISORY (we render below it rather than
/// refuse). Pure: runs in Build() from the values the user submitted with the edit job.</summary>
internal static class PixelSnap
{
    /// <summary>The snapped (width,height), or null when snapping is off / no resolution data / no aspect available.
    /// The requested aspect comes from an explicit width/height override if given, else from the source dimensions
    /// (<paramref name="srcW"/>/<paramref name="srcH"/>) — so the toggle works off the source with no UI field.</summary>
    public static (int w, int h)? Target(ModelResolution? r, int vres, bool snapOn, int reqW, int reqH, int srcW, int srcH)
    {
        if (!snapOn) return null;   // explicitly OFF — the ONLY no-op (snap not requested)
        // 0 = "use the source dimensions" (the sentinel); a negative override is out of range, not another way to say it.
        Ensure.NotNegative(reqW); int w = reqW == 0 ? srcW : reqW;
        Ensure.NotNegative(reqH); int h = reqH == 0 ? srcH : reqH;
        if (vres <= 0)
            throw new RenderValidationException("snap_resolution is on but virtual_resolution is 0 — set a virtual resolution or turn snapping off.");
        if (w == 0 || h == 0)
            throw new RenderValidationException("snap_resolution is on but no aspect is available (no source image dimensions and no width/height override) — cannot compute the snapped render size.");
        if (r is null || Math.Max(r.MaxW, r.MaxH) <= 0)
            throw new RenderValidationException("snap_resolution is on but this model has no resolution-range data — cannot snap. Turn snapping off, or add a resolution block to the model.");
        int minSide = Math.Min(r.MinW, r.MinH);
        int maxSide = Math.Max(r.MaxW, r.MaxH);
        Ensure.GreaterThanZero(r.Step);   // a non-positive latent step is a broken model resolution, not a 16 to invent
        return Compute(vres, w, h, minSide, maxSide, r.Step);
    }

    /// <summary>The shared <c>reference</c> %% knob → KSampler denoise. 0 = full denoise (generate fresh, no source
    /// reference); 100 = no denoise (copy the source, then just pixel-quantize it). Clamped to (0,1] so the sampler
    /// always runs at least minimally. <paramref name="reference"/> null = the key was absent, so the workflow's
    /// <paramref name="dflt"/> applies.
    /// <para>reference is a percentage: an out-of-range value is REFUSED, not silently clamped through the denoise
    /// math. The [0.01, 1.0] floor is the sampler-must-run-minimally decision (see #104), not input correction.</para></summary>
    public static double Denoise(int? reference, int dflt)
    {
        int r = Ensure.Between(reference ?? dflt, PctMin, PctMax, WorkflowParamKeys.Reference);
        return Math.Clamp(1.0 - r / 100.0, 0.01, 1.0);
    }

    /// <summary>The <c>reference</c> knob is a percentage: 0 = full denoise (generate fresh), 100 = no denoise (copy source).</summary>
    private const int PctMin = 0, PctMax = 100;

    public static (int w, int h) Compute(int vres, int reqW, int reqH, int minSide, int maxSide, int step)
    {
        int lng = Math.Max(reqW, reqH), shrt = Math.Min(reqW, reqH);
        bool landscape = reqW >= reqH;
        int gw = RoundToStep(vres, step);                       // grid long edge, step-aligned

        double idealK = (double)lng / gw;
        int kMax = Math.Max(1, maxSide / gw);                   // long edge k*gw <= max (advisory floor of 1)
        int kMinByShort = (int)Math.Ceiling((double)minSide * lng / ((double)gw * shrt));
        int kMin = Math.Max(1, kMinByShort);
        int k = Math.Clamp((int)Math.Round(idealK), Math.Min(kMin, kMax), kMax);  // advisory: if kMin>kMax, take kMax

        int m = step / Gcd(k, step);                            // grid_short multiple of m -> k*grid_short stays /step
        int gridShort = Math.Max(m, RoundToMult((double)gw * shrt / lng, m));

        int genLong = k * gw, genShort = k * gridShort;
        return landscape ? (genLong, genShort) : (genShort, genLong);
    }

    private static int RoundToStep(double x, int s) => Math.Max(s, (int)Math.Round(x / s) * s);
    private static int RoundToMult(double x, int m) => Math.Max(m, (int)Math.Round(x / m) * m);
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
