using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports the two ways this codebase reads a scalar while assuming the ADO.NET provider's CLR type.
///
/// <list type="number">
/// <item><b>An unboxing cast of an <c>ExecuteScalar</c> result</b> — <c>(int)(await cmd.ExecuteScalarAsync(ct))!</c>.
/// This is the real hazard and it is provider-independent: a SQLite <c>COUNT(*)</c> boxes a <see cref="long"/>, and
/// the CLR refuses to unbox a boxed <c>long</c> to <c>int</c> no matter what. Use
/// <c>DbValueExtensions.ScalarInt32Async</c> / <c>ScalarNullableInt64Async</c>.</item>
/// <item><b>A provider-typed <see cref="System.Data.Common.DbDataReader"/> getter</b> — <c>GetByte</c>,
/// <c>GetBoolean</c>, <c>GetDouble</c>, <c>GetInt32</c>. Measured, these do NOT currently fail:
/// <c>Microsoft.Data.Sqlite</c> converts internally even though the column's value is a <c>long</c> (pinned by
/// <c>SqliteAttachSpikeTests</c>). They are reported anyway because they lean on a per-provider convenience rather
/// than on anything guaranteed, and the converting reads in <c>DbValueExtensions</c> (<c>AsByte</c>, <c>AsBool</c>,
/// <c>AsDouble</c>, <c>AsInt32</c>, <c>AsNullable*</c>) do not — so the codebase stays uniform instead of relying on
/// two different providers agreeing.</item>
/// </list>
///
/// <para><c>GetString</c>, <c>GetInt64</c>, <c>GetDateTime</c>, <c>GetValue</c> and <c>IsDBNull</c> are NOT reported:
/// those agree across both providers, and <c>DbValueExtensions</c> is itself built on <c>GetValue</c>.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProviderTypedDbReadAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "IMGDB001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Do not read a DbDataReader column with a provider-typed getter",
        messageFormat: "'{0}' relies on the provider's CLR type for this column; use DbValueExtensions.{1} instead",
        category: "Portability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "GetByte/GetBoolean/GetDouble/GetInt32 return the column's declared type on SQL Server, while "
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
    /// A <c>(DateTime)</c> unbox therefore throws on SQLite, and it slipped past the first version of this rule
    /// because — unlike <c>int</c> — it is not a language keyword and needs the non-predefined branch below.</para>
    /// </summary>
    private static readonly Dictionary<string, string> ScalarReplacements = new()
    {
        ["int"] = "ScalarInt32Async",
        ["long"] = "ScalarNullableInt64Async",
        ["byte"] = "ScalarInt32Async",
        ["bool"] = "ScalarInt32Async",
        ["double"] = "ScalarInt32Async",
        ["DateTime"] = "Convert.ToDateTime",
    };

    /// <summary>The provider-typed getters, mapped to the <c>DbValueExtensions</c> member that replaces each.</summary>
    private static readonly Dictionary<string, string> Replacements = new()
    {
        ["GetByte"] = "AsByte",
        ["GetBoolean"] = "AsBool",
        ["GetDouble"] = "AsDouble",
        ["GetInt32"] = "AsInt32",
    };

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, ScalarRule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeCast, SyntaxKind.CastExpression);
    }

    /// <summary>Flags <c>(int)(await cmd.ExecuteScalarAsync(ct))</c> and the synchronous <c>(int)cmd.ExecuteScalar()</c>.</summary>
    private static void AnalyzeCast(SyntaxNodeAnalysisContext context)
    {
        var cast = (CastExpressionSyntax)context.Node;
        // PredefinedTypeSyntax covers the keyword types (int, long, bool...); IdentifierNameSyntax covers the rest,
        // which is how (DateTime) gets seen at all.
        var target = cast.Type switch
        {
            PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
            IdentifierNameSyntax named => named.Identifier.ValueText,
            _ => null,
        };
        if (target is null || !ScalarReplacements.TryGetValue(target, out var replacement))
            return;

        if (!MentionsExecuteScalar(cast.Expression))
            return;

        context.ReportDiagnostic(Diagnostic.Create(ScalarRule, cast.GetLocation(), target, replacement));
    }

    /// <summary>
    /// True when the cast operand is (or wraps) an <c>ExecuteScalar</c>/<c>ExecuteScalarAsync</c> call. Walks through
    /// the parenthesising, <c>await</c>, null-forgiving and <c>??</c> forms the codebase actually wrote.
    /// </summary>
    private static bool MentionsExecuteScalar(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression)
            .OfType<MemberAccessExpressionSyntax>()
            .Any(m => m.Name.Identifier.ValueText is "ExecuteScalar" or "ExecuteScalarAsync");

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return;

        var name = member.Name.Identifier.ValueText;
        if (!Replacements.TryGetValue(name, out var replacement))
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;

        // Only the DbDataReader family. A GetInt32 on some unrelated type is none of this rule's business.
        if (!IsDbDataReader(method.ContainingType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), name, replacement));
    }

    /// <summary>True when <paramref name="type"/> is <c>DbDataReader</c> or derives from it (e.g. <c>SqlDataReader</c>).</summary>
    private static bool IsDbDataReader(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == "System.Data.Common.DbDataReader")
                return true;
        return false;
    }
}
