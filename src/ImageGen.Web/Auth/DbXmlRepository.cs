using ImageGen.Domain.Repositories;
using Microsoft.AspNetCore.DataProtection.Repositories;
using System.Xml.Linq;

namespace ImageGen.Web.Auth;

/// <summary>
/// Persists the Data Protection key ring in the database (dbo.DataProtectionKey) instead of the OS user profile.
/// With the keys beside the accounts and sessions they protect, everything auth moves together: back up the
/// database and the cookies still unprotect on a restored box; wipe it and the keys die with the sessions.
///
/// <para>The interface is synchronous by contract, so the calls block on the async repository — the key manager
/// reads the ring lazily on first protect/unprotect and caches it, so this is a startup-shaped cost, not a
/// per-request one.</para>
/// </summary>
public sealed class DbXmlRepository(IDataProtectionKeyRepository keys) : IXmlRepository
{
    private readonly IDataProtectionKeyRepository _keys = keys;

    public IReadOnlyCollection<XElement> GetAllElements() =>
        [.. _keys.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult().Select(XElement.Parse)];

    public void StoreElement(XElement element, string friendlyName) =>
        _keys.AddAsync(friendlyName, element.ToString(SaveOptions.DisableFormatting), CancellationToken.None)
            .GetAwaiter().GetResult();
}
