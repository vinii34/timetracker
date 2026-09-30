using System.Text.RegularExpressions;

namespace TimeTracker.Core.Services;

/// <summary>
/// Un nom de tâche tiré d'un titre de fenêtre, <b>sur le poste</b> : c'est la proposition de
/// nom quand l'IA est coupée, ou en attendant sa réponse. « RE: Velmora - mapping INVOIC.xlsx -
/// Excel » donne « Velmora mapping INVOIC » : la marque de l'application, le préfixe de
/// réponse et l'extension ne nomment rien. Le résultat est pré-rempli dans un champ modifiable,
/// jamais enregistré sans que l'utilisateur ait cliqué.
/// </summary>
public static class ActivityNaming
{
    private static readonly Regex Separators = new(@"\s+[-–—|·]\s+", RegexOptions.Compiled);
    private static readonly Regex ReplyPrefix = new(@"^\s*((re|tr|fw|fwd|aw|réf)\s*:\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Extension = new(@"\.(xlsx?|xlsm|docx?|pdf|csv|txt|xml|json|edi|pptx?|zip|msg|eml)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CopySuffix = new(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"\S+@\S+", RegexOptions.Compiled);
    private static readonly Regex MorePages = new(@"\s+(and \d+ more pages?|et \d+ autres? pages?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Segments qui désignent l'application, le navigateur ou un profil, jamais un travail.</summary>
    private static readonly HashSet<string> AppSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Edge", "Edge", "Google Chrome", "Chrome", "Mozilla Firefox", "Firefox", "Excel", "Word",
        "PowerPoint", "Outlook", "OneNote", "Microsoft Teams", "Teams", "Slack", "Zoom", "Zoom Workplace",
        "Notepad", "Bloc-notes", "Explorateur de fichiers", "File Explorer", "Visual Studio Code",
        "Visual Studio", "Personnel", "Personal", "Work", "Travail", "Inbox", "Boîte de réception",
        "Nouvel onglet", "New Tab", "Mode protégé", "Protected View", "Compatibility Mode", "Mode de compatibilité"
    };

    /// <summary>Nom proposé d'après un titre, ou null si le titre ne nomme rien d'exploitable.</summary>
    public static string? FromTitle(string title, string process = "")
    {
        var segments = Separators.Split(MeetingWindowProbe.CleanTitle(title))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !AppSegments.Contains(s)
                        && !s.Equals(process, StringComparison.OrdinalIgnoreCase)
                        && !Email.IsMatch(s)
                        && !s.StartsWith("Profil", StringComparison.OrdinalIgnoreCase)
                        && !s.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (segments.Count == 0) return null;

        // Un premier segment court (« RE: Velmora ») appelle le suivant (« mapping INVOIC.xlsx »).
        var name = Clean(segments[0]);
        if (name.Length < 12 && segments.Count > 1) name = $"{name} {Clean(segments[1])}".Trim();

        if (TaskSuggester.Words(name).Count == 0) return null;
        if (name.Length <= 60) return name;
        int cut = name.LastIndexOf(' ', 60);
        return (cut > 30 ? name[..cut] : name[..60]).TrimEnd();
    }

    private static string Clean(string segment)
    {
        var s = ReplyPrefix.Replace(segment, "");
        s = MorePages.Replace(s, "");
        s = Extension.Replace(s, "");
        s = CopySuffix.Replace(s, "");
        s = s.Replace('_', ' ');
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', ':', '.');
    }
}
