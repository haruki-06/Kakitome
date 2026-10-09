using System.Text;
using Kakitome.Domain.Library;
using Kakitome.Domain.Summaries;

namespace Kakitome.Domain.Rendering;

/// <summary>Renders <c>summary.md</c> / <c>summary.txt</c> from the structured summary. Empty sections are omitted.</summary>
public static class SummaryRenderer
{
    public static string ToMarkdown(RecordingMetadata metadata, SummaryDocument summary)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(summary);

        var labels = ArtifactLabels.For(summary.Language ?? metadata.Language);
        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(summary.Title) ? metadata.Title : summary.Title;
        sb.Append("# ").Append(TranscriptRenderer.SingleLine(title)).Append("\n\n");
        sb.Append("- ").Append(labels.Project).Append(": ").Append(TranscriptRenderer.SingleLine(metadata.Project)).Append('\n');
        sb.Append("- ").Append(labels.Recorded).Append(": ")
            .Append(TimeFormat.DateTime(metadata.RecordedAt ?? metadata.CreatedAt)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(summary.Overview))
        {
            sb.Append("## ").Append(labels.Overview).Append("\n\n").Append(summary.Overview.Trim()).Append("\n\n");
        }

        AppendItems(sb, labels.KeyPoints, summary.KeyPoints, "- ");
        AppendItems(sb, labels.Decisions, summary.Decisions, "- ");

        if (summary.ActionItems.Count > 0)
        {
            sb.Append("## ").Append(labels.ActionItems).Append("\n\n");
            foreach (var item in summary.ActionItems)
            {
                sb.Append("- [ ] ").Append(ItemText(item)).Append(ActionDetails(item, labels)).Append('\n');
            }

            sb.Append('\n');
        }

        AppendItems(sb, labels.Questions, summary.Questions, "- ");

        if (summary.Topics.Count > 0)
        {
            sb.Append("## ").Append(labels.Topics).Append("\n\n")
                .Append(string.Join(labels.ListSeparator, summary.Topics.Select(TranscriptRenderer.SingleLine)))
                .Append("\n\n");
        }

        sb.Append("---\n\n*").Append(Footer(summary, labels)).Append("*\n");
        return TranscriptRenderer.TrimTrailingBlankLines(sb);
    }

    /// <summary>Reading text without timestamps (<see cref="ReadableText"/>).</summary>
    public static string ToPlainText(RecordingMetadata metadata, SummaryDocument summary) => ReadableText.Summary(metadata, summary);

    private static void AppendItems(StringBuilder sb, string heading, List<SummaryItem> items, string bullet)
    {
        if (items.Count == 0)
        {
            return;
        }

        sb.Append("## ").Append(heading).Append("\n\n");
        foreach (var item in items)
        {
            sb.Append(bullet).Append(ItemText(item)).Append('\n');
        }

        sb.Append('\n');
    }

    private static void AppendPlainHeading(StringBuilder sb, ArtifactLabels labels, string heading) =>
        sb.Append(labels.PlainHeadingOpen).Append(heading).Append(labels.PlainHeadingClose).Append('\n');

    private static string ItemText(SummaryItem item)
    {
        var text = TranscriptRenderer.SingleLine(item.Text);
        return item.AtSeconds is { } at ? $"{text} {TimeFormat.Stamp(at)}" : text;
    }

    private static string ActionDetails(ActionItem item, ArtifactLabels labels)
    {
        var parts = new List<string>();
        if (!ReadableText.IsMissing(item.Owner))
        {
            parts.Add($"{labels.Owner}: {TranscriptRenderer.SingleLine(item.Owner)}");
        }

        if (!ReadableText.IsMissing(item.Due))
        {
            parts.Add($"{labels.Due}: {TranscriptRenderer.SingleLine(item.Due)}");
        }

        return parts.Count == 0 ? string.Empty : $" ({string.Join(labels.ListSeparator, parts)})";
    }

    private static string Footer(SummaryDocument summary, ArtifactLabels labels)
    {
        var engine = summary.Generation.Engine;
        var model = string.IsNullOrWhiteSpace(engine.Model) ? engine.Provider : $"{engine.Provider} / {engine.Model}";
        return $"{labels.GeneratedBy} ({model}, {TimeFormat.DateTime(summary.Generation.CreatedAt)})";
    }
}
