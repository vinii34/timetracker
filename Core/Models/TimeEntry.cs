namespace TimeTracker.Core.Models;

/// <summary>
/// Un intervalle de temps passé sur une tâche.
/// </summary>
public class TimeEntry
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public bool IsMeeting { get; set; }
    public string? Notes { get; set; }

    /// <summary>Nom de la tâche associée (rempli lors des lectures jointes).</summary>
    public string TaskName { get; set; } = string.Empty;

    /// <summary>Durée courante : valeur enregistrée, sinon temps écoulé depuis le début.</summary>
    public TimeSpan Elapsed =>
        EndedAt.HasValue
            ? EndedAt.Value - StartedAt
            : DateTime.Now - StartedAt;
}
