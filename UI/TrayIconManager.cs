using System.Drawing;
using System.Windows.Forms;

namespace TimeTracker.UI;

/// <summary>
/// Icône de la barre système + menu contextuel. Construit avec WinForms
/// (NotifyIcon n'a pas d'équivalent natif WPF).
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _currentTaskItem;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly ToolStripMenuItem _changeTaskItem;
    private readonly ToolStripMenuItem _quickEditItem;
    private readonly ToolStripMenuItem _stopItem;
    private Icon? _generatedIcon;

    public event EventHandler? ChangeTaskRequested;
    public event EventHandler? PauseResumeRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? QuickEditRequested;
    public event EventHandler? OpenDashboardRequested;
    public event EventHandler? ManageTasksRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? QuitRequested;

    public TrayIconManager()
    {
        _generatedIcon = AppIcon.CreateTrayIcon();

        var menu = new ContextMenuStrip();

        _currentTaskItem = new ToolStripMenuItem("Aucune tâche") { Enabled = false };
        menu.Items.Add(_currentTaskItem);
        menu.Items.Add(new ToolStripSeparator());

        _changeTaskItem = new ToolStripMenuItem("Changer de tâche…");
        _changeTaskItem.Click += (s, e) => ChangeTaskRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_changeTaskItem);

        _pauseResumeItem = new ToolStripMenuItem("Pause");
        _pauseResumeItem.Click += (s, e) => PauseResumeRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_pauseResumeItem);

        _quickEditItem = new ToolStripMenuItem("Corriger…");
        _quickEditItem.Click += (s, e) => QuickEditRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_quickEditItem);

        _stopItem = new ToolStripMenuItem("Arrêter le suivi (fin de journée)");
        _stopItem.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_stopItem);

        menu.Items.Add(new ToolStripSeparator());

        var dashboard = new ToolStripMenuItem("Tableau de bord");
        dashboard.Click += (s, e) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(dashboard);

        var manageTasks = new ToolStripMenuItem("Gérer les tâches…");
        manageTasks.Click += (s, e) => ManageTasksRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(manageTasks);

        var settings = new ToolStripMenuItem("Paramètres…");
        settings.Click += (s, e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quitter");
        quit.Click += (s, e) => QuitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "TimeTracker",
            Visible = false,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (s, e) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (s, e) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _icon.BalloonTipClosed += (s, e) => _balloonAction = null;
    }

    public void Show() => _icon.Visible = true;

    /// <summary>Met à jour le tooltip (tronqué à 63 caractères par Windows).</summary>
    public void UpdateTooltip(string text)
    {
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void SetCurrentTask(string name, bool paused, bool hasTask = true)
    {
        _currentTaskItem.Text = paused ? $"⏸ {name} (en pause)" : $"▶ {name}";
        _pauseResumeItem.Text = paused ? "Reprendre" : "Pause";
        _pauseResumeItem.Enabled = hasTask;
        _stopItem.Enabled = hasTask;
        _quickEditItem.Enabled = hasTask;
    }

    /// <summary>Affiche les raccourcis actifs à droite des entrées de menu concernées.</summary>
    public void SetHotkeyHints(string changeTask, string quickEdit, string pause = "")
    {
        _changeTaskItem.ShortcutKeyDisplayString = changeTask;
        _quickEditItem.ShortcutKeyDisplayString = quickEdit;
        _pauseResumeItem.ShortcutKeyDisplayString = pause;
    }

    /// <summary>
    /// Affiche une notification. <paramref name="onClick"/> est déclenché si l'utilisateur clique
    /// la bulle (« Réunion détectée — clique pour annuler ») ; l'action est oubliée dès que la
    /// bulle se ferme, pour qu'un clic tardif sur une bulle suivante n'annule pas autre chose.
    /// </summary>
    public void ShowBalloon(string title, string text, Action? onClick = null)
    {
        _balloonAction = onClick;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(onClick is null ? 5000 : 15000);
    }

    private Action? _balloonAction;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generatedIcon?.Dispose();
    }
}
