namespace TimeTracker.Core.Models;

/// <summary>
/// Une tâche accompagnée de son usage réel (fenêtre « Gérer les tâches »).
/// Sert à distinguer une coquille jamais réutilisée d'une tâche qui porte des heures.
/// </summary>
public class TaskUsage
{
    public TaskItem Task { get; init; } = new();

    /// <summary>Nombre d'entrées de temps rattachées à cette tâche.</summary>
    public int EntryCount { get; init; }

    /// <summary>Temps total pointé sur cette tâche (entrées clôturées uniquement).</summary>
    public TimeSpan Total { get; init; }

    /// <summary>Début de la dernière entrée, ou null si la tâche n'a jamais servi.</summary>
    public DateTime? LastEntry { get; init; }
}
