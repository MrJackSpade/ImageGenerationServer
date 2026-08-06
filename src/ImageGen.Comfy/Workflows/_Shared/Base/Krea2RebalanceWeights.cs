namespace ImageGen.Comfy;

/// <summary>The published Krea 2 per-layer weight presets (12 comma-separated gains, one per tapped Qwen3-VL layer).</summary>
public static class Krea2RebalanceWeights
{
    /// <summary>Neutral (no-op) per-layer vector: 12 ones. Equivalent to "off".</summary>
    public const string NeutralWeights = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0";

    /// <summary>The published uncensor preset (the node's own DEFAULT_WEIGHTS): boosts Krea 2's deep semantic
    /// Qwen3-VL layers (×2.5 / ×5 / ×4) while leaving the early layers at ×1. Surfaced in the param help text.</summary>
    public const string UncensorWeights = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0";
}
