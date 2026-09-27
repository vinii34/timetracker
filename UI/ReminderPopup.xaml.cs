using System.Media;
using System.Windows;
using TimeTracker.Core.Services;

namespace TimeTracker.UI;

/// <summary>Réponse de l'utilisateur au rappel périodique.</summary>
public enum ReminderChoice
{
    /// <summary>Continuer la tâche en cours (ou rester en pause) : choix non destructif.</summary>
    Continue,
    /// <summary>Ouvrir le sélecteur pour basculer sur une autre tâche.</summary>
    Change,
    /// <summary>Sortir de pause et repartir sur la même tâche.</summary>
    Resume
}

public partial class ReminderPopup : Window
{
    private ReminderChoice _choice = ReminderChoice.Continue;

    /// <param name="hint">
    /// Ce que l'activité des fenêtres laisse penser, quand elle contredit la tâche en cours
    /// (« tu sembles plutôt sur … »). Une information, jamais une bascule.
    /// </param>
    internal ReminderPopup(string taskName, bool playSound, TimeSpan? pausedFor = null, string? hint = null)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        TaskNameText.Text = taskName;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        if (pausedFor is TimeSpan paused)
        {
            QuestionText.Text = "Toujours en pause ?";
            DetailText.Text = $"Suivi arrêté depuis {ExportService.FormatHm(paused)} — " +
                              "ce temps n'est compté sur aucune tâche.";
            RunningButtons.Visibility = Visibility.Collapsed;
            PausedButtons.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            PositionBottomRight();
            if (playSound) SystemSounds.Asterisk.Play();
            Activate();
        };
    }

    /// <summary>
    /// Affiche le rappel et rappelle <paramref name="answered"/> avec true si l'utilisateur
    /// continue sa tâche, false s'il veut changer. Fermer la fenêtre = continuer (choix non
    /// destructif).
    /// </summary>
    public static void Ask(string taskName, bool playSound, Action<bool> answered, string? hint = null)
    {
        var win = new ReminderPopup(taskName, playSound, hint: hint);
        win.Closed += (_, _) => answered(win._choice != ReminderChoice.Change);
        win.Show();
    }

    /// <summary>
    /// Rappel affiché pendant une pause : reprendre, changer de tâche, ou rester en pause.
    /// Fermer la fenêtre = rester en pause.
    /// </summary>
    public static void AskPaused(string taskName, bool playSound, TimeSpan pausedFor,
                                 Action<ReminderChoice> answered)
    {
        var win = new ReminderPopup(taskName, playSound, pausedFor);
        win.Closed += (_, _) => answered(win._choice);
        win.Show();
    }

    /// <summary>
    /// Coin bas-droit, mais <b>jamais hors de l'écran</b>.
    ///
    /// Ce rappel se déclenche typiquement au réveil de la machine, quand la configuration
    /// d'affichage n'est pas encore stabilisée : une position calculée à cet instant peut tomber
    /// en dehors du bureau visible. La fenêtre est <c>ShowInTaskbar="False"</c> et
    /// <c>WindowStyle="ToolWindow"</c>, donc absente de la barre des tâches <b>et</b> d'Alt-Tab :
    /// hors écran, elle serait introuvable. C'est ce qui, combinée à un <c>ShowDialog()</c>,
    /// rendait le tableau de bord incliquable le lendemain matin — chaque clic ne produisait
    /// qu'un « ding » de fenêtre bloquée.
    /// </summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        // Avec SizeToContent la hauteur peut ne pas être encore connue au Loaded.
        double height = ActualHeight > 0 ? ActualHeight : 160;
        Left = Math.Clamp(area.Right - Width - 12, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(area.Bottom - height - 12, area.Top, Math.Max(area.Top, area.Bottom - height));
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => Answer(ReminderChoice.Continue);

    private void StayPaused_Click(object sender, RoutedEventArgs e) => Answer(ReminderChoice.Continue);

    private void Change_Click(object sender, RoutedEventArgs e) => Answer(ReminderChoice.Change);

    private void Resume_Click(object sender, RoutedEventArgs e) => Answer(ReminderChoice.Resume);

    private void Answer(ReminderChoice choice)
    {
        _choice = choice;
        Close();
    }
}
