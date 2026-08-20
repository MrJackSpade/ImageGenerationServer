using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports literal-built constant string content used as a value anywhere it carries meaning — a magic string.
/// Ordinary/raw/u8 literals, constant interpolated strings, and constant-folded concatenations all carry the same
/// unnamed meaning, so a second copy silently drifts out of sync and a rename never reaches it. Introduce a named
/// constant and use that instead.
///
/// <para>Three shapes are covered. <b>Equality</b>: the <c>==</c>/<c>!=</c> operators, an <c>Equals(...)</c> call
/// (as the receiver or an argument), and every constant pattern — <c>is "x"</c>, a <c>switch</c> arm
/// <c>"x" =&gt; …</c>, and both classic (<c>case "x":</c>) and pattern (<c>case "x" when …</c>) switch labels.
/// <b>Arguments</b>: a literal passed to any method call (<c>Log("x")</c>), constructor
/// (<c>new StringBuilder("x")</c>), or indexer (<c>map["x"]</c>, <c>map?["x"]</c>, the <c>["x"] = …</c> form
/// in a collection initializer). <b>Object-initializer values</b>: a literal assigned to a member in an object
/// initializer (<c>new ParamSpec { Key = "steps" }</c>) — the mirror of a constructor argument — except for a
/// well-known display-prose property (see <see cref="IsExemptWellKnownProperty"/>). When such a value is an array or
/// collection creation (<c>Choices = new[] { "median", … }</c>), every literal <i>element</i> is flagged too — the same
/// magic-identifier shape as a scalar assignment (see <see cref="GetCollectionElements"/>); a nested object initializer
/// among the elements is left to the visitors that reach it directly.</para>
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
/// the literals lexically inside it, a <b>parameter</b> to exempt a literal passed to it at every call site —
/// declare it once on a custom logging method's message parameter instead of on every caller — or a
/// <b>property/field</b> to exempt a literal assigned to it in an object initializer at every construction site.
/// The reason is mandatory (<c>IMGSTR002</c>).</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MagicStringAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported for a magic string literal.</summary>
    public const string DiagnosticId = "IMGSTR001";

    /// <summary>The diagnostic id reported when the opt-out attribute carries an empty justification.</summary>
    public const string JustificationDiagnosticId = "IMGSTR002";

    /// <summary>
    /// Metadata name of the one real opt-out attribute. Resolved from the compilation so a same-simple-name type in
    /// user source cannot spoof an exemption.
    /// </summary>
    private const string AllowAttributeMetadataName = "ImageGen.Domain.CodeAnalysis.AllowMagicStringsAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Magic string literal",
        messageFormat: "Magic string content {0}; use a named constant, or annotate the enclosing type or member "
            + "with [AllowMagicStrings(\"reason\")] to allow it",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Literal-built constant string content used in an equality comparison (==, !=, Equals, or an is/switch/case "
            + "constant pattern), passed as an argument to a method, constructor, or indexer, or assigned to a member "
            + "in an object initializer is a magic string: "
            + "its meaning lives only in the quotes and a duplicate drifts out of sync silently. Introduce a named "
            + "constant and use that. A few well-known arguments are exempt by parameter name — an Exception/ILogger "
            + "message, any sql argument, an AddParam name, a ToString/ParseExact format specifier — as is any "
            + "argument whose parameter is marked [AllowMagicStrings], a well-known display-prose property "
            + "(Label, Help, Summary, …), and any property marked [AllowMagicStrings]. "
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
        [Rule, JustificationRule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? allowAttribute = start.Compilation.GetTypeByMetadataName(AllowAttributeMetadataName);
            start.RegisterSyntaxNodeAction(c => AnalyzeBinary(c, allowAttribute), SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeInvocation(c, allowAttribute), SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(
                c => AnalyzeObjectCreation(c, allowAttribute),
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression);
            start.RegisterSyntaxNodeAction(
                c => AnalyzeElementAccess(c, allowAttribute),
                SyntaxKind.ElementAccessExpression,
                SyntaxKind.ImplicitElementAccess,
                SyntaxKind.ElementBindingExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeConstantPattern(c, allowAttribute), SyntaxKind.ConstantPattern);
            start.RegisterSyntaxNodeAction(c => AnalyzeCaseLabel(c, allowAttribute), SyntaxKind.CaseSwitchLabel);
            start.RegisterSyntaxNodeAction(c => AnalyzeInitializerAssignment(c, allowAttribute), SyntaxKind.SimpleAssignmentExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeAllowAttribute(c, allowAttribute), SyntaxKind.Attribute);
        });
    }

    /// <summary>Flags <c>x == "a"</c> and <c>x != "a"</c> (a literal on either side).</summary>
    private static void AnalyzeBinary(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        BinaryExpressionSyntax binary = (BinaryExpressionSyntax)context.Node;
        ReportIfConstantString(context, binary.Left, allowAttribute);
        ReportIfConstantString(context, binary.Right, allowAttribute);
    }

    /// <summary>
    /// Flags a string literal passed as an argument to a method call — subject to the per-parameter opt-outs in
    /// <see cref="AnalyzeArguments"/> — plus a string literal used as the receiver of an <c>Equals</c> call
    /// (<c>"a".Equals(x)</c>), which is an equality comparison rather than a passed argument.
    /// </summary>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        IMethodSymbol? method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        AnalyzeArguments(context, method, invocation.ArgumentList.Arguments, allowAttribute);

        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: nameof(object.Equals) } member)
        {
            ReportIfConstantString(context, member.Expression, allowAttribute);
        }
    }

    /// <summary>
    /// Flags a string literal passed as a constructor argument — <c>new Foo("a")</c>, <c>new("a")</c> — under the
    /// same per-parameter opt-outs as any call, including the built-in Exception <c>message</c> carve-out.
    /// </summary>
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        BaseObjectCreationExpressionSyntax creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is null)
        {
            return;
        }

        IMethodSymbol? constructor = context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol as IMethodSymbol;
        AnalyzeArguments(context, constructor, creation.ArgumentList.Arguments, allowAttribute);
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
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        INamedTypeSymbol? allowAttribute)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            ArgumentSyntax argument = arguments[i];
            if (method is not null
                && ResolveParameter(method, argument, i) is { } parameter
                && (HasAllowAttribute(parameter, allowAttribute) || IsExemptWellKnownParameter(method, parameter)))
            {
                continue;
            }

            ReportIfConstantString(context, argument.Expression, allowAttribute);
        }
    }

    /// <summary>
    /// Maps an argument to the parameter it binds to — by name for a named argument, otherwise by position,
    /// falling back to a trailing <c>params</c> parameter for extra positional arguments.
    /// </summary>
    private static IParameterSymbol? ResolveParameter(IMethodSymbol method, ArgumentSyntax argument, int index)
    {
        if (argument.NameColon?.Name.Identifier.ValueText is { } name)
        {
            return method.Parameters.FirstOrDefault(p => p.Name == name);
        }

        if (index < method.Parameters.Length)
        {
            return method.Parameters[index];
        }

        IParameterSymbol? last = method.Parameters.LastOrDefault();
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
            ("format", nameof(object.ToString) or nameof(System.DateTime.ParseExact) or nameof(System.DateTime.TryParseExact)) => true,
            _ => false,
        };

    /// <summary>True when <paramref name="type"/> is <see cref="System.Exception"/> or derives from it.</summary>
    private static bool DerivesFromException(ITypeSymbol? type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == typeof(System.Exception).FullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Flags a string literal used as an indexer key — <c>map["a"]</c>, the null-conditional <c>map?["a"]</c>,
    /// and the <c>["a"] = …</c> form inside a collection/dictionary initializer.
    /// </summary>
    private static void AnalyzeElementAccess(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        SeparatedSyntaxList<ArgumentSyntax> arguments = context.Node switch
        {
            ElementAccessExpressionSyntax element => element.ArgumentList.Arguments,
            ImplicitElementAccessSyntax implicitElement => implicitElement.ArgumentList.Arguments,
            ElementBindingExpressionSyntax binding => binding.ArgumentList.Arguments,
            _ => default,
        };
        foreach (ArgumentSyntax argument in arguments)
        {
            ReportIfConstantString(context, argument.Expression, allowAttribute);
        }
    }

    /// <summary>Flags a string literal in a constant pattern — <c>is "a"</c>, <c>"a" =&gt; …</c>, <c>case "a" when …</c>.</summary>
    private static void AnalyzeConstantPattern(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        ConstantPatternSyntax pattern = (ConstantPatternSyntax)context.Node;
        ReportIfConstantString(context, pattern.Expression, allowAttribute);
    }

    /// <summary>Flags a string literal in a classic switch label — <c>case "a":</c>.</summary>
    private static void AnalyzeCaseLabel(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        CaseSwitchLabelSyntax label = (CaseSwitchLabelSyntax)context.Node;
        ReportIfConstantString(context, label.Value, allowAttribute);
    }

    /// <summary>
    /// Flags a string literal assigned to a member in an object initializer — the <c>Prop = "literal"</c> form of
    /// <c>new Foo { Prop = "literal" }</c> — mirroring the treatment of a constructor argument. Only assignments that
    /// live directly in an object initializer are considered (an ordinary <c>x = "literal"</c> statement is not a
    /// magic string — its meaning is the variable it fills). The assigned member is resolved so the opt-out can key on
    /// it: a member marked <c>[AllowMagicStrings]</c>, or a well-known display-prose property
    /// (<see cref="IsExemptWellKnownProperty"/>), is skipped.
    /// </summary>
    private static void AnalyzeInitializerAssignment(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        AssignmentExpressionSyntax assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Parent is not InitializerExpressionSyntax { RawKind: (int)SyntaxKind.ObjectInitializerExpression })
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is { } member
            && (HasAllowAttribute(member, allowAttribute) || IsExemptWellKnownProperty(member.Name)))
        {
            return;
        }

        if (GetCollectionElements(assignment.Right) is { } elements)
        {
            foreach (ExpressionSyntax element in elements)
            {
                ReportIfConstantString(context, element, allowAttribute);
            }

            return;
        }

        ReportIfConstantString(context, assignment.Right, allowAttribute);
    }

    /// <summary>
    /// When <paramref name="expression"/> is an array or collection creation — <c>new[] { … }</c>,
    /// <c>new string[] { … }</c>, <c>new List&lt;string&gt; { … }</c>, or a collection expression <c>[ … ]</c> — returns
    /// its element expressions, so a literal element (<c>Choices = new[] { "median", … }</c>) is flagged exactly like a
    /// scalar assignment. Returns <see langword="null"/> for any other RHS, which the caller flags directly. A nested
    /// <b>object</b> initializer among the elements is left to the object-creation/initializer visitors that reach it
    /// directly — here it is simply not literal-built constant content, so <see cref="ReportIfConstantString"/> passes it over.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<ExpressionSyntax>? GetCollectionElements(ExpressionSyntax expression) =>
        expression switch
        {
            ImplicitArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions,
            ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions,
            ObjectCreationExpressionSyntax { Initializer.RawKind: (int)SyntaxKind.CollectionInitializerExpression } creation
                => creation.Initializer!.Expressions,
            CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
            _ => null,
        };

    /// <summary>
    /// True for a well-known display-prose property whose string value is human-readable UI text, not a magic
    /// identifier — the object-initializer mirror of <see cref="IsExemptWellKnownParameter"/>. Matched by the member's
    /// simple name so the exemption follows the property across every construction site, keeping the churn off the
    /// display strings (a <c>ParamSpec.Label</c>, a card <c>Summary</c>) without annotating every schema. Identifier
    /// properties (<c>Key</c>, <c>Choices</c>, a node's widget-value input) are deliberately absent — those ARE magic
    /// strings and must be const-extracted or annotated <c>[AllowMagicStrings]</c>.
    /// </summary>
    private static bool IsExemptWellKnownProperty(string name) => name switch
    {
        "Label" or "Help" or "Summary" or "Hint" or "Note" or "Notes" or "Placeholder"
            or "Description" or "Title" or "Text" or "Tooltip" => true,
        _ => false,
    };

    /// <summary>
    /// Reports at <paramref name="expression"/> when it is literal-built constant string content and no enclosing
    /// scope opts out. This includes ordinary/raw/u8 literals, constant interpolated strings, and constant-folded
    /// concatenations. A named const identifier is deliberately not reported: extracting a name is the requested fix.
    /// </summary>
    private static void ReportIfConstantString(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax? expression,
        INamedTypeSymbol? allowAttribute)
    {
        if (expression is null)
        {
            return;
        }

        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        bool utf8Literal = expression.IsKind(SyntaxKind.Utf8StringLiteralExpression);
        bool literalBuilt = expression.IsKind(SyntaxKind.StringLiteralExpression)
            || expression is InterpolatedStringExpressionSyntax
            || expression.IsKind(SyntaxKind.AddExpression);
        if (!utf8Literal && (!literalBuilt
            || context.SemanticModel.GetConstantValue(expression, context.CancellationToken) is not { HasValue: true, Value: string }))
        {
            return;
        }

        if (IsExempt(context.ContainingSymbol, allowAttribute))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation(), expression.ToString()));
    }

    /// <summary>
    /// Reports <c>IMGSTR002</c> when an <c>[AllowMagicStrings]</c> application carries an empty or whitespace
    /// justification. A missing justification is left to the compiler — the constructor's required parameter
    /// already makes a bare <c>[AllowMagicStrings]</c> a build error.
    /// </summary>
    private static void AnalyzeAllowAttribute(SyntaxNodeAnalysisContext context, INamedTypeSymbol? allowAttribute)
    {
        AttributeSyntax attribute = (AttributeSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol ctor)
        {
            return;
        }

        if (allowAttribute is null || !SymbolEqualityComparer.Default.Equals(ctor.ContainingType, allowAttribute))
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

    /// <summary>True when <paramref name="symbol"/> carries <c>[AllowMagicStrings]</c> directly.</summary>
    private static bool HasAllowAttribute(ISymbol symbol, INamedTypeSymbol? allowAttribute) =>
        allowAttribute is not null
        && symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, allowAttribute));

    /// <summary>
    /// True when <paramref name="symbol"/> or any symbol enclosing it carries <c>[AllowMagicStrings]</c>. Walking
    /// the containing chain is what makes a class-level attribute cover every literal in its members, and a
    /// method-level one cover just that body.
    /// </summary>
    private static bool IsExempt(ISymbol? symbol, INamedTypeSymbol? allowAttribute)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            // The canonical attribute does not target namespaces or assemblies. Stop before them so a lookalike
            // assembly-level attribute can never turn a lexical exemption walk into a whole-compilation bypass.
            if (current is INamespaceSymbol or IAssemblySymbol)
            {
                break;
            }

            if (HasAllowAttribute(current, allowAttribute))
            {
                return true;
            }
        }

        return false;
    }
}
