//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Security;
using ImageGen.Domain.Logging;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace ImageGen.Application.Logging;

/// <summary>
/// <see cref="IUserLogService"/> that randomized-encrypts the text under the owning user's key and appends it to
/// <c>dbo.UserLog</c>. A no-op when auditing is disabled (<c>Logging:AuditUserPrompts</c> = false), so callers can
/// log unconditionally. Best-effort by design: it never throws into a render/request path — a logging failure must
/// not fail the work it was observing.
/// <para>"Best-effort" means it does not RETHROW; it never meant the failure goes unrecorded. This swallowed every
/// exception into an empty block, so an audit trail that had silently stopped being written — a broken cipher key, a
/// table that would not accept a row — was invisible precisely where a gap in the record matters most. The failure
/// now goes to the application log, which is the one place still working when the audit table is not.</para>
/// </summary>
public sealed class UserLogService(IUserCipher cipher, IUserLogRepository repository, bool enabled,
    ILogger<UserLogService> log) : IUserLogService
{
    private readonly IUserCipher _cipher = cipher;
    private readonly IUserLogRepository _repository = repository;
    private readonly bool _enabled = enabled;
    private readonly ILogger<UserLogService> _log = log;

    public async Task LogAsync(long userId, string category, string text, CancellationToken ct)
    {
        if (!_enabled)
            return;
        try
        {
            var payload = await _cipher.EncryptAsync(userId, text, ct);
            await _repository.AddAsync(userId, category, payload, DateTime.UtcNow, ct);
        }
        catch (Exception ex)
        {
            // No `text` in the message: it is the user's prompt, which is why it was being encrypted in the first place.
            _log.LogError(ex, "Audit log write failed for user {UserId} ({Category}); the entry was NOT recorded.",
                userId, category);
        }
    }
}
