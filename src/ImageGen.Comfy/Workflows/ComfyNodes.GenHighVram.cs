using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Raises a model's classifier-free-guidance scale over a late slice of the schedule (Ideogram 4's asymmetric
/// CFG). One typed record per ComfyUI class type; inputs are declared in the exact order the old anonymous-object
/// inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record CFGOverride : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CFGOverride;
    [JsonPropertyName("model")]         public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("cfg")]           public required double Cfg { get; init; }
    [JsonPropertyName("start_percent")] public required double StartPercent { get; init; }
    [JsonPropertyName("end_percent")]   public required double EndPercent { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Fuses a conditional and a separate unconditional model into a single guider at a base CFG (Ideogram 4's
/// dual-model classifier-free guidance).</summary>
public sealed record DualModelGuider : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.DualModelGuider;
    [JsonPropertyName("model")]          public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("positive")]       public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("model_negative")] public required Output<Slot.Model> ModelNegative { get; init; }
    [JsonPropertyName("negative")]       public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("cfg")]            public required double Cfg { get; init; }
    public static Output<Slot.Guider> Out(string id) => new(id, 0);
}

/// <summary>Ideogram 4's own logit-normal sigma schedule (driven through <see cref="SamplerCustomAdvanced"/>).</summary>
public sealed record Ideogram4Scheduler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Ideogram4Scheduler;
    [JsonPropertyName("steps")]  public required int Steps { get; init; }
    [JsonPropertyName("width")]  public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("mu")]     public required double Mu { get; init; }
    [JsonPropertyName("std")]    public required double Std { get; init; }
    public static Output<Slot.Sigmas> Out(string id) => new(id, 0);
}
