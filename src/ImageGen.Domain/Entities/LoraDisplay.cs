namespace ImageGen.Domain.Entities;

/// <summary>
/// A user's manually chosen cover image for a LoRA — what represents that LoRA in the picker grid. Per-user
/// (a <see cref="GatewayImageId"/> from the user's own history), so one user never sees another's pick. When
/// absent, the LoRA shows a placeholder. Unique per (UserId, LoraName); LoraName is the subfolder-qualified
/// <c>lora_name</c> exactly as ComfyUI reports it (e.g. <c>anime/foo.safetensors</c>).
/// </summary>
public sealed class LoraDisplay
{
    public long Id { get; init; }
    public required long UserId { get; init; }
    public required string LoraName { get; init; }
    public required string GatewayImageId { get; init; }
    public required DateTime SetAtUtc { get; init; }
}
