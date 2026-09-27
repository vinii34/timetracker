using System.Windows;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
using TimeTracker.Core.Services.Ai;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace TimeTracker.UI;

/// <summary>
/// Bibliothèque des tâches : renommer (l'historique suit), fusionner deux doublons,
/// supprimer une tâche jamais utilisée, épingler les tâches courantes en favori.
/// </summary>
public partial class TaskManagerWindow : Window
{
    /// <summary>Ligne affichée. <see cref="IsCurrent"/> protège la tâche en cours de suivi.</summary>
    internal sealed class TaskRow
    {
        public required TaskItem Task { get; init; }
        public int EntryCount { get; init; }
        public bool IsCurrent { get; init; }

        public long Id => Task.Id;
        public string Name => Task.Name;
        public string Star => Task.IsFavorite ? "★" : "";
        public string Entries { get; init; } = "";
        public string Total { get; init; } = "";
        public string LastUsed { get; init; } = "";
    }

    private readonly DatabaseService _db;
    private readonly long? _currentTaskId;
    private readonly AppSettings? _settings;
    private List<TaskRow> _allRows = new();

    /// <summary>Vrai si la bibliothèque a été modifiée (l'appelant rafraîchit ses vues).</summary>
    public bool Changed { get; private set; }

    /// <param name="settings">Réglages, pour l'assistant IA ; null (tests) = bouton inactif.</param>
    internal TaskManagerWindow(DatabaseService db, long? currentTaskId = null, AppSettings? settings = null)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _db = db;
        _currentTaskId = currentTaskId;
        _settings = settings;

        AiButton.IsEnabled = settings?.AiEnabled == true;
        AiStatusText.Text = settings?.AiEnabled == true
            ? ""
            : "Assistant IA désactivé (Paramètres > Assistant IA).";

