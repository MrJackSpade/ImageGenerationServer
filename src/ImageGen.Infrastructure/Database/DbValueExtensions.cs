using System.Data.Common;
using System.Globalization;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// Reads scalar values out of a reader or command <b>without</b> asserting the provider's CLR type for the column.
///
/// <para>SQL Server hands back exactly the type the column was declared as: <c>TINYINT</c> arrives as
/// <see cref="byte"/>, <c>INT</c> as <see cref="int"/>, <c>BIT</c> as <see cref="bool"/>, <c>FLOAT</c> as
/// <see cref="double"/>. So <c>reader.GetByte(3)</c> and <c>(int)await cmd.ExecuteScalarAsync()</c> both work, and
/// the codebase used them everywhere.</para>
///
/// <para>SQLite has <b>one</b> integer type, so every one of those columns comes back as a <see cref="long"/>.
/// Measured against <c>Microsoft.Data.Sqlite</c> (see <c>SqliteAttachSpikeTests</c>), the split is:</para>
/// <list type="bullet">
/// <item>The typed reader getters <b>do not fail</b> — the provider converts internally. They are routed through
/// here anyway so the codebase does not depend on two providers happening to agree, which is what
/// <c>IMGDB001</c> keeps true.</item>
/// <item>Unboxing a scalar <b>does</b> fail, on any provider and unavoidably: <c>ExecuteScalar</c> returns
/// <see cref="object"/>, a SQLite <c>COUNT(*)</c> boxes a <c>long</c>, and <c>(int)</c> on a boxed <c>long</c> is an
/// <see cref="InvalidCastException"/>. This codebase had ~8 of those and <b>the compiler could see none of them</b>.
/// <c>IMGDB002</c> exists for exactly that.</item>
/// </list>
///
/// <para><c>GetString</c>, <c>GetInt64</c>, <c>GetDateTime</c> and <c>GetFieldValue&lt;byte[]&gt;</c> are
/// deliberately absent: those already agree across both providers, so wrapping them would be noise.</para>
/// </summary>
internal static class DbValueExtensions
{
    /// <summary>A <c>TINYINT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static byte AsByte(this DbDataReader reader, int ordinal) =>
        Convert.ToByte(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>An <c>INT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static int AsInt32(this DbDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>BIT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static bool AsBool(this DbDataReader reader, int ordinal) =>
        Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>FLOAT</c> / SQLite <c>REAL</c> column, whatever CLR type the provider chose for it.</summary>
    internal static double AsDouble(this DbDataReader reader, int ordinal) =>
        Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary><see cref="AsInt32"/>, or null when the column is NULL.</summary>
    internal static int? AsNullableInt32(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.AsInt32(ordinal);

    /// <summary><see cref="AsBool"/>, or null when the column is NULL.</summary>
    internal static bool? AsNullableBool(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.AsBool(ordinal);

    /// <summary><see cref="AsDouble"/>, or null when the column is NULL.</summary>
    internal static double? AsNullableDouble(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.AsDouble(ordinal);

    /// <summary>
    /// A single-value query as an <see cref="int"/> — a <c>COUNT(*)</c>, an <c>EXISTS</c> flag. Treats no-row and
    /// NULL alike as 0, which is what every caller here means by "nothing matched".
    /// </summary>
    internal static async Task<int> ScalarInt32Async(this DbCommand cmd, CancellationToken ct)
    {
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A single-value query as a <see cref="long"/>, or null when there was no row or the value was NULL. Used for
    /// generated-identity reads, where null is meaningful: it says the guarded insert matched an existing row.
    /// </summary>
    internal static async Task<long?> ScalarNullableInt64Async(this DbCommand cmd, CancellationToken ct)
    {
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
