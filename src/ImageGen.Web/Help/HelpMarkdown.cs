using System.Text;
using Markdig;

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

    public static HelpPageViewModel Parse(string markdown)
    {
        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var intro = new StringBuilder();
        var sections = new List<(string Title, StringBuilder Body)>();
        (string Title, StringBuilder Body)? cur = null;
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var isFence = trimmed.StartsWith("```", StringComparison.Ordinal)
                       || trimmed.StartsWith("~~~", StringComparison.Ordinal);

            // A ## at column 0, outside a code fence, opens a new section. Fence lines never split (a shell comment
            // like "## note" inside ``` stays put).
            if (!inFence && !isFence && line.StartsWith("## ", StringComparison.Ordinal))
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
