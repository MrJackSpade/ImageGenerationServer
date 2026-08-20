using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports the two ways this codebase reads a scalar while assuming the ADO.NET provider's CLR type.
///
/// <list type="number">
/// <item><b>An unboxing cast of an <c>ExecuteScalar</c> result</b> — <c>(int)(await cmd.ExecuteScalarAsync(ct))!</c>.
/// This is the real hazard and it is provider-independent: a SQLite <c>COUNT(*)</c> boxes a <see cref="long"/>, and
/// the CLR refuses to unbox a boxed <c>long</c> to <c>int</c> no matter what. Use
/// <c>DbValueExtensions.ScalarInt32Async</c> / <c>ScalarNullableInt64Async</c>.</item>
/// <item><b>A provider-typed <see cref="System.Data.IDataRecord"/> getter</b> — <c>GetByte</c>,
/// <c>GetBoolean</c>, <c>GetDecimal</c>, <c>GetDouble</c>, <c>GetFloat</c>, <c>GetInt16</c>, <c>GetInt32</c>, and
/// the corresponding numeric <c>DbDataReader.GetFieldValue&lt;T&gt;</c> pairs. Measured, these do NOT currently fail:
/// <c>Microsoft.Data.Sqlite</c> converts internally even though the column's value is a <c>long</c> (pinned by
/// <c>SqliteAttachSpikeTests</c>). They are reported anyway because they lean on a per-provider convenience rather
/// than on anything guaranteed, and the converting reads in <c>DbValueExtensions</c> (<c>AsByte</c>, <c>AsBool</c>,
/// <c>AsDouble</c>, <c>AsInt32</c>, <c>AsNullable*</c>) do not — so the codebase stays uniform instead of relying on
/// two different providers agreeing.</item>
/// </list>
///
/// <para><c>GetString</c>, <c>GetInt64</c>, <c>GetDateTime</c>, <c>GetGuid</c>, <c>GetValue</c>,
/// <c>GetFieldValue&lt;byte[]&gt;</c>, and <c>IsDBNull</c> are NOT reported: those agree across both providers, and
/// <c>DbValueExtensions</c> is itself built on <c>GetValue</c>.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProviderTypedDbReadAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "IMGDB001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Do not read a data-record column with a provider-typed getter",
        messageFormat: "'{0}' relies on the provider's CLR type for this column; use DbValueExtensions.{1} instead",
        category: "Portability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Numeric IDataRecord getters and numeric DbDataReader.GetFieldValue<T> calls return the column's declared type on SQL Server, while "
            + "on SQLite the value is always a long that the provider converts for you. The converting reads in "
            + "DbValueExtensions do not depend on either behaviour.");

    private static readonly DiagnosticDescriptor ScalarRule = new(
        id: ScalarDiagnosticId,
        title: "Do not unbox an ExecuteScalar result with a cast",
        messageFormat: "'({0})' unboxes the ExecuteScalar result and throws when the provider returns a different "
            + "width (SQLite COUNT(*) boxes a long); use DbValueExtensions.{1} instead",
        category: "Portability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "ExecuteScalar returns object. Unboxing requires the exact runtime type, so (int) on a boxed "
            + "long is an InvalidCastException on any provider that returns a wider integer — which SQLite always "
            + "does. Convert instead of unboxing.");

    /// <summary>The second diagnostic id: an unboxing cast applied to an <c>ExecuteScalar</c> result.</summary>
    public const string ScalarDiagnosticId = "IMGDB002";

    /// <summary>
    /// The unbox targets worth reporting, mapped to what replaces each.
    ///
    /// <para><c>DateTime</c> is here for the same reason as the numeric widths and is easy to miss: SQL Server returns
    /// a <see cref="DateTime"/>, while SQLite stores timestamps as ISO-8601 TEXT and returns a <see cref="string"/>.
    /// A <c>(DateTime)</c> unbox therefore throws on SQLite, and — unlike <c>int</c> — it is not a language keyword,
    /// so it needs the non-predefined branch below.</para>
    /// </summary>
    private const string DbCommandMetadataName = "System.Data.Common.DbCommand";
    private const string DataRecordMetadataName = "System.Data.IDataRecord";
    private const string DateTimeMetadataName = "System.DateTime";

    private static readonly Dictionary<SpecialType, string> ScalarReplacements = new()
    {
        [SpecialType.System_Byte] = "Convert.ToByte",
        [SpecialType.System_SByte] = "Convert.ToSByte",
        [SpecialType.System_Int16] = "Convert.ToInt16",
        [SpecialType.System_UInt16] = "Convert.ToUInt16",
        [SpecialType.System_Int32] = "ScalarInt32Async",
        [SpecialType.System_UInt32] = "Convert.ToUInt32",
        [SpecialType.System_Int64] = "ScalarNullableInt64Async",
        [SpecialType.System_UInt64] = "Convert.ToUInt64",
        [SpecialType.System_Single] = "Convert.ToSingle",
        [SpecialType.System_Double] = "Convert.ToDouble",
        [SpecialType.System_Decimal] = "Convert.ToDecimal",
        [SpecialType.System_Boolean] = "Convert.ToBoolean",
    };

    /// <summary>The provider-typed getters, mapped to the <c>DbValueExtensions</c> member that replaces each.</summary>
    private static readonly Dictionary<string, string> Replacements = new()
    {
        [nameof(DbDataReader.GetByte)] = "AsByte",
        [nameof(DbDataReader.GetBoolean)] = "AsBool",
        [nameof(DbDataReader.GetDecimal)] = "AsDecimal",
        [nameof(DbDataReader.GetDouble)] = "AsDouble",
        [nameof(DbDataReader.GetFloat)] = "AsFloat",
        [nameof(DbDataReader.GetInt16)] = "AsInt16",
        [nameof(DbDataReader.GetInt32)] = "AsInt32",
    };

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, ScalarRule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? dbCommand = start.Compilation.GetTypeByMetadataName(DbCommandMetadataName);
            INamedTypeSymbol? dataRecord = start.Compilation.GetTypeByMetadataName(DataRecordMetadataName);
            INamedTypeSymbol? dateTime = start.Compilation.GetTypeByMetadataName(DateTimeMetadataName);
            start.RegisterSyntaxNodeAction(c => AnalyzeInvocation(c, dataRecord), SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeCast(c, dbCommand, dateTime), SyntaxKind.CastExpression);
        });
    }

    /// <summary>Flags <c>(int)(await cmd.ExecuteScalarAsync(ct))</c> and the synchronous <c>(int)cmd.ExecuteScalar()</c>.</summary>
    private static void AnalyzeCast(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? dbCommand,
        INamedTypeSymbol? dateTime)
    {
        CastExpressionSyntax cast = (CastExpressionSyntax)context.Node;
        ITypeSymbol? target = context.SemanticModel.GetTypeInfo(cast.Type, context.CancellationToken).Type;
        if (target is null || !TryScalarReplacement(target, dateTime, out string? replacement))
        {
            return;
        }

        if (!MentionsExecuteScalar(context, cast.Expression, dbCommand))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            ScalarRule, cast.GetLocation(), target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), replacement));
    }

    /// <summary>
    /// True when the cast operand is (or wraps) an <c>ExecuteScalar</c>/<c>ExecuteScalarAsync</c> call. Walks through
    /// the parenthesising, <c>await</c>, null-forgiving and <c>??</c> forms the codebase actually wrote.
    /// </summary>
    private static bool MentionsExecuteScalar(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression,
        INamedTypeSymbol? dbCommand) =>
        dbCommand is not null && expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => context.SemanticModel.GetSymbolInfo(i, context.CancellationToken).Symbol)
            .OfType<IMethodSymbol>()
            .Any(m => m.Name is nameof(DbCommand.ExecuteScalar) or nameof(DbCommand.ExecuteScalarAsync)
                && IsOrDerivesFrom(m.ContainingType, dbCommand));

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol? dataRecord)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        // IDataReader extends IDataRecord; concrete provider readers implement it too. An identically named method on
        // an unrelated type is none of this rule's business.
        if (dataRecord is null || !IsOrImplements(method.ContainingType, dataRecord))
        {
            return;
        }

        string name = method.Name;
        string? replacement = null;
        if (!Replacements.TryGetValue(name, out replacement)
            && !(name == nameof(DbDataReader.GetFieldValue)
                && method.TypeArguments.Length == 1
                && TryReaderReplacement(method.TypeArguments[0], out replacement)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), name, replacement));
    }

    private static bool TryScalarReplacement(
        ITypeSymbol type,
        INamedTypeSymbol? dateTime,
        out string? replacement)
    {
        if (ScalarReplacements.TryGetValue(type.SpecialType, out replacement))
        {
            return true;
        }

        if (dateTime is not null && SymbolEqualityComparer.Default.Equals(type, dateTime))
        {
            replacement = "Convert.ToDateTime";
            return true;
        }

        replacement = null;
        return false;
    }

    private static bool TryReaderReplacement(ITypeSymbol type, out string? replacement) =>
        type.SpecialType switch
        {
            SpecialType.System_Byte => Return("AsByte", out replacement),
            SpecialType.System_Boolean => Return("AsBool", out replacement),
            SpecialType.System_Decimal => Return("AsDecimal", out replacement),
            SpecialType.System_Double => Return("AsDouble", out replacement),
            SpecialType.System_Single => Return("AsFloat", out replacement),
            SpecialType.System_Int16 => Return("AsInt16", out replacement),
            SpecialType.System_Int32 => Return("AsInt32", out replacement),
            _ => Return(null, out replacement),
        };

    private static bool Return(string? value, out string? replacement)
    {
        replacement = value;
        return value is not null;
    }

    private static bool IsOrDerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOrImplements(INamedTypeSymbol? type, INamedTypeSymbol interfaceType) =>
        type is not null
        && (SymbolEqualityComparer.Default.Equals(type, interfaceType)
            || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType)));
}
