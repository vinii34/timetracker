using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
using TimeTracker.Core.Services.Ai;
using TimeTracker.UI;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace TimeTracker;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    private DatabaseService _db = null!;
    private TimerService _timer = null!;
    private HotkeyService _hotkeys = null!;
    private TrayIconManager _tray = null!;
    private AppSettings _settings = null!;
    private DashboardWindow? _dashboard;
    private MeetingDetector? _meetings;
    private MeetingTrace? _meetingTrace;
    private CalendarProbe? _calendar;

    /// <summary>Détecter et journaliser, mais ne jamais basculer (semaine d'observation).</summary>
    private bool _meetingObserveOnly;

    /// <summary>Tâche à reprendre à la fin de la réunion détectée (null = rien à reprendre).</summary>
    private TaskItem? _taskBeforeMeeting;

    /// <summary>
    /// Entrée créée par la bascule automatique, et la tâche « Réunion » sur laquelle elle a été
    /// pointée : de quoi proposer, en fin de réunion, de la nommer d'après le titre relevé.
    /// </summary>
    private long? _meetingEntryId;
    private long? _meetingTaskId;

    private bool _selectorOpen;
    private bool _modalOpen;   // correction à chaud / paramètres / gestion des tâches
    private bool _reminderOpen; // un rappel affiché ne doit pas se superposer au suivant
    private Window? _reminderPopup;   // le rappel « en cours » affiché, que la proposition de changement remplace

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetupExceptionHandling();
        Logger.Info("=== Démarrage TimeTracker ===");

        // --meetingprobe : diagnostic de détection de réunion. Traité AVANT l'instance unique,
        // parce qu'il n'a d'intérêt que pendant une vraie réunion — donc pendant que TimeTracker
        // tourne déjà. Il ne lit que le registre et les fenêtres : rien à verrouiller.
        var probeArg = e.Args.FirstOrDefault(a => a.StartsWith("--meetingprobe", StringComparison.Ordinal));
        if (probeArg != null)
        {
            var eq = probeArg.IndexOf('=');
            RunMeetingProbe(eq > 0 ? probeArg[(eq + 1)..] : null);
            Shutdown();
            return;
        }

        // --outlookprobe : même esprit, pour l'agenda. Indispensable parce que le poste de
        // développement n'a aucun profil Outlook : c'est le seul moyen de vérifier la lecture
        // COM là où elle doit marcher, sur le poste de travail.
        var outlookArg = e.Args.FirstOrDefault(a => a.StartsWith("--outlookprobe", StringComparison.Ordinal));
        if (outlookArg != null)
        {
            var eq = outlookArg.IndexOf('=');
            RunOutlookProbe(eq > 0 ? outlookArg[(eq + 1)..] : null);
            Shutdown();
            return;
        }

        // Le self-test fabrique des entrées bidon : il travaille sur une base jetable
        // pour ne jamais polluer le relevé de temps réel de l'utilisateur.
        bool selfTest = e.Args.Contains("--selftest");
        // --uitest[=<dossier>] : le dossier reçoit une capture PNG de chaque fenêtre.
        var uiTestArg = e.Args.FirstOrDefault(a => a.StartsWith("--uitest", StringComparison.Ordinal));
        bool uiTest = uiTestArg != null;

        // Tests traités AVANT l'instance unique : ils n'ouvrent ni barre système, ni raccourcis,
        // ni registre, et sur le poste de l'utilisateur TimeTracker tourne en permanence — sous
        // le mutex, ils se contentaient d'afficher « déjà en cours d'exécution ».
        if (selfTest || uiTest)
        {
            var throwawayDb = Path.Combine(Path.GetTempPath(), $"timetracker_selftest_{Guid.NewGuid():N}.db");
            _db = new DatabaseService(throwawayDb);
            _db.Initialize();
            _settings = _db.LoadSettings();
            if (selfTest) RunSelfTest();
            if (uiTest)
            {
                var index = uiTestArg!.IndexOf('=');
                RunUiTest(index > 0 ? uiTestArg[(index + 1)..] : null);
            }
            TryDeleteThrowawayDb(throwawayDb);
            Shutdown();
            return;
        }

        // Instance unique
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "TimeTracker_SingleInstance_2F8B", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("TimeTracker est déjà en cours d'exécution.", "TimeTracker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // --db=<chemin> : base alternative (tests manuels sans toucher au relevé réel). Le
        // registre de démarrage reste celui de l'installation réelle.
        var overrideDb = e.Args.FirstOrDefault(a => a.StartsWith("--db=", StringComparison.Ordinal))?[5..];
        if (overrideDb != null) StartupService.Frozen = true;

        _db = new DatabaseService(overrideDb);
        _db.Initialize();
        _settings = _db.LoadSettings();

        // Aligne le registre de démarrage sur le réglage enregistré.
        try { StartupService.SetEnabled(_settings.StartWithWindows); } catch { /* non bloquant */ }

        _timer = new TimerService(_db, _settings.ReminderIntervalMinutes);
        _timer.Tick += OnTimerTick;
        _timer.ReminderDue += OnReminderDue;
        _timer.PausedReminderDue += OnPausedReminderDue;
        _timer.CurrentTaskChanged += OnCurrentTaskChanged;

        _tray = new TrayIconManager();
        _tray.ChangeTaskRequested += (_, _) => OpenTaskSelector();
        _tray.PauseResumeRequested += (_, _) => TogglePause();
        _tray.StopRequested += (_, _) => StopTracking();
        _tray.QuickEditRequested += (_, _) => OpenQuickEdit();
        _tray.OpenDashboardRequested += (_, _) => OpenDashboard();
        _tray.ManageTasksRequested += (_, _) => OpenTaskManager();
        _tray.OpenSettingsRequested += (_, _) => OpenSettings();
        _tray.QuitRequested += (_, _) => Shutdown();
        _tray.Show();

        _hotkeys = new HotkeyService();
        RegisterHotkeysFromSettings(announceFailures: true);

        // Veille et verrouillage : voir OnMachineWokeUp.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Reprise d'une entrée laissée ouverte (fermeture précédente non propre).
        var open = _db.GetOpenEntry();
        if (open != null)
        {
            _timer.ResumeOpenEntry(open);
        }
        else if (_settings.AskTaskOnStartup && !e.Args.Contains("--nostartpopup"))
        {
            // Démarrage de journée : aucune tâche en cours → proposer d'en choisir une.
            OpenTaskSelector();
        }

        // --meetingtrace[=<fichier>] : journal continu du diagnostic, pour ajuster les
        // heuristiques après plusieurs jours d'usage réel. --meetingobserve détecte et
        // journalise sans jamais toucher au relevé de temps.
        var traceArg = e.Args.FirstOrDefault(a => a.StartsWith("--meetingtrace", StringComparison.Ordinal));
        if (traceArg != null)
        {
            var eq = traceArg.IndexOf('=');
            var tracePath = eq > 0
                ? traceArg[(eq + 1)..]
                : Path.Combine(Path.GetDirectoryName(Logger.LogPath)!, "meetingtrace.log");
            _meetingTrace = new MeetingTrace(tracePath);
            Logger.Info($"Journal de détection de réunion actif : {tracePath}");
        }
        _meetingObserveOnly = e.Args.Contains("--meetingobserve");
        if (_meetingObserveOnly)
        {
            _meetingTrace?.Event("Mode observation : réunions détectées, aucune bascule.");
            Logger.Info("Mode observation de réunion : aucune bascule automatique.");
        }

        ApplyMeetingDetection();
        ApplyActivityProbe();

        // --dashboard[=semaine] : ouvre directement le tableau de bord (raccourci menu Démarrer).
        var dashboardArg = e.Args.FirstOrDefault(a => a.StartsWith("--dashboard", StringComparison.Ordinal));
        if (dashboardArg != null)
        {
            OpenDashboard();
            if (dashboardArg.EndsWith("=semaine", StringComparison.OrdinalIgnoreCase))
                _dashboard?.SelectTab(1);
        }

        RefreshTrayState();
    }

    // ------------------------------------------------------------- Raccourcis

    /// <summary>
    /// (Ré)enregistre les deux raccourcis globaux d'après les réglages courants.
    /// Sert aussi de retour arrière quand une nouvelle combinaison est refusée par Windows.
    /// </summary>
    private void RegisterHotkeysFromSettings(bool announceFailures)
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();

        if (!TryRegister(_settings.HotkeyTask, () => OpenTaskSelector())) failed.Add(_settings.HotkeyTask);
        if (!TryRegister(_settings.HotkeyEdit, OpenQuickEdit)) failed.Add(_settings.HotkeyEdit);
        if (!TryRegister(_settings.HotkeyPause, TogglePause)) failed.Add(_settings.HotkeyPause);

        _tray.SetHotkeyHints(_settings.HotkeyTask, _settings.HotkeyEdit, _settings.HotkeyPause);

        bool TryRegister(string text, Action handler) =>
            Hotkey.TryParse(text, out var hotkey) && _hotkeys.Register(hotkey, handler);

        if (announceFailures && failed.Count > 0)
        {
            Logger.Info($"Raccourci(s) refusé(s) par Windows : {string.Join(", ", failed)}");
            _tray.ShowBalloon("Raccourci indisponible",
                $"{string.Join(" et ", failed)} déjà utilisé par une autre application. " +
                "Choisis-en un autre dans Paramètres.");
        }
    }

    /// <summary>
    /// Tente d'appliquer les raccourcis choisis dans les Paramètres. Renvoie un message d'erreur
    /// (et restaure les précédents) si Windows refuse une combinaison, sinon null.
    /// </summary>
    private string? ApplyHotkeys(Hotkey task, Hotkey edit, Hotkey pause)
    {
        _hotkeys.UnregisterAll();

        if (!_hotkeys.Register(task, () => OpenTaskSelector()))
        {
            RegisterHotkeysFromSettings(announceFailures: false);
            return $"« {task} » est déjà utilisé par une autre application.";
        }
        if (!_hotkeys.Register(edit, OpenQuickEdit))
        {
            RegisterHotkeysFromSettings(announceFailures: false);
            return $"« {edit} » est déjà utilisé par une autre application.";
        }
        if (!_hotkeys.Register(pause, TogglePause))
        {
            RegisterHotkeysFromSettings(announceFailures: false);
            return $"« {pause} » est déjà utilisé par une autre application.";
        }

        _tray.SetHotkeyHints(task.ToString(), edit.ToString(), pause.ToString());
        return null;
    }

    /// <summary>
    /// Sélecteur de tâche. <paramref name="offerBackdate"/> ajoute le décalage 5 / 15 min :
    /// après un rappel, le changement a souvent eu lieu avant qu'on pense à le déclarer.
    /// </summary>
    private void OpenTaskSelector(string? prefill = null, bool offerBackdate = false)
    {
        if (_selectorOpen) return;
        _selectorOpen = true;
        try
        {
            // Les tâches que l'activité des fenêtres suggère sont marquées 💡, avec leur raison.
            var suggestions = CurrentSuggestions();
            var tasks = BuildSelectorTasks(suggestions);
            IReadOnlyDictionary<long, string>? reasons = suggestions.Count > 0
                ? suggestions.ToDictionary(s => s.Task.Id, s => s.Reason)
                : null;

            var allTasks = _db.GetAllTasks();
            var vocabulary = TaskSimilarity.Vocabulary.Of(allTasks.Select(t => t.Name));
            var result = TaskSelectorPopup.Pick(tasks, prefill, offerBackdate, reasons,
                checkName: typed => NameReview.CheckNewName(typed, allTasks, _settings.MeetingTaskName, vocabulary),
                opened: SelectorAiRequest() is { } ai ? popup => _ = AskAiForSelector(ai.Provider, ai.Request, popup) : null);
            if (result is null) return;

            bool existed = result.ExistingTask != null || _db.FindTaskByName(result.NewTaskName!) != null;
            TaskItem task = result.ExistingTask
                            ?? _db.GetOrCreateTask(result.NewTaskName!);
            _timer.StartTask(task, BackdatedStart(result.BackdateMinutes));

            // Pour juger les suggestions à la collecte suivante : rang, 💡, origine du nom.
            // Jamais de titre de fenêtre ici — seulement des noms de tâches, comme partout.
            Logger.Info(result.ExistingTask != null
                ? $"Sélecteur : « {task.Name} » (rang {result.Rank}{(result.WasSuggested ? ", suggérée" : "")})."
                : $"Sélecteur : {(existed ? "tâche existante tapée" : "nouvelle tâche")} « {task.Name} »" +
                  (result.NameOrigin == "tapé" ? "" : $" (nom {result.NameOrigin})") + ".");
        }
        catch (Exception ex)
        {
            Logger.Error("OpenTaskSelector", ex);
            _tray?.ShowBalloon("Erreur lors du changement de tâche", ex.Message);
        }
        finally
        {
            _selectorOpen = false;
        }
    }

    /// <summary>
    /// Liste proposée dans le sélecteur : les favoris, puis <b>la tâche d'avant</b>, puis les
    /// suggestions 💡, puis les autres tâches récentes. Les chiffres 1-9 couvrent donc en
    /// priorité les favoris et l'aller-retour entre deux tâches.
    ///
    /// Mesuré les 29-30/09 (v1.7) : sur 6 choix dans la liste, 5 étaient la tâche d'avant —
    /// toujours au rang 6, derrière deux 💡, deux ★ et la tâche en cours elle-même — et aucun
    /// n'était une 💡. La tâche qui tourne n'a rien à faire dans la liste (on ne bascule pas
    /// sur elle) ; en pause elle y reste, pour la reprendre. Une réunion ne prend pas la place
    /// de la tâche d'avant : elle reste plus bas, parmi les récentes.
    /// </summary>
    private IReadOnlyList<TaskItem> BuildSelectorTasks(IReadOnlyList<TaskSuggester.Suggestion>? suggestions = null)
    {
        long? running = _timer?.HasActiveTask == true ? _timer.CurrentTask?.Id : null;
        var meetingOnly = _db.GetMeetingOnlyTaskIds();
        var recent = _db.GetRecentTasks(12).Where(t => t.Id != running).ToList();
        var previous = recent.FirstOrDefault(t => !meetingOnly.Contains(t.Id)
                                                  && !NameReview.IsMeeting(t.Name, _settings.MeetingTaskName));

        var list = new List<TaskItem>();
        var known = new HashSet<long>();
        void Add(TaskItem t) { if (t.Id != running && known.Add(t.Id)) list.Add(t); }

        foreach (var t in _db.GetFavoriteTasks()) Add(t);
        if (previous != null) Add(previous);
        foreach (var s in suggestions ?? Array.Empty<TaskSuggester.Suggestion>()) Add(s.Task);
        foreach (var t in recent) Add(t);
        return list.Take(15).ToList();
    }

    /// <summary>
    /// Heure de début décalée de <paramref name="minutes"/>, sans jamais remonter avant le
    /// début de l'entrée en cours (sinon celle-ci serait clôturée avant d'avoir commencé).
    /// </summary>
    private DateTime? BackdatedStart(int minutes) =>
        minutes <= 0 ? null : ClampToCurrentEntry(DateTime.Now.AddMinutes(-minutes));

    /// <summary>
    /// Empêche un instant antidaté de remonter avant le début de l'entrée en cours : celle-ci
    /// serait clôturée avant d'avoir commencé, donc d'une durée négative.
    /// </summary>
    private DateTime ClampToCurrentEntry(DateTime start) =>
        _timer.CurrentEntryId.HasValue && start < _timer.CurrentStart ? _timer.CurrentStart : start;

    private void TogglePause()
    {
        if (_timer.CurrentTask is null) { OpenTaskSelector(); return; }
        if (_timer.IsPaused) _timer.Resume();
        else _timer.Pause();
        RefreshTrayState();
    }

    // ------------------------------------------------ Suggestions par l'activité

    private ActivityProbe? _activity;
    private DispatcherTimer? _activityTimer;
    private DateTime _learnedUntil = DateTime.Now;

    /// <summary>Aligne le relevé d'activité sur le réglage (démarrage et après les Paramètres).</summary>
    private void ApplyActivityProbe()
    {
        if (!_settings.ActivitySuggestions)
        {
            _activity?.Stop();
            _activityTimer?.Stop();
            _shiftWindow?.Expire("suggestions coupées");
            return;
        }
        _activity ??= new ActivityProbe();
        if (!_activity.IsRunning) { _activity.Start(); _learnedUntil = DateTime.Now; }

        // Une fois par minute : apprentissage (différé) et détection d'un changement de tâche.
        _activityTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _activityTimer.Tick -= OnActivityMinute;
        _activityTimer.Tick += OnActivityMinute;
        if (!_activityTimer.IsEnabled) _activityTimer.Start();
        if (!_settings.ActivityShiftDetection) _shiftWindow?.Expire("réglage coupé");
    }

    private void OnActivityMinute(object? sender, EventArgs e)
    {
        LearnFromActivity(flush: false);
        CheckActivityShift();
    }

    /// <summary>
    /// Compte les mots des titres de fenêtres pour les tâches que la base désigne, avec
    /// <see cref="ActivityLearning.Delay"/> de recul — une bascule corrigée ou antidatée entre-temps
    /// est donc prise en compte (voir <see cref="ActivityLearning"/>). <paramref name="flush"/>
    /// apprend tout ce qui reste, à la fermeture. Les secondes s'accumulent en base
    /// (<c>task_hints</c>) — c'est ce que l'utilisateur a accepté le 2026-09-17.
    /// </summary>
    private void LearnFromActivity(bool flush)
    {
        try
        {
            if (_activity is null || !_activity.IsRunning || !_settings.ActivityLearning) return;
            var until = flush ? DateTime.Now : DateTime.Now - ActivityLearning.Delay;
            var since = _learnedUntil;
            if (until <= since) return;
            _learnedUntil = until;

            var samples = _activity.Recent(ActivityProbe.Retention).Where(s => s.At >= since && s.At < until).ToList();
            if (samples.Count == 0) return;
            var entries = _db.GetEntriesOverlapping(since, until);
            foreach (var (taskId, words) in ActivityLearning.Attribute(samples, entries))
                _db.AddTaskHints(taskId, words);
        }
        catch (Exception ex)
        {
            Logger.Error("LearnFromActivity", ex);
        }
    }

    // ------------------------------------------------ Changement de tâche probable

    private readonly ActivityShiftDetector _shiftDetector = new();
    private ActivityShiftWindow? _shiftWindow;

    /// <summary>Dernier réveil de veille ou déverrouillage : rien n'est proposé juste après.</summary>
    private DateTime _lastWake = DateTime.MinValue;

    private static readonly TimeSpan ShiftQuietAfterSwitch = TimeSpan.FromMinutes(10);

    /// <summary>Pourquoi il ne faut rien proposer maintenant, ou null.</summary>
    private string? ShiftSilenceReason(DateTime now)
    {
        if (!_settings.ActivityShiftDetection) return "réglage";
        if (_timer is null || !_timer.HasActiveTask || _timer.CurrentTask is null) return "pas de tâche active";
        if (_meetings?.InMeeting == true || (_meetingEntryId.HasValue && _meetingEntryId == _timer.CurrentEntryId))
            return "réunion";
        if (now - _timer.CurrentStart < ShiftQuietAfterSwitch) return "bascule récente";
        if (now - _lastWake < ShiftQuietAfterSwitch) return "réveil récent";
        // Pas le rappel : non modal, il peut rester ouvert des heures sans réponse et aurait
        // éteint la détection d'autant. La proposition le remplace (ShowShiftProposal).
        if (_shiftWindow != null || _selectorOpen || _modalOpen) return "fenêtre ouverte";
        return null;
    }

    /// <summary>Une évaluation par minute ; la proposition s'ouvre si le détecteur conclut.</summary>
    private void CheckActivityShift()
    {
        try
        {
            if (_activity is null || !_activity.IsRunning) return;
            var now = DateTime.Now;
            if (ShiftSilenceReason(now) != null) { _shiftDetector.Silence(); return; }

            var task = _timer.CurrentTask!;
            var hints = _db.GetTaskHints();
            var common = TaskSuggester.CommonLearnedWords(hints);
            var learned = hints.TryGetValue(task.Id, out var own)
                ? own.Where(w => w.Value >= 60).Select(w => w.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var others = TaskSuggester.RareNameWords(_db.GetAllTasks(), task.Id);
            var context = new ActivityShiftDetector.Context(task.Id, task.Name, _timer.CurrentStart, learned,
                                                            common.Contains, others.Contains);

            var shift = _shiftDetector.Evaluate(now, _activity.Recent(ActivityProbe.Retention), context);
            if (shift != null) ShowShiftProposal(BuildShiftProposal(task, shift, hints));
        }
        catch (Exception ex)
        {
            Logger.Error("CheckActivityShift", ex);
        }
    }

    /// <summary>Ce qu'on propose, décidé sans rien afficher (vérifiable par <c>--selftest</c>).</summary>
    /// <param name="Resumed">La tâche existante proposée est une tâche dont les fenêtres reviennent.</param>
    private sealed record ShiftProposal(TaskItem Current, ActivityShiftDetector.Shift Shift, DateTime Since,
                                        TaskSuggester.Suggestion? Local, string? LocalName,
                                        (IAiProvider Provider, TaskSuggestionAssistant.Request Request)? Ai,
                                        IReadOnlyList<TaskItem> Tasks, bool Resumed = false);

    /// <summary>
    /// Ce que le poste sait proposer seul — une tâche existante (jamais une réunion ni la tâche en
    /// cours) : d'abord celle dont les fenêtres reviennent (<see cref="ActivityLearning.ResumedTask"/>),
    /// sinon celle que suggèrent les mots ; un nom tiré du titre — et de quoi demander mieux à
    /// l'IA si l'utilisateur l'a autorisé. L'heure proposée ne remonte jamais avant l'entrée en cours.
    /// </summary>
    /// <param name="recent">Relevés en mémoire ; null = ceux du relevé d'activité (les tests fournissent les leurs).</param>
    private ShiftProposal BuildShiftProposal(TaskItem current, ActivityShiftDetector.Shift shift,
                                             Dictionary<long, Dictionary<string, int>> hints,
                                             IReadOnlyList<ActivityProbe.Sample>? recent = null)
    {
        var now = DateTime.Now;
        _shiftDetector.Proposed(shift, now);
        var tasks = _db.GetAllTasks();
        var byId = tasks.ToDictionary(t => t.Id);
        var meetingOnly = _db.GetMeetingOnlyTaskIds();
        bool Proposable(TaskItem t) => t.Id != current.Id && !meetingOnly.Contains(t.Id)
                                       && !NameReview.IsMeeting(t.Name, _settings.MeetingTaskName);

        TaskSuggester.Suggestion? local = null;
        recent ??= _activity?.Recent(ActivityProbe.Retention) ?? Array.Empty<ActivityProbe.Sample>();
        var resumed = ActivityLearning.ResumedTask(shift.Samples, shift.Keywords, recent,
            _db.GetEntriesOverlapping(now - ActivityProbe.Retention, now), current.Id,
            id => !byId.TryGetValue(id, out var t) || !Proposable(t));
        if (resumed != null)
            local = new TaskSuggester.Suggestion(byId[resumed.TaskId], resumed.Evidence, double.MaxValue,
                                                 $"mêmes fenêtres que sur cette tâche jusqu'à {resumed.LastSeen:HH:mm}");
        local ??= TaskSuggester.Suggest(tasks, shift.Samples, hints, max: 3).FirstOrDefault(s => Proposable(s.Task));
        var localName = ActivityNaming.FromTitle(shift.DominantTitle, shift.Processes.FirstOrDefault() ?? "");
        var ai = ActivityAiRequest(current.Name, shift.Keywords, shift.Processes, fromSelector: false);
        return new ShiftProposal(current, shift, ClampToCurrentEntry(shift.Since), local, localName, ai, tasks,
                                 Resumed: resumed != null);
    }

    /// <summary>
    /// Ouvre « Tu as changé de tâche ? », puis, si l'IA est de la partie, met la fenêtre à jour
    /// quand elle répond.
    /// </summary>
    private void ShowShiftProposal(ShiftProposal p)
    {
        // Le journal ne dit ni titre ni mot-clé : seulement la tâche en cours et la nature de la proposition.
        Logger.Info($"Changement d'activité probable sur « {p.Current.Name} » depuis {p.Since:HH:mm} " +
                    $"({Format(p.Shift.Evidence)}) — proposé : " +
                    (p.Local != null ? $"tâche existante « {p.Local.Task.Name} »{(p.Resumed ? " (reprise)" : "")}" : "pas de tâche existante") +
                    (p.LocalName != null ? ", nom tiré du titre" : "") +
                    (p.Ai != null ? ", IA interrogée" : "") + ".");

        // Un rappel resté ouvert pose une question moins précise au même endroit de l'écran :
        // il est fermé (compté « je continue »), la proposition prend sa place.
        _reminderPopup?.Close();

        var vocabulary = TaskSimilarity.Vocabulary.Of(p.Tasks.Select(t => t.Name));
        var window = new ActivityShiftWindow(p.Current.Name, p.Since, p.Shift.Evidence, p.Shift.DominantTitle,
            p.Shift.DominantTime, p.Local?.Task, p.Local?.Reason, p.LocalName,
            typed => NameReview.CheckNewName(typed, p.Tasks, _settings.MeetingTaskName, vocabulary),
            _settings.ReminderSound, existingIsResumed: p.Resumed);
        window.Answered += answer => OnShiftAnswered(p.Current, p.Shift, p.Since, answer);
        _shiftWindow = window;
        window.Show();

        if (p.Ai is { } ai) _ = AskAiForShift(ai.Provider, ai.Request, window);
    }

    private async Task AskAiForShift(IAiProvider provider, TaskSuggestionAssistant.Request request, ActivityShiftWindow window)
    {
        window.ShowAiPending(provider.Name, TaskSuggestionAssistant.DescribeSent(request, provider.Name));
        var answer = await RunActivityAi(provider, request);
        if (_shiftWindow != window) return;   // déjà répondue ou retirée
        if (answer is null) { window.ShowAiFailed(); return; }
        var existing = answer.Existing is null ? null : _db.FindTaskByName(answer.Existing);
        window.ShowAiAnswer(existing, existing is null ? answer.NewName : null, answer.Why);
    }

    private void OnShiftAnswered(TaskItem current, ActivityShiftDetector.Shift shift, DateTime since, ShiftAnswer answer)
    {
        _shiftWindow = null;
        try
        {
            // La tâche a pu changer entre l'affichage et la réponse (réunion, sélecteur) :
            // basculer depuis 10:42 rognerait alors une entrée qui n'est plus la bonne.
            bool stillCurrent = _timer.CurrentTask?.Id == current.Id && _timer.HasActiveTask;
            switch (answer.Kind)
            {
                case ShiftAnswerKind.Existing or ShiftAnswerKind.New when stillCurrent:
                {
                    // Relue en base : la fenêtre ne bloque rien, la tâche proposée a pu être
                    // fusionnée entre-temps (« Noms à vérifier »). La démarrer quand même ferait
                    // échouer l'entrée sur la clé étrangère après avoir clôturé la précédente.
                    var task = answer.Task is { } chosen ? _db.GetTask(chosen.Id) : _db.GetOrCreateTask(answer.NewName!);
                    if (task is null)
                    {
                        Logger.Info($"Changement accepté vers « {answer.Task!.Name} », qui n'existe plus (fusionnée entre-temps) : rien n'est fait.");
                        break;
                    }
                    var start = answer.Backdate ? ClampToCurrentEntry(since) : (DateTime?)null;
                    _timer.StartTask(task, start);
                    Logger.Info($"Changement accepté : « {task.Name} » " +
                                (answer.Kind == ShiftAnswerKind.Existing ? "(tâche existante" : "(nouvelle tâche") +
                                $", {answer.Origin}) {(start is DateTime s ? $"depuis {s:HH:mm}" : "à partir de maintenant")}.");
                    RefreshTrayState();
                    break;
                }
                case ShiftAnswerKind.Existing or ShiftAnswerKind.New:
                    Logger.Info("Changement accepté trop tard : la tâche en cours a changé entre-temps, rien n'est fait.");
                    break;
                case ShiftAnswerKind.Stay:
                    _shiftDetector.Stayed(shift);
                    Logger.Info($"Changement refusé : il reste sur « {current.Name} ».");
                    break;
                default:
                    Logger.Info($"Proposition de changement sans réponse ({answer.Origin}).");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("OnShiftAnswered", ex);
        }
    }

    // ------------------------------------------------ IA d'après l'activité

    private DateTime _lastActivityAiCall = DateTime.MinValue;
    private DateTime _activityAiDay = DateTime.MinValue;
    private int _activityAiCalls;

    /// <summary>Au plus un appel automatique toutes les 5 min, et un plafond de sécurité par jour.</summary>
    private static readonly TimeSpan ActivityAiInterval = TimeSpan.FromMinutes(5);
    private const int ActivityAiDailyCap = 100;

    /// <summary>
    /// De quoi interroger l'IA d'après l'activité, ou null si ce n'est pas permis maintenant :
    /// interrupteurs, cadence, trop peu de mots-clés, fournisseur mal configuré.
    /// </summary>
    private (IAiProvider Provider, TaskSuggestionAssistant.Request Request)? ActivityAiRequest(
        string? currentTask, IReadOnlyList<string> keywords, IReadOnlyList<string> processes, bool fromSelector)
    {
        if (!_settings.AiEnabled || !_settings.AiActivitySuggestions) return null;
        var now = DateTime.Now;
        if (now - _lastActivityAiCall < ActivityAiInterval) return null;
        if (_activityAiDay != now.Date) { _activityAiDay = now.Date; _activityAiCalls = 0; }
        if (_activityAiCalls >= ActivityAiDailyCap) return null;
        if (keywords.Count < 3) return null;

        IAiProvider provider;
        try { provider = AiProviderFactory.Create(_settings); }
        catch (Exception ex) { Logger.Info($"IA : suggestion d'activité impossible — {ex.Message}"); return null; }

        var meetingOnly = _db.GetMeetingOnlyTaskIds();
        var names = _db.GetAllTasks()
                       .Where(t => !meetingOnly.Contains(t.Id) && !NameReview.IsMeeting(t.Name, _settings.MeetingTaskName))
                       .OrderByDescending(t => t.LastUsed ?? DateTime.MinValue)
                       .Select(t => t.Name)
                       .Take(TaskSuggestionAssistant.MaxTaskNames)
                       .ToList();
        return (provider, new TaskSuggestionAssistant.Request(currentTask, keywords, processes, names, fromSelector));
    }

    /// <summary>La même chose à l'ouverture du sélecteur, d'après les 10 dernières minutes.</summary>
    private (IAiProvider Provider, TaskSuggestionAssistant.Request Request)? SelectorAiRequest()
    {
        if (_activity is null || !_activity.IsRunning || !_settings.AiEnabled || !_settings.AiActivitySuggestions) return null;
        var samples = _activity.Recent(TimeSpan.FromMinutes(10));
        var common = TaskSuggester.CommonLearnedWords(_db.GetTaskHints());
        var keywords = ActivityShiftDetector.Keywords(samples, common.Contains);
        var processes = samples.Where(s => !s.Process.Equals("TimeTracker", StringComparison.OrdinalIgnoreCase) && s.Process.Length > 0)
                               .GroupBy(s => s.Process, StringComparer.OrdinalIgnoreCase)
                               .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToList();
        return ActivityAiRequest(_timer?.CurrentTask?.Name, keywords, processes, fromSelector: true);
    }

    private async Task AskAiForSelector(IAiProvider provider, TaskSuggestionAssistant.Request request, TaskSelectorPopup popup)
    {
        bool closed = false;
        popup.Closed += (_, _) => closed = true;
        var sent = TaskSuggestionAssistant.DescribeSent(request, provider.Name);
        var answer = await RunActivityAi(provider, request);
        if (closed) return;
        if (answer?.Existing is { } existing) popup.ShowAiSuggestion(existing, true, answer.Why, sent);
        else if (answer?.NewName is { } name) popup.ShowAiSuggestion(name, false, answer.Why, sent);
        else popup.ShowAiNothing(answer is null ? "✨ L'IA n'a pas répondu." : "✨ L'IA ne voit pas de tâche évidente.", sent);
    }

    /// <summary>
    /// Un appel, journalisé en comptes seulement (jamais les mots-clés, jamais la réponse) ; null
    /// en cas d'échec, qui reste silencieux pour l'utilisateur : la proposition locale demeure.
    /// </summary>
    private async Task<TaskSuggestionAssistant.Answer?> RunActivityAi(IAiProvider provider, TaskSuggestionAssistant.Request request)
    {
        _lastActivityAiCall = DateTime.Now;
        _activityAiCalls++;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            TaskSuggestionAssistant.Answer answer;
            try
            {
                answer = await TaskSuggestionAssistant.RunAsync(provider, request, _settings.MeetingTaskName, cts.Token);
            }
            catch (Exception first) when (AiHttp.IsTransient(first))
            {
                // Un 503 « high demand » de Gemini (29/09) passe en général quelques secondes après.
                Logger.Info($"IA : {first.Message} — nouvel essai dans 3 s.");
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                _activityAiCalls++;
                answer = await TaskSuggestionAssistant.RunAsync(provider, request, _settings.MeetingTaskName, cts.Token);
            }
            Logger.Info($"IA : suggestion d'activité ({(request.FromSelector ? "sélecteur" : "changement")}, " +
                        $"{request.Keywords.Count} mots-clés, {Math.Min(request.TaskNames.Count, TaskSuggestionAssistant.MaxTaskNames)} noms) → " +
                        (answer.Existing != null ? "tâche existante" : answer.NewName != null ? "nom nouveau" : "rien") +
                        $" en {watch.Elapsed.TotalSeconds:0.0} s ({provider.Name}, appel {_activityAiCalls} du jour).");
            return answer;
        }
        catch (Exception ex)
        {
            Logger.Info($"IA : suggestion d'activité échouée après {watch.Elapsed.TotalSeconds:0.0} s ({provider.Name}) — {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------ Noms à vérifier

    /// <summary>Propositions locales du moment (fautes, doublons), sans celles déjà refusées.</summary>
    private List<NameReview.Item> FindNamesToReview() =>
        NameReview.Find(_db.GetTaskUsage(), _timer?.CurrentTask?.Id, _settings.MeetingTaskName,
                        NameReview.LoadDismissed(_db));

    /// <summary>
    /// « Noms à vérifier » : les propositions locales à cocher, et l'IA en plus sur un clic.
    /// Ce qui est décoché en validant ne revient plus.
    /// </summary>
    private void OpenNameReview()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            var rows = FindNamesToReview().Select(AiSuggestionsWindow.FromReview).ToList();
            var header = rows.Count == 0
                ? "Aucun nom à vérifier pour l'instant."
                : $"{rows.Count} proposition(s) : fautes de frappe et doublons probables, repérés sur ce poste. " +
                  "Une proposition cochée que tu décoches puis valides ne reviendra plus.";
            var window = new AiSuggestionsWindow(header, rows, title: "TimeTracker — Noms à vérifier",
                                                 askAi: _settings.AiEnabled ? AskAiCleanup : null);
            var outcome = window.ShowModal(_dashboard);

            NameReview.Dismiss(_db, outcome.Declined.Select(r => r.Key));
            var (applied, skipped) = NameReview.Apply(_db, outcome.Chosen.Select(r => (r.Merge, r.Rename)),
                                                      _timer.CurrentTask?.Id, "noms à vérifier");
            if (outcome.Chosen.Count + outcome.Declined.Count > 0)
                Logger.Info($"Noms à vérifier : {applied} appliqué(s), {outcome.Declined.Count} écarté(s)" +
                            (skipped.Count > 0 ? $", {skipped.Count} impossible(s)" : "") + ".");
            if (applied > 0)
            {
                _timer.ReloadCurrentTask();
                RefreshTrayState();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("OpenNameReview", ex);
            _tray?.ShowBalloon("Erreur dans la vérification des noms", ex.Message);
        }
        finally
        {
            _modalOpen = false;
        }
    }

    /// <summary>Le nettoyage de l'assistant IA, ajouté à la liste des noms à vérifier (un appel, sur un clic).</summary>
    private async Task<(IReadOnlyList<AiSuggestionsWindow.Row> Rows, string Summary)> AskAiCleanup(CancellationToken cancellation)
    {
        var provider = AiProviderFactory.Create(_settings);
        var usage = _db.GetTaskUsage();
        Logger.Info($"IA : {usage.Count} nom(s) de tâche envoyé(s) à {provider.Name} (noms à vérifier).");
        var proposal = await TaskCleanupAssistant.RunAsync(provider, usage, cancellation);
        Logger.Info($"IA : {proposal.Merges.Count} fusion(s), {proposal.Renames.Count} renommage(s) proposés, " +
                    $"{proposal.Ignored.Count} écarté(s).");
        return (AiSuggestionsWindow.FromProposal(proposal),
                $"{provider.Name} : {proposal.Merges.Count} fusion(s) et {proposal.Renames.Count} renommage(s) ajoutés — " +
                $"envoyé : {usage.Count} noms de tâches.");
    }

    /// <summary>
    /// Tâches que les fenêtres récentes suggèrent, la plus probable d'abord. Vide si la fonction
    /// est coupée ou si rien ne se dégage. Les raisons contiennent des titres de fenêtres : elles
    /// s'affichent à l'utilisateur, elles ne se journalisent pas.
    /// </summary>
    private IReadOnlyList<TaskSuggester.Suggestion> CurrentSuggestions()
    {
        if (_activity is null || !_activity.IsRunning) return Array.Empty<TaskSuggester.Suggestion>();
        try
        {
            return TaskSuggester.Suggest(_db.GetAllTasks(), _activity.Recent(TaskSuggester.Window),
                                         _settings.ActivityLearning ? _db.GetTaskHints() : null);
        }
        catch (Exception ex)
        {
            Logger.Error("CurrentSuggestions", ex);
            return Array.Empty<TaskSuggester.Suggestion>();
        }
    }

    /// <summary>Fin de journée : l'entrée en cours est clôturée (le temps est conservé).</summary>
    private void StopTracking()
    {
        var task = _timer.CurrentTask;
        if (task is null) return;

        var elapsed = _timer.IsPaused ? TimeSpan.Zero : _timer.Elapsed;
        var question = _timer.IsPaused
            ? $"Arrêter le suivi de « {task.Name} » (actuellement en pause) ?"
            : $"Arrêter le suivi de « {task.Name} » ({Format(elapsed)}) ?";

        var answer = MessageBox.Show(
            $"{question}\n\nLe temps déjà pointé est conservé.",
            "Arrêter le suivi", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _timer.Stop();
        Logger.Info($"Suivi arrêté (« {task.Name} »).");
        RefreshTrayState();
    }

    private void OpenDashboard()
    {
        // Une seule instance : si déjà ouverte, on la ramène au premier plan.
        if (_dashboard != null)
        {
            if (_dashboard.WindowState == WindowState.Minimized)
                _dashboard.WindowState = WindowState.Normal;
            _dashboard.Activate();
            return;
        }
        _dashboard = new DashboardWindow(_db, _settings, BuildTrackerActions());
        _dashboard.Closed += (_, _) => _dashboard = null;
        _dashboard.Show();
        _dashboard.Activate();
    }

    /// <summary>Commandes de suivi câblées sur la barre du tableau de bord.</summary>
    private TrackerActions BuildTrackerActions() => new()
    {
        State = () =>
        {
            if (_timer.CurrentTask is null) return ("Aucune tâche en cours", false, false);
            var label = _timer.IsPaused
                ? $"⏸ {_timer.CurrentTask.Name} — en pause"
                : $"▶ {_timer.CurrentTask.Name} — {Format(_timer.Elapsed)}";
            return (label, _timer.IsPaused, true);
        },
        ChangeTask = () => OpenTaskSelector(),
        QuickEdit = OpenQuickEdit,
        TogglePause = TogglePause,
        StopTracking = StopTracking,
        // Une fusion, un renommage ou un préfixe de réunion changé : le compte du bandeau est à refaire.
        ManageTasks = () => { OpenTaskManager(); _namesCountAt = DateTime.MinValue; },
        OpenSettings = () => { OpenSettings(); _namesCountAt = DateTime.MinValue; },
        NamesToReview = CountNamesToReview,
        ReviewNames = () => { OpenNameReview(); _namesCountAt = DateTime.MinValue; }
    };

    private int _namesCount;
    private DateTime _namesCountAt = DateTime.MinValue;

    /// <summary>
    /// Compte du bandeau, recalculé au plus une fois par minute : le tableau de bord le demande à
    /// chaque activation, et la comparaison deux à deux des noms n'est pas gratuite.
    /// </summary>
    private int CountNamesToReview()
    {
        if (DateTime.Now - _namesCountAt < TimeSpan.FromMinutes(1)) return _namesCount;
        // Seules les propositions cochées d'office comptent : les autres (réunion d'un côté
        // seulement) restent visibles dans la liste, sans tenir le bandeau allumé.
        _namesCount = FindNamesToReview().Count(i => i.Recommended);
        _namesCountAt = DateTime.Now;
        return _namesCount;
    }

    /// <summary>Bibliothèque des tâches : renommage, fusion, suppression, favoris.</summary>
    private void OpenTaskManager()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            if (TaskManagerWindow.Show(_dashboard, _db, _timer.CurrentTask?.Id, _settings))
            {
                // Un renommage a pu toucher la tâche en cours : le tray affiche encore l'ancien nom.
                _timer.ReloadCurrentTask();
                RefreshTrayState();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("OpenTaskManager", ex);
            _tray?.ShowBalloon("Erreur dans la gestion des tâches", ex.Message);
        }
        finally
        {
            _modalOpen = false;
        }
    }

    /// <summary>Correction à chaud (raccourci global ou menu du tray).</summary>
    private void OpenQuickEdit()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            QuickEditWindow.Show(_db, _timer);
            RefreshTrayState();
        }
        catch (Exception ex)
        {
            Logger.Error("OpenQuickEdit", ex);
            _tray?.ShowBalloon("Erreur lors de la correction", ex.Message);
        }
        finally
        {
            _modalOpen = false;
        }
    }

    private void OpenSettings()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            if (SettingsWindow.Show(_settings, _db, ApplyHotkeys))
            {
                // Réglages appliqués à chaud : le rappel repart sur le nouvel intervalle.
                _timer.UpdateReminderInterval(_settings.ReminderIntervalMinutes);
                _timer.SnoozeReminder();
                ApplyMeetingDetection();
                ApplyActivityProbe();
                _tray.ShowBalloon("Paramètres enregistrés",
                    $"Rappel toutes les {_settings.ReminderIntervalMinutes} min · " +
                    $"{_settings.HotkeyTask} / {_settings.HotkeyEdit} / {_settings.HotkeyPause} · " +
                    $"réunions {(_settings.MeetingDetection ? "détectées" : "non détectées")}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("OpenSettings", ex);
            _tray?.ShowBalloon("Erreur dans les paramètres", ex.Message);
        }
        finally
        {
            _modalOpen = false;
        }
    }

    // -------------------------------------------------- Détection de réunion

    /// <summary>
    /// Aligne le détecteur sur le réglage courant (appelé au démarrage et après les Paramètres).
    /// </summary>
    private void ApplyMeetingDetection()
    {
        // Avec un journal actif, le détecteur tourne même si la bascule est coupée dans les
        // réglages : une semaine d'observation doit collecter ses données dans tous les cas.
        if (!_settings.MeetingDetection && _meetingTrace is null)
        {
            _meetings?.Stop();
            _timer.RemindersSuspended = false;
            return;
        }

        // Les sondes sont figées à la construction du détecteur : si le réglage d'agenda a
        // changé dans les Paramètres, il faut le reconstruire pour que ça prenne effet.
        if (_meetings != null && (_calendar != null) != _settings.OutlookCalendar)
        {
            _meetings.Stop();
            _meetings.MeetingStarted -= OnMeetingStarted;
            _meetings.MeetingEnded -= OnMeetingEnded;
            _meetings.MeetingUpdated -= OnMeetingUpdated;
            _meetings.MeetingSwitched -= OnMeetingSwitched;
            _meetings.ChoiceNeeded -= OnMeetingChoiceNeeded;
            _meetings = null;
            _calendar = null;
        }

        if (_meetings is null)
        {
            _meetings = MeetingDetector.CreateDefault(_settings, CreateCalendarProbe());
            _meetings.Trace = _meetingTrace;
            _meetings.MeetingStarted += OnMeetingStarted;
            _meetings.MeetingEnded += OnMeetingEnded;
            _meetings.MeetingUpdated += OnMeetingUpdated;
            _meetings.MeetingSwitched += OnMeetingSwitched;
            _meetings.ChoiceNeeded += OnMeetingChoiceNeeded;
        }
        if (!_meetings.IsRunning) _meetings.Start();
    }

    /// <summary>
    /// Sonde d'agenda, ou null si le réglage la coupe. Sa <b>source</b> est branchée ici et
    /// nulle part ailleurs : c'est le seul endroit à toucher le jour où ce poste passe au
    /// « nouveau Outlook » et qu'il faut une source Microsoft Graph à la place du COM.
    /// </summary>
    private CalendarProbe? CreateCalendarProbe()
    {
        _calendar = null;
        if (!_settings.OutlookCalendar) return null;

        _calendar = new CalendarProbe(new OutlookComCalendarSource());
        return _calendar;
    }

    /// <summary>
    /// L'agenda ne permet pas de décider seul, le détecteur demande. Trois questions :
    ///
    /// — <b>des créneaux commencent</b> et rien n'est en cours : faut-il pointer, et lequel ?
    ///   Sans réponse, ou sur un « non », rien ne bascule — mais s'il y assiste quand même, le
    ///   micro déclenchera et la réunion portera le sujet de l'agenda ;
    /// — <b>il est en réunion</b> et plusieurs créneaux la couvrent : laquelle est-ce ? Ne sert
    ///   qu'à nommer ;
    /// — <b>une autre réunion commence</b> pendant la sienne : y passer, ou rester ? Rester ne
    ///   coûte rien ; passer coupe l'entrée à l'heure du nouveau créneau.
    ///
    /// Le cas « connecté en avance, le créneau arrive après » ne passe pas par ici : le
    /// détecteur attache le créneau sans rien demander, la réunion en cours reste la même.
    /// Un créneau qui suit sans concurrent ne passe pas par ici non plus : coupé d'office.
    /// </summary>
    private void OnMeetingChoiceNeeded(MeetingChoice choice)
    {
        try
        {
            var subjects = string.Join(" / ", choice.Candidates.Select(c => $"« {c.Subject} »"));
            var slots = string.Join(", ", choice.Candidates.Select(c => $"{c.Start:HH:mm}–{c.End:HH:mm} {c.Response}"));

            if (!_settings.OutlookAsk || !MeetingSwitchingEnabled)
            {
                Logger.Info($"Agenda : {subjects} — question ({choice.Kind}) désactivée.");
                return;
            }

            Logger.Info($"Agenda : question ({choice.Kind}) posée pour {subjects} ({slots})" +
                        (choice.Current is null ? "" : $", en cours « {choice.Current.Subject} »") + ".");

            CalendarPromptWindow.Ask(choice, _settings.ReminderSound, chosen =>
            {
                _meetings?.Resolve(choice, chosen);
                Logger.Info($"Agenda : {subjects} → " + (chosen is null
                    ? choice.Kind switch
                    {
                        MeetingChoiceKind.Switch => "il reste sur la réunion en cours.",
                        MeetingChoiceKind.Name => "aucune de celles-ci.",
                        _ => "pas de bascule (l'agenda nommera quand même)."
                    }
                    : $"« {chosen.Subject} » " + choice.Kind switch
                    {
                        MeetingChoiceKind.Switch => "— il y passe.",
                        MeetingChoiceKind.Name => "nomme la réunion en cours.",
                        _ => "— bascule acceptée."
                    }));
            });
        }
        catch (Exception ex)
        {
            Logger.Error("OnMeetingChoiceNeeded", ex);
        }
    }

    /// <summary>
    /// Réunion détectée : bascule sur la tâche de réunion, en antidatant au premier indice.
    /// L'entrée porte <c>is_meeting</c>, ce qui la distingue à l'export.
    /// </summary>
    /// <summary>La détection a-t-elle le droit de toucher au relevé de temps ?</summary>
    private bool MeetingSwitchingEnabled => _settings.MeetingDetection && !_meetingObserveOnly;

    /// <summary>
    /// Instant de début à pointer : <b>le plus tôt entre le premier indice et l'heure prévue à
    /// l'agenda</b>. Règle donnée par l'utilisateur le 2026-08-09.
    ///
    /// — connecté en avance, micro détecté à 13:55 pour un créneau 14:00 → l'entrée part de 13:55,
    ///   parce qu'il travaillait déjà sur la réunion ;
    /// — accepté à l'heure, ou rejoint en retard → l'entrée part de l'heure de l'agenda.
    ///
    /// Le garde-fou contre un antidatage abusif reste <c>ClampToCurrentEntry</c> : la réunion ne
    /// peut jamais commencer avant l'entrée en cours, donc jamais rogner ce qu'il a déjà pointé.
    /// </summary>
    private static DateTime MeetingStart(MeetingInfo meeting) =>
        meeting.Calendar is { } calendar && calendar.Start < meeting.StartedAt
            ? calendar.Start
            : meeting.StartedAt;

    private void OnMeetingStarted(MeetingInfo meeting)
    {
        try
        {
            if (!MeetingSwitchingEnabled)
            {
                _meetingTrace?.Event($"Réunion détectée [{meeting.Apps}] « {meeting.Label} » " +
                                     $"depuis {meeting.StartedAt:HH:mm:ss} — observation, aucune bascule.");
                Logger.Info($"Réunion observée [{meeting.Apps}] « {meeting.Label} » (aucune bascule).");
                _tray.ShowBalloon("Réunion détectée (observation)",
                    $"{meeting.Label}\nRelevé pour diagnostic, le suivi n'est pas modifié.");
                return;
            }

            // Une proposition de changement n'a plus de sens : la réunion est le changement.
            _shiftWindow?.Expire("réunion");

            // Un suivi en pause ou à l'arrêt ne donne rien à « reprendre » après la réunion :
            // mieux vaut laisser le rappel reposer la question que redémarrer une tâche au hasard.
            _taskBeforeMeeting = _timer.IsPaused ? null : _timer.CurrentTask;

            var task = _db.GetOrCreateTask(_settings.MeetingTaskName);
            // La réunion a pu commencer avant la tâche en cours (« j'ai pointé pendant l'appel ») :
            // sans borne, l'entrée en cours serait clôturée avant son propre début.
            var start = ClampToCurrentEntry(MeetingStart(meeting));
            _timer.StartTask(task, start, isMeeting: true);
            _meetingEntryId = _timer.CurrentEntryId;
            _meetingTaskId = task.Id;
            _timer.RemindersSuspended = true;

            var summary = $"Réunion détectée [{meeting.Apps}] « {meeting.Label} » " +
                          $"→ « {task.Name} » depuis {start:HH:mm:ss}" +
                          (_taskBeforeMeeting is null ? "" : $", retour prévu sur « {_taskBeforeMeeting.Name} »");
            Logger.Info(summary);
            _meetingTrace?.Event(summary);
            NameRunningMeeting(meeting);

            var resume = _taskBeforeMeeting is null
                ? ""
                : $" « {_taskBeforeMeeting.Name} » reprendra à la fin.";
            _tray.ShowBalloon("Réunion détectée",
                $"{meeting.Label}\nPointé sur « {task.Name} » depuis {start:HH:mm}.{resume}\n" +
                "Clique ici si ce n'en est pas une.",
                onClick: CancelDetectedMeeting);

            RefreshTrayState();
        }
        catch (Exception ex)
        {
            Logger.Error("OnMeetingStarted", ex);
        }
    }

    /// <summary>
    /// Nomme l'entrée de réunion <b>pendant</b> la réunion, dès qu'un titre exploitable est
    /// connu — et il l'est souvent dès le départ, l'agenda donnant son sujet à l'heure du créneau.
    ///
    /// Le nommage en fin de réunion (<see cref="ApplyMeetingTitle"/>) restait invisible tant
    /// que la réunion durait : le tray affichait « Réunion — 0:12 », et l'utilisateur a renommé
    /// l'entrée à la main quinze fois en cinq semaines (« Réunion non renommé automatiquement
    /// (C'était "…") », 2026-08-10), après quoi la fin de réunion trouvait l'entrée « déjà
    /// nommée » et ne touchait plus rien. Ce qu'on sait doit être visible là où il regarde.
    ///
    /// Mêmes retenues qu'en fin de réunion : titre générique → rien ; entrée réaffectée par
    /// l'utilisateur (tâche ≠ celle qu'on lui a donnée) → on ne repasse pas derrière lui.
    /// </summary>
    private void NameRunningMeeting(MeetingInfo meeting)
    {
        if (_meetingEntryId is null || _meetingEntryId != _timer.CurrentEntryId || _meetingTaskId is null) return;
        if (_timer.CurrentTask?.Id != _meetingTaskId) return;   // renommée à la main

        var title = meeting.Title;
        if (string.IsNullOrWhiteSpace(title) || MeetingWindowProbe.IsGenericTitle(title)) return;

        var task = _db.GetOrCreateTask($"{_settings.MeetingTaskName} — {Shorten(title, 60)}");
        if (task.Id == _meetingTaskId) return;

        _timer.ReassignCurrentTask(task);
        _meetingTaskId = task.Id;
        Logger.Info($"Réunion en cours nommée : entrée {_meetingEntryId} → « {task.Name} ».");
        RefreshTrayState();
    }

    /// <summary>Le titre de la réunion en cours a changé (agenda arrivé, fenêtre renommée).</summary>
    private void OnMeetingUpdated(MeetingInfo meeting)
    {
        try
        {
            if (!MeetingSwitchingEnabled) return;
            NameRunningMeeting(meeting);
        }
        catch (Exception ex)
        {
            Logger.Error("OnMeetingUpdated", ex);
        }
    }

    /// <summary>
    /// Une réunion en remplace une autre sans interruption : l'entrée de la première est
    /// clôturée à la frontière et une nouvelle s'ouvre, <b>sans repasser par la tâche d'avant</b>
    /// (elle reprendra à la fin de la dernière). Le titre de la première est posé comme en fin
    /// de réunion. Collecte du 2026-08-10 au 09-14 : neuf enchaînements fondus en une entrée.
    /// </summary>
    private void OnMeetingSwitched(MeetingInfo finished, MeetingInfo started)
    {
        try
        {
            if (!MeetingSwitchingEnabled)
            {
                _meetingTrace?.Event($"Réunion suivante « {started.Label} » à {started.StartedAt:HH:mm:ss} — observation.");
                return;
            }

            var previousEntryId = _meetingEntryId;
            var previousTaskId = _meetingTaskId;

            // Le suivi a pu être arrêté pendant la réunion : on ne redémarre alors rien, la
            // réunion suivante se comporte comme une détection sur un suivi à l'arrêt.
            var task = _db.GetOrCreateTask(_settings.MeetingTaskName);
            var at = ClampToCurrentEntry(started.StartedAt);
            _timer.StartTask(task, at, isMeeting: true);
            _meetingEntryId = _timer.CurrentEntryId;
            _meetingTaskId = task.Id;
            _timer.RemindersSuspended = true;

            var named = ApplyMeetingTitle(finished, previousEntryId, previousTaskId);

            var summary = $"Réunion suivante « {started.Label} » à {at:HH:mm:ss} — " +
                          $"« {finished.Label} » clôturée ({Format(at - finished.StartedAt)})" +
                          (named is null ? "" : $", pointée sur « {named} »") + ".";
            Logger.Info(summary);
            _meetingTrace?.Event(summary);
            NameRunningMeeting(started);

            _tray.ShowBalloon("Réunion suivante",
                $"{started.Label}\n« {finished.Label} » clôturée à {at:HH:mm}.");
            RefreshTrayState();
        }
        catch (Exception ex)
        {
            Logger.Error("OnMeetingSwitched", ex);
        }
    }

    /// <summary>
    /// Fin de réunion : reprise de la tâche d'avant à l'instant réel de fin (pas à la fin du
    /// délai de grâce, sinon la reprise perdrait ces minutes).
    /// </summary>
    private void OnMeetingEnded(MeetingInfo meeting)
    {
        try
        {
            if (!MeetingSwitchingEnabled)
            {
                _meetingTrace?.Event($"Fin de réunion « {meeting.Label} » " +
                                     $"({Format(meeting.Duration)}) — observation.");
                return;
            }

            _timer.RemindersSuspended = false;
            var end = ClampToCurrentEntry(meeting.EndedAt ?? DateTime.Now);
            var duration = Format(end - meeting.StartedAt);

            // Relevés avant la reprise : StartTask va clôturer l'entrée de réunion et en ouvrir
            // une autre, or c'est celle-là qu'on proposera de nommer.
            var meetingEntryId = _meetingEntryId;
            var meetingTaskId = _meetingTaskId;
            _meetingEntryId = null;
            _meetingTaskId = null;

            // Le suivi a pu être arrêté (ou mis en pause) entre-temps : il n'y a alors ni tâche
            // à reprendre ni entrée à clôturer. Vu pendant la collecte le 2026-07-30 — suivi
            // arrêté à 18:35, machine en veille, fin de réunion conclue au réveil à 22:41 : la
            // tâche d'avant avait été rouverte à 18:40 et a couru jusqu'au lendemain 08:47.
            bool trackingLive = _timer.CurrentEntryId.HasValue;
            string outcome, resumeText;

            if (trackingLive && _taskBeforeMeeting is not null)
            {
                _timer.StartTask(_taskBeforeMeeting, end);
                outcome = $"retour sur « {_taskBeforeMeeting.Name} »";
                resumeText = $" · {outcome}.";
            }
            else if (trackingLive)
            {
                _timer.Stop(end);
                outcome = "suivi arrêté";
                resumeText = ". Aucune tâche à reprendre : le suivi est arrêté.";
            }
            else
            {
                outcome = "suivi déjà arrêté, rien repris";
                resumeText = ". Le suivi était déjà arrêté : rien n'a été repris.";
            }

            // Ce que la détection a appris est écrit sur l'entrée, pas proposé : la bulle ne
            // fait plus que rendre compte (voir ApplyMeetingTitle).
            var named = ApplyMeetingTitle(meeting, meetingEntryId, meetingTaskId);
            var namedText = named is null ? "" : $"\nPointée sur « {named} ».";
            _tray.ShowBalloon("Fin de réunion", $"{duration} pointés en réunion{resumeText}{namedText}");

            Logger.Info($"Fin de réunion « {meeting.Label} » ({duration}) à {end:HH:mm:ss} — {outcome}.");
            _meetingTrace?.Event($"Fin de réunion « {meeting.Label} » ({duration}) à {end:HH:mm:ss}, {outcome}.");
            _taskBeforeMeeting = null;
            RefreshTrayState();
        }
        catch (Exception ex)
        {
            Logger.Error("OnMeetingEnded", ex);
        }
    }

    /// <summary>
    /// Pose sur l'entrée de réunion ce que la détection a appris : le titre relevé en commentaire,
    /// et le nom de la tâche quand ce titre est exploitable. Renvoie le nom retenu, ou null.
    ///
    /// <b>Écrit, et non plus proposé.</b> Le titre n'existait que dans une bulle de la barre
    /// système ; la semaine du 2026-08-03 au 07 a montré que cette bulle n'arrive pas jusqu'à
    /// l'utilisateur — 9 propositions, 0 clic, et 12 réunions sur 13 renommées de mémoire le soir
    /// dans le tableau de bord, alors que l'appli tenait le bon titre pour 8 d'entre elles.
    ///
    /// Trois retenues, héritées de la collecte précédente :
    /// — pas de titre, ou un titre <b>générique</b> : c'est le cas de tout Zoom, qui n'expose
    ///   jamais le sujet de sa réunion. « Réunion — Zoom Meeting » ne rend service à personne ;
    /// — l'entrée a déjà changé de tâche : l'utilisateur l'a nommée lui-même pendant la réunion.
    ///   Le commentaire est quand même posé, mais son nom à lui n'est pas touché ;
    /// — l'entrée a disparu (bascule annulée, suppression).
    ///
    /// ⚠️ Jamais un renommage de la tâche « Réunion » elle-même : il réécrirait le libellé de
    /// toutes les réunions déjà pointées. L'entrée visée est forcément clôturée à cet instant
    /// (la fin de réunion l'a fermée), donc le <c>TimerService</c> n'est pas concerné.
    /// </summary>
    private string? ApplyMeetingTitle(MeetingInfo meeting, long? entryId, long? meetingTaskId)
    {
        if (entryId is not long id) return null;

        var title = meeting.Title;
        if (string.IsNullOrWhiteSpace(title) || MeetingWindowProbe.IsGenericTitle(title)) return null;

        var entry = _db.GetEntry(id);
        if (entry is null) return null;

        _db.UpdateEntryNotes(id, title);

        if (meetingTaskId is null || entry.TaskId != meetingTaskId)
        {
            Logger.Info($"Titre de réunion noté sur l'entrée {id} : « {title} » " +
                        "— tâche laissée telle quelle, déjà nommée.");
            return null;
        }

        var task = _db.GetOrCreateTask($"{_settings.MeetingTaskName} — {Shorten(title, 60)}");
        if (entry.TaskId == task.Id) return task.Name;   // déjà nommée pendant la réunion

        _db.UpdateEntryTask(id, task.Id);
        _db.TouchTask(task.Id);

        Logger.Info($"Réunion nommée : entrée {id} → « {task.Name} » (titre relevé : « {title} »).");
        return task.Name;
    }

    /// <summary>Coupe un titre trop long sur un mot, pour que le sélecteur reste lisible.</summary>
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd() + "…";
    }

    /// <summary>
    /// L'utilisateur a cliqué la bulle : ce n'était pas une réunion. La bascule est défaite et
    /// le détecteur se tait jusqu'à la fin des indices en cours.
    /// </summary>
    private void CancelDetectedMeeting()
    {
        try
        {
            _meetings?.IgnoreCurrentMeeting();
            _timer.RemindersSuspended = false;
            _taskBeforeMeeting = null;
            _meetingEntryId = null;
            _meetingTaskId = null;

            if (_timer.UndoAutoSwitch())
                _tray.ShowBalloon("Bascule annulée",
                    $"Retour sur « {_timer.CurrentTask?.Name} », le temps lui est rendu.");
            else
                _tray.ShowBalloon("Bascule annulée",
                    "Aucune tâche précédente à reprendre : le suivi est arrêté.");

            Logger.Info("Bascule de réunion annulée par l'utilisateur.");
            // Précieux au dépouillement : marque un faux positif de la détection.
            _meetingTrace?.Event("FAUX POSITIF — bascule annulée par l'utilisateur.");
            RefreshTrayState();
        }
        catch (Exception ex)
        {
            Logger.Error("CancelDetectedMeeting", ex);
        }
    }

    /// <summary>
    /// Rappel périodique. Affiché <b>sans modalité</b> : un <c>ShowDialog()</c> bloquait toutes
    /// les fenêtres de l'application, et comme ce rappel n'apparaît ni dans la barre des tâches
    /// ni dans Alt-Tab, la moindre fois où il ne s'affichait pas (réveil de veille) laissait le
    /// tableau de bord définitivement incliquable. Journalisé aussi : sans trace à l'affichage,
    /// le blocage était indiagnosticable depuis les journaux.
    /// </summary>
    private void OnReminderDue(TaskItem task)
    {
        if (_reminderOpen) return;
        // Une proposition de changement est à l'écran : c'est déjà la question, en mieux.
        if (_shiftWindow != null) { _timer.SnoozeReminder(); return; }
        _reminderOpen = true;
        Logger.Info($"Rappel affiché (« {task.Name} »).");

        _reminderPopup = ReminderPopup.Ask(task.Name, _settings.ReminderSound, keepGoing =>
        {
            _reminderOpen = false;
            _reminderPopup = null;
            if (keepGoing)
                _timer.SnoozeReminder();
            else
                // « Non, je change » : le basculement a pu avoir lieu avant le rappel,
                // d'où la proposition de décaler le début de 5 ou 15 minutes.
                OpenTaskSelector(offerBackdate: true);
        });
    }

    /// <summary>Rappel pendant une pause : ce temps n'est compté nulle part, on le signale.</summary>
    private void OnPausedReminderDue(TaskItem task)
    {
        if (_reminderOpen) return;
        _reminderOpen = true;
        Logger.Info($"Rappel de pause affiché (« {task.Name} »).");

        ReminderPopup.AskPaused(task.Name, _settings.ReminderSound,
            DateTime.Now - _timer.PausedSince, choice =>
        {
            _reminderOpen = false;
            if (choice == ReminderChoice.Resume)
            {
                _timer.Resume();
                RefreshTrayState();
            }
            else if (choice == ReminderChoice.Change)
            {
                OpenTaskSelector();
            }
            else
            {
                _timer.SnoozeReminder();
            }
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnMachineLeft("veille", suspended: true);
        else if (e.Mode == PowerModes.Resume) OnMachineWokeUp("réveil de veille");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            OnMachineLeft("verrouillage", suspended: false);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            OnMachineWokeUp("session déverrouillée");
    }

    // Instant où le poste a été quitté (verrouillé, puis souvent endormi), et si la veille a
    // eu lieu : pendant une veille le détecteur ne tourne pas, son état n'est plus fiable.
    private DateTime? _awaySince;
    private string _awayReason = "";
    private bool _awaySuspended;

    /// <summary>Le poste est quitté : on note l'instant, on ne touche encore à rien.</summary>
    private void OnMachineLeft(string reason, bool suspended)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Un verrouillage suivi d'une veille : c'est le verrouillage qui date le départ.
            if (_awaySince is null) { _awaySince = DateTime.Now; _awayReason = reason; }
            if (suspended) _awaySuspended = true;
        });
    }

    /// <summary>
    /// Repousse le prochain rappel au réveil de la machine, puis règle l'absence.
    ///
    /// L'utilisateur ne l'éteint pas tous les soirs et oublie parfois d'arrêter sa dernière
    /// tâche : au réveil, le tick suivant voyait des heures écoulées depuis le dernier rappel et
    /// en déclenchait un <b>immédiatement</b>, pendant que l'affichage se reconfigure. Un intervalle
    /// complet de plus ne coûte rien ; le rappel au pire moment coûtait la matinée.
    ///
    /// ⚠️ <see cref="SystemEvents"/> notifie sur un thread à lui : tout doit repasser par le
    /// dispatcher, le <c>TimerService</c> n'étant pas prévu pour être touché de l'extérieur.
    /// </summary>
    private void OnMachineWokeUp(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _timer?.SnoozeReminder();
                Logger.Info($"Rappel repoussé ({reason}).");
                _lastWake = DateTime.Now;
                // Une proposition ouverte avant la veille parle d'une activité d'hier soir.
                if (_shiftWindow != null && DateTime.Now - _shiftWindow.OpenedAt > ActivityShiftWindow.Lifetime)
                    _shiftWindow.Expire("périmée au réveil");
                HandleAbsence(DateTime.Now);
            }
            catch (Exception ex)
            {
                Logger.Error("OnMachineWokeUp", ex);
            }
        });
    }

    /// <summary>
    /// Une absence assez longue clôture la tâche en cours <b>à l'heure du départ</b> et met le
    /// suivi en pause. Cinq semaines de journaux montrent la même correction chaque matin : une
    /// entrée partie la veille à 17h et arrêtée à la main le lendemain (« API Z3 17:39→19:39 »,
    /// « Config Tessal 17:18→19:00 »…). L'heure du verrouillage est la vérité, pas celle du réveil.
    ///
    /// Rien n'est ouvert à l'écran ici — c'est la règle du réveil (HANDOFF §8). Le rappel
    /// « toujours en pause ? », non modal, arrive deux minutes plus tard et propose de reprendre
    /// ou de changer de tâche : un trou de deux minutes au matin, contre une nuit à corriger.
    ///
    /// Une absence <b>sans veille</b> pendant une réunion en cours n'est pas coupée : le
    /// détecteur a continué de tourner, il sait qu'elle dure — l'utilisateur est en salle,
    /// portable verrouillé, sur une réunion acceptée à l'agenda.
    /// </summary>
    private void HandleAbsence(DateTime now)
    {
        if (_awaySince is not DateTime since) return;
        var reason = _awayReason + (_awaySuspended && _awayReason != "veille" ? " puis veille" : "");
        bool suspended = _awaySuspended;
        _awaySince = null;
        _awayReason = "";
        _awaySuspended = false;

        var away = now - since;
        int cutoff = _settings.AbsenceCutoffMinutes;
        if (cutoff <= 0 || away.TotalMinutes < cutoff) return;
        if (_timer is null || !_timer.HasActiveTask) return;

        if (!suspended && _meetings?.InMeeting == true)
        {
            Logger.Info($"Absence de {since:HH:mm} à {now:HH:mm} ({reason}) pendant une réunion : rien clôturé.");
            return;
        }

        var task = _timer.CurrentTask!;
        _timer.PauseAt(since, remindIn: TimeSpan.FromMinutes(2));
        Logger.Info($"Absence de {since:HH:mm} à {now:HH:mm} ({reason}, {Format(away)}) : " +
                    $"« {task.Name} » clôturée à {since:HH:mm}, suivi en pause.");
        RefreshTrayState();
    }

    private void OnTimerTick(TaskItem task, TimeSpan elapsed)
    {
        _tray.UpdateTooltip($"{task.Name} — {Format(elapsed)}");
    }

    private void OnCurrentTaskChanged(TaskItem? task)
    {
        // Changement fait ailleurs (sélecteur, correction, pause) : la proposition est dépassée.
        _shiftWindow?.Expire("tâche changée ailleurs");
        _namesCountAt = DateTime.MinValue;   // une tâche a pu être créée
        RefreshTrayState();
    }

    private void RefreshTrayState()
    {
        if (_tray is null) return;   // --selftest : ni barre système ni icône
        if (_timer.CurrentTask is null)
        {
            _tray.UpdateTooltip("TimeTracker — aucune tâche");
            _tray.SetCurrentTask("Aucune tâche", paused: false, hasTask: false);
        }
        else
        {
            var label = _timer.IsPaused
                ? $"{_timer.CurrentTask.Name} (en pause)"
                : $"{_timer.CurrentTask.Name} — {Format(_timer.Elapsed)}";
            _tray.UpdateTooltip(label);
            _tray.SetCurrentTask(_timer.CurrentTask.Name, _timer.IsPaused);
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}" : $"{t.Minutes}min";

    private void SetupExceptionHandling()
    {
        // Exceptions sur le thread UI : on journalise et on garde l'appli en vie.
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("DispatcherUnhandledException", args.Exception);
            _tray?.ShowBalloon("Une erreur est survenue",
                "Détails enregistrés dans le journal. L'application reste active.");
            args.Handled = true;
        };
        // Exceptions sur d'autres threads : au moins les journaliser.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("AppDomain.UnhandledException", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// Exerce le chemin « 2e changement de tâche » (clôture entrée précédente + nouvelle)
    /// sans interface, pour isoler un éventuel plantage côté services. Lancé via --selftest.
    /// </summary>
    private void RunSelfTest()
    {
        try
        {
            Logger.Info("SELFTEST début");
            var a = _db.GetOrCreateTask("Selftest A");
            var timer = new TimerService(_db, 30);
            timer.StartTask(a);
            var b = _db.GetOrCreateTask("Selftest B");
            timer.StartTask(b);            // <-- chemin EndEntry de l'entrée précédente

            // Correction à chaud : antidate le début (rogne l'entrée précédente) puis réaffecte.
            timer.AdjustCurrentStart(DateTime.Now.AddMinutes(-5));
            timer.ReassignCurrentTask(a);

            // Pause puis arrêt de fin de journée : l'entrée doit être clôturée, pas supprimée.
            timer.Pause();
            timer.Resume();
            timer.Stop();
            var stopOk = timer.CurrentTask is null && _db.GetOpenEntry() is null;

            // Bibliothèque de tâches : favori, renommage, fusion, suppression d'une inutilisée.
            _db.SetFavorite(a.Id, true);
            var favoriteOk = _db.GetFavoriteTasks().Any(t => t.Id == a.Id);
            _db.UpdateTaskName(b.Id, "Selftest B renommée");
            var renameOk = _db.GetTask(b.Id)?.Name == "Selftest B renommée";

            var entriesBefore = _db.CountEntriesForTask(a.Id) + _db.CountEntriesForTask(b.Id);
            _db.MergeTasks(b.Id, a.Id);
            var mergeOk = _db.GetTask(b.Id) is null
                          && _db.CountEntriesForTask(a.Id) == entriesBefore;

            var orphan = _db.GetOrCreateTask("Selftest orpheline");
            _db.DeleteTask(orphan.Id);
            var deleteOk = _db.GetTask(orphan.Id) is null;

            var usage = _db.GetTaskUsage();

            // Détection de réunion : la machine à états est déroulée avec une sonde scriptée
            // et des délais à zéro (impossible de déclencher une vraie réunion depuis un test).
            var probe = new ScriptedMeetingProbe();
            var meetingSettings = new AppSettings { MeetingStartDelaySeconds = 0, MeetingEndGraceSeconds = 0 };
            var detector = new MeetingDetector(meetingSettings, new IMeetingProbe[] { probe });
            MeetingInfo? startedMeeting = null, endedMeeting = null;
            detector.MeetingStarted += m => startedMeeting = m;
            detector.MeetingEnded += m => endedMeeting = m;

            probe.InMeeting = true;
            probe.Title = "Point hebdo | Microsoft Teams";
            detector.PollOnce();
            bool inMeeting = detector.InMeeting;
            probe.InMeeting = false;
            detector.PollOnce();

            var detectOk = startedMeeting != null && inMeeting
                           && endedMeeting != null && !detector.InMeeting
                           && endedMeeting.Title == "Point hebdo | Microsoft Teams";

            // Un libellé générique doit céder la place à un vrai titre qui arrive après coup :
            // pendant la collecte, la fenêtre « Zoom Workplace » — ouverte en permanence —
            // nommait des réunions Teams parce qu'elle était vue la première.
            var lateProbe = new ScriptedMeetingProbe { InMeeting = true, Title = "Zoom Workplace" };
            var lateDetector = new MeetingDetector(meetingSettings, new IMeetingProbe[] { lateProbe });
            MeetingInfo? lateMeeting = null;
            lateDetector.MeetingStarted += m => lateMeeting = m;
            lateDetector.PollOnce();
            var genericFirst = lateMeeting?.Title;
            lateProbe.Title = "Point Hebdo PRJ - Velmora | Microsoft Teams";
            lateDetector.PollOnce();
            // La sonde scriptée court-circuite MeetingWindowProbe : le titre reste brut ici,
            // c'est le classement générique/non générique qui est vérifié, pas le nettoyage.
            var lateTitleOk = genericFirst == "Zoom Workplace"
                              && lateMeeting?.Title == "Point Hebdo PRJ - Velmora | Microsoft Teams";

            // Bascule automatique puis annulation : l'entrée de réunion disparaît, la précédente
            // est rouverte et redevient l'entrée en cours (le temps lui est rendu).
            var meetingTimer = new TimerService(_db, 30);
            meetingTimer.StartTask(a);
            var beforeId = meetingTimer.CurrentEntryId;
            meetingTimer.StartTask(_db.GetOrCreateTask("Selftest réunion"), DateTime.Now, isMeeting: true);
            var meetingFlagOk = _db.GetOpenEntry()?.IsMeeting == true;
            var undoOk = meetingTimer.UndoAutoSwitch()
                         && meetingTimer.CurrentEntryId == beforeId
                         && _db.GetOpenEntry()?.Id == beforeId;
            meetingTimer.Stop();

            // Nettoyage des titres : compteur de notifications et marque du navigateur retirés.
            // Le ​ est bien réel : Edge glisse une espace de largeur nulle dans sa marque.
            var titleOk = MeetingWindowProbe.CleanTitle(
                "(3) Meet - abc-defg-hij - Personnel – Microsoft​ Edge") == "Meet - abc-defg-hij";

            // Libellés : tous les cas viennent de titres relevés pendant les semaines de collecte.
            var labelOk =
                MeetingWindowProbe.ShortLabel("point RFE Orvane | Microsoft Teams") == "point RFE Orvane"
                && MeetingWindowProbe.ShortLabel("Meeting join | Point Hebdo PRJ - Velmora | Microsoft Teams")
                   == "Point Hebdo PRJ - Velmora"
                && MeetingWindowProbe.ShortLabel("Huddle: @Camille Durand - Contoso - Slack 🎤")
                   == "Huddle: @Camille Durand"
                && MeetingWindowProbe.ShortLabel("Meet – PROJ_ATLAS_Migration and 4 more pages")
                   == "Meet – PROJ_ATLAS_Migration"
                // Onglet Chat de Teams (2026-08-05 et 06) : préfixe et mention externe retirés.
                && MeetingWindowProbe.ShortLabel(
                       "Chat | Velmora / Contoso Discovery Call # 1 (External) | Microsoft Teams")
                   == "Velmora / Contoso Discovery Call # 1"
                && MeetingWindowProbe.IsGenericTitle("Zoom Workplace")
                && MeetingWindowProbe.IsGenericTitle("Zoom Meeting")
                && MeetingWindowProbe.IsGenericTitle("Microsoft Teams")
                && !MeetingWindowProbe.IsGenericTitle("Huddle: @Camille Durand")
                // Le faux titre de la barre d'outils Zoom, qui a nommé 5 réunions sur 13.
                && MeetingWindowProbe.EchoesClassName("ZPToolBarParentWnd", "ZPToolBarParentWndClass")
                && MeetingWindowProbe.EchoesClassName("VideoFrameWnd", "VideoFrameWndClass")
                && !MeetingWindowProbe.EchoesClassName("Zoom Meeting", "ConfMultiTabContentWndClass");

            // Fin de réunion alors que le suivi a été arrêté entre-temps : rien ne doit être
            // rouvert (le 2026-07-30, une tâche rouverte au réveil a couru toute la nuit).
            var stoppedTimer = new TimerService(_db, 30);
            stoppedTimer.StartTask(a);
            stoppedTimer.StartTask(_db.GetOrCreateTask("Selftest réunion"), DateTime.Now, isMeeting: true);
            stoppedTimer.Stop();
            var afterStopOk = stoppedTimer.CurrentEntryId is null && _db.GetOpenEntry() is null;

            // Nommer une réunion : l'entrée est RÉAFFECTÉE à une tâche portant le titre relevé.
            // La tâche « Réunion » ne doit pas bouger — la renommer réécrirait le libellé de
            // toutes les réunions déjà pointées.
            // ⚠️ Nom distinct de « Selftest réunion » ci-dessus : GetOrCreateTask compare en
            // COLLATE NOCASE, donc une simple différence de casse retomberait sur la même tâche.
            const string genericName = "Selftest libellé réunion";
            var genericTask = _db.GetOrCreateTask(genericName);
            var meetingEntry = _db.StartEntry(genericTask.Id, DateTime.Now.AddHours(-2), isMeeting: true);
            _db.EndEntry(meetingEntry, DateTime.Now.AddHours(-1));
            var namedTask = _db.GetOrCreateTask($"{genericName} — Point hebdo");
            _db.UpdateEntryTask(meetingEntry, namedTask.Id);
            _db.UpdateEntryNotes(meetingEntry, "Point hebdo");
            var renamed = _db.GetEntry(meetingEntry);
            var nameMeetingOk = renamed != null && renamed.TaskId == namedTask.Id && renamed.IsMeeting
                                && namedTask.Id != genericTask.Id
                                && _db.GetTask(genericTask.Id)?.Name == genericName
                                // Le titre relevé survit à la réaffectation : c'est lui qui reste
                                // lisible dans l'export et l'infobulle quand l'utilisateur a
                                // renommé l'entrée lui-même.
                                && renamed.Notes == "Point hebdo";

            // Ce que la détection écrit sur l'entrée, dans les trois cas de la semaine du
            // 2026-08-03 : titre exploitable (Teams/Slack), titre générique (tout Zoom), et
            // entrée que l'utilisateur avait déjà nommée lui-même pendant la réunion.
            var appliedOk = CheckApplyMeetingTitle();

            // Agenda : coopération avec la détection automatique (connexion en avance, refus
            // suivi d'une participation, heure de début).
            var calendarOk = CheckCalendarCooperation();
            var sequencesOk = CheckMeetingSequences();
            var absenceOk = CheckAbsenceCutoff();
            var duplicatesOk = CheckTaskSimilarity();
            var namesOk = CheckNameHygiene();
            var suggestOk = CheckTaskSuggester();
            var shiftOk = CheckShiftDetector() && CheckShiftWiring() && CheckShiftResume();
            var selectorOk = CheckSelectorOrder();
            var learningOk = CheckActivityLearning();
            var aiOk = CheckAiAssistant();
            var activityAiOk = CheckActivityAi();
            // Objectif hebdo : aller-retour en base avec une décimale, quelle que soit la culture.
            var goalSettings = _db.LoadSettings();
            goalSettings.DailyHoursGoal = 7.5;
            goalSettings.GoalPerWeek = true;
            goalSettings.HotkeyPause = "Ctrl+Alt+P";
            _db.SaveSettings(goalSettings);
            var goalBack = _db.LoadSettings();
            var goalOk = Math.Abs(goalBack.DailyHoursGoal - 7.5) < 0.001
                         && Math.Abs(goalBack.WeeklyHoursGoal - 37.5) < 0.001
                         && goalBack.GoalPerWeek
                         && goalBack.HotkeyPause == "Ctrl+Alt+P";
            // Base d'avant la v1.6.2 : seule l'ancienne clé hebdomadaire existe → 37,5 devient 7,5 par jour.
            _db.SetSetting(AppSettings.KeyWeeklyHoursGoal, "37.5");
            _db.DeleteSetting(AppSettings.KeyDailyHoursGoal);
            _db.DeleteSetting(AppSettings.KeyGoalPerWeek);
            var migrated = _db.LoadSettings();
            goalOk = goalOk && Math.Abs(migrated.DailyHoursGoal - 7.5) < 0.001 && migrated.GoalPerWeek;

            // Un titre long est coupé sur un mot, sinon le sélecteur devient illisible.
            const string longTitle =
                "Transformez votre énergie solaire avec la IQ Battery 5P – Webinaire exclusif Enphase";
            var shortened = Shorten(longTitle, 60);
            var shortenOk = shortened.Length <= 61 && shortened.EndsWith('…')
                            && longTitle.StartsWith(shortened[..^1])
                            && Shorten("Point hebdo", 60) == "Point hebdo";

            var recent = _db.GetRecentTasks(10);
            var today = _db.GetEntriesForDay(DateTime.Now);
            var weekStart = DateTime.Now.Date.AddDays(-((int)DateTime.Now.DayOfWeek + 6) % 7);
            var week = _db.GetEntriesForRange(weekStart, weekStart.AddDays(7));

            // Export : on écrit dans le dossier temporaire, pas chez l'utilisateur.
            var tmp = Path.Combine(Path.GetTempPath(), $"timetracker_selftest_{Guid.NewGuid():N}");
            ExportService.ExportCsv(today, tmp + ".csv");
            ExportService.ExportXlsx(week, tmp + ".xlsx", weekStart, weekStart.AddDays(6));
            var csvSize = new FileInfo(tmp + ".csv").Length;
            var xlsxSize = new FileInfo(tmp + ".xlsx").Length;
            File.Delete(tmp + ".csv");
            File.Delete(tmp + ".xlsx");

            // Réglages : aller-retour complet en base (SaveSettings était jamais appelé en beta).
            var before = _db.LoadSettings();
            _db.SaveSettings(before);
            var after = _db.LoadSettings();
            var settingsOk = after.ReminderIntervalMinutes == before.ReminderIntervalMinutes
                             && after.HotkeyTask == before.HotkeyTask;

            // Raccourcis : parsing aller-retour.
            var hotkeyOk = Hotkey.TryParse("Ctrl+Alt+T", out var hk) && hk.ToString() == "Ctrl+Alt+T"
                           && TimeInput.TryParse("9h05", out var t) && t == new TimeSpan(9, 5, 0);

            // Le verdict en tête de ligne : il disait « OK » même avec un « doublons=False » (29/09).
            var summary = $"recent={recent.Count}, entrées_jour={today.Count}, " +
                        $"entrées_semaine={week.Count}, csv={csvSize}o, xlsx={xlsxSize}o, " +
                        $"réglages={settingsOk}, raccourcis/heures={hotkeyOk}, " +
                        $"arrêt={stopOk}, favori={favoriteOk}, renommage={renameOk}, " +
                        $"fusion={mergeOk}, suppression={deleteOk}, tâches_listées={usage.Count}, " +
                        $"réunion_détection={detectOk}, réunion_drapeau={meetingFlagOk}, " +
                        $"réunion_annulation={undoOk}, réunion_titre={titleOk}, " +
                        $"réunion_libellé={labelOk}, réunion_titre_tardif={lateTitleOk}, " +
                        $"réunion_après_arrêt={afterStopOk}, réunion_nommée={nameMeetingOk}, " +
                        $"réunion_titre_posé={appliedOk}, agenda={calendarOk}, " +
                        $"enchaînements={sequencesOk}, absence={absenceOk}, " +
                        $"doublons={duplicatesOk}, noms={namesOk}, suggestions={suggestOk}, changement={shiftOk}, " +
                        $"sélecteur={selectorOk}, " +
                        $"apprentissage={learningOk}, objectif={goalOk}, IA={aiOk}, IA_activité={activityAiOk}, " +
                        $"titre_tronqué={shortenOk}";
            Logger.Info($"SELFTEST {(summary.Contains("=False") ? "ÉCHEC" : "OK")} : {summary}");
        }
        catch (Exception ex)
        {
            Logger.Error("SELFTEST ÉCHEC", ex);
        }
    }

    /// <summary>
    /// Déroule la coopération agenda ↔ détection automatique sur les trois cas décrits par
    /// l'utilisateur le 2026-08-09. C'est le seul endroit où cette logique est vérifiable :
    /// ce poste n'a pas de profil Outlook.
    /// </summary>
    private bool CheckCalendarCooperation()
    {
        var settings = new AppSettings { MeetingStartDelaySeconds = 0, MeetingEndGraceSeconds = 0 };
        var now = DateTime.Now;

        CalendarMeeting Slot(string id, string subject) => new()
        {
            Id = id,
            Subject = subject,
            Start = now.AddMinutes(-1),
            End = now.AddHours(1),
            Response = CalendarResponse.SansReponse
        };

        // --- Cas 1 : connecté en avance. Le micro a déjà déclenché quand le créneau commence.
        //     La réunion en cours NE DOIT PAS être redémarrée : c'est la règle explicite.
        var source1 = new ScriptedCalendarSource();
        // Relecture à chaque tour : le test fait bouger l'agenda dans la même seconde.
        var calendar1 = new CalendarProbe(source1, TimeSpan.Zero);
        var mic1 = new ScriptedMeetingProbe { InMeeting = true, Title = "Zoom Workplace" };
        var detector1 = new MeetingDetector(settings, new IMeetingProbe[] { mic1, calendar1 });
        int started1 = 0;
        MeetingInfo? meeting1 = null;
        detector1.MeetingStarted += m => { started1++; meeting1 = m; };

        detector1.PollOnce();                                   // micro seul : la réunion démarre
        bool beforeCalendar = started1 == 1 && meeting1?.Calendar is null;
        source1.Meetings.Add(Slot("c1", "Point Hebdo PRJ - Velmora"));   // le créneau arrive après
        detector1.PollOnce();

        bool earlyJoinOk = beforeCalendar
                           && started1 == 1                     // ← aucune seconde bascule
                           && meeting1?.Calendar?.Subject == "Point Hebdo PRJ - Velmora"
                           // L'agenda nomme mieux que la fenêtre, même arrivé en retard.
                           && meeting1?.Title == "Point Hebdo PRJ - Velmora";

        // --- Cas 2 : rien de détecté, mais l'utilisateur répond « oui » à la question.
        //     L'agenda devient concluant à lui seul : il peut écouter sans ouvrir son micro.
        var source2 = new ScriptedCalendarSource();
        source2.Meetings.Add(Slot("c2", "Revue de sprint"));
        var calendar2 = new CalendarProbe(source2);
        calendar2.DecisionNeeded += fresh => calendar2.Accept(fresh[0].Id);
        var detector2 = new MeetingDetector(settings, new IMeetingProbe[] { calendar2 });
        MeetingInfo? meeting2 = null;
        detector2.MeetingStarted += m => meeting2 = m;
        detector2.PollOnce();

        bool acceptedOk = meeting2?.Title == "Revue de sprint" && detector2.InMeeting;

        // --- Cas 3 : il répond « non » (ou par erreur), puis y participe quand même.
        //     L'agenda ne déclenche rien, mais dès que le micro mord la réunion porte son sujet.
        var source3 = new ScriptedCalendarSource();
        source3.Meetings.Add(Slot("c3", "Discovery Call Velmora"));
        var calendar3 = new CalendarProbe(source3);
        calendar3.DecisionNeeded += fresh => calendar3.Decline(fresh[0].Id);
        var mic3 = new ScriptedMeetingProbe { InMeeting = false, Title = "Zoom Workplace" };
        var detector3 = new MeetingDetector(settings, new IMeetingProbe[] { mic3, calendar3 });
        MeetingInfo? meeting3 = null;
        detector3.MeetingStarted += m => meeting3 = m;

        detector3.PollOnce();
        bool silentAfterRefusal = !detector3.InMeeting && meeting3 is null;
        mic3.InMeeting = true;                                  // il y va finalement
        detector3.PollOnce();

        bool refusedThenJoinedOk = silentAfterRefusal
                                   && detector3.InMeeting
                                   && meeting3?.Title == "Discovery Call Velmora";

        // --- Heure de début : le plus tôt entre le premier indice et le créneau de l'agenda.
        var slot = new CalendarMeeting
        {
            Id = "h", Subject = "s",
            Start = DateTime.Today.AddHours(14), End = DateTime.Today.AddHours(15)
        };
        bool startRuleOk =
            // connecté en avance à 13:55 → l'entrée part de 13:55
            MeetingStart(new MeetingInfo { StartedAt = DateTime.Today.AddHours(13.9166), Calendar = slot })
                == DateTime.Today.AddHours(13.9166)
            // rejoint en retard à 14:10 → l'entrée part du début prévu, 14:00
            && MeetingStart(new MeetingInfo { StartedAt = DateTime.Today.AddHours(14.1666), Calendar = slot })
                == slot.Start
            // sans agenda, rien ne change : le premier indice fait foi
            && MeetingStart(new MeetingInfo { StartedAt = DateTime.Today.AddHours(9) })
                == DateTime.Today.AddHours(9);

        return earlyJoinOk && acceptedOk && refusedThenJoinedOk && startRuleOk;
    }

    /// <summary>
    /// Déroule ce que la collecte du 2026-08-10 au 09-14 a réclamé : deux réunions qui se suivent
    /// ne doivent plus se fondre en une seule entrée, et deux réunions aux mêmes horaires se
    /// départagent par une question. Vérifiable seulement ici, avec un agenda scripté.
    /// </summary>
    private bool CheckMeetingSequences()
    {
        var settings = new AppSettings { MeetingStartDelaySeconds = 0, MeetingEndGraceSeconds = 0 };
        var now = DateTime.Now;

        CalendarMeeting Slot(string id, string subject, double fromMin, double toMin,
                             CalendarResponse response = CalendarResponse.Acceptee) => new()
        {
            Id = id, Subject = subject,
            Start = now.AddMinutes(fromMin), End = now.AddMinutes(toMin), Response = response
        };

        // --- Cas 1 : réunions qui se suivent. A est terminée à l'agenda, B est seule à couvrir
        //     l'instant, le micro est toujours capté : coupure à la frontière, sans question.
        var source1 = new ScriptedCalendarSource();
        var a = Slot("a", "Point Hebdo PRJ - Velmora", -60, -0.5);       // vient de finir
        source1.Meetings.Add(a);
        var calendar1 = new CalendarProbe(source1, TimeSpan.Zero);
        var mic1 = new ScriptedMeetingProbe { InMeeting = true, Title = "Zoom Workplace" };
        var detector1 = new MeetingDetector(settings, new IMeetingProbe[] { mic1, calendar1 });
        MeetingInfo? finished1 = null, started1 = null;
        int choices1 = 0;
        detector1.MeetingSwitched += (f, s) => { finished1 = f; started1 = s; };
        // La question « pointer A ? » au début du créneau est normale ; aucune autre ne doit venir.
        detector1.ChoiceNeeded += c => { if (c.Kind != MeetingChoiceKind.Start) choices1++; };

        // Le détecteur ne voit A que si elle couvre encore l'instant : on démarre avant sa fin.
        a = Slot("a", "Point Hebdo PRJ - Velmora", -60, 0.02);          // finit dans ~1 s
        source1.Meetings[0] = a;
        detector1.PollOnce();
        bool attachedA = detector1.Current?.Calendar?.Id == "a";
        var b = Slot("b", "Office Hours AM", 0, 30);
        source1.Meetings.Add(b);
        Thread.Sleep(1500);                                          // A est maintenant finie
        detector1.PollOnce();

        bool chainOk = attachedA && finished1 != null && started1 != null
                       && finished1.EndedAt == (b.Start > a.End ? b.Start : a.End)
                       && started1.Calendar?.Id == "b" && started1.Title == "Office Hours AM"
                       && detector1.Current?.Calendar?.Id == "b"
                       && choices1 == 0;

        // --- Cas 2 : deux réunions aux mêmes horaires, rien en cours → une question avec les
        //     deux ; la réponse accepte l'une et refuse l'autre, et c'est elle qui est pointée.
        var source2 = new ScriptedCalendarSource();
        source2.Meetings.Add(Slot("x", "Réunion facultative", -1, 60, CalendarResponse.SansReponse));
        source2.Meetings.Add(Slot("y", "Réunion obligatoire", -1, 60));
        var calendar2 = new CalendarProbe(source2, TimeSpan.Zero);
        var detector2 = new MeetingDetector(settings, new IMeetingProbe[] { calendar2 });
        MeetingChoice? choice2 = null;
        MeetingInfo? meeting2 = null;
        detector2.ChoiceNeeded += c => choice2 = c;
        detector2.MeetingStarted += m => meeting2 = m;
        detector2.PollOnce();
        bool askedBoth = choice2 is { Kind: MeetingChoiceKind.Start, Candidates.Count: 2 }
                         && meeting2 is null && !detector2.InMeeting;
        var chosen = choice2?.Candidates.FirstOrDefault(c => c.Id == "x");
        if (choice2 != null) detector2.Resolve(choice2, chosen);
        detector2.PollOnce();
        bool simultaneousOk = askedBoth && detector2.InMeeting
                              && meeting2?.Calendar?.Id == "x" && meeting2?.Title == "Réunion facultative"
                              && calendar2.IsAccepted("x") && calendar2.IsDeclined("y");

        // --- Cas 3 : chevauchement. A court encore quand B commence : question « y passer ? »,
        //     rien ne bouge sans réponse ; sur « oui », coupure à l'heure de B.
        var source3 = new ScriptedCalendarSource();
        source3.Meetings.Add(Slot("a3", "Suivi projet ORVANE", -30, 30));
        var calendar3 = new CalendarProbe(source3, TimeSpan.Zero);
        var mic3 = new ScriptedMeetingProbe { InMeeting = true, Title = "Zoom Workplace" };
        var detector3 = new MeetingDetector(settings, new IMeetingProbe[] { mic3, calendar3 });
        MeetingChoice? choice3 = null;
        int switches3 = 0;
        detector3.ChoiceNeeded += c => choice3 = c;
        detector3.MeetingSwitched += (_, _) => switches3++;
        detector3.PollOnce();
        var started3 = detector3.Current?.StartedAt ?? DateTime.MinValue;
        var b3 = Slot("b3", "Daily check-in", -0.1, 30, CalendarResponse.Provisoire);
        source3.Meetings.Add(b3);
        detector3.PollOnce();
        detector3.PollOnce();                                         // pas de seconde question
        bool overlapAsked = choice3 is { Kind: MeetingChoiceKind.Switch, Candidates.Count: 1, CurrentOver: false }
                            && choice3.Current?.Id == "a3" && switches3 == 0
                            && detector3.Current?.Calendar?.Id == "a3";
        if (choice3 != null) detector3.Resolve(choice3, b3);
        // Coupure à l'heure de B — sans jamais remonter avant le début de la réunion coupée.
        bool overlapOk = overlapAsked && switches3 == 1
                         && detector3.Current?.Calendar?.Id == "b3"
                         && detector3.Current?.StartedAt == (b3.Start > started3 ? b3.Start : started3);

        // --- Cas 4 : même chevauchement, mais il reste : B est refusée, et quand A finit
        //     ensuite, la réunion continue sans être coupée vers B.
        now = DateTime.Now;   // les cas précédents ont dormi : « A finit dans 1 s » se compte d'ici
        var source4 = new ScriptedCalendarSource();
        source4.Meetings.Add(Slot("a4", "Workshop", -30, 0.02));
        source4.Meetings.Add(Slot("b4", "Office Hours", -0.1, 30));
        var calendar4 = new CalendarProbe(source4, TimeSpan.Zero);
        var mic4 = new ScriptedMeetingProbe { InMeeting = true, Title = "Zoom Workplace" };
        var detector4 = new MeetingDetector(settings, new IMeetingProbe[] { mic4, calendar4 });
        MeetingChoice? choice4 = null;
        int switches4 = 0;
        detector4.ChoiceNeeded += c => choice4 = c;
        detector4.MeetingSwitched += (_, _) => switches4++;
        detector4.PollOnce();                                         // deux créneaux : « laquelle ? »
        bool whichAsked = choice4 is { Kind: MeetingChoiceKind.Name, Candidates.Count: 2 };
        if (choice4 != null) detector4.Resolve(choice4, choice4.Candidates.First(c => c.Id == "a4"));
        bool namedA = detector4.Current?.Calendar?.Id == "a4" && detector4.Current?.Title == "Workshop";
        Thread.Sleep(1500);                                           // A finie, B refusée
        detector4.PollOnce();
        bool stayOk = whichAsked && namedA && switches4 == 0 && detector4.Current?.Calendar?.Id == "a4";

        Logger.Info($"SELFTEST enchaînements : suite={chainOk}, simultanées={simultaneousOk}, " +
                    $"chevauchement={overlapOk}, reste={stayOk}");
        return chainOk && simultaneousOk && overlapOk && stayOk;
    }

    /// <summary>
    /// Doublons de tâches, comparés mot à mot : les cas réellement vus dans les journaux —
    /// coquille, accent, espace autour d'une barre, chiffre collé, même sujet avec et sans
    /// « Réunion — » — et les faux doublons que la comparaison du nom entier proposait le
    /// 2026-09-29 : même gabarit, client ou sens différent.
    /// </summary>
    private bool CheckTaskSimilarity()
    {
        bool Dup(string a, string b) => TaskSimilarity.IsDuplicate(a, b, out _);

        bool normalized = TaskSimilarity.Normalize("Débug Dornac") == "debug dornac"
                          && TaskSimilarity.Normalize("Config Flow1 et 2") == "config flow 1 et 2"
                          && TaskSimilarity.Normalize("ORVANE Groupe / Contoso") == "orvane groupe contoso";

        bool duplicates = Dup("Debug Dornac", "Débug Dornac")
                          && Dup("Config kestrio", "Config Kestrio")
                          && Dup("Config Flow1 et 2", "Conflig Flow 1 et 2")
                          && Dup("Suivi projet facturation électronique ORVANE Groupe / Contoso",
                                 "Suivi projet facturation électronique ORVANE Groupe/Contoso")
                          && Dup("Onborading Tessal", "Onboarding Tessal")          // lettres inversées
                          && Dup("Onboarding Tesal", "Onboarding Tessal")           // lettre oubliée
                          && Dup("Réunion (\"Point Hebdo PRJ - Velmora\")", "Réunion — Point Hebdo PRJ - Velmora")
                          && Dup("Point Hebdo PRJ - Velmora", "Réunion — Point Hebdo PRJ - Velmora")
                          // Un sujet d'agenda long qui gagne un mot : toléré.
                          && Dup("Réunion — Point hebdo projet Velmora Contoso", "Réunion — AR: Point hebdo projet Velmora Contoso");

        bool distinct = !Dup("Config Orvane", "Config Tessal")
                        && !Dup("Réunion Velmora", "Réunion Kestrio")
                        && !Dup("Config Flow1 et 2", "Config Flow 3")
                        && !Dup("Config Orvane Flow1", "Config Orvane Flow2")          // nombre différent
                        && !Dup("Suivi projet facturation électronique ORVANE/Contoso",
                                "Suivi projet facturation électronique VELMORA/Contoso")         // client différent
                        && !Dup("Adaptation mapping EDI Kestrio",
                                "Adaptation mapping EDI Tessal")
                        && !Dup("Paramétrage flux Outgoing Orvane", "Paramétrage flux Incoming Orvane")
                        && !Dup("Réunion Orvane", "Orvane")                                      // réunion ≠ travail
                        && !Dup("Tests Orvane", "Tests Out Orvane");                             // mot de plus, nom court

        var tasks = new List<TaskItem>
        {
            new() { Id = 1, Name = "Config Flow1 et 2" },
            new() { Id = 2, Name = "Conflig Flow 1 et 2" },
            new() { Id = 3, Name = "Config Orvane" },
            new() { Id = 4, Name = "Debug Dornac" },
            new() { Id = 5, Name = "Débug Dornac" }
        };
        var pairs = TaskSimilarity.FindDuplicates(tasks);
        bool found = pairs.Count == 2
                     && pairs.Any(p => p.A.Id == 4 && p.B.Id == 5)
                     && pairs.Any(p => p.A.Id == 1 && p.B.Id == 2);

        Logger.Info($"SELFTEST doublons : normalisation={normalized}, vrais={duplicates}, faux={distinct}, liste={found}");
        return normalized && duplicates && distinct && found;
    }

    /// <summary>
    /// Fautes de frappe : correction mot à mot d'après le vocabulaire des autres tâches, « Tu
    /// voulais dire … ? » à la saisie, et les propositions du bandeau (fusion, renommage, refus
    /// mémorisé). Les pièges vus sur les vrais noms : un pluriel, « e-reporting » écrit d'un bloc,
    /// un nom propre à deux lettres d'un mot courant, une substitution sur un mot court.
    /// </summary>
    private bool CheckNameHygiene()
    {
        var names = new[]
        {
            "Onboarding Orvane", "Onboarding Velmora", "Onboarding Kestrio", "Config Orvane",
            "Config Velmora", "Config Kestrio", "Tests Orvane", "Tests Velmora", "Tests Kestrio",
            "Reporting Orvane", "Reporting Velmora", "Reporting Kestrio", "Internal sync", "Internal review",
            "Internal Orvane", "Data check", "Data import", "Data export"
        };
        bool typosOk =
            TaskSimilarity.CorrectTypos("Onborading Tessal", names, 0)?.Name == "Onboarding Tessal"
            && TaskSimilarity.CorrectTypos("Conflig Tessal", names, 0)?.Name == "Config Tessal"
            && TaskSimilarity.CorrectTypos("Test Tessal", names, 0) is null           // pluriel : pas une faute
            && TaskSimilarity.CorrectTypos("Tax ereporting Tessal", names, 0) is null // e-reporting d'un bloc
            && TaskSimilarity.CorrectTypos("Format Inferbal Tessal", names, 0) is null // deux lettres de « internal »
            && TaskSimilarity.CorrectTypos("Date Tessal", names, 0) is null            // substitution, mot court
            && TaskSimilarity.CorrectTypos("Onboarding Tessal", names, 0) is null;

        var tasks = names.Select((n, i) => new TaskItem { Id = i + 1, Name = n }).ToList();
        var existingHint = NameReview.CheckNewName("onboarding orvanne", tasks, "Réunion");
        var typoHint = NameReview.CheckNewName("Onborading Tessal", tasks, "Réunion");
        bool hintsOk = existingHint is { IsExisting: true, Suggested: "Onboarding Orvane" }
                       && typoHint is { IsExisting: false, Suggested: "Onboarding Tessal" }
                       && NameReview.CheckNewName("Config Orvane", tasks, "Réunion") is null   // exacte
                       && NameReview.CheckNewName("Refonte Tessal", tasks, "Réunion") is null;

        // Bandeau : une fusion (la tâche qui a le moins d'entrées part dans l'autre, jamais la
        // tâche en cours), un renommage, et ce qui a été refusé ne revient pas.
        TaskUsage U(long id, string name, int count) => new() { Task = new TaskItem { Id = id, Name = name }, EntryCount = count };
        var usage = names.Select((n, i) => U(i + 1, n, 3)).ToList();
        usage.Add(U(100, "Conflig Orvane", 1));                 // doublon de « Config Orvane »
        usage.Add(U(101, "Onborading Dornac", 2));              // faute, sans tâche jumelle
        usage.Add(U(102, "Réunion — Point Hebdo PRJ - Velmora", 4));
        usage.Add(U(103, "Point Hebdo PRJ - Velmora", 1));
        var items = NameReview.Find(usage, currentTaskId: null, "Réunion", new HashSet<string>());
        var merge = items.FirstOrDefault(i => i.Merge?.From == "Conflig Orvane");
        var rename = items.FirstOrDefault(i => i.Rename?.From == "Onborading Dornac");
        var meeting = items.FirstOrDefault(i => i.Merge?.From == "Point Hebdo PRJ - Velmora");
        var dismissed = NameReview.Find(usage, null, "Réunion", new HashSet<string> { merge?.Key ?? "?" });
        var protectedCurrent = NameReview.Find(usage, currentTaskId: 100, "Réunion", new HashSet<string>());
        bool reviewOk = merge is { Recommended: true } && merge.Merge!.Into == "Config Orvane"
                        && rename?.Rename?.To == "Onboarding Dornac"
                        // Réunion d'un côté seulement : proposée, mais pas cochée d'office.
                        && meeting is { Recommended: false } && meeting.Merge!.Into == "Réunion — Point Hebdo PRJ - Velmora"
                        && dismissed.All(i => i.Key != merge.Key)
                        && protectedCurrent.All(i => i.Merge?.From != "Conflig Orvane");

        // Refus mémorisés en base.
        NameReview.Dismiss(_db, new[] { "m:1:2", "r:3:x" });
        NameReview.Dismiss(_db, new[] { "m:1:2", "" });
        var stored = NameReview.LoadDismissed(_db);
        bool dismissOk = stored.Count == 2 && stored.Contains("m:1:2") && stored.Contains("r:3:x");

        // Préfixe changé dans les Paramètres (« Réunion » → « Meetting », 29/09) : les tâches déjà
        // nommées restent des réunions, et le même sujet sous les deux préfixes est un doublon.
        bool prefixOk = NameReview.IsMeeting("Réunion — Point Hebdo PRJ - Velmora", "Meetting")
                        && NameReview.IsMeeting("Meetting — Point Hebdo PRJ - Velmora", "Meetting")
                        && NameReview.IsMeeting("Réunion", "Meetting")
                        && !NameReview.IsMeeting("Réunions clients Orvane", "Meetting")
                        && !NameReview.IsMeeting("Config Orvane", "Meetting")
                        && TaskSimilarity.IsDuplicate("Réunion — Point Hebdo PRJ - Velmora",
                                                      "Meetting — Point Hebdo PRJ - Velmora", out _, "Meetting");

        Logger.Info($"SELFTEST noms : fautes={typosOk}, saisie={hintsOk}, bandeau={reviewOk}, refus={dismissOk}, préfixe={prefixOk}");
        return typosOk && hintsOk && reviewOk && dismissOk && prefixOk;
    }

    /// <summary>
    /// Détection d'un changement de tâche sur des relevés fabriqués : 20 min sur un classeur
    /// Orvane puis 6 min sur autre chose → proposé, depuis la première fenêtre étrangère ; un coup
    /// d'œil d'une minute ne déclenche rien ; la condition doit tenir deux évaluations ; une fois
    /// proposé, le même changement ne revient pas ; « je reste » le range dans la tâche.
    /// </summary>
    private bool CheckShiftDetector()
    {
        var start = DateTime.Now.AddMinutes(-30);
        List<ActivityProbe.Sample> Run(params (string Title, string Process, int Minutes)[] parts)
        {
            var list = new List<ActivityProbe.Sample>();
            var at = start;
            foreach (var (title, process, minutes) in parts)
                for (int i = 0; i < minutes * 12; i++) { list.Add(new ActivityProbe.Sample(at, process, title)); at = at.AddSeconds(5); }
            return list;
        }
        var none = new HashSet<string>();
        ActivityShiftDetector.Context Ctx(long id = 1) =>
            new(id, "Config Orvane", start, none, w => w == "contoso");

        const string orvane = "Orvane - mapping INVOIC.xlsx - Excel";
        const string velmora = "RE: Velmora - commande 4512 - camille.durand@contoso.test - Outlook";

        // 20 min Orvane, puis 6 min Velmora : première évaluation → rien (hystérésis), seconde → proposé.
        var samples = Run((orvane, "EXCEL", 20), (velmora, "OUTLOOK", 6));
        var now = samples[^1].At;
        var detector = new ActivityShiftDetector();
        var first = detector.Evaluate(now.AddSeconds(-60), samples.Where(s => s.At <= now.AddSeconds(-60)).ToList(), Ctx());
        var shift = detector.Evaluate(now, samples, Ctx());
        bool shiftOk = first is null && shift != null
                       && shift.Since == start.AddMinutes(20)
                       && shift.Keywords.Contains("velmora") && !shift.Keywords.Contains("orvane")
                       && !shift.Keywords.Contains("durand")                  // adresse e-mail : jamais un mot-clé
                       && shift.DominantTitle == velmora
                       && ActivityNaming.FromTitle(velmora, "OUTLOOK") == "Velmora commande 4512";

        // Déjà proposé : pas une seconde fois, même après le délai.
        detector.Proposed(shift!, now);
        bool onceOk = detector.Evaluate(now.AddMinutes(1), samples, Ctx()) is null;

        // Un coup d'œil d'une minute à un mail, puis retour : rien.
        var glance = Run((orvane, "EXCEL", 20), (velmora, "OUTLOOK", 1), (orvane, "EXCEL", 5));
        var d2 = new ActivityShiftDetector();
        var gNow = glance[^1].At;
        d2.Evaluate(gNow.AddSeconds(-60), glance, Ctx());
        bool glanceOk = d2.Evaluate(gNow, glance, Ctx()) is null;

        // Même fenêtre qu'au début de la tâche : ce n'est pas un changement.
        var same = Run((orvane, "EXCEL", 26));
        var d3 = new ActivityShiftDetector();
        d3.Evaluate(same[^1].At.AddSeconds(-60), same, Ctx());
        bool sameOk = d3.Evaluate(same[^1].At, same, Ctx()) is null;

        // Mot appris pour la tâche : « velmora » vu avec « Config Orvane » → pas un changement.
        var learned = new ActivityShiftDetector();
        var ctxLearned = new ActivityShiftDetector.Context(1, "Config Orvane", start,
            new HashSet<string> { "velmora", "commande" }, _ => false);
        learned.Evaluate(now.AddSeconds(-60), samples, ctxLearned);
        bool learnedOk = learned.Evaluate(now, samples, ctxLearned) is null;

        // Tâche fourre-tout : « velmora » a été appris avec elle (mails de tous les clients), mais
        // c'est le nom d'une autre tâche — il ne la décrit pas, le changement est vu.
        var catchAll = new ActivityShiftDetector();
        var ctxCatchAll = new ActivityShiftDetector.Context(1, "Gen admin, Intranet, e-mails", start,
            new HashSet<string> { "velmora", "commande", "orvane", "mapping", "invoic" }, _ => false,
            w => w is "velmora" or "orvane");
        catchAll.Evaluate(now.AddSeconds(-60), samples, ctxCatchAll);
        var caught = catchAll.Evaluate(now, samples, ctxCatchAll);
        learnedOk = learnedOk && caught != null && caught.Keywords.Contains("velmora");

        // « Je reste » : l'activité proposée rejoint la tâche en cours.
        var d4 = new ActivityShiftDetector();
        d4.Evaluate(now.AddSeconds(-60), samples, Ctx());
        var s4 = d4.Evaluate(now, samples, Ctx());
        if (s4 != null) d4.Stayed(s4);
        d4.Evaluate(now.AddSeconds(30), samples, Ctx());
        bool stayOk = s4 != null && d4.Evaluate(now.AddSeconds(60), samples, Ctx()) is null;

        // Silence (réunion, pause…) : l'hystérésis repart de zéro.
        var d5 = new ActivityShiftDetector();
        d5.Evaluate(now.AddSeconds(-60), samples, Ctx());
        d5.Silence();
        bool silenceOk = d5.Evaluate(now, samples, Ctx()) is null;

        // Noms tirés des titres : marque d'application, préfixe de réponse, extension retirés.
        bool namingOk = ActivityNaming.FromTitle("Orvane - mapping INVOIC.xlsx - Excel", "EXCEL") == "Orvane mapping INVOIC"
                        && ActivityNaming.FromTitle("Portail Tessal - suivi des factures - Personnel - Microsoft​ Edge", "msedge")
                           == "Portail Tessal"
                        && ActivityNaming.FromTitle("Boîte de réception - camille.durand@contoso.test - Outlook", "OUTLOOK") is null;

        Logger.Info($"SELFTEST changement : détecté={shiftOk}, une_fois={onceOk}, coup_d_œil={glanceOk}, " +
                    $"même_fenêtre={sameOk}, appris={learnedOk}, je_reste={stayOk}, silence={silenceOk}, nom={namingOk}");
        return shiftOk && onceOk && glanceOk && sameOk && learnedOk && stayOk && silenceOk && namingOk;
    }

    /// <summary>
    /// Le câblage d'<c>App</c> autour de la proposition, sans fenêtre : ce qui est proposé
    /// (tâche existante, nom tiré du titre, heure bornée à l'entrée en cours), puis ce que fait
    /// chaque réponse — nouvelle tâche antidatée, tâche existante à partir de maintenant, « je
    /// reste », et une réponse arrivée après un changement de tâche fait ailleurs.
    /// </summary>
    private bool CheckShiftWiring()
    {
        var previousTimer = _timer;
        _timer = new TimerService(_db, 30);
        try
        {
            var now = DateTime.Now;
            var current = _db.GetOrCreateTask("Selftest câblage Orvane");
            // « Kestrio » : un mot qu'aucune autre tâche de la base jetable ne porte (sinon il ne désigne rien).
            var velmora = _db.GetOrCreateTask("Selftest câblage suivi Kestrio");
            _timer.StartTask(current, now.AddMinutes(-30));
            var firstEntry = _timer.CurrentEntryId;

            const string title = "RE: Kestrio - commande 4512 - Outlook";
            var samples = Enumerable.Range(0, 84)
                .Select(i => new ActivityProbe.Sample(now.AddMinutes(-8).AddSeconds(5 * i), "OUTLOOK", title)).ToList();
            ActivityShiftDetector.Shift Shift(DateTime since) => new(since, TimeSpan.FromMinutes(7), title,
                TimeSpan.FromMinutes(7), new[] { "kestrio", "commande" }, new[] { "OUTLOOK" }, samples);

            var p = BuildShiftProposal(current, Shift(now.AddMinutes(-8)), new Dictionary<long, Dictionary<string, int>>());
            // Un début de changement antérieur à l'entrée en cours est ramené à son début.
            var clamped = BuildShiftProposal(current, Shift(now.AddHours(-2)), new Dictionary<long, Dictionary<string, int>>());
            bool proposalOk = p.Local?.Task.Id == velmora.Id && p.LocalName == "Kestrio commande 4512"
                              && p.Since == now.AddMinutes(-8) && p.Ai is null
                              && clamped.Since == _timer.CurrentStart;

            // Nouvelle tâche, antidatée : l'entrée d'avant est clôturée à l'heure du changement.
            OnShiftAnswered(current, p.Shift, p.Since,
                new ShiftAnswer(ShiftAnswerKind.New, null, "Selftest câblage commande Velmora", true, "IA"));
            var closed = firstEntry is long id ? _db.GetEntry(id) : null;
            bool newOk = _timer.CurrentTask?.Name == "Selftest câblage commande Velmora"
                         && _timer.CurrentStart == p.Since && closed?.EndedAt == p.Since;

            // Tâche existante, à partir de maintenant.
            var before = DateTime.Now;
            var nowCurrent = _timer.CurrentTask!;
            OnShiftAnswered(nowCurrent, p.Shift, p.Since, new ShiftAnswer(ShiftAnswerKind.Existing, velmora, null, false, "local"));
            bool existingOk = _timer.CurrentTask?.Id == velmora.Id && _timer.CurrentStart >= before;

            // Tâche proposée fusionnée pendant que la fenêtre était ouverte : rien ne bouge, et
            // surtout l'entrée en cours reste ouverte (avant, elle était clôturée pour rien).
            var merged = _db.GetOrCreateTask("Selftest câblage fusionnée");
            _db.MergeTasks(merged.Id, velmora.Id);
            var entryBefore = _timer.CurrentEntryId;
            OnShiftAnswered(velmora, p.Shift, p.Since, new ShiftAnswer(ShiftAnswerKind.Existing, merged, null, true, "local"));
            existingOk = existingOk && _timer.CurrentTask?.Id == velmora.Id && _timer.CurrentEntryId == entryBefore
                         && _db.GetOpenEntry()?.Id == entryBefore;

            // Réponse arrivée trop tard : la tâche a changé entre-temps, rien ne bouge.
            OnShiftAnswered(current, p.Shift, p.Since, new ShiftAnswer(ShiftAnswerKind.New, null, "Selftest câblage tardive", true, "local"));
            bool lateOk = _timer.CurrentTask?.Id == velmora.Id && _db.FindTaskByName("Selftest câblage tardive") is null;

            // « Je reste » : rien ne bascule.
            OnShiftAnswered(velmora, p.Shift, p.Since, new ShiftAnswer(ShiftAnswerKind.Stay, null, null, false, "refusée"));
            bool stayOk = _timer.CurrentTask?.Id == velmora.Id;

            _timer.Stop();
            Logger.Info($"SELFTEST câblage changement : proposition={proposalOk}, nouvelle={newOk}, " +
                        $"existante={existingOk}, tardive={lateOk}, reste={stayOk}");
            return proposalOk && newOk && existingOk && lateOk && stayOk;
        }
        finally
        {
            _timer = previousTimer;
        }
    }

    /// <summary>
    /// Reprise d'une tâche récente (le cas du 29/09 à 17:26) : de retour sur les fenêtres de la
    /// tâche quittée il y a un quart d'heure, c'est elle qui est proposée — pas une réunion qui
    /// en porterait un mot, pas la tâche en cours. Une activité sans rapport ne désigne rien.
    /// </summary>
    private bool CheckShiftResume()
    {
        // Base à part : les entrées des autres vérifications couvrent la même demi-heure, alors
        // qu'une vraie base n'a jamais deux entrées en même temps.
        var previousDb = _db;
        var previousTimer = _timer;
        var path = Path.Combine(Path.GetTempPath(), $"timetracker_selftest_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(path);
        _db.Initialize();
        _timer = new TimerService(_db, 30);
        try
        {
            var now = DateTime.Now;
            var back = _db.GetOrCreateTask("Selftest reprise portail Dornac");
            var meeting = _db.GetOrCreateTask("Selftest point Dornac hebdo");   // réunion par ses entrées, pas par son nom
            var current = _db.GetOrCreateTask("Selftest reprise Orvane");
            _timer.StartTask(back, now.AddMinutes(-26));
            _timer.StartTask(meeting, now.AddMinutes(-16), isMeeting: true);
            _timer.StartTask(current, now.AddMinutes(-13));

            const string portal = "Portail Dornac - factures en attente - Microsoft Edge";
            var recent = new List<ActivityProbe.Sample>();
            void Add(string title, string process, int fromMinutes, int toMinutes)
            {
                for (var at = now.AddMinutes(-fromMinutes); at < now.AddMinutes(-toMinutes); at = at.AddSeconds(5))
                    recent.Add(new ActivityProbe.Sample(at, process, title));
            }
            Add(portal, "msedge", 26, 16);
            Add("Point Dornac hebdo | Microsoft Teams", "ms-teams", 16, 13);
            Add("Orvane - mapping INVOIC.xlsx - Excel", "EXCEL", 13, 6);
            Add(portal, "msedge", 6, 0);
            var foreign = recent.Where(s => s.At >= now.AddMinutes(-6)).ToList();
            var shift = new ActivityShiftDetector.Shift(now.AddMinutes(-6), TimeSpan.FromMinutes(6), portal,
                TimeSpan.FromMinutes(6), new[] { "portail", "dornac", "factures", "attente" }, new[] { "msedge" }, foreign);

            var p = BuildShiftProposal(current, shift, new Dictionary<long, Dictionary<string, int>>(), recent);
            bool resumedOk = p.Resumed && p.Local?.Task.Id == back.Id;

            // Un autre onglet du même portail (titre jamais vu) : les mots-clés vus assez longtemps suffisent.
            var otherTab = foreign.Select(s => s with { Title = "Portail Dornac - relances clients - Microsoft Edge" }).ToList();
            bool wordsOk = ActivityLearning.ResumedTask(otherTab, shift.Keywords, recent,
                               _db.GetEntriesOverlapping(now.AddMinutes(-30), now), current.Id, _ => false)?.TaskId == back.Id;

            // Seule la réunion a vu ces fenêtres, ou rien ne les a vues : pas de reprise.
            var meetingOnly = _db.GetMeetingOnlyTaskIds();
            var entries = _db.GetEntriesOverlapping(now.AddMinutes(-30), now);
            bool noneOk = meetingOnly.Contains(meeting.Id) && !meetingOnly.Contains(back.Id)
                          && ActivityLearning.ResumedTask(foreign, shift.Keywords, recent, entries, current.Id,
                                                          id => id == back.Id) is null
                          && ActivityLearning.ResumedTask(
                                 foreign.Select(s => s with { Title = "Kestrio - commande 4512 - Outlook" }).ToList(),
                                 new[] { "kestrio", "commande" }, recent, entries, current.Id, _ => false) is null;

            _timer.Stop();
            Logger.Info($"SELFTEST reprise : fenêtres={resumedOk}, mots={wordsOk}, aucune={noneOk}");
            return resumedOk && wordsOk && noneOk;
        }
        finally
        {
            _timer = previousTimer;
            _db = previousDb;
            TryDeleteThrowawayDb(path);
        }
    }

    /// <summary>
    /// Ordre du sélecteur : favoris, tâche d'avant (pas une réunion), suggestions, récentes ; la
    /// tâche qui tourne n'y est pas — sauf en pause, pour la reprendre.
    /// </summary>
    private bool CheckSelectorOrder()
    {
        var previousTimer = _timer;
        _timer = new TimerService(_db, 30);
        try
        {
            var now = DateTime.Now;
            var before = _db.GetOrCreateTask("Selftest sélecteur tâche d'avant");
            var meeting = _db.GetOrCreateTask("Selftest sélecteur point hebdo");
            var suggested = _db.GetOrCreateTask("Selftest sélecteur suggérée");
            var current = _db.GetOrCreateTask("Selftest sélecteur en cours");
            _timer.StartTask(before, now.AddMinutes(-20));
            _timer.StartTask(meeting, now.AddMinutes(-10), isMeeting: true);
            _timer.StartTask(current, now.AddMinutes(-5));

            var suggestions = new[]
            {
                new TaskSuggester.Suggestion(current, TimeSpan.FromMinutes(5), 2, "fenêtre"),
                new TaskSuggester.Suggestion(suggested, TimeSpan.FromMinutes(4), 1, "fenêtre")
            };
            int favorites = _db.GetFavoriteTasks().Count;
            var list = BuildSelectorTasks(suggestions).Select(t => t.Id).ToList();
            bool orderOk = list.IndexOf(before.Id) == favorites
                           && list.IndexOf(suggested.Id) == favorites + 1
                           && !list.Contains(current.Id)
                           && list.IndexOf(meeting.Id) > favorites + 1;

            _timer.Pause();
            bool pausedOk = BuildSelectorTasks(suggestions).Any(t => t.Id == current.Id);
            _timer.Stop();

            Logger.Info($"SELFTEST sélecteur : ordre={orderOk}, pause={pausedOk}");
            return orderOk && pausedOk;
        }
        finally
        {
            _timer = previousTimer;
        }
    }

    /// <summary>
    /// Apprentissage d'après la base : un relevé va à la tâche que l'entrée désigne après coup
    /// (bascule antidatée comprise), rien pendant une réunion ni hors de toute entrée (pause).
    /// </summary>
    private bool CheckActivityLearning()
    {
        var t0 = DateTime.Today.AddHours(10);
        var entries = new List<TimeEntry>
        {
            new() { Id = 1, TaskId = 10, StartedAt = t0, EndedAt = t0.AddMinutes(10) },
            // Bascule antidatée à 10:10 vers la tâche 20 : ces minutes-là sont à elle.
            new() { Id = 2, TaskId = 20, StartedAt = t0.AddMinutes(10), EndedAt = t0.AddMinutes(20) },
            new() { Id = 3, TaskId = 30, StartedAt = t0.AddMinutes(20), EndedAt = t0.AddMinutes(30), IsMeeting = true },
            new() { Id = 4, TaskId = 40, StartedAt = t0.AddMinutes(40), EndedAt = null }   // ouverte
        };
        var samples = new List<ActivityProbe.Sample>
        {
            new(t0.AddMinutes(5), "EXCEL", "Orvane mapping"),
            new(t0.AddMinutes(12), "OUTLOOK", "Velmora commande"),
            new(t0.AddMinutes(25), "ms-teams", "Point Hebdo PRJ"),     // réunion : pas appris
            new(t0.AddMinutes(35), "EXCEL", "Kestrio pause"),          // aucune entrée : pas appris
            new(t0.AddMinutes(45), "TimeTracker", "Tableau de bord"),  // TimeTracker : pas appris
            new(t0.AddMinutes(50), "EXCEL", "Tessal portail")
        };
        var learned = ActivityLearning.Attribute(samples, entries);
        bool ok = learned.Count == 3
                  && learned[10].ContainsKey("orvane") && !learned[10].ContainsKey("velmora")
                  && learned[20]["velmora"] == 5
                  && !learned.ContainsKey(30)
                  && learned[40].ContainsKey("tessal") && !learned[40].ContainsKey("tableau");

        // Entrées recouvrant une plage : l'entrée ouverte en fait partie.
        var task = _db.GetOrCreateTask("Selftest recouvrement");
        var open = _db.StartEntry(task.Id, DateTime.Now.AddMinutes(-30));
        var overlapping = _db.GetEntriesOverlapping(DateTime.Now.AddMinutes(-20), DateTime.Now.AddMinutes(-10));
        _db.EndEntry(open, DateTime.Now);
        bool overlapOk = overlapping.Any(e => e.Id == open);

        Logger.Info($"SELFTEST apprentissage : attribution={ok}, recouvrement={overlapOk}");
        return ok && overlapOk;
    }

    /// <summary>
    /// Suggestion par l'IA, sans réseau : ce qui part (mots-clés, pas de titre), la lecture d'une
    /// réponse réaliste (balises, nom existant à la casse près, quasi-doublon, nom inventé,
    /// tâche en cours, JSON absent) et le texte « ce qui est parti ».
    /// </summary>
    private bool CheckActivityAi()
    {
        var names = new[] { "Config Orvane", "Onboarding Velmora", "Gen admin, Intranet, e-mails" };
        var request = new TaskSuggestionAssistant.Request("Config Orvane", new[] { "velmora", "commande", "4512" },
                                                          new[] { "OUTLOOK" }, names, FromSelector: false);
        var prompt = TaskSuggestionAssistant.BuildPrompt(request);
        bool promptOk = prompt.Contains("velmora, commande, 4512") && prompt.Contains("- Onboarding Velmora")
                        && prompt.Contains("Tâche en cours : « Config Orvane »") && prompt.Contains("\"new_name\"");

        var existing = TaskSuggestionAssistant.Parse("```json\n{\"existing\":\"onboarding velmora\",\"new_name\":\"\",\"why\":\"client cité\"}\n```",
                                                     names, "Config Orvane");
        var nearDup = TaskSuggestionAssistant.Parse("{\"existing\":\"\",\"new_name\":\"Onborading Velmora\",\"why\":\"\"}", names, "Config Orvane");
        var fresh = TaskSuggestionAssistant.Parse("Voici : {\"existing\":\"Tâche inventée\",\"new_name\":\"« Commande Velmora 4512 »\",\"why\":\"mail\"}",
                                                  names, "Config Orvane");
        var current = TaskSuggestionAssistant.Parse("{\"existing\":\"Config Orvane\",\"new_name\":\"\",\"why\":\"\"}", names, "Config Orvane");
        bool malformed;
        try { TaskSuggestionAssistant.Parse("désolé, je ne sais pas", names, null); malformed = false; }
        catch (FormatException) { malformed = true; }

        bool parseOk = existing.Existing == "Onboarding Velmora" && existing.NewName is null && existing.Why == "client cité"
                       && nearDup.Existing == "Onboarding Velmora" && nearDup.NewName is null
                       && fresh.Existing is null && fresh.NewName == "Commande Velmora 4512"
                       && current.Existing is null && current.NewName is null
                       && malformed;

        var sent = TaskSuggestionAssistant.DescribeSent(request, "Gemini / test");
        bool sentOk = sent.Contains("3 mots-clés (velmora, commande, 4512)") && sent.Contains("3 noms de tâches")
                      && sent.Contains("Jamais un titre entier");

        // Mots-clés : jamais les mots communs, jamais les fenêtres de TimeTracker.
        var keywords = ActivityShiftDetector.Keywords(new[]
        {
            new ActivityProbe.Sample(DateTime.Now, "OUTLOOK", "Velmora commande - Outlook"),
            new ActivityProbe.Sample(DateTime.Now, "TimeTracker", "Tableau de bord secret"),
        }, w => w == "commande");
        bool keywordsOk = keywords.SequenceEqual(new[] { "velmora" });

        // Erreur d'API : une ligne lisible (le 503 de Gemini prenait sept lignes du journal), et
        // seules les surcharges passagères valent un nouvel essai.
        const string busy = "{\n  \"error\": {\n    \"code\": 503,\n    \"message\": \"This model is currently experiencing high demand.\",\n    \"status\": \"UNAVAILABLE\"\n  }\n}\n";
        bool errorOk = AiHttp.ErrorMessage(busy) == "This model is currently experiencing high demand."
                       && AiHttp.ErrorMessage("Bad\ngateway") == "Bad gateway"
                       && AiHttp.IsTransient(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.ServiceUnavailable))
                       && !AiHttp.IsTransient(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.BadRequest))
                       && !AiHttp.IsTransient(new FormatException());

        Logger.Info($"SELFTEST IA activité : prompt={promptOk}, lecture={parseOk}, envoyé={sentOk}, mots_clés={keywordsOk}, erreur={errorOk}");
        return promptOk && parseOk && sentOk && keywordsOk && errorOk;
    }

    /// <summary>
    /// Suggestion par l'activité : un mot rare (« orvane ») dans le titre au premier plan désigne
    /// la tâche qui le porte ; un mot présent partout (« config ») ne désigne rien ; trop peu de
    /// temps ne suggère rien.
    /// </summary>
    private bool CheckTaskSuggester()
    {
        var tasks = new List<TaskItem>
        {
            new() { Id = 1, Name = "Config Orvane" },
            new() { Id = 2, Name = "Config Tessal" },
            new() { Id = 3, Name = "Gen admin, Intranet, e-mails" },
            new() { Id = 4, Name = "Réunion" }
        };
        var now = DateTime.Now;
        var step = TimeSpan.FromSeconds(5);

        IReadOnlyList<ActivityProbe.Sample> Samples(string title, int count) =>
            Enumerable.Range(0, count)
                      .Select(i => new ActivityProbe.Sample(now.AddSeconds(-5 * i), "EXCEL", title))
                      .ToList();

        // 10 min sur un classeur Orvane : Config Orvane, et pas Config Tessal malgré « config ».
        var orvane = TaskSuggester.Suggest(tasks, Samples("Orvane - mapping INVOIC.xlsx - Excel", 120), null, step);
        bool orvaneOk = orvane.Count == 1 && orvane[0].Task.Id == 1
                       && orvane[0].Evidence >= TimeSpan.FromMinutes(10)
                       && orvane[0].Reason.Contains("10 min");

        // Le seul mot commun est « config » : présent dans deux tâches, il ne doit rien suggérer seul.
        var config = TaskSuggester.Suggest(tasks, Samples("Config générale - Bloc-notes", 120), null, step);
        bool configWeak = config.Count == 0 || config[0].Score < orvane[0].Score / 4;

        // Deux minutes ne suffisent pas.
        var brief = TaskSuggester.Suggest(tasks, Samples("Orvane - mapping INVOIC.xlsx - Excel", 24), null, step);
        bool briefOk = brief.Count == 0;

        // Mélange : 15 min Tessal, 5 min Orvane → Tessal d'abord.
        var mixed = Samples("Portail Tessal - Google Chrome", 180)
            .Concat(Samples("Orvane - mapping.xlsx", 60)).ToList();
        var ranked = TaskSuggester.Suggest(tasks, mixed, null, step);
        bool rankedOk = ranked.Count == 2 && ranked[0].Task.Id == 2 && ranked[1].Task.Id == 1;

        // Apprentissage : « Portail Chorus Pro » ne cite aucune tâche, mais a été vu 15 min avec
        // « Kestrio Siren seul » → suggérée, avec la raison « déjà vue ». Un mot appris sur
        // toutes les tâches (« chrome ») ne compte pas.
        tasks.Add(new TaskItem { Id = 5, Name = "Kestrio Siren seul" });
        var hints = new Dictionary<long, Dictionary<string, int>>
        {
            [5] = new() { ["portail"] = 900, ["chorus"] = 900, ["chrome"] = 900 },
            [1] = new() { ["chrome"] = 900 },
            [2] = new() { ["chrome"] = 900 },
            [3] = new() { ["chrome"] = 900, ["outlook"] = 3000 }
        };
        var learned = TaskSuggester.Suggest(tasks, Samples("Portail Chorus Pro - Google Chrome", 120), hints, step);
        bool learnedOk = learned.Count == 1 && learned[0].Task.Id == 5 && learned[0].Reason.Contains("déjà vue");
        var chromeOnly = TaskSuggester.Suggest(tasks, Samples("Nouvel onglet - Google Chrome", 120), hints, step);
        bool genericLearnedOk = chromeOnly.Count == 0;

        return orvaneOk && configWeak && briefOk && rankedOk && learnedOk && genericLearnedOk;
    }

    /// <summary>
    /// Assistant IA, sans réseau : le prompt porte les noms, la réponse se lit malgré les
    /// balises et le texte autour, et une tâche inventée par le modèle est écartée.
    /// </summary>
    private bool CheckAiAssistant()
    {
        var usage = new List<TaskUsage>
        {
            new() { Task = new TaskItem { Id = 1, Name = "Config Flow1 et 2" }, EntryCount = 12 },
            new() { Task = new TaskItem { Id = 2, Name = "Conflig Flow 1 et 2" }, EntryCount = 1 },
            new() { Task = new TaskItem { Id = 3, Name = "Aide Emma pour Brunel" }, EntryCount = 2 },
            new() { Task = new TaskItem { Id = 4, Name = "brunel" }, EntryCount = 3 }
        };
        var prompt = TaskCleanupAssistant.BuildPrompt(usage);
        bool promptOk = prompt.Contains("- Conflig Flow 1 et 2 (1)") && prompt.Contains("\"merges\"");

        const string response = "Voici ma proposition :\n```json\n{\"merges\":[" +
            "{\"from\":\"conflig flow 1 et 2\",\"into\":\"Config Flow1 et 2\",\"why\":\"faute de frappe\"}," +
            "{\"from\":\"Aide Emma pour Brunel\",\"into\":\"Brunel\",\"why\":\"même client\"}," +
            "{\"from\":\"Tâche inventée\",\"into\":\"brunel\",\"why\":\"?\"}]," +
            "\"renames\":[{\"from\":\"brunel\",\"to\":\"Brunel\",\"why\":\"majuscule\"}," +
            "{\"from\":\"brunel\",\"to\":\"brunel\",\"why\":\"sans effet\"}]}\n```\nBonne journée.";
        var proposal = TaskCleanupAssistant.Parse(response, usage.Select(u => u.Task.Name).ToList());
        bool parseOk = proposal.Merges.Count == 2
                       && proposal.Merges[0].From == "Conflig Flow 1 et 2"     // casse ramenée à celle de la base
                       && proposal.Merges[1].Into == "brunel"
                       && proposal.Renames.Count == 1 && proposal.Renames[0].To == "Brunel"
                       && proposal.Ignored.Count == 1;

        bool factoryOk = AiProviderFactory.Create(AiProviderFactory.Gemini, "k", "", "").Name.Contains("gemini-3.5-flash-lite")
                         && AiProviderFactory.Create(AiProviderFactory.OpenAi, "", "llama3", "http://localhost:11434/v1/").Name.Contains("localhost:11434/v1)")
                         && AiProviderFactory.Create(AiProviderFactory.Anthropic, "k", "", "").Name.StartsWith("Anthropic");
        bool refusesWithoutKey;
        try { AiProviderFactory.Create(AiProviderFactory.Gemini, "", "", ""); refusesWithoutKey = false; }
        catch (InvalidOperationException) { refusesWithoutKey = true; }

        // Aller-retour des réglages IA et d'apprentissage.
        var s = _db.LoadSettings();
        s.AiEnabled = true; s.AiProvider = "openai"; s.AiApiKey = "sk-test"; s.AiModel = "m"; s.AiBaseUrl = "http://x";
        s.ActivityLearning = false;
        _db.SaveSettings(s);
        var back = _db.LoadSettings();
        bool settingsOk = back.AiEnabled && back.AiProvider == "openai" && back.AiApiKey == "sk-test"
                          && back.AiModel == "m" && back.AiBaseUrl == "http://x" && !back.ActivityLearning;

        // Apprentissage en base : accumulation, fusion, effacement.
        var a = _db.GetOrCreateTask("Selftest apprentissage A");
        var b = _db.GetOrCreateTask("Selftest apprentissage B");
        _db.AddTaskHints(a.Id, new Dictionary<string, int> { ["portail"] = 60, ["chorus"] = 30 });
        _db.AddTaskHints(a.Id, new Dictionary<string, int> { ["portail"] = 40 });
        _db.AddTaskHints(b.Id, new Dictionary<string, int> { ["portail"] = 10 });
        var hints = _db.GetTaskHints();
        bool hintsOk = hints[a.Id]["portail"] == 100 && hints[a.Id]["chorus"] == 30 && hints[b.Id]["portail"] == 10;
        _db.MergeTasks(b.Id, a.Id);
        var merged = _db.GetTaskHints();
        bool mergedOk = merged[a.Id]["portail"] == 110 && !merged.ContainsKey(b.Id);
        // Oubli ciblé (« Gérer les tâches ») : une seule tâche, les autres gardent ce qu'elles savent.
        var c = _db.GetOrCreateTask("Selftest apprentissage fourre-tout");
        _db.AddTaskHints(c.Id, new Dictionary<string, int> { ["velmora"] = 60, ["kestrio"] = 60, ["orvane"] = 60 });
        bool forgetOk = _db.CountTaskHints().GetValueOrDefault(c.Id) == 3
                        && _db.ClearTaskHints(c.Id) == 3
                        && !_db.CountTaskHints().ContainsKey(c.Id) && _db.CountTaskHints()[a.Id] == 2;
        bool clearedOk = forgetOk && _db.ClearTaskHints() == 2 && _db.GetTaskHints().Count == 0;

        return promptOk && parseOk && factoryOk && refusesWithoutKey && settingsOk && hintsOk && mergedOk && clearedOk;
    }

    /// <summary>
    /// Une absence longue clôture l'entrée <b>à l'heure du départ</b> et met en pause, avec un
    /// rappel rapproché : c'est le mécanisme de la fin de journée oubliée.
    /// </summary>
    private bool CheckAbsenceCutoff()
    {
        var timer = new TimerService(_db, 30);
        var task = _db.GetOrCreateTask("Selftest absence");
        var started = DateTime.Now.AddHours(-3);
        timer.StartTask(task, started);
        var entryId = timer.CurrentEntryId;

        var left = DateTime.Now.AddHours(-2);
        timer.PauseAt(left, remindIn: TimeSpan.FromMinutes(2));
        var entry = entryId is long id ? _db.GetEntry(id) : null;

        bool closedAtDeparture = entry?.EndedAt == left && timer.IsPaused && timer.CurrentTask?.Id == task.Id
                                 && timer.PausedSince == left;

        // Jamais avant le début de l'entrée, même si l'instant de départ est antérieur.
        timer.Resume();
        var resumedId = timer.CurrentEntryId;
        timer.PauseAt(DateTime.Now.AddHours(-5));
        var resumed = resumedId is long rid ? _db.GetEntry(rid) : null;
        bool clamped = resumed?.EndedAt == resumed?.StartedAt;

        timer.Stop();
        return closedAtDeparture && clamped;
    }

    /// <summary>
    /// Déroule <see cref="ApplyMeetingTitle"/> sur les trois situations réellement rencontrées
    /// pendant la semaine du 2026-08-03 au 07.
    /// </summary>
    private bool CheckApplyMeetingTitle()
    {
        var meetingTask = _db.GetOrCreateTask("Selftest titre posé");

        // 1. Titre exploitable (Teams) : la tâche est nommée et le titre reste en commentaire.
        var usable = _db.StartEntry(meetingTask.Id, DateTime.Now.AddHours(-3), isMeeting: true);
        _db.EndEntry(usable, DateTime.Now.AddHours(-2));
        var named = ApplyMeetingTitle(new MeetingInfo { Title = "Point Hebdo PRJ - Velmora" },
                                      usable, meetingTask.Id);
        var usableEntry = _db.GetEntry(usable);
        bool usableOk = named == $"{_settings.MeetingTaskName} — Point Hebdo PRJ - Velmora"
                        && usableEntry?.TaskId != meetingTask.Id
                        && usableEntry?.Notes == "Point Hebdo PRJ - Velmora"
                        // La tâche « Réunion » générique ne bouge pas : la renommer réécrirait
                        // le libellé de toutes les réunions déjà pointées.
                        && _db.GetTask(meetingTask.Id)?.Name == "Selftest titre posé";

        // 2. Titre générique : c'est tout Zoom. Ni nom, ni commentaire — rien à dire.
        var generic = _db.StartEntry(meetingTask.Id, DateTime.Now.AddHours(-2), isMeeting: true);
        _db.EndEntry(generic, DateTime.Now.AddHours(-1));
        var genericNamed = ApplyMeetingTitle(new MeetingInfo { Title = "Zoom Meeting" },
                                             generic, meetingTask.Id);
        var genericEntry = _db.GetEntry(generic);
        bool genericOk = genericNamed is null
                         && genericEntry?.TaskId == meetingTask.Id
                         && string.IsNullOrEmpty(genericEntry?.Notes);

        // 3. L'utilisateur a nommé l'entrée lui-même pendant la réunion (7 fois en 5 jours la
        //    première semaine, 4 fois la seconde) : son nom est intouchable, le titre relevé
        //    n'atterrit que dans le commentaire.
        var ownName = _db.GetOrCreateTask("Selftest nommée à la main");
        var manual = _db.StartEntry(ownName.Id, DateTime.Now.AddHours(-1), isMeeting: true);
        _db.EndEntry(manual, DateTime.Now);
        var manualNamed = ApplyMeetingTitle(new MeetingInfo { Title = "Huddle: @Camille Durand" },
                                            manual, meetingTask.Id);
        var manualEntry = _db.GetEntry(manual);
        bool manualOk = manualNamed is null
                        && manualEntry?.TaskId == ownName.Id
                        && manualEntry?.Notes == "Huddle: @Camille Durand";

        return usableOk && genericOk && manualOk;
    }

    /// <summary>
    /// Sonde de réunion pilotée à la main : permet à <c>--selftest</c> de dérouler la machine
    /// à états de <see cref="MeetingDetector"/> sans dépendre d'une vraie réunion.
    /// </summary>
    /// <summary>
    /// Agenda scripté. Indispensable : le poste de développement n'a aucun profil Outlook
    /// configuré (comme il n'a ni Teams ni Zoom), donc toute la coopération agenda ↔ détection
    /// automatique doit pouvoir être déroulée sans Outlook.
    /// </summary>
    private sealed class ScriptedCalendarSource : ICalendarSource
    {
        public List<CalendarMeeting> Meetings { get; } = new();

        public string Name => "agenda scripté";

        public CalendarSnapshot Read(DateTime from, DateTime to) =>
            new(true, "scripté", Meetings.Where(m => m.Start < to && m.End > from).ToList());
    }

    private sealed class ScriptedMeetingProbe : IMeetingProbe
    {
        public bool InMeeting { get; set; }
        public string? Title { get; set; }

        public string Name => "scriptée";

        public IReadOnlyList<MeetingSignal> Poll() => InMeeting
            ? new[]
            {
                new MeetingSignal(MeetingSource.Microphone, "test.exe", null, Conclusive: true),
                new MeetingSignal(MeetingSource.Window, "test.exe", Title, Conclusive: false)
            }
            : Array.Empty<MeetingSignal>();
    }

    /// <summary>
    /// Relève, à l'instant présent, tout ce sur quoi la détection de réunion se fonde :
    /// applications captant le micro, fenêtres visibles, et verdict des sondes.
    ///
    /// Ce mode existe parce que les motifs de titres ne peuvent pas être validés en aveugle :
    /// il faut lancer ce diagnostic <b>pendant</b> une vraie réunion Teams ou Zoom et lire le
    /// relevé pour savoir quels titres et quelles classes de fenêtre l'appli voit réellement.
    /// </summary>
    private void RunMeetingProbe(string? outputFile)
    {
        // Réglages par défaut : le diagnostic tourne sans ouvrir la base (TimeTracker est
        // probablement déjà lancé), et il liste de toute façon le micro sans filtrage.
        var settings = new AppSettings();
        var report = new System.Text.StringBuilder();
        report.AppendLine($"=== Diagnostic détection de réunion — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        try
        {
            var mic = new MicrophoneProbe(settings);
            var windows = new MeetingWindowProbe(settings);

            var micAll = mic.ActiveAppsUnfiltered();
            report.AppendLine();
            report.AppendLine($"-- Micro capté en ce moment ({micAll.Count}) --");
            report.AppendLine(micAll.Count == 0
                ? "  (aucune application ne capte le micro)"
                : string.Join(Environment.NewLine, micAll.Select(a => "  " + a)));
            report.AppendLine($"  Filtre appliqué : {settings.MeetingApps}");

            var micSignals = mic.Poll();
            var windowSignals = windows.Poll();

            report.AppendLine();
            report.AppendLine($"-- Indices retenus ({micSignals.Count + windowSignals.Count}) --");
            foreach (var s in micSignals.Concat(windowSignals))
                report.AppendLine($"  [{s.Source}] app={s.App} concluant={s.Conclusive} titre={s.Title ?? "(aucun)"}");
            if (micSignals.Count + windowSignals.Count == 0) report.AppendLine("  (aucun)");

            bool verdict = micSignals.Concat(windowSignals).Any(s => s.Conclusive);
            report.AppendLine();
            report.AppendLine($"VERDICT : {(verdict ? "réunion détectée" : "pas de réunion")}");

            report.AppendLine();
            report.AppendLine("-- Toutes les fenêtres visibles (process | classe | titre) --");
            foreach (var w in MeetingWindowProbe.VisibleWindows().OrderBy(w => w.Process))
                report.AppendLine($"  {w.Process,-22} | {w.ClassName,-28} | {MeetingWindowProbe.CleanTitle(w.Title)}");

            var path = outputFile ?? Path.Combine(Path.GetTempPath(),
                $"timetracker_meetingprobe_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(true));
            Logger.Info($"MEETINGPROBE écrit dans {path} — verdict : {(verdict ? "réunion" : "pas de réunion")}");

            MessageBox.Show(
                $"Verdict : {(verdict ? "réunion détectée" : "pas de réunion")}\n\n" +
                $"Relevé complet :\n{path}",
                "TimeTracker — diagnostic de réunion", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("MEETINGPROBE", ex);
            MessageBox.Show($"Le diagnostic a échoué : {ex.Message}", "TimeTracker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Hauteur utile d'un portable 1080p à <b>150 %</b> — l'écran de l'utilisateur : 1080 / 1,5
    /// moins la barre des tâches et le cadre. Le poste de développement a un grand écran à 100 %,
    /// donc rien n'y dépasse jamais : sans ce budget en dur, le défaut ne se voit pas ici.
    /// </summary>
    private const double LaptopHeightBudget = 670;

    /// <summary>Fenêtres qui s'ouvrent d'elles-mêmes, sans action de l'utilisateur.</summary>
    private static readonly string[] AutomaticPopups = { "ReminderPopup", "CalendarPromptWindow", "ActivityShiftWindow" };

    /// <summary>
    /// Une fenêtre qui s'ouvre toute seule ne prend pas le focus (il tape peut-être ailleurs —
    /// signalé le 2026-09-30), reste dans la zone de travail même quand son contenu grandit après
    /// l'ouverture (la réponse de l'IA poussait les boutons de « Tu as changé de tâche ? » sous la
    /// barre des tâches), et garde ses boutons visibles quand elle est plafonnée.
    /// </summary>
    private static void CheckAutomaticPopup(string name, Window window)
    {
        if (!AutomaticPopups.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return;
        if (window.ShowActivated)
            throw new InvalidOperationException("ShowActivated : elle prendrait le focus à l'ouverture");

        var area = SystemParameters.WorkArea;
        if (window.Top < area.Top - 1 || window.Top + window.ActualHeight > area.Bottom + 1)
            throw new InvalidOperationException(
                $"hors de la zone de travail : {window.Top:F0} + {window.ActualHeight:F0} > {area.Bottom:F0}");

        if (window.FindName("SwitchButton") is FrameworkElement button && window.Content is FrameworkElement content)
        {
            var bottom = button.TranslatePoint(new System.Windows.Point(0, button.ActualHeight), content).Y;
            if (bottom > content.ActualHeight + 1)
                throw new InvalidOperationException($"boutons hors de la fenêtre ({bottom:F0} > {content.ActualHeight:F0})");
        }
    }

    /// <summary>
    /// Une fenêtre plus haute que l'écran de l'utilisateur doit être défilable, sinon ses
    /// derniers réglages et ses boutons sont hors de portée — c'est ce qui est arrivé à la
    /// fenêtre Paramètres en v1.3, sans même une barre de défilement pour le laisser deviner.
    /// </summary>
    /// <returns>
    /// Mention à ajouter au résultat quand la fenêtre ne tiendrait pas telle quelle sur l'écran
    /// de l'utilisateur. Sans elle, impossible de distinguer « vérifié » de « jamais déclenché ».
    /// </returns>
    private static string CheckFitsOnLaptop(Window window)
    {
        // Mesure sans contrainte : la MaxHeight posée par WindowFit dépend de l'écran courant,
        // qui n'est pas celui de l'utilisateur. C'est la hauteur naturelle qui nous intéresse.
        var limit = window.MaxHeight;
        window.MaxHeight = double.PositiveInfinity;
        window.UpdateLayout();
        double natural = window.ActualHeight;
        window.MaxHeight = limit;

        if (natural <= LaptopHeightBudget) return "";

        if (!HasScrollViewer(window))
            throw new InvalidOperationException(
                $"hauteur naturelle {natural:F0} > {LaptopHeightBudget} points sans ScrollViewer : " +
                "la fenêtre dépasserait l'écran de l'utilisateur (portable à 150 %)");

        return $"(défilable, {natural:F0}pts)";
    }

    private static bool HasScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer || HasScrollViewer(child)) return true;
        }
        return false;
    }

    /// <summary>
    /// Instantané de ce que l'agenda donne, puis quitte. Écrit tout ce qui servira à comprendre
    /// un échec à distance : présence d'Outlook, filtre exact envoyé (c'est le point le plus
    /// fragile — voir le piège de culture dans <see cref="OutlookComCalendarSource"/>), et les
    /// réunions relevées avec leur statut de réponse.
    /// </summary>
    private void RunOutlookProbe(string? outputFile)
    {
        try
        {
            var report = new System.Text.StringBuilder();
            var now = DateTime.Now;
            report.AppendLine($"TimeTracker — diagnostic agenda du {now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine();

            bool classic = File.Exists(
                Environment.ExpandEnvironmentVariables(
                    @"%ProgramFiles%\Microsoft Office\Root\Office16\OUTLOOK.EXE"));
            var running = System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length;
            var newOutlook = System.Diagnostics.Process.GetProcessesByName("olk").Length;
            report.AppendLine($"Outlook classique installé : {(classic ? "oui" : "non ou ailleurs")}");
            report.AppendLine($"Processus OUTLOOK.EXE en cours : {running}");
            report.AppendLine($"Processus « nouveau Outlook » (olk.exe) en cours : {newOutlook}");
            if (running == 0)
                report.AppendLine("  ⚠️ Outlook classique n'est pas lancé : TimeTracker ne le démarre " +
                                  "jamais de lui-même. Ouvre-le puis relance ce diagnostic.");
            report.AppendLine();

            var source = new OutlookComCalendarSource();
            var snapshot = source.Read(now.AddHours(-4), now.AddHours(8));
            report.AppendLine($"Lecture : {(snapshot.Available ? "OK" : "ÉCHEC")} — {snapshot.Detail}");
            report.AppendLine($"Filtre envoyé à Outlook : {source.LastFilter}");
            report.AppendLine($"(culture courante : {System.Globalization.CultureInfo.CurrentCulture.Name})");
            report.AppendLine();

            report.AppendLine("-- Réunions relevées --");
            if (snapshot.Meetings.Count == 0) report.AppendLine("  (aucune)");
            foreach (var m in snapshot.Meetings)
                report.AppendLine($"  {(m.Covers(now) ? "»" : " ")} {m}"
                                  + (m.Organizer.Length > 0 ? $" — {m.Organizer}" : ""));

            var current = snapshot.Meetings.FirstOrDefault(m => !m.AllDay && m.Covers(now));
            report.AppendLine();
            report.AppendLine($"MAINTENANT : {(current is null ? "aucune réunion à l'agenda" : $"« {current.Subject} »")}");

            // Sur le Bureau et non dans %TEMP% : ce fichier est fait pour être retrouvé et
            // envoyé, comme l'archive de « Recuperer les journaux.bat ».
            var path = outputFile ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"TimeTracker-agenda-{DateTime.Now:yyyyMMdd-HHmm}.txt");
            File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(true));
            // Le détail, pas seulement OK/ÉCHEC : deux essais à deux minutes d'intervalle le
            // 2026-08-09 ont donné deux verdicts opposés, et le journal ne disait pas pourquoi.
            Logger.Info($"OUTLOOKPROBE écrit dans {path} — lecture " +
                        $"{(snapshot.Available ? "OK" : "ÉCHEC")} : {snapshot.Detail}");

            MessageBox.Show(
                $"Lecture de l'agenda : {(snapshot.Available ? "OK" : "ÉCHEC")}\n{snapshot.Detail}\n\n" +
                $"Relevé complet :\n{path}",
                "TimeTracker — diagnostic agenda", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("OUTLOOKPROBE", ex);
            MessageBox.Show($"Le diagnostic a échoué : {ex.Message}", "TimeTracker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Ouvre et rend chaque fenêtre hors écran, puis la referme. Attrape les
    /// <c>XamlParseException</c> qui ne surviennent qu'au rendu (un binding TwoWay sur une
    /// source en lecture seule avait déjà fait planter le sélecteur de tâche). Lancé via --uitest.
    /// </summary>
    private void RunUiTest(string? snapshotDir)
    {
        Logger.Info("UITEST début");

        var task = _db.GetOrCreateTask("Fenêtre de test");
        var favorite = _db.GetOrCreateTask("Tâche favorite de test");
        _db.SetFavorite(favorite.Id, true);
        var timer = new TimerService(_db, 30);
        timer.StartTask(task, DateTime.Now.AddMinutes(-42));
        var closedId = _db.StartEntry(task.Id, DateTime.Now.AddHours(-3));
        _db.EndEntry(closedId, DateTime.Now.AddHours(-2));
        var closed = _db.GetEntriesForDay(DateTime.Now).First(x => x.Id == closedId);

        // Une réunion clôturée : sans elle, la colonne de marquage du tableau de bord se rendrait
        // vide et le rendu ne prouverait rien.
        var meetingTask = _db.GetOrCreateTask("Réunion — Point hebdo de test");
        var uiMeetingId = _db.StartEntry(meetingTask.Id, DateTime.Now.AddHours(-5), isMeeting: true);
        _db.EndEntry(uiMeetingId, DateTime.Now.AddHours(-4));

        var recent = BuildSelectorTasks();

        var results = new List<string>();

        Render("TaskSelectorPopup", () => new TaskSelectorPopup(recent, null));
        Render("TaskSelectorPopup/Suggestion", () => new TaskSelectorPopup(recent, null,
            suggestions: new Dictionary<long, string> { [recent[0].Id] = "12 min sur « Fenêtre de test — classeur.xlsx »" }));
        var uiTasks = _db.GetAllTasks();
        Render("TaskSelectorPopup/Tu voulais dire", () => new TaskSelectorPopup(recent, "Fenêtre de tset",
            checkName: typed => NameReview.CheckNewName(typed, uiTasks, _settings.MeetingTaskName)));
        const string uiSent = "Envoyé à Gemini / gemini-3.5-flash-lite : 5 mots-clés (velmora, commande, 4512, livraison, retard), " +
                              "applis OUTLOOK, le nom de la tâche en cours et 42 noms de tâches. Jamais un titre entier.";
        Render("TaskSelectorPopup/IA", () => new TaskSelectorPopup(recent, null),
            w => ((TaskSelectorPopup)w).ShowAiSuggestion("Commande Velmora en retard", false, "mail client cité", uiSent));
        ActivityShiftWindow ShiftWindow(TaskItem? existing) => new(task.Name, DateTime.Now.AddMinutes(-8),
            TimeSpan.FromMinutes(7), "RE: Velmora - commande 4512 - Outlook", TimeSpan.FromMinutes(5),
            existing, existing is null ? null : "4 min sur « Velmora - suivi.xlsx » (fenêtre déjà vue avec cette tâche)",
            "Velmora commande 4512", typed => NameReview.CheckNewName(typed, uiTasks, _settings.MeetingTaskName), playSound: false);
        Render("ActivityShiftWindow", () => ShiftWindow(favorite));
        Render("ActivityShiftWindow/IA en attente", () => ShiftWindow(null),
            w => ((ActivityShiftWindow)w).ShowAiPending("Gemini / gemini-3.5-flash-lite", uiSent));
        Render("ActivityShiftWindow/IA", () => ShiftWindow(favorite), w =>
        {
            var shift = (ActivityShiftWindow)w;
            shift.ShowAiPending("Gemini / gemini-3.5-flash-lite", uiSent);
            shift.ShowAiAnswer(null, "Commande Velmora en retard", "mail client cité");
        });
        // Reprise (mêmes fenêtres qu'une tâche récente) : l'avis contraire de l'IA s'affiche sans la remplacer.
        Render("ActivityShiftWindow/Reprise", () => new ActivityShiftWindow(task.Name, DateTime.Now.AddMinutes(-8),
            TimeSpan.FromMinutes(7), "Velmora - suivi.xlsx - Excel", TimeSpan.FromMinutes(5), favorite,
            "mêmes fenêtres que sur cette tâche jusqu'à 16:52", "Velmora suivi", null, playSound: false,
            existingIsResumed: true), w =>
        {
            var shift = (ActivityShiftWindow)w;
            shift.ShowAiPending("Gemini / gemini-3.5-flash-lite", uiSent);
            shift.ShowAiAnswer(task, null, "mots-clés proches");
        });
        // La réponse de l'IA sur un écran bas : le texte défile, les boutons restent visibles.
        Render("ActivityShiftWindow/petit écran", () =>
        {
            var w = ShiftWindow(favorite);
            w.MaxHeight = 380;
            return w;
        }, w =>
        {
            var shift = (ActivityShiftWindow)w;
            shift.ShowAiPending("Gemini / gemini-3.5-flash-lite", uiSent + " " + uiSent);
            shift.ShowAiAnswer(null, "Commande Velmora en retard", "mail client cité");
        });
        Render("ExportRangeWindow", () => new ExportRangeWindow(DateTime.Now.Date.AddDays(-30), DateTime.Now.Date));
        Render("TaskSelectorPopup/Décalage", () => new TaskSelectorPopup(recent, null, offerBackdate: true));
        Render("ReminderPopup", () => new ReminderPopup(task.Name, playSound: false));
        Render("ReminderPopup/Pause", () =>
            new ReminderPopup(task.Name, playSound: false, pausedFor: TimeSpan.FromMinutes(35)));
        var slotA = new CalendarMeeting
        {
            Id = "uitest-a",
            Subject = "Point Hebdo PRJ - Velmora",
            Start = DateTime.Now,
            End = DateTime.Now.AddHours(1),
            Organizer = "Camille Durand",
            Response = CalendarResponse.SansReponse
        };
        var slotB = new CalendarMeeting
        {
            Id = "uitest-b",
            Subject = "Office Hours AM",
            Start = DateTime.Now,
            End = DateTime.Now.AddMinutes(30),
            Response = CalendarResponse.Acceptee
        };
        Render("CalendarPromptWindow", () => new CalendarPromptWindow(
            new MeetingChoice(MeetingChoiceKind.Start, new[] { slotA }), playSound: false));
        Render("CalendarPromptWindow/Plusieurs", () => new CalendarPromptWindow(
            new MeetingChoice(MeetingChoiceKind.Start, new[] { slotA, slotB }), playSound: false));
        Render("CalendarPromptWindow/Laquelle", () => new CalendarPromptWindow(
            new MeetingChoice(MeetingChoiceKind.Name, new[] { slotA, slotB }), playSound: false));
        Render("CalendarPromptWindow/Suivante", () => new CalendarPromptWindow(
            new MeetingChoice(MeetingChoiceKind.Switch, new[] { slotB }, slotA), playSound: false));
        Render("CalendarPromptWindow/Terminée", () => new CalendarPromptWindow(
            new MeetingChoice(MeetingChoiceKind.Switch, new[] { slotB }, slotA, CurrentOver: true),
            playSound: false));
        Render("QuickEditWindow", () => new QuickEditWindow(_db, timer));
        Render("EntryEditWindow", () => new EntryEditWindow(_db, closed));
        Render("SettingsWindow", () => new SettingsWindow(_settings, _db, (_, _, _) => null));
        // La même, contrainte à l'écran de l'utilisateur : c'est la seule façon de voir ici ce
        // qu'il voit chez lui. Sans ça, le débordement de la v1.3 restait invisible.
        Render("SettingsWindow/petit écran", () =>
        {
            var w = new SettingsWindow(_settings, _db, (_, _, _) => null);
            w.MaxHeight = LaptopHeightBudget;
            return w;
        });
        // Un doublon probable, pour que le panneau de fusion se rende (il est masqué sans).
        _db.GetOrCreateTask("Fenetre de Test");
        Render("TaskManagerWindow", () => new TaskManagerWindow(_db, task.Id, _settings));
        Render("AiSuggestionsWindow", () => new AiSuggestionsWindow("Gemini / gemini-3.5-flash-lite",
            new TaskCleanupAssistant.Proposal(
                new[] { new TaskCleanupAssistant.Merge("Fenetre de Test", "Fenêtre de test", "faute de frappe") },
                new[] { new TaskCleanupAssistant.Rename("Selftest A", "Selftest — A", "cohérence") },
                new[] { "fusion « Inventée » → « Fenêtre de test » (tâche inconnue)" })));
        Render("AiSuggestionsWindow/Noms à vérifier", () =>
        {
            var review = NameReview.Find(new List<TaskUsage>
                {
                    new() { Task = new TaskItem { Id = 1, Name = "Config Orvane" }, EntryCount = 6 },
                    new() { Task = new TaskItem { Id = 2, Name = "Conflig Orvane" }, EntryCount = 1 },
                    new() { Task = new TaskItem { Id = 3, Name = "Point Hebdo PRJ - Velmora" }, EntryCount = 1 },
                    new() { Task = new TaskItem { Id = 4, Name = "Réunion — Point Hebdo PRJ - Velmora" }, EntryCount = 4 }
                }, null, _settings.MeetingTaskName, new HashSet<string>())
                .Select(AiSuggestionsWindow.FromReview);
            return new AiSuggestionsWindow("2 proposition(s) : fautes de frappe et doublons probables, repérés sur ce poste",
                review, title: "TimeTracker — Noms à vérifier",
                askAi: _ => Task.FromResult<(IReadOnlyList<AiSuggestionsWindow.Row>, string)>((Array.Empty<AiSuggestionsWindow.Row>(), "")));
        });
        // Objectif posé pour que la barre d'avancement se rende (masquée à zéro). Base jetable.
        _settings.DailyHoursGoal = 7.5;
        Render("Dashboard/Jour", () => new DashboardWindow(_db, _settings, BuildUiTestActions(timer)));
        Render("Dashboard/Semaine", () =>
        {
            var w = new DashboardWindow(_db, _settings, BuildUiTestActions(timer));
            w.SelectTab(1);
            return w;
        });

        timer.Flush();
        Logger.Info($"UITEST {(results.Any(r => r.Contains("ÉCHEC")) ? "ÉCHEC" : "OK")} : {string.Join(", ", results)}");

        // afterShow : ce qui n'arrive qu'une fois la fenêtre affichée (réponse de l'IA).
        void Render(string name, Func<Window> factory, Action<Window>? afterShow = null)
        {
            Window? window = null;
            try
            {
                window = factory();
                // Toute fenêtre doit porter l'icône : sans elle, Windows affiche un cadre vide
                // dans la barre des tâches et dans Alt-Tab. Aucune ne la posait avant le
                // 2026-08-09 ; ce garde-fou attrape la prochaine fenêtre qui l'oublierait.
                if (window.Icon is null) throw new InvalidOperationException("Icon non posée");

                // Hors écran : la fenêtre est réellement rendue sans clignoter devant l'utilisateur.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowInTaskbar = false;
                window.Show();
                window.UpdateLayout();
                afterShow?.Invoke(window);
                window.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                if (snapshotDir != null) SaveSnapshot(window, snapshotDir, name);
                // Avant CheckFitsOnLaptop, qui change la hauteur de la fenêtre pour la mesurer.
                CheckAutomaticPopup(name, window);
                results.Add($"{name}=OK{CheckFitsOnLaptop(window)}");
            }
            catch (Exception ex)
            {
                Logger.Error($"UITEST {name}", ex);
                results.Add($"{name}=ÉCHEC");
            }
            finally
            {
                try { window?.Close(); } catch { /* déjà fermée */ }
            }
        }
    }

    /// <summary>
    /// Barre de suivi du tableau de bord pendant <c>--uitest</c> : l'état est réel (pour que
    /// la capture soit représentative), les commandes sont inertes (aucune fenêtre modale).
    /// </summary>
    private static TrackerActions BuildUiTestActions(TimerService timer) => new()
    {
        State = () => timer.CurrentTask is null
            ? ("Aucune tâche en cours", false, false)
            : ($"▶ {timer.CurrentTask.Name} — {Format(timer.Elapsed)}", timer.IsPaused, true),
        // Bandeau affiché pour que le rendu le couvre.
        NamesToReview = () => 3
    };

    /// <summary>Enregistre le rendu d'une fenêtre en PNG (revue visuelle sans piloter l'écran).</summary>
    private static void SaveSnapshot(Window window, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace('/', '-');
        using var stream = File.Create(Path.Combine(directory, safeName + ".png"));
        encoder.Save(stream);
    }

    /// <summary>Nettoie la base jetable des tests (et ses fichiers WAL).</summary>
    private static void TryDeleteThrowawayDb(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { Logger.Error($"Nettoyage self-test ({file})", ex); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SystemEvents garde une référence statique : sans ça, l'App survit à sa propre sortie.
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _meetings?.Stop();
        _activityTimer?.Stop();
        _shiftWindow?.Expire("fermeture de TimeTracker");
        // Les 15 dernières minutes n'ont pas encore été apprises : la base est à jour, on y va.
        LearnFromActivity(flush: true);
        _activity?.Dispose();
        try { _timer?.Flush(); } catch (Exception ex) { Logger.Error("OnExit/Flush", ex); }
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        Logger.Info("=== Arrêt TimeTracker ===");
        base.OnExit(e);
    }
}
