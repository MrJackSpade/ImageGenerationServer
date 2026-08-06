using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports a property or field whose type is a nullable value type — <c>int?</c>, <c>long?</c>, <c>double?</c>,
/// <c>bool?</c>, <c>decimal?</c>, <c>DateTime?</c>, <c>Guid?</c>, an <c>enum?</c>, … (any <see cref="System.Nullable{T}"/>).
/// A nullable value type that exists only because the caller might not supply it is a defect: it should be a
/// non-nullable property with a default (<c>public int X { get; set; } = 0;</c>) so the value is guaranteed at the
/// DTO/API boundary and no downstream layer has to <c>?? default</c> a value that should already exist.
///
/// <para><b>Scope — value types only.</b> Only <see cref="System.Nullable{T}"/>-typed members are flagged.
/// Reference-type nullability (<c>string?</c>) is out of scope — that is NRT's job, enforced separately by
/// <c>WarningsAsErrors=nullable</c> — so this rule never touches a <c>string?</c>. Covered members: auto- and
/// full property declarations, record positional parameters (they compile to properties), and fields. Method
/// parameters, returns, and locals are out of scope: the ask is specifically properties/fields on objects.</para>
///
/// <para>Annotate the member with <c>[AllowNullable("reason")]</c> to exempt the genuinely-valid case where
/// <c>null</c> carries a meaning no default can express — a <c>DateTime? FinishedAtUtc</c> whose <c>null</c> means
/// <i>not finished</i>, a <c>double? ChangeScore</c> whose <c>null</c> means <i>not computed</i>. The reason is
/// mandatory (<c>IMGNULL004</c>). A record positional parameter is annotated with the <c>[property: AllowNullable(…)]</c>
/// target, since the attribute lands on the synthesized property. A type-level opt-out is honoured too: an
/// <c>[AllowNullable]</c> on the enclosing type exempts every value-type-nullable member inside it, matching the
/// containing-chain walk used by the magic-string rule.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NullableValueTypePropertyAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported for a nullable value-type property or field.</summary>
    public const string DiagnosticId = "IMGNULL003";

    /// <summary>The diagnostic id reported when the opt-out attribute carries an empty justification.</summary>
    public const string JustificationDiagnosticId = "IMGNULL004";

    /// <summary>
    /// Simple (unqualified) name of the opt-out attribute. Matched by name so this analyzer never has to
    /// reference the assembly that declares it — see <c>ImageGen.Domain.CodeAnalysis.AllowNullableAttribute</c>.
    /// </summary>
    private const string AllowAttributeName = "AllowNullableAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Nullable value-type member",
        messageFormat: "'{0}' is a nullable value type; make it non-nullable with a default, or annotate it with "
            + "[AllowNullable(\"reason\")] if null carries a meaning no default can express",
        category: "Nullability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A property or field typed as a nullable value type (int?, double?, bool?, DateTime?, an "
            + "enum?, any Nullable<T>) is usually nullable only because the caller might omit it — a defect. Make it "
            + "a non-nullable property with a default so the value is guaranteed at the DTO/API boundary and no "
            + "downstream layer has to coalesce it. Reference-type nullability (string?) is out of scope (that is "
            + "NRT's job). Where null carries a meaning no default can express, annotate the member with "
            + "[AllowNullable(\"reason\")].");

    private static readonly DiagnosticDescriptor JustificationRule = new(
        id: JustificationDiagnosticId,
        title: "AllowNullable requires a justification",
        messageFormat: "[AllowNullable] needs a non-empty justification saying what null means here and why no "
            + "default can express it",
        category: "Nullability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The nullable-value-type opt-out is meant to be deliberate: it must carry a written reason. An "
            + "empty or whitespace justification defeats that, so it is rejected.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Rule, JustificationRule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Property, SymbolKind.Field);
        context.RegisterSyntaxNodeAction(AnalyzeAllowAttribute, SyntaxKind.Attribute);
    }

    /// <summary>
    /// Flags a property or field whose type is a nullable value type, unless the member — or an enclosing type,
    /// via the containing-chain walk in <see cref="IsExempt"/> — carries <c>[AllowNullable]</c>. A record
    /// positional parameter is reached here through its synthesized property, whose location is the parameter, so
    /// the diagnostic points at the source to fix.
    /// </summary>
    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        ITypeSymbol? type = context.Symbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };
        if (type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            return;
        }

        if (IsExempt(context.Symbol))
        {
            return;
        }

        foreach (Location location in context.Symbol.Locations)
        {
            if (location.IsInSource)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, context.Symbol.Name));
                return;
            }
        }
    }

    /// <summary>
    /// Reports <c>IMGNULL004</c> when an <c>[AllowNullable]</c> application carries an empty or whitespace
    /// justification. A missing justification is left to the compiler — the constructor's required parameter
    /// already makes a bare <c>[AllowNullable]</c> a build error.
    /// </summary>
    private static void AnalyzeAllowAttribute(SyntaxNodeAnalysisContext context)
    {
        AttributeSyntax attribute = (AttributeSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol ctor)
        {
            return;
        }

        if (ctor.ContainingType?.Name != AllowAttributeName)
        {
            return;
        }

        if (attribute.ArgumentList?.Arguments.FirstOrDefault() is not { } argument)
        {
            return;
        }

        Optional<object?> justification = context.SemanticModel.GetConstantValue(argument.Expression, context.CancellationToken);
        if (justification is { HasValue: true, Value: string text } && string.IsNullOrWhiteSpace(text))
        {
            context.ReportDiagnostic(Diagnostic.Create(JustificationRule, argument.GetLocation()));
        }
    }

    /// <summary>True when <paramref name="symbol"/> carries <c>[AllowNullable]</c> directly.</summary>
    private static bool HasAllowAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == AllowAttributeName);

    /// <summary>
    /// True when <paramref name="symbol"/> or any symbol enclosing it carries <c>[AllowNullable]</c>. Walking the
    /// containing chain is what lets a type-level attribute exempt every value-type-nullable member inside it.
    /// </summary>
    private static bool IsExempt(ISymbol? symbol)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (HasAllowAttribute(current))
            {
                return true;
            }
        }

        return false;
    }
}
