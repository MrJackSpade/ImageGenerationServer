using Markdig;
using System.Text;

namespace ImageGen.Web.Help;

/// <summary>One collapsible section of the help page: a level-2 (##) heading and the HTML its body rendered to.</summary>
public sealed record HelpSection(string Title, string HtmlBody);

public sealed class HelpPageViewModel
{
    /// <summary>Everything before the first ## heading (the # title + any intro), rendered to HTML.</summary>
    public required string IntroHtml { get; init; }

    /// <summary>The ## sections, in document order. The view wraps each in a &lt;details&gt; that starts collapsed.</summary>
    public required IReadOnlyList<HelpSection> Sections { get; init; }
}

/// <summary>
/// Renders the deployed help.md into an intro block plus one collapsible section per level-2 (##) heading. Each part
/// is rendered independently (Markdig, advanced extensions) so the view can wrap sections in &lt;details&gt; elements —
/// which start collapsed on every load with no persisted state. ### and deeper headings render inside their section.
/// </summary>
public static class HelpMarkdown
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static class Markers
    {
        /// <summary>Windows line ending, normalized to <see cref="Lf"/> before the document is split into lines.</summary>
        public const string CrLf = "\r\n";

        /// <summary>The newline the document is normalized to and split on.</summary>
        public const string Lf = "\n";

        /// <summary>Backtick code-fence marker; a line opening or closing one never splits a section.</summary>
        public const string BacktickFence = "```";

        /// <summary>Tilde code-fence marker.</summary>
        public const string TildeFence = "~~~";

        /// <summary>Level-2 heading prefix at column 0 that opens a new collapsible section.</summary>
        public const string SectionHeadingPrefix = "## ";
    }

    public static HelpPageViewModel Parse(string markdown)
    {
        string[] lines = markdown.Replace(Markers.CrLf, Markers.Lf).Replace('\r', '\n').Split('\n');
        StringBuilder intro = new StringBuilder();
        List<(string Title, StringBuilder Body)> sections = new List<(string Title, StringBuilder Body)>();
        (string Title, StringBuilder Body)? cur = null;
        bool inFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            bool isFence = trimmed.StartsWith(Markers.BacktickFence, StringComparison.Ordinal)
                       || trimmed.StartsWith(Markers.TildeFence, StringComparison.Ordinal);

            // A ## at column 0, outside a code fence, opens a new section. Fence lines never split (a shell comment
            // like "## note" inside ``` stays put).
            if (!inFence && !isFence && line.StartsWith(Markers.SectionHeadingPrefix, StringComparison.Ordinal))
            {
                if (cur is not null)
                    sections.Add(cur.Value);
                cur = (line[3..].Trim(), new StringBuilder());
                continue;
            }

            if (isFence)
                inFence = !inFence;

            if (cur is not null)
                cur.Value.Body.Append(line).Append('\n');
            else
                intro.Append(line).Append('\n');
        }
        if (cur is not null)
            sections.Add(cur.Value);

        return new HelpPageViewModel
        {
            IntroHtml = Markdown.ToHtml(intro.ToString(), Pipeline),
            Sections = sections
                .Select(s => new HelpSection(s.Title, Markdown.ToHtml(s.Body.ToString(), Pipeline)))
                .ToList(),
        };
    }
}
