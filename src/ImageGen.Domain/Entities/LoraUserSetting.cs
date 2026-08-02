namespace ImageGen.Domain.Entities;

/// <summary>
/// A user's per-LoRA preferences: an optional trigger-word override (<see cref="TriggerWords"/> null = use the CivitAI
/// default) and whether those words should auto-attach to the prompt when the LoRA is added. Per-user; the cover image
/// lives separately in <c>LoraDisplay</c>.
/// </summary>
public sealed class LoraUserSetting
{
    public required long UserId { get; init; }
    public required string LoraName { get; init; }
    public string? TriggerWords { get; init; }
    public bool AutoAttach { get; init; } = true;
}
