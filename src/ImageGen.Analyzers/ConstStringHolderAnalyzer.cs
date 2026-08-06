using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports a <c>const string</c> field whose containing type is not a <b>pure holder</b> — a <c>static class</c>
/// whose every member is itself a <c>const string</c> field. The convention is that a constant string lives in a
/// dedicated, context-grouped holder (<c>ComfyNodeTypes</c>, <c>WorkflowParamKeys</c>, a workflow's nested
/// <c>Nodes</c>) rather than loose next to the code that uses it, so related constants are named and found together
/// and no literal drifts. This analyzer enforces the mechanical half of that: a <c>const string</c> ⇒ its container
/// is a static class of <i>nothing but</i> <c>const string</c> fields.
///
/// <para><b>What is a pure holder.</b> A <c>static class</c> is a pure holder when every one of its members is a
/// <c>const string</c> field — no methods, properties, events, indexers, operators, constructors, nested types, and
/// no other fields (not a <c>static readonly</c> array, not a <c>const int</c>). Purity is literal: a class that
/// pairs its <c>const string</c>s with a <c>Choices</c> array or a <c>Parse</c> helper is <b>not</b> a holder, and
/// its constants must move to a dedicated holder while the helper stays behind.</para>
///
/// <para><b>Nested holders are their own container.</b> A workflow class with a <c>Build()</c> method and a nested
/// <c>private static class Nodes { const string … }</c> is fine: the outer class declares no <c>const string</c>
/// directly (so it is never judged as a holder), and the nested class is a pure holder in its own right. The check
/// always looks at the type that <i>directly</i> declares the field.</para>
///
/// <para><b>No escape hatch.</b> There is deliberately no opt-out attribute — a <c>const string</c> that cannot live
/// in a pure holder is restructured, not annotated. (The <c>ImageGen.Analyzers</c> project, which carries the Roslyn
/// <c>DiagnosticId</c> convention on classes that also have methods, is excluded from analysis at the project
/// reference, so it needs no exemption here.)</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConstStringHolderAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported for a const string outside a pure holder.</summary>
    public const string DiagnosticId = "IMGSTR003";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "const string outside a pure holder",
        messageFormat: "const string '{0}' must live in a dedicated static holder class whose members are all "
            + "const strings; its container is not a pure holder ({1}) — move the constant into a context-grouped holder",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A const string may only be declared in a static class that contains nothing but const string "
            + "fields — a dedicated holder that groups related constants by context, like ComfyNodeTypes or "
            + "WorkflowParamKeys. A const string in a non-static class, a record, a struct, or a static class that "
            + "also has methods/properties/other fields (a Choices array, a Parse helper, a const int) is flagged: "
            + "move it into a pure holder (a nested 'private static class Nodes' next to a workflow's Build() is the "
            + "canonical shape). There is no opt-out attribute.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
    }

    /// <summary>
    /// Flags a <c>const string</c> field whose directly-containing type is not a pure holder. The message names the
    /// first non-holder reason found (a method, a non-const field, a nested type, …) so the fix — move the constant
    /// into a dedicated holder — is obvious from the diagnostic.
    /// </summary>
    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        IFieldSymbol field = (IFieldSymbol)context.Symbol;
        if (!field.IsConst || field.Type.SpecialType != SpecialType.System_String)
        {
            return;
        }

        if (field.ContainingType is not { } container)
        {
            return;
        }

        if (DisqualifyingMember(container) is not { } reason)
        {
            return;
        }

        foreach (Location location in field.Locations)
        {
            if (location.IsInSource)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, field.Name, reason));
                return;
            }
        }
    }

    /// <summary>
    /// Returns a short description of the first thing that makes <paramref name="container"/> not a pure const-string
    /// holder, or <c>null</c> when it is a pure holder. A pure holder is a <c>static class</c> whose every member is a
    /// <c>const string</c> field; anything else — a non-static class/struct/record, a method, a property, a nested
    /// type, a non-const or non-string field — disqualifies it.
    /// </summary>
    private static string? DisqualifyingMember(INamedTypeSymbol container)
    {
        if (container.TypeKind != TypeKind.Class)
        {
            return container.TypeKind == TypeKind.Struct ? "a struct" : $"a {container.TypeKind.ToString().ToLowerInvariant()}";
        }

        if (container.IsRecord)
        {
            return "a record";
        }

        if (!container.IsStatic)
        {
            return "a non-static class";
        }

        foreach (ISymbol member in container.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (member is IFieldSymbol { IsConst: true } f && f.Type.SpecialType == SpecialType.System_String)
            {
                continue;
            }

            return member switch
            {
                IFieldSymbol => $"a non-const-string field '{member.Name}'",
                IMethodSymbol => $"a method '{member.Name}'",
                IPropertySymbol => $"a property '{member.Name}'",
                INamedTypeSymbol => $"a nested type '{member.Name}'",
                _ => $"a member '{member.Name}'",
            };
        }

        return null;
    }
}