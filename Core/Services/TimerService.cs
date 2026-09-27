using System.Windows.Threading;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Pilote la tâche active : démarrage/arrêt des entrées, mise à jour de la durée,
/// et déclenchement des rappels périodiques.
/// </summary>
public class TimerService
{
    private readonly DatabaseService _db;
    private readonly DispatcherTimer _timer;

    public TaskItem? CurrentTask { get; private set; }
    public long? CurrentEntryId { get; private set; }
    public DateTime CurrentStart { get; private set; }
    public bool IsPaused { get; private set; }

    private DateTime _lastReminder;
    private int _reminderIntervalMinutes;

    /// <summary>Émis chaque seconde quand une tâche tourne (durée écoulée).</summary>
    public event Action<TaskItem, TimeSpan>? Tick;

    /// <summary>Émis quand l'intervalle de rappel est atteint.</summary>
    public event Action<TaskItem>? ReminderDue;

    /// <summary>
    /// Émis quand l'intervalle est atteint alors que le suivi est en pause : sans lui,
    /// une pause oubliée à midi ne se remarque qu'en fin de journée.
    /// </summary>
    public event Action<TaskItem>? PausedReminderDue;

    /// <summary>Émis quand la tâche active change (ou s'arrête → null).</summary>
    public event Action<TaskItem?>? CurrentTaskChanged;

