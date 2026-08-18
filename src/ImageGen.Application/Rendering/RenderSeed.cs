namespace ImageGen.Application.Rendering;

/// <summary>Generation-seed limits and random resolution shared by the enqueue boundary and renderer adapters.</summary>
public static class RenderSeed
{
    /// <summary>Largest integer JSON/browser clients can round-trip without losing precision.</summary>
    public const long MaxExactValue = 9_007_199_254_740_991;

    /// <summary>A fresh seed in the exactly representable browser/JSON range, including 0.</summary>
    public static long Random() => System.Random.Shared.NextInt64(0, MaxExactValue + 1);
}
