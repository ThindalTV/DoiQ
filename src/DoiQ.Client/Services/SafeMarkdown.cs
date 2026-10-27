using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace DoiQ.Client.Services;

/// <summary>
/// Renders user and agent markdown (task text often comes from email via an agent) without raw HTML and with
/// links and images limited to http, https and mailto. Anything else, such as javascript:, data: or vbscript:,
/// becomes a dead "#" link.
/// </summary>
public static class SafeMarkdown
{
    // Agents write GitHub-flavoured markdown, so tables, ~~strikethrough~~ and - [ ] task lists render too.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .Build();
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    public static MarkupString Render(string? text)
    {
        var doc = Markdown.Parse(text ?? "", Pipeline);
        foreach (var link in doc.Descendants<LinkInline>())
            if (!IsSafeUrl(link.Url)) link.Url = "#";
        foreach (var link in doc.Descendants<AutolinkInline>())
            if (!link.IsEmail && !IsSafeUrl(link.Url)) link.Url = "#";
        return new MarkupString(doc.ToHtml(Pipeline));
    }

    /// <summary>Relative URLs are fine; an absolute one must use an allowed scheme.</summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        // Browsers ignore whitespace and control characters inside a scheme ("java\tscript:"), so drop them first.
        var compact = new string(url.Where(c => c > ' ').ToArray());
        var colon = compact.IndexOf(':');
        if (colon < 0) return true;
        var firstSeparator = compact.IndexOfAny(['/', '?', '#']);
        if (firstSeparator >= 0 && firstSeparator < colon) return true;   // e.g. "/path:with-colon"
        var scheme = compact[..colon];
        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }
}