    public TimerService(DatabaseService db, int reminderIntervalMinutes)
    {
        _db = db;
        _reminderIntervalMinutes = reminderIntervalMinutes;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public bool HasActiveTask => CurrentTask != null && !IsPaused;

    public TimeSpan Elapsed =>
        CurrentTask == null ? TimeSpan.Zero : DateTime.Now - CurrentStart;

    public void UpdateReminderInterval(int minutes)
    {
        if (minutes > 0) _reminderIntervalMinutes = minutes;
    }

    /// <summary>Reprend une entrée laissée ouverte lors d'une fermeture précédente.</summary>
    public void ResumeOpenEntry(TimeEntry open)
    {
        CurrentTask = _db.GetTask(open.TaskId);
        CurrentEntryId = open.Id;
        CurrentStart = open.StartedAt;
        IsPaused = false;
        _lastReminder = DateTime.Now;
        _timer.Start();
        CurrentTaskChanged?.Invoke(CurrentTask);
    }

    /// <summary>
    /// Bascule sur une tâche. L'entrée précédente est clôturée.
    /// <paramref name="startedAt"/> permet un démarrage antidaté (ex. « j'ai changé il y a 20 min »).
    /// </summary>
    public void StartTask(TaskItem task, DateTime? startedAt = null, bool isMeeting = false)
    {
        var now = DateTime.Now;
        var start = startedAt ?? now;

        // Clôturer l'entrée courante au point de bascule.
        if (CurrentEntryId.HasValue)
            _db.EndEntry(CurrentEntryId.Value, start);

        CurrentTask = task;
        CurrentStart = start;
        CurrentEntryId = _db.StartEntry(task.Id, start, isMeeting);
        _db.TouchTask(task.Id);

        IsPaused = false;
        _lastReminder = now;
        _timer.Start();
        CurrentTaskChanged?.Invoke(CurrentTask);
    }

    public void Pause() => PauseAt(DateTime.Now);

    /// <summary>
    /// Met en pause en clôturant l'entrée à un instant passé : le poste s'est verrouillé ou
    /// endormi à <paramref name="at"/> avec une tâche en cours, et on ne s'en aperçoit qu'au
    /// réveil. <paramref name="remindIn"/> avance le rappel « toujours en pause ? » — au retour
    /// d'une nuit, attendre l'intervalle normal laisserait un trou d'une demi-heure au matin.
    /// </summary>
    public void PauseAt(DateTime at, TimeSpan? remindIn = null)
    {
        if (CurrentEntryId is null || IsPaused) return;
        var now = DateTime.Now;
        _db.EndEntry(CurrentEntryId.Value, at < CurrentStart ? CurrentStart : at);
        CurrentEntryId = null;
        IsPaused = true;
        PausedSince = at;
        _lastReminder = remindIn is { } soon
            ? now - TimeSpan.FromMinutes(_reminderIntervalMinutes) + soon
            : now;
        Logger.Info($"Suivi en pause (« {CurrentTask?.Name} »), entrée clôturée à {at:HH:mm}.");
        // Le timer continue de tourner : c'est lui qui déclenche le rappel « toujours en pause ? ».
        CurrentTaskChanged?.Invoke(CurrentTask);
    }

    /// <summary>Instant de mise en pause (durée d'inactivité affichée dans le rappel).</summary>
    public DateTime PausedSince { get; private set; }

    /// <summary>
    /// Arrête le suivi en conservant l'entrée en cours (fin de journée de travail).
    /// À la différence de <see cref="CancelCurrentEntry"/>, le temps déjà pointé est gardé.
    /// <paramref name="at"/> permet de clôturer à un instant passé : une réunion détectée se
    /// termine quand ses indices disparaissent, pas à la fin du délai de grâce.
    /// </summary>
    public void Stop(DateTime? at = null)
    {
        _timer.Stop();
        if (CurrentEntryId.HasValue) _db.EndEntry(CurrentEntryId.Value, at ?? DateTime.Now);
        CurrentEntryId = null;
        CurrentTask = null;
        IsPaused = false;
        CurrentTaskChanged?.Invoke(null);
    }

    public void Resume()
    {
        if (CurrentTask is null || !IsPaused) return;
        var now = DateTime.Now;
        CurrentStart = now;
        CurrentEntryId = _db.StartEntry(CurrentTask.Id, now);
        _db.TouchTask(CurrentTask.Id);
        IsPaused = false;
        _lastReminder = now;
        _timer.Start();
        Logger.Info($"Suivi repris (« {CurrentTask.Name} ») après {Math.Round((now - PausedSince).TotalMinutes)} min de pause.");
        CurrentTaskChanged?.Invoke(CurrentTask);
    }

    /// <summary>Réinitialise le compteur de rappel (ex. l'utilisateur a confirmé « je continue »).</summary>
    public void SnoozeReminder()
    {
        _lastReminder = DateTime.Now;
    }

    /// <summary>
    /// Met les rappels en sommeil sans toucher au chronomètre : pendant une réunion détectée,
    /// « Toujours sur cette tâche ? » tomberait au pire moment. Le compteur est repoussé tant
    /// que la suspension dure, donc le premier rappel n'arrive pas juste après la réunion.
    /// </summary>
    public bool RemindersSuspended { get; set; }

    /// <summary>
    /// Ajuste l'heure de début de la tâche en cours (« j'ai basculé il y a 20 min »).
    /// L'entrée précédente est rognée pour finir au même instant : pas de chevauchement.
    /// </summary>
    public void AdjustCurrentStart(DateTime newStart)
    {
        if (CurrentEntryId is null) return;

        var previous = _db.GetEntryBefore(CurrentEntryId.Value);
        if (previous is { EndedAt: not null } && previous.EndedAt > newStart && previous.StartedAt <= newStart)
            _db.UpdateEntryTimes(previous.Id, previous.StartedAt, newStart);

        CurrentStart = newStart;
        _db.UpdateEntryStart(CurrentEntryId.Value, newStart);
    }

    /// <summary>
    /// Rattache l'entrée en cours à une autre tâche (correction de saisie).
    /// On ne renomme PAS la tâche en base : cela réécrirait aussi tout l'historique
    /// des entrées passées portant ce nom.
    /// </summary>
    public void ReassignCurrentTask(TaskItem task)
    {
        if (CurrentTask is null || task.Id == CurrentTask.Id) return;

        CurrentTask = task;
        if (CurrentEntryId.HasValue) _db.UpdateEntryTask(CurrentEntryId.Value, task.Id);
        _db.TouchTask(task.Id);
        CurrentTaskChanged?.Invoke(CurrentTask);
    }

    /// <summary>
    /// Relit la tâche en cours en base : son libellé a pu changer dans la fenêtre
    /// « Gérer les tâches » pendant que le chronomètre tournait.
    /// </summary>
    public void ReloadCurrentTask()
    {
        if (CurrentTask is null) return;
        var fresh = _db.GetTask(CurrentTask.Id);
        if (fresh is null || fresh.Name == CurrentTask.Name) return;

        CurrentTask = fresh;
        CurrentTaskChanged?.Invoke(CurrentTask);
    }

    /// <summary>
    /// Annule la dernière bascule automatique : l'entrée créée est supprimée et l'entrée
    /// précédente rouverte, comme si la détection n'avait pas eu lieu. Le temps de la réunion
    /// revient donc à la tâche d'avant, sans trou ni entrée coupée en deux.
    /// Renvoie faux si rien n'a pu être repris (la bascule est partie d'un suivi à l'arrêt) :
    /// le suivi est alors simplement stoppé.
    /// </summary>
    public bool UndoAutoSwitch()
    {
        if (CurrentEntryId is null) return false;

        var previous = _db.GetEntryBefore(CurrentEntryId.Value);
        _db.DeleteEntry(CurrentEntryId.Value);
        CurrentEntryId = null;

        if (previous is null)
        {
            _timer.Stop();
            CurrentTask = null;
            IsPaused = false;
            CurrentTaskChanged?.Invoke(null);
            return false;
        }

        // Rouvrir = repasser ended_at à NULL : l'entrée redevient celle que porte le chronomètre.
        _db.UpdateEntryTimes(previous.Id, previous.StartedAt, null);
        CurrentEntryId = previous.Id;
        CurrentTask = _db.GetTask(previous.TaskId);
        CurrentStart = previous.StartedAt;
        IsPaused = false;
        _lastReminder = DateTime.Now;
        _timer.Start();
        CurrentTaskChanged?.Invoke(CurrentTask);
        return true;
    }

    /// <summary>
    /// Supprime l'entrée en cours et arrête le suivi (« je n'aurais pas dû démarrer ça »).
    /// </summary>
    public void CancelCurrentEntry()
    {
        _timer.Stop();
        if (CurrentEntryId.HasValue) _db.DeleteEntry(CurrentEntryId.Value);
        CurrentEntryId = null;
        CurrentTask = null;
        IsPaused = false;
        CurrentTaskChanged?.Invoke(null);
    }

    /// <summary>Clôture proprement l'entrée en cours (appelé à la fermeture de l'appli).</summary>
    public void Flush()
    {
        _timer.Stop();
        if (CurrentEntryId.HasValue)
        {
            _db.EndEntry(CurrentEntryId.Value, DateTime.Now);
            CurrentEntryId = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (CurrentTask is null) return;

        if (!IsPaused) Tick?.Invoke(CurrentTask, DateTime.Now - CurrentStart);

        if (RemindersSuspended) { _lastReminder = DateTime.Now; return; }

        if ((DateTime.Now - _lastReminder).TotalMinutes < _reminderIntervalMinutes) return;

        _lastReminder = DateTime.Now; // évite les rappels en rafale
        if (IsPaused) PausedReminderDue?.Invoke(CurrentTask);
        else ReminderDue?.Invoke(CurrentTask);
    }
}
