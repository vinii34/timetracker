using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Parcourt les fenêtres visibles à la recherche d'une réunion.
///
/// Deux natures d'indices, volontairement distinguées (voir <see cref="MeetingSignal.Conclusive"/>) :
///
/// — <b>concluants</b> : la vue de réunion Zoom, reconnaissable à sa classe de fenêtre dédiée
///   ou à son process hôte <c>CptHost</c>. Zoom n'ouvre ces fenêtres que pendant une réunion :
///   elles suffisent à conclure, y compris micro coupé.
///
/// — <b>qualifiants</b> : tous les titres. Ils ne concluent <b>jamais</b>, même dans une
///   application de réunion : « Appels | Microsoft Teams » est l'onglet *Appels* et non un
///   appel, et « Réunion du 12 » dans Edge n'est qu'un document ouvert. Une bascule à tort
///   pollue la timesheet, alors que rater le titre ne coûte qu'un libellé générique. C'est donc
///   la sonde micro qui déclenche, et le titre qui nomme.
/// </summary>
public sealed class MeetingWindowProbe : IMeetingProbe
{
    private readonly AppSettings _settings;

    public MeetingWindowProbe(AppSettings settings) => _settings = settings;

    public string Name => "fenêtres";

    /// <summary>Applications de réunion natives : leur fenêtre est retenue quel que soit son titre.</summary>
    private static readonly string[] NativeMeetingProcesses =
        { "ms-teams", "teams", "zoom", "cpthost", "slack", "webex", "lync" };

    /// <summary>
    /// Classes de fenêtre que Zoom n'ouvre que pendant une réunion.
    ///
    /// Corrigées d'après la collecte du 2026-07-27 au 31 : les classes supposées à l'aveugle
    /// (<c>ZPContentViewWndClass</c>, <c>ZPFloatVideoWndClass</c>) ne sont <b>jamais apparues</b>
    /// en cinq jours, pas plus que le process <c>CptHost</c> — ce Zoom héberge la réunion dans
    /// <c>Zoom.exe</c>. Celles-ci ont été relevées sur 50 et 53 relevés, <b>toutes à l'intérieur</b>
    /// d'une réunion confirmée par le micro, aucune en dehors : elles peuvent conclure seules,
    /// ce qui rattrape la réunion Zoom rejointe micro coupé.
    /// </summary>
    private static readonly string[] ConclusiveClasses =
        { "ConfMultiTabContentWndClass", "ZPToolBarParentWndClass" };

    /// <summary>
    /// Process qui n'existe que le temps d'une réunion Zoom (son hôte). Conservé pour les
    /// versions de Zoom qui l'utilisent encore : jamais observé pendant la collecte.
    /// </summary>
    private const string ZoomMeetingProcess = "cpthost";

