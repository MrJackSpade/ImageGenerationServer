//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Logging;

/// <summary>
/// Writes a prompt-bearing event to the per-user encrypted log instead of leaking it to the plaintext app log. The
/// interface lives in Domain so the Forge worker (which references Domain only) can resolve and call it via DI; the
/// implementation encrypts the text under the user's key and persists it. Whether anything is actually written is
/// gated by <c>Logging:AuditUserPrompts</c> inside the implementation, so callers need not check the flag.
/// </summary>
public interface IUserLogService
{
    Task LogAsync(long userId, string category, string text, CancellationToken ct);
}
