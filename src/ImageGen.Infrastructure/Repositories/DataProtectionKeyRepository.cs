using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>Stateless (a fresh connection per call) — registered singleton so the key manager's singleton
/// XML-repository adapter can hold it.</summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class DataProtectionKeyRepository(IDbConnectionFactory connectionFactory) : IDataProtectionKeyRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        // Id order is insertion order — the key manager expects to see keys in the order they were created.
        await using DbCommand cmd = conn.Command("SELECT Xml FROM dbo.DataProtectionKey ORDER BY Id;");
        List<string> keys = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public async Task AddAsync(string friendlyName, string xml, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "INSERT INTO dbo.DataProtectionKey (FriendlyName, Xml, CreatedAtUtc) VALUES (@name, @xml, @now);");
        _ = cmd.AddParam("@name", friendlyName);
        _ = cmd.AddLargeParam("@xml", xml);
        _ = cmd.AddParam("@now", DateTime.UtcNow);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }
}
