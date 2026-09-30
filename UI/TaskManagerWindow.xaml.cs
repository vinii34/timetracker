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
    private Dictionary<long, int> _learnedWords = new();

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
        _learnedWords = _db.CountTaskHints();

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
    /// Propose les fusions évidentes — coquille, accent, espace — sans rien envoyer nulle part.
    /// Même règle que le bandeau « Noms à vérifier » (<see cref="NameReview"/>) : le nom sans
    /// faute survit, même s'il porte moins d'entrées (« Onborading » 6 / « Onboarding » 3 sur les
    /// vrais noms), et jamais la tâche en cours ne part. Cinq paires au plus : au-delà, c'est le
    /// filtre qui sert.
    /// </summary>
    private void LoadDuplicates()
    {
        var byName = _allRows.GroupBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);
        var usage = _allRows.Select(r => new TaskUsage { Task = r.Task, EntryCount = r.EntryCount }).ToList();
        var pairs = NameReview.Find(usage, _currentTaskId, _settings?.MeetingTaskName ?? TaskSimilarity.DefaultMeetingName,
                                    new HashSet<string>())
            .Where(i => i.Merge != null && byName.ContainsKey(i.Merge.From) && byName.ContainsKey(i.Merge.Into))
            .Select(i =>
            {
                var source = byName[i.Merge!.From];
                var target = byName[i.Merge.Into];
                return new DuplicateRow(source, target,
                    $"« {source.Name} » ({source.Entries}) ≈ « {target.Name} » ({target.Entries})");
            })
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

        int learned = row != null ? _learnedWords.GetValueOrDefault(row.Id) : 0;
        ForgetButton.IsEnabled = learned > 0;
        ForgetButton.Content = learned > 0 ? $"🧹 Oublier ses fenêtres ({learned} mots)"
                             : row != null ? "🧹 Rien d'appris" : "🧹 Oublier ses fenêtres";

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

    /// <summary>
    /// Oubli ciblé de ce qu'une tâche a appris des fenêtres. Pensé pour la tâche fourre-tout :
    /// avant la v1.7, elle apprenait les sujets de mails de tous les clients et « ressemblait »
    /// à tout. L'effacement général reste dans les Paramètres.
    /// </summary>
    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;
        int learned = _learnedWords.GetValueOrDefault(row.Id);
        if (learned == 0) return;

        var answer = MessageBox.Show(this,
            $"Oublier les {learned} mot(s) de fenêtres appris pour « {row.Name} » ?\n\n" +
            "Elle réapprendra au fil de l'usage. Le temps pointé n'est pas touché.",
            "Oublier ce qui a été appris", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            int removed = _db.ClearTaskHints(row.Id);
            Logger.Info($"Apprentissage oublié pour « {row.Name} » : {removed} mot(s).");
            Reload(row.Id);
            ShowInfo($"{removed} mot(s) oublié(s) pour « {row.Name} ».");
        }
        catch (Exception ex)
        {
            Logger.Error("TaskManager.Forget", ex);
            ShowError($"Effacement impossible : {ex.Message}");
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

        var (applied, skipped) = NameReview.Apply(_db, chosen.Select(r => (r.Merge, r.Rename)), _currentTaskId, "IA");
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
