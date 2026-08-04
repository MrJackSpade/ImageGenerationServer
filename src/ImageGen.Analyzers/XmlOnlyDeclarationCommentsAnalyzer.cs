using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ImageGen.Analyzers;

/// <summary>
/// Reports a plain <c>//</c> or <c>/* */</c> comment that is attached to a type or member
/// declaration — either written on the line(s) directly above it, or trailing it inline on the
/// same line. Such "declaration-level" comments must instead be XML documentation comments
/// (<c>///</c>). Comments inside method, accessor, or other statement bodies are left alone.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XmlOnlyDeclarationCommentsAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "IMGDOC001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Declaration comments must be XML documentation comments",
        messageFormat: "Use an XML documentation comment (///) on this declaration, not a // or /* */ comment",
        category: "Documentation",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A comment directly above, or trailing inline on, a type or member declaration must be an "
            + "XML documentation comment (///). Plain // and /* */ comments are only permitted inside method and "
            + "accessor bodies.");

    /// <summary>The declaration kinds whose attached comments are required to be XML.</summary>
    private static readonly ImmutableArray<SyntaxKind> DeclarationKinds = ImmutableArray.Create(
        SyntaxKind.ClassDeclaration,
        SyntaxKind.StructDeclaration,
        SyntaxKind.InterfaceDeclaration,
        SyntaxKind.EnumDeclaration,
        SyntaxKind.RecordDeclaration,
        SyntaxKind.RecordStructDeclaration,
        SyntaxKind.DelegateDeclaration,
        SyntaxKind.MethodDeclaration,
        SyntaxKind.ConstructorDeclaration,
        SyntaxKind.DestructorDeclaration,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.ConversionOperatorDeclaration,
        SyntaxKind.PropertyDeclaration,
        SyntaxKind.IndexerDeclaration,
        SyntaxKind.EventDeclaration,
        SyntaxKind.EventFieldDeclaration,
        SyntaxKind.FieldDeclaration,
        SyntaxKind.EnumMemberDeclaration);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeDeclaration, DeclarationKinds);
    }

    /// <summary>
    /// Inspects the trivia immediately above the declaration (leading) and trailing it inline on the
    /// same line, flagging any non-XML comment. Body comments live on inner statement tokens and are
    /// therefore never visited here.
    /// </summary>
    private static void AnalyzeDeclaration(SyntaxNodeAnalysisContext context)
    {
        SyntaxNode node = context.Node;

        foreach (SyntaxTrivia trivia in node.GetLeadingTrivia())
        {
            ReportIfPlainComment(context, trivia);
        }

        foreach (SyntaxTrivia trivia in node.GetTrailingTrivia())
        {
            ReportIfPlainComment(context, trivia);
        }
    }

    /// <summary>
    /// Reports the diagnostic when <paramref name="trivia"/> is a plain (non-XML) comment.
    /// </summary>
    /// <remarks>
    /// XML doc comments normally lex as <see cref="SyntaxKind.SingleLineDocumentationCommentTrivia"/> /
    /// <see cref="SyntaxKind.MultiLineDocumentationCommentTrivia"/>, which we never match. But a project
    /// built without documentation parsing (DocumentationMode.None — the default when
    /// GenerateDocumentationFile is not set) lexes <c>///</c> and <c>/** */</c> as ordinary comment
    /// trivia instead. So for the ordinary comment kinds we still inspect the text and exempt the
    /// doc-comment forms, leaving only true <c>//</c> and <c>/* */</c> comments to flag.
    /// </remarks>
    private static void ReportIfPlainComment(SyntaxNodeAnalysisContext context, SyntaxTrivia trivia)
    {
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
        {
            if (!IsSingleLineDocForm(trivia.ToString()))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, trivia.GetLocation()));
            }
        }
        else if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
        {
            if (!IsMultiLineDocForm(trivia.ToString()))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, trivia.GetLocation()));
            }
        }
    }

    /// <summary>True when the text is a <c>///</c> doc comment (exactly three slashes, not <c>////</c>).</summary>
    private static bool IsSingleLineDocForm(string text)
        => text.Length >= 3
            && text[0] == '/' && text[1] == '/' && text[2] == '/'
            && (text.Length == 3 || text[3] != '/');

    /// <summary>True when the text is a <c>/** */</c> doc comment (leading <c>/**</c>, but not <c>/**/</c>).</summary>
    private static bool IsMultiLineDocForm(string text)
        => text.Length >= 4
            && text[0] == '/' && text[1] == '*' && text[2] == '*'
            && text[3] != '/';
}
