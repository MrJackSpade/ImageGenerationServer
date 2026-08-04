//TODO: CHECK FOR FALLBACKS
using System.Data.Common;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// Builds commands and parameters through the ADO.NET base types, so a repository never names a provider.
///
/// <para>These exist because the two conveniences the repositories were written against are provider-specific, not
/// part of the base API: <c>new SqlCommand(sql, conn, tx)</c> and <c>Parameters.AddWithValue(...)</c>. The base types
/// offer <see cref="DbConnection.CreateCommand"/> and <see cref="DbCommand.CreateParameter"/> instead, which are
/// four lines each at every call site. Wrapping them once keeps ~100 command sites and ~300 parameter sites reading
/// the way they did.</para>
/// </summary>
internal static class DbCommandExtensions
{
    /// <summary>A command for <paramref name="sql"/> on this connection, optionally enlisted in a transaction.</summary>
    internal static DbCommand Command(this DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (transaction is not null)
            cmd.Transaction = transaction;
        return cmd;
    }

    /// <summary>
    /// Adds a named parameter. A null <paramref name="value"/> becomes <see cref="DBNull"/> — ADO.NET treats a CLR
    /// null as "no value supplied" and would fail the call rather than send SQL NULL, which is why the call sites
    /// were all littered with <c>(object?)x ?? DBNull.Value</c>.
    /// </summary>
    internal static DbCommand AddParam(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return cmd;
    }

    /// <summary>
    /// Adds a parameter carrying a large payload — an image blob, or a JSON document with no length bound.
    ///
    /// <para>The call sites used to say <c>Parameters.Add("@b", SqlDbType.VarBinary, -1)</c>. The <c>-1</c> was the
    /// point: it declares MAX so SQL Server does not infer a length from the first value and then truncate or
    /// re-prepare on the next one. Inferring is exactly what plain <see cref="AddParam"/> does, so these keep their
    /// own method — SQLite needs no such hint, and gets a plain parameter.</para>
    /// </summary>
    internal static DbCommand AddLargeParam(this DbCommand cmd, string name, object? value)
    {
        if (cmd is Microsoft.Data.SqlClient.SqlCommand sqlCommand)
        {
            var typed = value is byte[]
                ? sqlCommand.Parameters.Add(name, System.Data.SqlDbType.VarBinary, -1)
                : sqlCommand.Parameters.Add(name, System.Data.SqlDbType.NVarChar, -1);
            typed.Value = value ?? DBNull.Value;
            return cmd;
        }

        return cmd.AddParam(name, value);
    }
}
