using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// The text-to-image / text-to-video render SIZE, resolved from the coupled aspect-ratio + Megapixels inputs (#186).
///
/// <para>When a configuration exposes no <c>megapixels</c> control the base size is returned unchanged — exactly
/// today's <c>p.Dims(...)</c> behavior. When megapixels IS set the base size supplies only the aspect RATIO: it is
/// scaled to the megapixel budget via <see cref="BudgetScale.Snap"/> on the envelope's step and clamped into the
/// model's <see cref="ModelResolution"/> envelope.</para>
///
/// <para>The base size is the ALREADY-aspect-resolved size (<c>Dims(aspect)</c>): the aspect map's entry for the
/// requested shape, or — when the composer's Custom size dropped the aspect map for this request (<c>MergeParamsDict</c>)
/// — the submitted flat width/height. So the ratio always reflects what the caller asked for, whether that came from
/// an aspect button (UI or MCP) or an explicit width/height, without this needing to re-derive which.</para>
///
/// <para>This is deliberate, requested normalization — the defined contract for the size inputs (issue #186) — not
/// error-swallowing: a bad UI state or an incoherent / out-of-range budget snaps to coherent dims the model can
/// render. Genuinely degenerate input (a side ≤ 0) still throws, via the <c>Ensure</c> guards inside
/// <see cref="BudgetScale.Snap"/>.</para>
/// </summary>
internal static class RenderSizing
{
    /// <summary>The resolved render size: <paramref name="baseDims"/> verbatim when <paramref name="megapixels"/> is
    /// null (no megapixels control), otherwise <paramref name="baseDims"/>' aspect ratio scaled to the budget and
    /// clamped into <paramref name="env"/>. A set megapixels with no envelope to snap to is a broken configuration and
    /// is refused — the size cannot be computed without the model's resolution step.</summary>
    public static (int w, int h) Resolve((int w, int h) baseDims, double? megapixels, ModelResolution? env)
    {
        if (megapixels is not double mp)
        {
            return baseDims;
        }

        ModelResolution envelope = env
            ?? throw new RenderValidationException(
                "This configuration exposes a megapixels control but declares no resolution envelope to snap the render size to.");

        // Snap's Ensure.GreaterThanZero(step) throws on a ≤0 step, so a broken envelope fails fast HERE rather than
        // being papered over with a fallback step.
        (int snapW, int snapH) = BudgetScale.Snap(baseDims.w, baseDims.h, mp, envelope.Step);
        return ResolutionGuard.Clamp(envelope, snapW, snapH);
    }
}
