namespace TimeTracker.UI;

/// <summary>
/// Commandes de suivi exposées au tableau de bord. Le pilotage réel (TimerService, popups,
/// tray) reste dans <c>App</c> : la fenêtre ne fait que déclencher, ce qui lui permet aussi
/// d'être rendue seule par <c>--uitest</c> avec des délégués vides.
/// </summary>
public sealed class TrackerActions
{
    /// <summary>État affiché dans la barre : libellé, pause en cours, tâche active ou non.</summary>
    public Func<(string Label, bool IsPaused, bool HasTask)> State { get; init; }
        = () => ("Aucune tâche en cours", false, false);

    public Action ChangeTask { get; init; } = () => { };
    public Action QuickEdit { get; init; } = () => { };
    public Action TogglePause { get; init; } = () => { };
    public Action StopTracking { get; init; } = () => { };
    public Action ManageTasks { get; init; } = () => { };
    public Action OpenSettings { get; init; } = () => { };

    /// <summary>Nombre de noms de tâches à vérifier (fautes, doublons) ; zéro masque le bandeau.</summary>
    public Func<int> NamesToReview { get; init; } = () => 0;

    public Action ReviewNames { get; init; } = () => { };
}