    /// <summary>
    /// Titres retenus comme libellé de réunion. Aucun n'est concluant à lui seul :
    /// voir l'en-tête de la classe.
    /// </summary>
    private static readonly Regex[] TitlePatterns =
    {
        new(@"\bR[ée]unions?\b", RegexOptions.IgnoreCase),
        new(@"\bMeeting\b", RegexOptions.IgnoreCase),
        new(@"\bMeet\s*[-–—]", RegexOptions.IgnoreCase),   // onglet Meet rejoint : « Meet — abc-defg-hij »
        new(@"\bGoogle Meet\b", RegexOptions.IgnoreCase),
        new(@"\bMicrosoft Teams\b", RegexOptions.IgnoreCase),
        new(@"\bZoom\b", RegexOptions.IgnoreCase),
        new(@"\bWebex\b", RegexOptions.IgnoreCase),
        // Slack nomme sa fenêtre « Huddle: @Camille Durand - Contoso - Slack 🎤 » pendant
        // un huddle. Ancré en tête, exprès : « Slack - Huddle Preview » n'est qu'un survol.
        new(@"^Huddle\s*:", RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// Titres qui reconnaissent l'application mais ne nomment aucune réunion : ils ne doivent
    /// jamais l'emporter sur un vrai libellé (voir <c>MeetingDetector.BestTitle</c>).
    ///
    /// Motivé par la collecte : la fenêtre principale de Zoom, « Zoom Workplace », reste ouverte
    /// toute la journée (relevée 559 fois, dont 379 hors de toute réunion). Comme le libellé
    /// retenu était le titre le plus long, elle a nommé « Zoom Workplace » des réunions qui se
    /// tenaient ailleurs — dont un appel dans un onglet du navigateur le 2026-07-27.
    /// </summary>
    private static readonly Regex[] GenericTitles =
    {
        new(@"^Zoom\b", RegexOptions.IgnoreCase),              // « Zoom Workplace », « Zoom Meeting »
        new(@"^Microsoft Teams\b", RegexOptions.IgnoreCase),
        new(@"^Sharing control bar\b", RegexOptions.IgnoreCase),
        new(@"^Annotation\b", RegexOptions.IgnoreCase),
        new(@"^(?:Meeting chat|Screen sharing meeting controls|End meeting or leave meeting\?)$",
            RegexOptions.IgnoreCase),
    };

    /// <summary>Le titre reconnaît une application de réunion sans en nommer aucune.</summary>
    public static bool IsGenericTitle(string title) => GenericTitles.Any(r => r.IsMatch(title));

    /// <summary>
    /// Le « titre » n'est que l'écho du nom de classe de la fenêtre : ce n'en est pas un.
    ///
    /// Zoom titre sa barre d'outils <c>ZPToolBarParentWnd</c>, du nom de sa classe
    /// <c>ZPToolBarParentWndClass</c> (idem <c>VideoFrameWnd</c> / <c>VideoFrameWndClass</c>).
    /// Depuis que cette classe conclut, ce faux titre traversait tout : ne correspondant à aucun
    /// <see cref="GenericTitles"/>, il passait <b>devant</b> « Zoom Meeting » dans
    /// <c>MeetingDetector.BestTitle</c> et a nommé 5 des 13 réunions de la semaine du 2026-08-03.
    /// Écarté ici plutôt qu'ajouté aux génériques : la règle vaut pour les fenêtres à venir.
    /// </summary>
    public static bool EchoesClassName(string title, string className) =>
        title.Length > 0
        && (className.Equals(title, StringComparison.OrdinalIgnoreCase)
            || className.Equals(title + "Class", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<MeetingSignal> Poll()
    {
        var apps = _settings.MeetingAppList();
        var found = new List<MeetingSignal>();

        foreach (var w in VisibleWindows())
        {
            bool nativeApp = NativeMeetingProcesses.Any(
                p => w.Process.Contains(p, StringComparison.OrdinalIgnoreCase));
            bool knownApp = nativeApp || apps.Any(
                p => w.Process.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!knownApp) continue;

            var title = CleanTitle(w.Title);
            bool conclusive =
                w.Process.Contains(ZoomMeetingProcess, StringComparison.OrdinalIgnoreCase)
                || ConclusiveClasses.Any(c => w.ClassName.Equals(c, StringComparison.OrdinalIgnoreCase));

            if (!conclusive && !TitlePatterns.Any(r => r.IsMatch(title))) continue;

            // Reconnaître et nommer sont deux usages distincts : le motif ci-dessus a besoin de
            // la marque de l'application (« … | Microsoft Teams »), le libellé n'en veut pas.
            var label = ShortLabel(title);
            if (EchoesClassName(label, w.ClassName)) label = "";
            found.Add(new MeetingSignal(MeetingSource.Window, w.Process,
                                        label.Length > 0 ? label : null, conclusive));
        }

        return found;
    }

    /// <summary>Une fenêtre visible relevée par l'énumération (aussi utilisée par le diagnostic).</summary>
    public readonly record struct WindowInfo(string Process, string ClassName, string Title);

    /// <summary>Processus dont les fenêtres intéressent le journal de diagnostic.</summary>
    private static readonly string[] WatchedProcesses =
    {
        "ms-teams", "teams", "zoom", "cpthost", "slack", "webex", "lync",
        "msedge", "chrome", "firefox", "chromium", "brave", "opera"
    };

    private static readonly string[] BrowserProcesses =
        { "msedge", "chrome", "firefox", "chromium", "brave", "opera" };

    /// <summary>Titre remplacé dans le journal quand il n'a pas à en sortir.</summary>
    public const string MaskedTitle = "(titre masqué)";

    /// <summary>
    /// Fenêtres à consigner dans <see cref="MeetingTrace"/> : celles des applications de réunion,
    /// <b>y compris quand leur titre ne correspond à aucun motif</b> — c'est justement en lisant
    /// ces titres-là qu'on découvre ce qu'il faut reconnaître.
    ///
    /// Un titre de navigateur n'est conservé que s'il ressemble à une réunion ou si ce navigateur
    /// capte le micro : sans ce filtre, une semaine de trace serait une semaine d'historique de
    /// navigation, ce qui n'a pas à quitter le poste.
    /// </summary>
    public static IReadOnlyList<WindowInfo> CandidateWindows(IReadOnlyList<string> micApps)
    {
        var result = new List<WindowInfo>();

        foreach (var w in VisibleWindows())
        {
            if (!WatchedProcesses.Any(p => w.Process.Contains(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            var title = CleanTitle(w.Title);
            bool browser = BrowserProcesses.Any(
                p => w.Process.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (browser)
            {
                bool micActive = micApps.Any(
                    a => a.Contains(w.Process, StringComparison.OrdinalIgnoreCase));
                if (!micActive && !TitlePatterns.Any(r => r.IsMatch(title))) title = MaskedTitle;
            }

            result.Add(new WindowInfo(w.Process, w.ClassName, title));
        }

        return result;
    }

    /// <summary>
    /// Fenêtres visibles et titrées, avec leur classe et leur process.
    /// Exposé pour <c>--meetingprobe</c> : c'est en lisant ce relevé pendant une vraie réunion
    /// qu'on ajuste les motifs ci-dessus.
    /// </summary>
    public static IReadOnlyList<WindowInfo> VisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var processNames = new Dictionary<uint, string>();

        try
        {
            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle)) return true;

                var title = GetText(handle, GetWindowText, GetWindowTextLength(handle));
                if (title.Length == 0) return true;

                GetWindowThreadProcessId(handle, out uint pid);
                if (!processNames.TryGetValue(pid, out var process))
                {
                    try { process = Process.GetProcessById((int)pid).ProcessName; }
                    catch { process = ""; }   // process déjà sorti entre l'énumération et la lecture
                    processNames[pid] = process;
                }

                windows.Add(new WindowInfo(process, GetText(handle, GetClassName, 256), title));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Error("MeetingWindowProbe/EnumWindows", ex);
        }

        return windows;
    }

    /// <summary>
    /// Titre débarrassé de ce qui n'appartient pas à la réunion : compteur de notifications
    /// en tête, et marque du navigateur en queue (« … - Personnel – Microsoft Edge »).
    /// </summary>
    public static string CleanTitle(string raw)
    {
        // Edge glisse une espace de largeur nulle dans « Microsoft​ Edge » : sans ce nettoyage,
        // le motif de suppression ci-dessous ne reconnaîtrait pas sa propre marque.
        var title = raw.Replace("​", "").Trim();
        title = UnreadPrefix.Replace(title, "");
        title = BrowserTail.Replace(title, "");
        return title.Trim();
    }

    /// <summary>
    /// Titre réduit à ce qui nomme la réunion. Distinct de <see cref="CleanTitle"/>, qui sert à
    /// la <i>reconnaître</i> et doit donc garder la marque de l'application.
    ///
    /// Les retraits viennent tous de titres réellement relevés pendant les collectes :
    /// « Meeting join | Point Hebdo PRJ - Velmora | Microsoft Teams » (écran d'attente Teams),
    /// « Meet – PROJ_ATLAS… and 4 more pages » (décompte d'onglets d'Edge),
    /// « Huddle: @Camille Durand - Contoso - Slack 🎤 » (queue Slack) et
    /// « Chat | Velmora / Contoso Discovery Call # 1 (External) | Microsoft Teams »
    /// (onglet Chat de Teams, semaine du 2026-08-03 : le libellé gardait ses deux parasites).
    /// </summary>
    public static string ShortLabel(string raw)
    {
        var title = CleanTitle(raw);
        title = JoinPrefix.Replace(title, "");
        title = ChatPrefix.Replace(title, "");
        title = TeamsTail.Replace(title, "");
        title = SlackTail.Replace(title, "");
        title = TabCount.Replace(title, "");
        title = ExternalTail.Replace(title, "");
        return title.Trim();
    }

    private static readonly Regex JoinPrefix = new(@"^Meeting join\s*\|\s*", RegexOptions.IgnoreCase);

    private static readonly Regex ChatPrefix = new(@"^Chat\s*\|\s*", RegexOptions.IgnoreCase);

    /// <summary>Teams marque les réunions à participants externes ; ce n'est pas le sujet.</summary>
    private static readonly Regex ExternalTail = new(@"\s*\((?:External|Externe)\)\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex TeamsTail = new(@"\s*\|\s*Microsoft Teams\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex SlackTail = new(
        @"\s*[-–—]\s*(?:[^-–—]{1,40}\s*[-–—]\s*)?Slack\b.*$", RegexOptions.IgnoreCase);

    private static readonly Regex TabCount = new(@"\s+and \d+ more pages?\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex UnreadPrefix = new(@"^\(\d+\)\s*");

    private static readonly Regex BrowserTail = new(
        @"\s*[-–—]\s*(?:[^-–—]{1,40}\s*[-–—]\s*)?" +
        @"(?:Microsoft Edge|Google Chrome|Mozilla Firefox|Chromium|Brave|Opera)\s*$",
        RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ Win32

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr param);

    private delegate int GetStringProc(IntPtr handle, StringBuilder text, int max);

    /// <summary>Lit une chaîne Win32 en dimensionnant le tampon (les titres peuvent être longs).</summary>
    private static string GetText(IntPtr handle, GetStringProc read, int length)
    {
        if (length <= 0) return "";
        var buffer = new StringBuilder(length + 1);
        int written = read(handle, buffer, buffer.Capacity);
        return written > 0 ? buffer.ToString() : "";
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
