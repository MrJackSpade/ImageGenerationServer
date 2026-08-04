//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// One LoRA used to generate an image: the subfolder-qualified <c>lora_name</c> and the strength it was applied at
/// (to both model and CLIP). Recorded per image so the viewer can list them and Reload can reproduce the exact stack.
/// </summary>
public sealed record HistoryLora(string Name, double Weight);
