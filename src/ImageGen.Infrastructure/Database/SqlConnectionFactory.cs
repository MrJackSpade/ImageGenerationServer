//TODO: CHECK FOR FALLBACKS
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Database;

/// <summary>The SQL Server implementation of <see cref="IDbConnectionFactory"/>. Nothing to configure per connection —
/// the schema is really named <c>dbo</c> and referential actions are always enforced.</summary>
public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
