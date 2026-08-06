using Microsoft.Data.SqlClient;
using System.Data.Common;

namespace ImageGen.Infrastructure.Database;

/// <summary>The SQL Server implementation of <see cref="IDbConnectionFactory"/>. Nothing to configure per connection —
/// the schema is really named <c>dbo</c> and referential actions are always enforced.</summary>
public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        SqlConnection connection = new(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
