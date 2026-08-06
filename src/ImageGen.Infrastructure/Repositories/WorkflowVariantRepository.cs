using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="IWorkflowVariantRepository"/> over <c>dbo.WorkflowVariant</c>. Stateless (a fresh connection per call),
/// so it registers as a singleton alongside the other machine-scoped catalogue repositories.
///
/// <para>Nothing here is encrypted. A variant names a shipped configuration and a parameter snapshot — facts about
/// this box's catalogue, not a user's words, and there is no owning user to key a cipher by (the same reasoning as
/// <see cref="CatalogOverrideRepository"/>).</para>
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class WorkflowVariantRepository(IDbConnectionFactory connectionFactory) : IWorkflowVariantRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkflowVariant>> VariantsAsync(string machineName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT VariantId, BaseConfigId, FriendlyName, ParamsJson FROM dbo.WorkflowVariant WHERE MachineName = @m;");
        _ = cmd.AddParam("@m", machineName);

        List<WorkflowVariant> result = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WorkflowVariant(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task AddAsync(string machineName, WorkflowVariant variant, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
INSERT INTO dbo.WorkflowVariant (MachineName, VariantId, BaseConfigId, FriendlyName, ParamsJson, CreatedAtUtc)
VALUES (@m, @id, @base, @name, @params, @now);");
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@id", variant.VariantId);
        _ = cmd.AddParam("@base", variant.BaseConfigId);
        _ = cmd.AddParam("@name", variant.FriendlyName);
        _ = cmd.AddParam("@params", variant.ParamsJson);
        _ = cmd.AddParam("@now", DateTime.UtcNow);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string machineName, string variantId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.WorkflowVariant WHERE MachineName = @m AND VariantId = @id;");
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@id", variantId);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }
}
