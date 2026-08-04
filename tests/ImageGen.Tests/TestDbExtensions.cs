using System.Data.Common;

namespace ImageGen.Tests;

/// <summary>
/// The provider-neutral command/parameter helpers, for tests that reach past the repositories to assert on raw rows.
/// Mirrors <c>ImageGen.Infrastructure.Database.DbCommandExtensions</c>, which is internal to that assembly — these
/// tests only need the same two conveniences, not access to the whole namespace.
/// </summary>
internal static class TestDbExtensions
{
    /// <summary>A command for <paramref name="sql"/> on this connection.</summary>
    internal static DbCommand Command(this DbConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>Adds a named parameter, mapping a CLR null to SQL NULL.</summary>
    internal static DbCommand AddParam(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
