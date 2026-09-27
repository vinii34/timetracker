using System.Windows;
using System.Windows.Input;
using TimeTracker.Core.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace TimeTracker.UI;

public partial class TaskSelectorPopup : Window
{
    /// <summary>Résultat du choix : soit une tâche existante, soit le nom d'une nouvelle.</summary>
    public class PickResult
    {
        public TaskItem? ExistingTask { get; init; }
        public string? NewTaskName { get; init; }

        /// <summary>Minutes de décalage du démarrage (0 = maintenant).</summary>
        public int BackdateMinutes { get; init; }
    }

    /// <summary>Ligne de la liste : les favoris passent devant, le chiffre est un raccourci.</summary>
    internal sealed class TaskRow
    {
        public required TaskItem Task { get; init; }
        public int Index { get; init; }

        /// <summary>Pourquoi cette tâche est suggérée d'après les fenêtres ; null sinon.</summary>
        public string? Reason { get; init; }

        /// <summary>Chiffre 1-9, ou puce pour les lignes au-delà (accessibles à la souris).</summary>
        public string Shortcut => Index is >= 1 and <= 9 ? Index.ToString() : "•";
        public string Display => (Reason is null ? "" : "💡 ") + (Task.IsFavorite ? $"★ {Task.Name}" : Task.Name);
        public string? Tip => Reason is null ? null : $"Suggérée d'après tes fenêtres : {Reason}";
    }

    private PickResult? _result;

    /// <param name="suggestions">Raison, par id de tâche, pour celles que l'activité suggère.</param>
    internal TaskSelectorPopup(IReadOnlyList<TaskItem> tasks, string? prefill, bool offerBackdate = false,
                               IReadOnlyDictionary<long, string>? suggestions = null)
    {
        InitializeComponent();
        Icon = AppIcon.Image;

        RecentList.ItemsSource = tasks
            .Select((t, i) => new TaskRow
            {
                Task = t, Index = i + 1,
                Reason = suggestions != null && suggestions.TryGetValue(t.Id, out var why) ? why : null
            })
            .ToList();

        if (offerBackdate) BackdatePanel.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(prefill))
        {
            NewTaskBox.Text = prefill;
            NewTaskBox.SelectAll();
        }

        Loaded += (_, _) =>
        {
            // Déclenché depuis le tray/raccourci alors qu'une autre appli a le focus :
            // forcer la fenêtre au premier plan et donner le focus au champ texte.
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
            NewTaskBox.Focus();
        };
    }

    /// <summary>
    /// Ouvre le popup en modal et renvoie le choix, ou null si annulé.
    /// <paramref name="offerBackdate"/> affiche le décalage 5 / 15 min (rappel « je change »).
    /// </summary>
    public static PickResult? Pick(IReadOnlyList<TaskItem> tasks, string? prefill = null,
        bool offerBackdate = false, IReadOnlyDictionary<long, string>? suggestions = null)
    {
        var win = new TaskSelectorPopup(tasks, prefill, offerBackdate, suggestions);
        win.ShowDialog();
        return win._result;
    }

    /// <summary>Décalage choisi, en minutes (0 si le panneau n'est pas affiché).</summary>
    private int SelectedBackdate()
    {
        if (BackdatePanel.Visibility != Visibility.Visible) return 0;
        if (Backdate5.IsChecked == true) return 5;
        if (Backdate15.IsChecked == true) return 15;
        return 0;
    }

    private void Confirm(TaskItem? existing, string? newName)
    {
        _result = new PickResult
        {
            ExistingTask = existing,
            NewTaskName = newName,
            BackdateMinutes = SelectedBackdate()
        };
        Close();
    }

    private void StartNew_Click(object sender, RoutedEventArgs e) => CommitNewTask();

    private void CommitNewTask()
    {
        var name = NewTaskBox.Text.Trim();
        if (name.Length == 0) return;
        Confirm(null, name);
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitNewTask(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void RecentList_DoubleClick(object sender, MouseButtonEventArgs e) => CommitSelection();

    private void RecentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitSelection(); e.Handled = true; }
    }

    private void CommitSelection()
    {
        if (RecentList.SelectedItem is not TaskRow row) return;
        Confirm(row.Task, null);
    }

    // Sélection par chiffre 1-9 où que soit le focus.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!NewTaskBox.IsKeyboardFocusWithin)
        {
            int? digit = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
                _ => null
            };
            if (digit is int idx && RecentList.Items.Count > idx)
            {
                RecentList.SelectedIndex = idx;
                CommitSelection();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
