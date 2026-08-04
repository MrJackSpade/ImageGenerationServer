using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports the null-forgiving (<c>!</c>) postfix operator — <c>foo!</c>, <c>x!.Bar</c>, <c>list[i]!</c>.
/// Asserting non-null silences the compiler's nullable flow analysis without giving it a reason to
/// believe the value is actually non-null, so a wrong assertion turns a compile-time warning into a
/// runtime <see cref="System.NullReferenceException"/>. The fix is to restructure the code so the
/// compiler can prove non-null on its own (guard clauses, <c>is { } x</c> patterns, throwing helpers),
/// never to re-assert it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NullForgivingOperatorAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "IMGNULL001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Null-forgiving operator (!) is banned",
        messageFormat: "Remove the null-forgiving operator (!); restructure the code so nullability is provable",
        category: "Nullability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The null-forgiving postfix operator suppresses nullable flow analysis instead of satisfying "
            + "it. A wrong assertion becomes a NullReferenceException at runtime. Restructure with guard clauses, "
            + "'is { } x' patterns, non-null flow, or throwing helpers so the compiler proves non-null itself.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeSuppression, SyntaxKind.SuppressNullableWarningExpression);
    }

    /// <summary>
    /// Reports at the <c>!</c> token. <see cref="SyntaxKind.SuppressNullableWarningExpression"/> is exact — it is
    /// only the postfix null-forgiving operator, never logical-not (<c>!x</c>), <c>!=</c>, or a <c>!</c> in a literal —
    /// so no text heuristics are needed.
    /// </summary>
    private static void AnalyzeSuppression(SyntaxNodeAnalysisContext context)
    {
        var suppression = (PostfixUnaryExpressionSyntax)context.Node;
        context.ReportDiagnostic(Diagnostic.Create(Rule, suppression.OperatorToken.GetLocation()));
    }
}
