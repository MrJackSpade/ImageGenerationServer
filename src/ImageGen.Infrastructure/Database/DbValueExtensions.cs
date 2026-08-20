using ImageGen.Domain;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// Reads scalar values out of a reader or command <b>without</b> asserting the provider's CLR type for the column.
///
/// <para>SQL Server hands back exactly the type the column was declared as: <c>TINYINT</c> arrives as
/// <see cref="byte"/>, <c>INT</c> as <see cref="int"/>, <c>BIT</c> as <see cref="bool"/>, <c>FLOAT</c> as
/// <see cref="double"/>. So <c>reader.GetByte(3)</c> and <c>(int)await cmd.ExecuteScalarAsync()</c> both work, and
/// the codebase relies on them throughout.</para>
///
/// <para>SQLite has <b>one</b> integer type, so every one of those columns comes back as a <see cref="long"/>.
/// Measured against <c>Microsoft.Data.Sqlite</c> (see <c>SqliteAttachSpikeTests</c>), the split is:</para>
/// <list type="bullet">
/// <item>The typed reader getters <b>do not fail</b> — the provider converts internally. They are routed through
/// here anyway so the codebase does not depend on two providers happening to agree, which is what
/// <c>IMGDB001</c> keeps true.</item>
/// <item>Unboxing a scalar <b>does</b> fail, on any provider and unavoidably: <c>ExecuteScalar</c> returns
/// <see cref="object"/>, a SQLite <c>COUNT(*)</c> boxes a <c>long</c>, and <c>(int)</c> on a boxed <c>long</c> is an
/// <see cref="InvalidCastException"/>. <b>The compiler can see none of these</b>, so <c>IMGDB002</c> exists for
/// exactly that.</item>
/// </list>
///
/// <para><c>GetString</c>, <c>GetInt64</c>, <c>GetDateTime</c> and <c>GetFieldValue&lt;byte[]&gt;</c> are
/// deliberately absent: those already agree across both providers, so wrapping them would be noise.</para>
/// </summary>
internal static class DbValueExtensions
{
    /// <summary>A <c>TINYINT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static byte AsByte(this IDataRecord reader, int ordinal) =>
        Convert.ToByte(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>An <c>INT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static int AsInt32(this IDataRecord reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>SMALLINT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static short AsInt16(this IDataRecord reader, int ordinal) =>
        Convert.ToInt16(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>BIT</c> / SQLite <c>INTEGER</c> column, whatever CLR type the provider chose for it.</summary>
    internal static bool AsBool(this IDataRecord reader, int ordinal) =>
        Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>FLOAT</c> / SQLite <c>REAL</c> column, whatever CLR type the provider chose for it.</summary>
    internal static double AsDouble(this IDataRecord reader, int ordinal) =>
        Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>REAL</c> column, whatever CLR type the provider chose for it.</summary>
    internal static float AsFloat(this IDataRecord reader, int ordinal) =>
        Convert.ToSingle(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A <c>DECIMAL</c> column, whatever CLR type the provider chose for it.</summary>
    internal static decimal AsDecimal(this IDataRecord reader, int ordinal) =>
        Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary><see cref="AsInt32"/>, or null when the column is NULL.</summary>
    internal static int? AsNullableInt32(this IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.AsInt32(ordinal);

    /// <summary><see cref="AsBool"/>, or null when the column is NULL.</summary>
    internal static bool? AsNullableBool(this IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.AsBool(ordinal);

    /// <summary><see cref="AsDouble"/>, or null when the column is NULL.</summary>
    internal static double? AsNullableDouble(this IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.AsDouble(ordinal);

    /// <summary>A nullable <c>BIT</c> / SQLite <c>INTEGER</c> column read as a <see cref="TriState"/>: NULL is
    /// <see cref="TriState.Unspecified"/>, 1 is <see cref="TriState.True"/>, 0 is <see cref="TriState.False"/>. The DB
    /// column stays a nullable bit — the enum is the in-memory shape, mapped here at the boundary.</summary>
    internal static TriState AsTriState(this IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? TriState.Unspecified : reader.AsBool(ordinal) ? TriState.True : TriState.False;

    /// <summary>A <see cref="TriState"/> as the value to bind to a nullable-bit parameter: <see cref="TriState.True"/>
    /// and <see cref="TriState.False"/> become the boolean; <see cref="TriState.Unspecified"/> becomes
    /// <see cref="DBNull"/>, so "not provided" persists as NULL exactly as the old <c>bool? = null</c> did.</summary>
    internal static object ToNullableBitParam(this TriState value) => value switch
    {
        TriState.True => true,
        TriState.False => false,
        _ => DBNull.Value,
    };

    /// <summary>
    /// A single-value query as an <see cref="int"/> — a <c>COUNT(*)</c>, an <c>EXISTS</c> flag. Treats no-row and
    /// NULL alike as 0, which is what every caller here means by "nothing matched".
    /// </summary>
    internal static async Task<int> ScalarInt32Async(this DbCommand cmd, CancellationToken ct)
    {
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A single-value query as a <see cref="long"/>, or null when there was no row or the value was NULL. Used for
    /// generated-identity reads, where null is meaningful: it says the guarded insert matched an existing row.
    /// </summary>
    internal static async Task<long?> ScalarNullableInt64Async(this DbCommand cmd, CancellationToken ct)
    {
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