        Loaded += (_, _) => { Reload(); Activate(); };
        SizeChanged += (_, _) => AdjustNameColumn();
    }

    /// <summary>Ouvre la fenêtre en modal ; renvoie true si quelque chose a changé.</summary>
    public static bool Show(Window? owner, DatabaseService db, long? currentTaskId = null, AppSettings? settings = null)
    {
        var win = new TaskManagerWindow(db, currentTaskId, settings);
        if (owner != null && owner.IsLoaded) win.Owner = owner;
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
        return win.Changed;
    }

    // ---------------------------------------------------------------- Chargement

    private void Reload(long? selectId = null)
    {
        long? keep = selectId ?? SelectedRow?.Id;

        _allRows = _db.GetTaskUsage().Select(u => new TaskRow
        {
            Task = u.Task,
            EntryCount = u.EntryCount,
            IsCurrent = _currentTaskId == u.Task.Id,
            Entries = u.EntryCount.ToString(),
            Total = u.Total > TimeSpan.Zero ? ExportService.FormatHm(u.Total) : "—",
            LastUsed = u.LastEntry?.ToString("dd/MM/yyyy") ?? "jamais"
        }).ToList();

        ApplyFilter();
        LoadDuplicates();

        if (keep is long id)
            TaskList.SelectedItem = TaskList.Items.OfType<TaskRow>().FirstOrDefault(r => r.Id == id);

        AdjustNameColumn();
        UpdateSelection();
    }

    /// <summary>Une paire de doublons probables : la source (moins d'entrées) fusionne dans la cible.</summary>
    internal sealed record DuplicateRow(TaskRow Source, TaskRow Target, string Text);

    /// <summary>
    /// Propose les fusions évidentes — coquille, accent, espace — sans rien envoyer nulle part
    /// (<see cref="TaskSimilarity"/>). La tâche qui porte le moins d'entrées est fusionnée dans
    /// l'autre, jamais la tâche en cours de suivi. Cinq paires au plus : au-delà, c'est le
    /// filtre qui sert.
    /// </summary>
    private void LoadDuplicates()
    {
        var byId = _allRows.ToDictionary(r => r.Id);
        var pairs = TaskSimilarity.FindDuplicates(_allRows.Select(r => r.Task).ToList())
            .Select(d =>
            {
                var a = byId[d.A.Id];
                var b = byId[d.B.Id];
                var (source, target) = a.EntryCount <= b.EntryCount ? (a, b) : (b, a);
                if (source.IsCurrent) (source, target) = (target, source);
                return new DuplicateRow(source, target,
                    $"« {source.Name} » ({source.Entries}) ≈ « {target.Name} » ({target.Entries})");
            })
            .Where(p => !p.Source.IsCurrent)
            .Take(5)
            .ToList();

        DuplicateList.ItemsSource = pairs;
        DuplicatePanel.Visibility = pairs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MergeDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not DuplicateRow pair) return;

        var answer = MessageBox.Show(this,
            $"Rattacher les {pair.Source.Entries} entrée(s) de « {pair.Source.Name} » à « {pair.Target.Name} », " +
            $"puis supprimer « {pair.Source.Name} » ?\n\nAucun temps n'est perdu.",
            "Fusionner deux doublons", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Merge(pair.Source, pair.Target.Id, pair.Target.Name);
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var favOnly = FavoritesOnlyCheck.IsChecked == true;

        TaskList.ItemsSource = _allRows
            .Where(r => !favOnly || r.Task.IsFavorite)
            .Where(r => filter.Length == 0
                        || r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    private TaskRow? SelectedRow => TaskList.SelectedItem as TaskRow;

    /// <summary>Étire la colonne « Tâche » sur la largeur restante.</summary>
    private void AdjustNameColumn()
    {
        double available = TaskList.ActualWidth - (30 + 70 + 80 + 110) - 28;
        if (available > 120) NameColumn.Width = available;
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
        UpdateSelection();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
        UpdateSelection();
    }

    private void TaskList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateSelection();

    private void TaskList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow != null) NameBox.Focus();
    }

    /// <summary>Reflète la ligne sélectionnée dans le panneau du bas.</summary>
    private void UpdateSelection()
    {
        var row = SelectedRow;
        HideMessage();

        bool has = row != null;
        NameBox.IsEnabled = has;
        RenameButton.IsEnabled = has;
        FavoriteButton.IsEnabled = has;
        MergeTargetBox.IsEnabled = has;
        MergeButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;

        if (row is null)
        {
            SelectionText.Text = "Aucune tâche sélectionnée";
            NameBox.Text = "";
            MergeTargetBox.ItemsSource = null;
            FavoriteButton.Content = "★ Ajouter aux favoris";
            return;
        }

        SelectionText.Text = row.IsCurrent
            ? $"« {row.Name} » — tâche en cours de suivi"
            : $"« {row.Name} » — {row.Entries} entrée(s), {row.Total}";
        NameBox.Text = row.Name;
        FavoriteButton.Content = row.Task.IsFavorite ? "★ Retirer des favoris" : "★ Ajouter aux favoris";

        // Cibles de fusion : toutes les autres tâches, quelle que soit la vue filtrée.
        MergeTargetBox.ItemsSource = _allRows.Where(r => r.Id != row.Id).ToList();
        MergeTargetBox.DisplayMemberPath = "Name";
        MergeTargetBox.SelectedIndex = -1;

        // La tâche en cours reste modifiable en nom, mais pas supprimable ni fusionnable :
        // le TimerService pointe dessus, elle disparaîtrait sous ses pieds.
        DeleteButton.IsEnabled = !row.IsCurrent;
        MergeButton.IsEnabled = !row.IsCurrent;
    }

    // ------------------------------------------------------------------ Actions

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Rename_Click(sender, e); e.Handled = true; }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ShowError("Le nom ne peut pas être vide."); return; }
        if (name == row.Name) { ShowError("Le nom est inchangé."); return; }

        // Un nom déjà pris ne peut pas être « pris » deux fois : c'est une fusion déguisée.
        var clash = _db.FindTaskByName(name);
        if (clash != null && clash.Id != row.Id)
        {
            var answer = MessageBox.Show(this,
                $"« {clash.Name} » existe déjà.\n\n" +
                $"Fusionner « {row.Name} » ({row.Entries} entrée(s)) dans « {clash.Name} » ?\n" +
                "Les entrées seront rattachées à la tâche existante.",
                "Nom déjà utilisé", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            if (row.IsCurrent) { ShowError("Impossible de fusionner la tâche en cours de suivi."); return; }
            Merge(row, clash.Id, clash.Name);
            return;
        }

        try
        {
            _db.UpdateTaskName(row.Id, name);
            Changed = true;
            Logger.Info($"Tâche {row.Id} renommée : « {row.Name} » → « {name} ».");
            Reload(row.Id);
            ShowInfo($"Renommée en « {name} » — l'historique suit.");
        }
        catch (Exception ex)
        {
            Logger.Error("TaskManager.Rename", ex);
            ShowError($"Renommage impossible : {ex.Message}");
        }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        bool favorite = !row.Task.IsFavorite;
        _db.SetFavorite(row.Id, favorite);
        Changed = true;
        Logger.Info($"Tâche « {row.Name} » {(favorite ? "ajoutée aux" : "retirée des")} favoris.");
        Reload(row.Id);
        ShowInfo(favorite
            ? $"« {row.Name} » est en tête du sélecteur de tâche."
            : $"« {row.Name} » n'est plus un favori.");
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;
        if (MergeTargetBox.SelectedItem is not TaskRow target)
        {
            ShowError("Choisis la tâche dans laquelle fusionner celle-ci.");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Rattacher les {row.Entries} entrée(s) de « {row.Name} » à « {target.Name} », " +
            $"puis supprimer « {row.Name} » ?\n\nAucun temps n'est perdu.",
            "Fusionner deux tâches", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Merge(row, target.Id, target.Name);
    }

    private void Merge(TaskRow row, long targetId, string targetName)
    {
        try
        {
            _db.MergeTasks(row.Id, targetId);
            Changed = true;
            Logger.Info($"Tâches fusionnées : « {row.Name} » → « {targetName} » ({row.EntryCount} entrée(s)).");
            Reload(targetId);
            ShowInfo($"« {row.Name} » fusionnée dans « {targetName} ».");
        }
        catch (Exception ex)
        {
            Logger.Error("TaskManager.Merge", ex);
            ShowError($"Fusion impossible : {ex.Message}");
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        // L'historique est protégé par la clé étrangère : on oriente vers la fusion.
        if (row.EntryCount > 0)
        {
            ShowError($"« {row.Name} » porte {row.EntryCount} entrée(s) de temps. " +
                      "Fusionne-la dans la bonne tâche plutôt que de la supprimer.");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Supprimer « {row.Name} » de la liste des tâches ?\n\n" +
            "Cette tâche n'a jamais servi : aucun temps ne sera perdu.",
            "Supprimer une tâche", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _db.DeleteTask(row.Id);
            Changed = true;
            Logger.Info($"Tâche « {row.Name} » supprimée de la bibliothèque.");
            Reload();
            ShowInfo($"« {row.Name} » supprimée.");
        }
        catch (Exception ex)
        {
            Logger.Error("TaskManager.Delete", ex);
            ShowError($"Suppression impossible : {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Assistant IA

    /// <summary>
    /// Envoie les noms des tâches au fournisseur configuré, affiche ses propositions à cocher,
    /// applique la sélection. La tâche en cours de suivi n'est jamais fusionnée (source) ; un
    /// renommage vers un nom déjà pris est écarté plutôt que transformé en fusion déguisée.
    /// </summary>
    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || !_settings.AiEnabled) return;
        HideMessage();

        IAiProvider provider;
        try { provider = AiProviderFactory.Create(_settings); }
        catch (Exception ex) { ShowError(ex.Message); return; }

        var usage = _db.GetTaskUsage();
        AiButton.IsEnabled = false;
        AiStatusText.Text = $"Interrogation de {provider.Name} ({usage.Count} noms envoyés)…";
        Logger.Info($"IA : {usage.Count} nom(s) de tâche envoyé(s) à {provider.Name}.");

        TaskCleanupAssistant.Proposal proposal;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            proposal = await TaskCleanupAssistant.RunAsync(provider, usage, cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error("TaskManager.Ai", ex);
            ShowError($"L'assistant n'a pas répondu : {ex.Message}");
            AiStatusText.Text = "";
            AiButton.IsEnabled = true;
            return;
        }

        AiStatusText.Text = "";
        AiButton.IsEnabled = true;
        Logger.Info($"IA : {proposal.Merges.Count} fusion(s), {proposal.Renames.Count} renommage(s) proposés, " +
                    $"{proposal.Ignored.Count} écarté(s).");

        var chosen = AiSuggestionsWindow.Show(this, provider.Name, proposal);
        if (chosen.Count == 0) return;

        int applied = 0;
        var skipped = new List<string>();
        foreach (var row in chosen)
        {
            try
            {
                if (row.Merge is { } m)
                {
                    var source = _db.FindTaskByName(m.From);
                    var target = _db.FindTaskByName(m.Into);
                    if (source is null || target is null || source.Id == target.Id) { skipped.Add(m.From); continue; }
                    if (source.Id == _currentTaskId) { skipped.Add($"{m.From} (tâche en cours)"); continue; }
                    _db.MergeTasks(source.Id, target.Id);
                    Logger.Info($"Tâches fusionnées (IA) : « {m.From} » → « {m.Into} ».");
                    applied++;
                }
                else if (row.Rename is { } r)
                {
                    var task = _db.FindTaskByName(r.From);
                    if (task is null) { skipped.Add(r.From); continue; }
                    var clash = _db.FindTaskByName(r.To);
                    if (clash != null && clash.Id != task.Id) { skipped.Add($"{r.From} → {r.To} (nom déjà pris)"); continue; }
                    _db.UpdateTaskName(task.Id, r.To);
                    Logger.Info($"Tâche renommée (IA) : « {r.From} » → « {r.To} ».");
                    applied++;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("TaskManager.Ai/apply", ex);
                skipped.Add(row.Title);
            }
        }

        if (applied > 0) Changed = true;
        Reload();
        var summary = $"{applied} proposition(s) appliquée(s).";
        if (skipped.Count > 0) summary += $" Écarté : {string.Join(" ; ", skipped)}.";
        if (skipped.Count > 0) ShowError(summary); else ShowInfo(summary);
    }

    // ------------------------------------------------------------------ Messages

    // Pinceaux figés : « Color » est ambigu (System.Drawing vs System.Windows.Media)
    // dès qu'on active WinForms et WPF dans le même projet.
    private static readonly Brush ErrorBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush InfoBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));

    private void ShowError(string message) => SetMessage(message, ErrorBrush);

    private void ShowInfo(string message) => SetMessage(message, InfoBrush);

    private void SetMessage(string message, Brush color)
    {
        MessageText.Text = message;
        MessageText.Foreground = color;
        MessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessageText.Visibility = Visibility.Collapsed;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
