using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports a string literal used as a value anywhere it carries meaning — a magic string. The literal's
/// meaning lives only in the quotes, so a second copy silently drifts out of sync and a rename never reaches
/// it. Introduce a named constant and use that instead.
///
/// <para>Two shapes are covered. <b>Equality</b>: the <c>==</c>/<c>!=</c> operators, an <c>Equals(...)</c> call
/// (as the receiver or an argument), and every constant pattern — <c>is "x"</c>, a <c>switch</c> arm
/// <c>"x" =&gt; …</c>, and both classic (<c>case "x":</c>) and pattern (<c>case "x" when …</c>) switch labels.
/// <b>Arguments</b>: a literal passed to any method call (<c>Log("x")</c>), constructor
/// (<c>new StringBuilder("x")</c>), or indexer (<c>map["x"]</c>, <c>map?["x"]</c>, the <c>["x"] = …</c> form
/// in a collection initializer).</para>
///
/// <para>Several built-in carve-outs skip an argument by its (parameter, method) name — see
/// <see cref="IsExemptWellKnownParameter"/>: an exception or <c>ILogger</c> <c>message</c> (diagnostic prose),
/// any <c>sql</c> parameter and the <c>name</c> of an <c>AddParam(...)</c> call (SQL text and its
/// <c>@parameter</c> tokens), and the <c>format</c> of <c>ToString</c>/<c>ParseExact</c> (framework format
/// specifiers like <c>"N"</c>). Other arguments of those calls — an exception <c>paramName</c>, a structured log
/// value — are still reported.</para>
///
/// <para><see cref="string.Empty"/> is unaffected: it is a field access, not a literal, so it is never seen
/// here. The empty literal <c>""</c> <b>is</b> a literal and is reported, which forces <c>string.Empty</c>.
/// Attribute arguments (<c>[Obsolete("x")]</c>) are out of scope — they are declarative metadata, not a call.</para>
///
/// <para>Annotate a class, struct, method, or constructor with <c>[AllowMagicStrings("reason")]</c> to exempt
/// the literals lexically inside it, or a <b>parameter</b> to exempt a literal passed to it at every call site —
/// declare it once on a custom logging method's message parameter instead of on every caller. The reason is
/// mandatory (<c>IMGSTR002</c>).</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MagicStringAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported for a magic string literal.</summary>
    public const string DiagnosticId = "IMGSTR001";

    /// <summary>The diagnostic id reported when the opt-out attribute carries an empty justification.</summary>
    public const string JustificationDiagnosticId = "IMGSTR002";

    /// <summary>
    /// Simple (unqualified) name of the opt-out attribute. Matched by name so this analyzer never has to
    /// reference the assembly that declares it — see <c>ImageGen.Domain.CodeAnalysis.AllowMagicStringsAttribute</c>.
    /// </summary>
    private const string AllowAttributeName = "AllowMagicStringsAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Magic string literal",
        messageFormat: "Magic string literal {0}; use a named constant, or annotate the enclosing type or member "
            + "with [AllowMagicStrings(\"reason\")] to allow it",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A string literal used in an equality comparison (==, !=, Equals, or an is/switch/case "
            + "constant pattern) or passed as an argument to a method, constructor, or indexer is a magic string: "
            + "its meaning lives only in the quotes and a duplicate drifts out of sync silently. Introduce a named "
            + "constant and use that. A few well-known arguments are exempt by parameter name — an Exception/ILogger "
            + "message, any sql argument, an AddParam name, a ToString/ParseExact format specifier — as is any "
            + "argument whose parameter is marked [AllowMagicStrings]. "
            + "string.Empty is a field, not a literal, so it is never reported; the empty "
            + "literal \"\" is. Where hardcoding the literal is genuinely the point, annotate the enclosing type "
            + "or member with [AllowMagicStrings(\"reason\")].");

    private static readonly DiagnosticDescriptor JustificationRule = new(
        id: JustificationDiagnosticId,
        title: "AllowMagicStrings requires a justification",
        messageFormat: "[AllowMagicStrings] needs a non-empty justification saying why the literals here are allowed",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The magic-string opt-out is meant to be deliberate: it must carry a written reason. An empty "
            + "or whitespace justification defeats that, so it is rejected.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule, JustificationRule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBinary, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeObjectCreation,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeElementAccess,
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.ImplicitElementAccess,
            SyntaxKind.ElementBindingExpression);
        context.RegisterSyntaxNodeAction(AnalyzeConstantPattern, SyntaxKind.ConstantPattern);
        context.RegisterSyntaxNodeAction(AnalyzeCaseLabel, SyntaxKind.CaseSwitchLabel);
        context.RegisterSyntaxNodeAction(AnalyzeAllowAttribute, SyntaxKind.Attribute);
    }

    /// <summary>Flags <c>x == "a"</c> and <c>x != "a"</c> (a literal on either side).</summary>
    private static void AnalyzeBinary(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        ReportIfLiteral(context, binary.Left);
        ReportIfLiteral(context, binary.Right);
    }

    /// <summary>
    /// Flags a string literal passed as an argument to a method call — subject to the per-parameter opt-outs in
    /// <see cref="AnalyzeArguments"/> — plus a string literal used as the receiver of an <c>Equals</c> call
    /// (<c>"a".Equals(x)</c>), which is an equality comparison rather than a passed argument.
    /// </summary>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        AnalyzeArguments(context, method, invocation.ArgumentList.Arguments);

        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Equals" } member)
            ReportIfLiteral(context, member.Expression);
    }

    /// <summary>
    /// Flags a string literal passed as a constructor argument — <c>new Foo("a")</c>, <c>new("a")</c> — under the
    /// same per-parameter opt-outs as any call, including the built-in Exception <c>message</c> carve-out.
    /// </summary>
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is null)
            return;
        var constructor = context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol as IMethodSymbol;
        AnalyzeArguments(context, constructor, creation.ArgumentList.Arguments);
    }

    /// <summary>
    /// Reports every string-literal argument in <paramref name="arguments"/>, except one whose target parameter
    /// opts out: a parameter marked <c>[AllowMagicStrings]</c>, or a well-known message parameter (an
    /// Exception-derived constructor's or an <c>ILogger</c> call's <c>message</c>). An argument is mapped to its
    /// parameter by name when named and by position otherwise, so the opt-out follows the value wherever it lands.
    /// </summary>
    private static void AnalyzeArguments(
        SyntaxNodeAnalysisContext context,
        IMethodSymbol? method,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (method is not null
                && ResolveParameter(method, argument, i) is { } parameter
                && (HasAllowAttribute(parameter) || IsExemptWellKnownParameter(method, parameter)))
                continue;
            ReportIfLiteral(context, argument.Expression);
        }
    }

    /// <summary>
    /// Maps an argument to the parameter it binds to — by name for a named argument, otherwise by position,
    /// falling back to a trailing <c>params</c> parameter for extra positional arguments.
    /// </summary>
    private static IParameterSymbol? ResolveParameter(IMethodSymbol method, ArgumentSyntax argument, int index)
    {
        if (argument.NameColon?.Name.Identifier.ValueText is { } name)
            return method.Parameters.FirstOrDefault(p => p.Name == name);
        if (index < method.Parameters.Length)
            return method.Parameters[index];
        var last = method.Parameters.LastOrDefault();
        return last is { IsParams: true } ? last : null;
    }

    /// <summary>
    /// True for a well-known argument whose literal is exempt by convention — recognised by the
    /// (parameter name, method name) pair, which is why the exemption is described as "by name":
    /// <list type="bullet">
    /// <item><c>message</c> of an <see cref="System.Exception"/>-derived constructor or a
    /// <c>Microsoft.Extensions.Logging.LoggerExtensions</c> call — human-readable diagnostic prose.</item>
    /// <item>any parameter named <c>sql</c> — query text is inherently literal, on whatever method receives it —
    /// and the <c>name</c> of an <c>AddParam(...)</c> call (its bound <c>@parameter</c> token). <c>name</c> stays
    /// method-scoped because it is otherwise far too common a parameter to blanket-exempt.</item>
    /// <item><c>format</c> of <c>ToString</c>/<c>ParseExact</c>/<c>TryParseExact</c> — standard framework format
    /// specifiers (<c>"N"</c>, <c>"X2"</c>, <c>"o"</c>, <c>"yyyy-MM-dd"</c>) pinned by the runtime. <c>string.Format</c>
    /// is deliberately excluded: its template can carry prose.</item>
    /// </list>
    /// </summary>
    private static bool IsExemptWellKnownParameter(IMethodSymbol method, IParameterSymbol parameter) =>
        (parameter.Name, method.Name) switch
        {
            ("message", _) => DerivesFromException(method.ContainingType)
                || method.ContainingType?.ToDisplayString() == "Microsoft.Extensions.Logging.LoggerExtensions",
            ("sql", _) => true,
            ("name", "AddParam") => true,
            ("format", "ToString" or "ParseExact" or "TryParseExact") => true,
            _ => false,
        };

    /// <summary>True when <paramref name="type"/> is <see cref="System.Exception"/> or derives from it.</summary>
    private static bool DerivesFromException(ITypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Exception")
                return true;
        return false;
    }

    /// <summary>
    /// Flags a string literal used as an indexer key — <c>map["a"]</c>, the null-conditional <c>map?["a"]</c>,
    /// and the <c>["a"] = …</c> form inside a collection/dictionary initializer.
    /// </summary>
    private static void AnalyzeElementAccess(SyntaxNodeAnalysisContext context)
    {
        var arguments = context.Node switch
        {
            ElementAccessExpressionSyntax element => element.ArgumentList.Arguments,
            ImplicitElementAccessSyntax implicitElement => implicitElement.ArgumentList.Arguments,
            ElementBindingExpressionSyntax binding => binding.ArgumentList.Arguments,
            _ => default,
        };
        foreach (var argument in arguments)
            ReportIfLiteral(context, argument.Expression);
    }

    /// <summary>Flags a string literal in a constant pattern — <c>is "a"</c>, <c>"a" =&gt; …</c>, <c>case "a" when …</c>.</summary>
    private static void AnalyzeConstantPattern(SyntaxNodeAnalysisContext context)
    {
        var pattern = (ConstantPatternSyntax)context.Node;
        ReportIfLiteral(context, pattern.Expression);
    }

    /// <summary>Flags a string literal in a classic switch label — <c>case "a":</c>.</summary>
    private static void AnalyzeCaseLabel(SyntaxNodeAnalysisContext context)
    {
        var label = (CaseSwitchLabelSyntax)context.Node;
        ReportIfLiteral(context, label.Value);
    }

    /// <summary>
    /// Reports at <paramref name="expression"/> when it is a string literal and no enclosing scope opts out.
    /// The empty literal is included on purpose; only <see cref="string.Empty"/> (a field, never a literal) is
    /// out of reach.
    /// </summary>
    private static void ReportIfLiteral(SyntaxNodeAnalysisContext context, ExpressionSyntax? expression)
    {
        if (expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return;
        if (IsExempt(context.ContainingSymbol))
            return;
        context.ReportDiagnostic(Diagnostic.Create(Rule, literal.GetLocation(), literal.Token.Text));
    }

    /// <summary>
    /// Reports <c>IMGSTR002</c> when an <c>[AllowMagicStrings]</c> application carries an empty or whitespace
    /// justification. A missing justification is left to the compiler — the constructor's required parameter
    /// already makes a bare <c>[AllowMagicStrings]</c> a build error.
    /// </summary>
    private static void AnalyzeAllowAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol ctor)
            return;
        if (ctor.ContainingType?.Name != AllowAttributeName)
            return;
        if (attribute.ArgumentList?.Arguments.FirstOrDefault() is not { } argument)
            return;

        var justification = context.SemanticModel.GetConstantValue(argument.Expression, context.CancellationToken);
        if (justification is { HasValue: true, Value: string text } && string.IsNullOrWhiteSpace(text))
            context.ReportDiagnostic(Diagnostic.Create(JustificationRule, argument.GetLocation()));
    }

    /// <summary>True when <paramref name="symbol"/> carries <c>[AllowMagicStrings]</c> directly.</summary>
    private static bool HasAllowAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == AllowAttributeName);

    /// <summary>
    /// True when <paramref name="symbol"/> or any symbol enclosing it carries <c>[AllowMagicStrings]</c>. Walking
    /// the containing chain is what makes a class-level attribute cover every literal in its members, and a
    /// method-level one cover just that body.
    /// </summary>
    private static bool IsExempt(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
            if (HasAllowAttribute(current))
                return true;
        return false;
    }
}
