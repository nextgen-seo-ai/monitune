namespace MonitorTune;

/// <summary>
/// Тексты окна «О программе» на языке интерфейса.
///
/// Эти блоки длинные и структурированные (абзацы, списки, пары вопрос-ответ),
/// поэтому лежат обычными строками в AboutContentRu / AboutContentEn, а не в .resw:
/// в ресурсном файле такой текст читается и правится заметно хуже.
/// </summary>
internal static class AboutContent
{
    static bool Ru => Loc.ActiveLanguage.StartsWith("ru", System.StringComparison.OrdinalIgnoreCase);

    public static string ShortPitch => Ru ? AboutContentRu.ShortPitch : AboutContentEn.ShortPitch;
    public static string About      => Ru ? AboutContentRu.About      : AboutContentEn.About;
    public static string HowItWorks => Ru ? AboutContentRu.HowItWorks : AboutContentEn.HowItWorks;
    public static string Privacy    => Ru ? AboutContentRu.Privacy    : AboutContentEn.Privacy;

    public static string[] Features => Ru ? AboutContentRu.Features : AboutContentEn.Features;

    public static (string Q, string A)[] Faq => Ru ? AboutContentRu.Faq : AboutContentEn.Faq;

    public static string LicenseTitle => Ru ? AboutContentRu.LicenseTitle : AboutContentEn.LicenseTitle;
    public static string LicenseText  => Ru ? AboutContentRu.LicenseText  : AboutContentEn.LicenseText;
}
