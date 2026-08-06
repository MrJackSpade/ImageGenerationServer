using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>
/// The one place a resolved render size is checked against a model's documented <see cref="ModelResolution"/>
/// envelope. Used on BOTH the settings-write path (where an operator types an aspect map) and the SUBMIT path (the
/// final resolved width/height, from the aspect map or flat width/height, just before the graph is built) — so a size
/// the model cannot render is refused with the model's own numbers instead of failing minutes later at the GPU.
/// <para>No bound is invented here: a configuration that declares no envelope is not second-guessed
/// (<see cref="EnsureWithin"/> no-ops on a null envelope).</para>
/// </summary>
[AllowMagicStrings("human-readable render-size refusal messages")]
internal static class ResolutionGuard
{

    /// <summary>Null when (<paramref name="w"/>,<paramref name="h"/>) is within <paramref name="env"/>; otherwise a
    /// reason naming the violated side range or step multiple, with the model's own numbers. <paramref name="subject"/>
    /// names what the size belongs to (an aspect entry on the write path, "the render size" at submit).</summary>
    public static string? Violation(ModelResolution env, int w, int h, string subject)
    {
        if (w < env.MinW || w > env.MaxW || h < env.MinH || h > env.MaxH)
        {
            return $"{subject} is {w}x{h}, outside what this model supports ({env.MinW}-{env.MaxW} wide, {env.MinH}-{env.MaxH} tall)";
        }

        if (env.Step > 0 && (w % env.Step != 0 || h % env.Step != 0))
        {
            return $"{subject} is {w}x{h}; this model needs both sides to be a multiple of {env.Step}";
        }

        return null;
    }

    /// <summary>The refusal message for a resolved render size against <paramref name="env"/>, or null when it fits —
    /// the standard "the render size" subject over <see cref="Violation"/>. A null envelope (the configuration declares
    /// none) yields null: no bound to enforce. Shared by the render path (<see cref="EnsureWithin"/>) and the submit
    /// path (the catalog's request-size check), so both refuse an out-of-envelope size with identical wording.</summary>
    public static string? RenderSizeViolation(ModelResolution? env, int w, int h)
        => env is null ? null : Violation(env, w, h, "the render size");

    /// <summary>Refuse a resolved render size the model does not document, at submit — before the graph is built. A
    /// null envelope (the configuration declares none) is left alone.</summary>
    public static void EnsureWithin(ModelResolution? env, int w, int h)
    {
        if (RenderSizeViolation(env, w, h) is { } msg)
        {
            throw new RenderValidationException(msg + ".");
        }
    }
}
