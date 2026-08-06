using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports a null-coalescing (<c>??</c>) or null-coalescing-assignment (<c>??=</c>) whose left operand the
/// compiler has already proven non-null at that point. When the left can never be null, the right side is
/// unreachable dead logic and usually signals confused intent (<c>string s = ""; var v = s ?? "";</c>). The
/// fallback should be deleted — or, if it was masking a real bug, that bug fixed.
///
/// <para>Fires only on a conclusive <see cref="NullableFlowState.NotNull"/>. A genuinely nullable operand
/// (<see cref="NullableFlowState.MaybeNull"/>) — the correct use of <c>??</c> — and a nullable-oblivious one
/// (<see cref="NullableFlowState.None"/>, e.g. an unannotated external API) are left alone, which is what keeps
/// the rule free of false positives.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeadNullCoalescingAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "IMGNULL002";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Null-coalescing on a non-null operand is dead code",
        messageFormat: "The left operand of '{0}' is never null here; the fallback is dead code — remove it",
        category: "Nullability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "When the left operand of ?? or ??= is provably non-null (the compiler's nullable flow state "
            + "is NotNull), the right operand can never run. Delete the dead fallback, or fix the logic error it was "
            + "hiding. Genuinely nullable and nullable-oblivious operands are not reported.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCoalesce, SyntaxKind.CoalesceExpression);
        context.RegisterSyntaxNodeAction(AnalyzeCoalesceAssignment, SyntaxKind.CoalesceAssignmentExpression);
    }

    /// <summary>Flags <c>left ?? right</c> when <c>left</c> is flow-non-null.</summary>
    private static void AnalyzeCoalesce(SyntaxNodeAnalysisContext context)
    {
        BinaryExpressionSyntax coalesce = (BinaryExpressionSyntax)context.Node;
        Report(context, coalesce.Left, coalesce.OperatorToken);
    }

    /// <summary>Flags <c>left ??= right</c> when <c>left</c> is flow-non-null.</summary>
    private static void AnalyzeCoalesceAssignment(SyntaxNodeAnalysisContext context)
    {
        AssignmentExpressionSyntax coalesce = (AssignmentExpressionSyntax)context.Node;
        Report(context, coalesce.Left, coalesce.OperatorToken);
    }

    /// <summary>
    /// Reports at the operator token when the compiler's nullable flow state for <paramref name="left"/> is a
    /// conclusive <see cref="NullableFlowState.NotNull"/>. Any other state (MaybeNull, None) is left alone.
    /// </summary>
    private static void Report(SyntaxNodeAnalysisContext context, ExpressionSyntax left, SyntaxToken operatorToken)
    {
        NullableFlowState flowState = context.SemanticModel.GetTypeInfo(left, context.CancellationToken).Nullability.FlowState;
        if (flowState != NullableFlowState.NotNull)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, operatorToken.GetLocation(), operatorToken.ValueText));
    }
}
