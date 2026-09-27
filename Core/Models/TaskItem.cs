namespace TimeTracker.Core.Models;

/// <summary>
/// Une tâche sur laquelle l'utilisateur peut pointer du temps.
/// </summary>
public class TaskItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime? LastUsed { get; set; }

    /// <summary>Tâche épinglée : proposée en tête du sélecteur, avant les récentes.</summary>
    public bool IsFavorite { get; set; }
}
