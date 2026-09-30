using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
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

        /// <summary>Rang de la tâche choisie dans la liste (1 = première), 0 pour une tâche tapée.</summary>
        public int Rank { get; init; }

        /// <summary>La tâche choisie portait le 💡 (suggérée d'après les fenêtres).</summary>
        public bool WasSuggested { get; init; }

        /// <summary>D'où vient le nom tapé : « tapé », « corrigé » (Tu voulais dire), « IA ».</summary>
        public string NameOrigin { get; init; } = "tapé";
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
    private readonly Func<string, NameReview.Hint?>? _checkName;
    private readonly DispatcherTimer _hintDelay;
    private NameReview.Hint? _hint;
    private string? _aiName;
    private string? _applied;
    private string _appliedOrigin = "tapé";

    /// <param name="suggestions">Raison, par id de tâche, pour celles que l'activité suggère.</param>
    /// <param name="checkName">Contrôle du nom tapé (« Tu voulais dire … ? ») ; null = aucun.</param>
    internal TaskSelectorPopup(IReadOnlyList<TaskItem> tasks, string? prefill, bool offerBackdate = false,
                               IReadOnlyDictionary<long, string>? suggestions = null,
                               Func<string, NameReview.Hint?>? checkName = null)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _checkName = checkName;
        // Le contrôle attend une pause de frappe : « Onboardin » à mi-mot n'est pas une faute.
        _hintDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hintDelay.Tick += (_, _) => { _hintDelay.Stop(); UpdateHint(); };

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
            if (NewTaskBox.Text.Length > 0) UpdateHint();   // nom pré-rempli : contrôlé d'emblée
        };
        Closed += (_, _) => _hintDelay.Stop();
    }

    /// <summary>
    /// Ouvre le popup en modal et renvoie le choix, ou null si annulé.
    /// <paramref name="offerBackdate"/> affiche le décalage 5 / 15 min (rappel « je change »).
    /// <paramref name="opened"/> reçoit la fenêtre affichée : c'est là que l'appelant lance ce
    /// qui arrivera après coup (la proposition de l'IA).
    /// </summary>
    public static PickResult? Pick(IReadOnlyList<TaskItem> tasks, string? prefill = null,
        bool offerBackdate = false, IReadOnlyDictionary<long, string>? suggestions = null,
        Func<string, NameReview.Hint?>? checkName = null, Action<TaskSelectorPopup>? opened = null)
    {
        var win = new TaskSelectorPopup(tasks, prefill, offerBackdate, suggestions, checkName);
        if (opened != null) win.Loaded += (_, _) => opened(win);
        win.ShowDialog();
        return win._result;
    }

    /// <summary>
    /// La proposition de l'IA est arrivée : elle s'affiche sous le champ tant que rien n'y est
    /// tapé, Tab la reprend. <paramref name="sent"/> dit ce qui est parti — obligatoire à l'écran.
    /// </summary>
    internal void ShowAiSuggestion(string name, bool existing, string why, string sent)
    {
        if (!IsLoaded) return;
        _aiName = name;
        AiText.Text = (existing ? $"✨ Tâche proposée : « {name} »" : $"✨ Nom proposé : « {name} »")
                      + (why.Length > 0 ? $" — {why}" : "") + " (Tab pour l'utiliser)";
        AiSentText.Text = sent;
        UpdateAiVisibility();
    }

    /// <summary>L'IA a été interrogée sans résultat utilisable : on le dit, sans insister.</summary>
    internal void ShowAiNothing(string message, string sent)
    {
        if (!IsLoaded) return;
        _aiName = null;
        AiText.Text = message;
        AiSentText.Text = sent;
        AiPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Décalage choisi, en minutes (0 si le panneau n'est pas affiché).</summary>
    private int SelectedBackdate()
    {
        if (BackdatePanel.Visibility != Visibility.Visible) return 0;
        if (Backdate5.IsChecked == true) return 5;
        if (Backdate15.IsChecked == true) return 15;
        return 0;
    }

    private void Confirm(TaskRow? row, string? newName, string origin = "tapé")
    {
        _result = new PickResult
        {
            ExistingTask = row?.Task,
            NewTaskName = newName,
            BackdateMinutes = SelectedBackdate(),
            Rank = row?.Index ?? 0,
            WasSuggested = row?.Reason != null,
            NameOrigin = origin
        };
        Close();
    }

    private void StartNew_Click(object sender, RoutedEventArgs e) => CommitNewTask();

    private void CommitNewTask()
    {
        var name = NewTaskBox.Text.Trim();
        if (name.Length == 0) return;
        var origin = _applied != null && name == _applied.Trim() ? _appliedOrigin : "tapé";
        Confirm(null, name, origin);
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitNewTask(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    /// <summary>Tab reprend la correction proposée, ou à défaut le nom proposé par l'IA.</summary>
    private void NewTaskBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None) return;
        if (HintButton.Visibility == Visibility.Visible && _hint != null) { Apply(_hint.Suggested, "corrigé"); e.Handled = true; }
        else if (AiPanel.Visibility == Visibility.Visible && _aiName != null) { Apply(_aiName, "IA"); e.Handled = true; }
    }

    private void NewTaskBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        HintButton.Visibility = Visibility.Collapsed;
        _hintDelay.Stop();
        if (_checkName != null) _hintDelay.Start();
        UpdateAiVisibility();
    }

    private void UpdateHint()
    {
        var text = NewTaskBox.Text.Trim();
        _hint = text.Length == 0 ? null : _checkName?.Invoke(text);
        if (_hint is null || _hint.Suggested == text) { HintButton.Visibility = Visibility.Collapsed; return; }
        HintText.Text = $"{_hint.Message} — Tab pour {(_hint.IsExisting ? "la reprendre" : "corriger")}";
        HintButton.Visibility = Visibility.Visible;
    }

    private void UpdateAiVisibility() =>
        AiPanel.Visibility = _aiName != null && NewTaskBox.Text.Trim().Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

    private void Apply(string name, string origin)
    {
        _applied = name;
        _appliedOrigin = origin;
        NewTaskBox.Text = name;
        NewTaskBox.CaretIndex = name.Length;
        NewTaskBox.Focus();
        HintButton.Visibility = Visibility.Collapsed;
    }

    private void Hint_Click(object sender, RoutedEventArgs e)
    {
        if (_hint != null) Apply(_hint.Suggested, "corrigé");
    }

    private void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_aiName != null) Apply(_aiName, "IA");
    }

    private void RecentList_DoubleClick(object sender, MouseButtonEventArgs e) => CommitSelection();

    private void RecentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitSelection(); e.Handled = true; }
    }

    private void CommitSelection()
    {
        if (RecentList.SelectedItem is not TaskRow row) return;
        Confirm(row, null);
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
