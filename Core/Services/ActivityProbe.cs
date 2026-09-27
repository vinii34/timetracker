using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace TimeTracker.Core.Services;

/// <summary>
/// Relève toutes les 5 s la fenêtre <b>au premier plan</b> (process, titre) et garde les
/// dernières minutes en mémoire. C'est la matière première des suggestions de tâche
/// (<see cref="TaskSuggester"/>) : « tu passes du temps sur une fenêtre qui parle de Orvane ».
///
/// ⚠️ <b>Rien n'est écrit nulle part.</b> Ni en base, ni dans le journal, ni dans la trace de
/// diagnostic : un titre de fenêtre est un morceau de vie privée (un mail ouvert, une page
/// web), et TimeTracker promet que rien ne quitte le poste. Le relevé vit en mémoire, plafonné
/// à <see cref="Retention"/>, et disparaît avec le processus. Les seuls titres qui
/// atteignent l'utilisateur sont ceux qu'on lui montre dans une suggestion — les siens.
/// </summary>
public sealed class ActivityProbe : IDisposable
{
    /// <summary>Une observation : ce qui était au premier plan à cet instant.</summary>
    public sealed record Sample(DateTime At, string Process, string Title);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Profondeur conservée : de quoi juger la dernière demi-heure, pas plus.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _timer;
    private readonly List<Sample> _samples = new();

    public ActivityProbe()
    {
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => PollOnce();
    }

    public void Start() => _timer.Start();

    public void Stop() { _timer.Stop(); _samples.Clear(); }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Observations des <paramref name="window"/> dernières minutes, la plus ancienne d'abord.</summary>
    public IReadOnlyList<Sample> Recent(TimeSpan window)
    {
        var since = DateTime.Now - window;
        return _samples.Where(s => s.At >= since).ToList();
    }

    /// <summary>Un relevé. Public pour les tests, qui l'appellent sans attendre le temps réel.</summary>
    public void PollOnce()
    {
        try
        {
            var (process, title) = Foreground();
            if (title.Length == 0) return;   // bureau, écran de verrouillage : rien à apprendre
            Record(new Sample(DateTime.Now, process, title));
        }
        catch (Exception ex)
        {
            Logger.Error("ActivityProbe", ex);
        }
    }

    /// <summary>Ajoute une observation (les tests en fabriquent) et purge les anciennes.</summary>
    public void Record(Sample sample)
    {
        _samples.Add(sample);
        var cutoff = DateTime.Now - Retention;
        int drop = 0;
        while (drop < _samples.Count && _samples[drop].At < cutoff) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);
    }

    private static (string Process, string Title) Foreground()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return ("", "");

        int length = GetWindowTextLength(handle);
        if (length <= 0) return ("", "");
        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);

        GetWindowThreadProcessId(handle, out uint pid);
        string process;
        try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { process = ""; }

        return (process, MeetingWindowProbe.CleanTitle(buffer.ToString()));
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
