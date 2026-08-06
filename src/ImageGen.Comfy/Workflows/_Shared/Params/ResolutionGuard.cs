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

    /// <summary>Refuse a resolved render size the model does not document, at submit — before the graph is built. A
    /// null envelope (the configuration declares none) is left alone.</summary>
    public static void EnsureWithin(ModelResolution? env, int w, int h)
    {
        if (env is not null && Violation(env, w, h, "the render size") is { } msg)
        {
            throw new RenderValidationException(msg + ".");
        }
    }
}
