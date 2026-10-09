namespace Kakitome.Domain.Rendering;

/// <summary>
/// Labels used inside exported Library files. Files are written in the transcript's language so they read
/// naturally outside the app; Japanese is the primary language, English the fallback.
/// </summary>
public sealed record ArtifactLabels(
    string Project,
    string Recorded,
    string Duration,
    string Language,
    string Speaker,
    string Overview,
    string KeyPoints,
    string Decisions,
    string ActionItems,
    string Questions,
    string Topics,
    string Owner,
    string Due,
    string GeneratedBy,
    string ListSeparator,
    string PlainHeadingOpen,
    string PlainHeadingClose,
    string PlainBullet)
{
    public static readonly ArtifactLabels Japanese = new(
        Project: "プロジェクト",
        Recorded: "録音日時",
        Duration: "長さ",
        Language: "言語",
        Speaker: "話者",
        Overview: "概要",
        KeyPoints: "要点",
        Decisions: "決定事項",
        ActionItems: "アクションアイテム",
        Questions: "質問・未解決事項",
        Topics: "トピック",
        Owner: "担当",
        Due: "期限",
        GeneratedBy: "Kakitome がローカルで生成",
        ListSeparator: "、",
        PlainHeadingOpen: "【",
        PlainHeadingClose: "】",
        PlainBullet: "・");

    public static readonly ArtifactLabels English = new(
        Project: "Project",
        Recorded: "Recorded",
        Duration: "Duration",
        Language: "Language",
        Speaker: "Speaker",
        Overview: "Overview",
        KeyPoints: "Key points",
        Decisions: "Decisions",
        ActionItems: "Action items",
        Questions: "Open questions",
        Topics: "Topics",
        Owner: "Owner",
        Due: "Due",
        GeneratedBy: "Generated locally by Kakitome",
        ListSeparator: ", ",
        PlainHeadingOpen: "[",
        PlainHeadingClose: "]",
        PlainBullet: "- ");

    public static ArtifactLabels For(string? language) =>
        language is not null && language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? Japanese : English;
}
