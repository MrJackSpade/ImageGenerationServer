namespace ImageGen.Domain.Repositories;

/// <summary>
/// The ASP.NET Data Protection key ring, one XML document per key. Persisted in the database so the keys that
/// unprotect the auth cookie live and die with the accounts and sessions they protect, instead of in the OS
/// user profile where they outlive a database wipe and go missing on a move to another box.
/// </summary>
public interface IDataProtectionKeyRepository
{
    /// <summary>Every stored key element's XML, in insertion order.</summary>
    Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct);

    Task AddAsync(string friendlyName, string xml, CancellationToken ct);
}
