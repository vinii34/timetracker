using Microsoft.Win32;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Détecte les applications qui captent le micro <b>en ce moment</b>.
///
/// Windows tient à jour, sous <c>ConsentStore\microphone</c>, un couple
/// <c>LastUsedTimeStart</c> / <c>LastUsedTimeStop</c> par application : tant que la capture
/// est en cours, <c>LastUsedTimeStop</c> vaut 0. C'est la source de l'icône de micro de la
/// barre système — lisible sans COM ni WASAPI, et indifférente à la façon dont la réunion
/// tourne (Teams natif, Teams ou Meet dans un onglet, Zoom…).
///
/// Le filtre par application est indispensable : Discord, la dictée Windows ou OBS captent
/// le micro sans qu'il s'agisse d'une réunion de travail.
/// </summary>
public sealed class MicrophoneProbe : IMeetingProbe
{
    private const string ConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly AppSettings _settings;

    public MicrophoneProbe(AppSettings settings) => _settings = settings;

    public string Name => "micro";

    public IReadOnlyList<MeetingSignal> Poll()
    {
        var apps = _settings.MeetingAppList();
        var found = new List<MeetingSignal>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (root is null) return found;
            Collect(root, apps, found, canRecurse: true);
        }
        catch (Exception ex)
        {
            Logger.Error("MicrophoneProbe", ex);
        }
        return found;
    }

    /// <summary>
    /// Toutes les applications captant le micro, filtre d'applications mis à part.
    /// Sert au diagnostic <c>--meetingprobe</c> : c'est ce qu'il faut voir pour savoir quel
    /// motif ajouter à la liste.
    /// </summary>
    public IReadOnlyList<string> ActiveAppsUnfiltered()
    {
        var all = new List<MeetingSignal>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (root != null) Collect(root, apps: null, all, canRecurse: true);
        }
        catch (Exception ex)
        {
            Logger.Error("MicrophoneProbe/diagnostic", ex);
        }
        return all.Select(s => s.App).ToList();
    }

    /// <summary>
    /// Parcourt le magasin. Les applications du Store sont à la racine, les autres sous la
    /// sous-clé <c>NonPackaged</c> — d'où un seul niveau de récursion.
    /// </summary>
    private static void Collect(RegistryKey key, IReadOnlyList<string>? apps,
                                List<MeetingSignal> found, bool canRecurse)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub is null) continue;

            var stop = sub.GetValue("LastUsedTimeStop");
            if (stop is null)
            {
                if (canRecurse) Collect(sub, apps, found, canRecurse: false);
                continue;
            }

            // 0 = capture toujours en cours. Toute autre valeur est un horodatage de fin.
            if (Convert.ToInt64(stop) != 0) continue;

            var app = FriendlyApp(name);
            if (apps is null || Matches(app, name, apps))
                found.Add(new MeetingSignal(MeetingSource.Microphone, app, Title: null, Conclusive: true));
        }
    }

    /// <summary>
    /// Nom lisible d'une clé du magasin : les chemins d'exe y sont écrits avec des <c>#</c>
    /// à la place des séparateurs (<c>C:#Program Files#…#msedge.exe</c>), les applications du
    /// Store sous la forme <c>MSTeams_8wekyb3d8bbwe</c>.
    /// </summary>
    private static string FriendlyApp(string keyName)
    {
        var last = keyName.Split('#').Last();
        if (last.Contains('.')) return last;                       // un exe
        var underscore = last.IndexOf('_');
        return underscore > 0 ? last[..underscore] : last;          // un paquet du Store
    }

    private static bool Matches(string app, string keyName, IReadOnlyList<string> apps) =>
        apps.Any(p => app.Contains(p, StringComparison.OrdinalIgnoreCase)
                      || keyName.Contains(p, StringComparison.OrdinalIgnoreCase));
}
