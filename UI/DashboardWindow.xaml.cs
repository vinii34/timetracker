using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace TimeTracker.UI;

public partial class DashboardWindow : Window
{
    /// <summary>Ligne affichée dans la liste du jour (l'Id sert à l'édition/suppression).</summary>
    public sealed class EntryRow
    {
        public long Id { get; init; }
        public string Start { get; init; } = "";
        public string End { get; init; } = "";
        public string Name { get; init; } = "";
        public string Duration { get; init; } = "";
        /// <summary>Entrée non clôturée : c'est la tâche en cours, on n'y touche pas d'ici.</summary>
        public bool IsOpen { get; init; }
        /// <summary>Pictogramme de réunion, vide sinon. Null pour l'infobulle = pas d'infobulle.</summary>
        public string Marker { get; init; } = "";
        public string? MarkerTip { get; init; }
    }

    /// <summary>
    /// Marque des entrées venues de la détection automatique (<c>is_meeting</c>). Le drapeau
    /// existait en base et sortait à l'export, mais restait invisible à l'écran.
    /// </summary>
    private const string MeetingMarker = "👥";

    private readonly DatabaseService _db;
    private readonly AppSettings _settings;
    private readonly TrackerActions _actions;
    private readonly DispatcherTimer _refreshTimer;

    private DateTime _day = DateTime.Now.Date;
    private DateTime _weekStart = StartOfWeek(DateTime.Now);

    /// <summary>
    /// Nom technique de colonne → libellé affiché. Les colonnes du tableau croisé sont nommées
    /// « D0 »…« D6 » et non « lun. 20/07 » : dans un chemin de binding WPF, « . » et « / » sont
    /// des séparateurs, et une colonne ainsi nommée resterait désespérément vide.
    /// </summary>
    private readonly Dictionary<string, string> _weekHeaders = new();

    /// <summary>
    /// Empreinte du contenu affiché dans l'onglet Semaine. Le rafraîchissement automatique
    /// tourne toutes les 2 s alors que les durées ne bougent qu'à la minute : sans cette garde
    /// le DataGrid régénérerait ses colonnes en boucle.
    /// </summary>
    private string _weekSignature = "";

    /// <summary>
    /// <paramref name="actions"/> câble la barre de suivi ; omis (ex. <c>--uitest</c>),
    /// les boutons restent rendus mais inertes.
    /// </summary>
    public DashboardWindow(DatabaseService db, AppSettings settings, TrackerActions? actions = null)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _db = db;
        _settings = settings;
        _actions = actions ?? new TrackerActions();

        // Rafraîchit en continu : la tâche en cours change et sa durée grimpe.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => { RefreshActiveTab(onlyIfLive: true); UpdateTrackerBar(); };

        Loaded += (_, _) =>
        {
            LoadDay(); LoadWeek(); AdjustTaskColumn(); UpdateTrackerBar();
            _refreshTimer.Start();
        };
        Activated += (_, _) => { RefreshActiveTab(onlyIfLive: false); UpdateTrackerBar(); };
        Closed += (_, _) => _refreshTimer.Stop();
        SizeChanged += (_, _) => AdjustTaskColumn();
    }

    // ------------------------------------------------------------ Barre de suivi

    /// <summary>Reflète l'état du suivi (libellé, pause/reprise, boutons applicables).</summary>
    private void UpdateTrackerBar()
    {
        var (label, isPaused, hasTask) = _actions.State();

        CurrentTaskText.Text = label;
        PauseButton.Content = isPaused ? "▶ Reprendre" : "⏸ Pause";
        PauseButton.IsEnabled = hasTask;
        StopButton.IsEnabled = hasTask;
        QuickEditButton.IsEnabled = hasTask;
    }

    /// <summary>
    /// Exécute une commande de suivi puis resynchronise l'affichage : l'action a pu créer,
    /// clôturer ou renommer une entrée de la journée affichée.
    /// </summary>
    private void RunAction(Action action)
    {
        // Les commandes ouvrent des fenêtres modales : le rafraîchissement périodique
        // recharcherait la liste sous la fenêtre de dialogue.
        _refreshTimer.Stop();
        try { action(); }
        catch (Exception ex)
        {
            Logger.Error("Dashboard.RunAction", ex);
            MessageBox.Show(this, ex.Message, "TimeTracker",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refreshTimer.Start();
            LoadDay();
            LoadWeek();
            UpdateTrackerBar();
        }
    }

    private void ChangeTask_Click(object sender, RoutedEventArgs e) => RunAction(_actions.ChangeTask);

    private void QuickEdit_Click(object sender, RoutedEventArgs e) => RunAction(_actions.QuickEdit);

    private void TogglePause_Click(object sender, RoutedEventArgs e) => RunAction(_actions.TogglePause);

    private void Stop_Click(object sender, RoutedEventArgs e) => RunAction(_actions.StopTracking);

    private void ManageTasks_Click(object sender, RoutedEventArgs e) => RunAction(_actions.ManageTasks);

    private void Settings_Click(object sender, RoutedEventArgs e) => RunAction(_actions.OpenSettings);

    /// <summary>Sélectionne un onglet par index. Utilisé par <c>--uitest</c> pour forcer son rendu.</summary>
    internal void SelectTab(int index) => Tabs.SelectedIndex = index;

    private static DateTime StartOfWeek(DateTime d)
    {
        int delta = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return d.Date.AddDays(-delta);
    }

    private bool WeekTabActive => Tabs.SelectedIndex == 1;

    /// <summary>Période couverte par l'onglet actif (bornes incluses), utilisée par l'export.</summary>
    private (DateTime From, DateTime ToInclusive) ActiveRange =>
        WeekTabActive ? (_weekStart, _weekStart.AddDays(6)) : (_day, _day);

    /// <summary>
    /// Recharge l'onglet visible. Avec <paramref name="onlyIfLive"/>, ne fait rien si la période
    /// affichée ne contient pas aujourd'hui (une période passée ne bouge plus).
    /// </summary>
    private void RefreshActiveTab(bool onlyIfLive)
    {
        var (from, to) = ActiveRange;
        var today = DateTime.Now.Date;
        if (onlyIfLive && (today < from || today > to)) return;

        if (WeekTabActive) LoadWeek();
        else LoadDay();
    }

    /// <summary>Étire la colonne « Tâche » pour occuper la largeur restante de la liste.</summary>
    private void AdjustTaskColumn()
    {
        // largeur utile = largeur de la liste - (marqueur 26 + Début 60 + Fin 60 + Durée 70)
        //                 - marge ascenseur/bordure
        double available = EntriesList.ActualWidth - (26 + 60 + 60 + 70) - 28;
        if (available > 80) TaskColumn.Width = available;
    }

    /// <summary>
    /// Marqueur d'une ligne <b>agrégée</b> (total par tâche, tableau croisé de la semaine) : posé
    /// seulement si <b>toutes</b> les entrées de la tâche sur la période viennent de la détection.
    /// Une tâche qui mélange réunion et travail normal reste sans marque — mieux vaut pas de
    /// signal qu'un signal faux, la ligne du jour restant là pour le détail.
    /// </summary>
    private static string AggregateMarker(IEnumerable<TimeEntry> entries, string taskName)
    {
        var own = entries.Where(e => e.TaskName == taskName).ToList();
        return own.Count > 0 && own.All(e => e.IsMeeting) ? MeetingMarker + " " : "";
    }

    // ------------------------------------------------------------ Onglet Jour

    private void LoadDay()
    {
        var isToday = _day == DateTime.Now.Date;
        DayHeaderText.Text = isToday
            ? $"Aujourd'hui — {AppCulture.LongDate(_day)}"
            : AppCulture.LongDate(_day);

        var entries = _db.GetEntriesForDay(_day);

        // Le rafraîchissement automatique ne doit pas faire perdre la ligne sélectionnée.
        long? selectedId = (EntriesList.SelectedItem as EntryRow)?.Id;

        EntriesList.ItemsSource = entries.Select(e => new EntryRow
        {
            Id = e.Id,
            Start = e.StartedAt.ToString("HH:mm"),
            End = e.EndedAt?.ToString("HH:mm") ?? "en cours",
            Name = e.TaskName,
            Duration = ExportService.FormatHm(e.Elapsed),
            IsOpen = e.EndedAt is null,
            Marker = e.IsMeeting ? MeetingMarker : "",
            // Le titre relevé est déjà dans le nom de la tâche quand la détection a pu la nommer.
            // Il ne reste dans le commentaire que si l'utilisateur avait nommé l'entrée lui-même :
            // l'infobulle est alors le seul endroit où il peut le relire, sans coûter de colonne.
            MarkerTip = e.IsMeeting
                ? string.IsNullOrWhiteSpace(e.Notes)
                    ? "Réunion détectée automatiquement"
                    : $"Réunion détectée automatiquement — titre relevé : {e.Notes}"
                : null
        }).ToList();

        if (selectedId is long id)
        {
            EntriesList.SelectedItem = EntriesList.Items
                .OfType<EntryRow>()
                .FirstOrDefault(r => r.Id == id);
        }

        TotalsList.ItemsSource = ExportService.TotalsByTask(entries)
            .Select(t => new
            {
                Name = AggregateMarker(entries, t.TaskName) + t.TaskName,
                Duration = ExportService.FormatHm(t.Total)
            })
            .ToList();

        var grand = TimeSpan.FromSeconds(entries.Sum(e => e.Elapsed.TotalSeconds));
        GrandTotalText.Text = $"Total journée : {ExportService.FormatHm(grand)}";
        if (_settings.DailyHoursGoal > 0)
        {
            var dayGoal = TimeSpan.FromHours(_settings.DailyHoursGoal);
            GrandTotalText.Text += $"  ·  objectif {ExportService.FormatHm(dayGoal)}" +
                                   (grand >= dayGoal ? " ✓" : $" (reste {ExportService.FormatHm(dayGoal - grand)})");
        }
        UpdateStatus(entries.Count);
    }

    private void PrevDay_Click(object sender, RoutedEventArgs e) { _day = _day.AddDays(-1); LoadDay(); }

    private void NextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_day >= DateTime.Now.Date) return;   // pas de navigation dans le futur
        _day = _day.AddDays(1);
        LoadDay();
    }

    private void Today_Click(object sender, RoutedEventArgs e) { _day = DateTime.Now.Date; LoadDay(); }

    // --------------------------------------------------------- Onglet Semaine

    private void LoadWeek()
    {
        var end = _weekStart.AddDays(6);
        WeekHeaderText.Text = $"Semaine du {AppCulture.DayMonth(_weekStart)} au {AppCulture.MediumDate(end)}";

        var entries = _db.GetEntriesForRange(_weekStart, _weekStart.AddDays(7));
        var days = ExportService.EachDay(_weekStart, end).ToList();

        // Tableau croisé tâche × jour construit en DataTable : le DataGrid génère
        // automatiquement une colonne par jour, sans XAML dynamique.
        var table = new DataTable();
        _weekHeaders.Clear();

        table.Columns.Add("Tache", typeof(string));
        _weekHeaders["Tache"] = "Tâche";
        for (int i = 0; i < days.Count; i++)
        {
            table.Columns.Add($"D{i}", typeof(string));
            _weekHeaders[$"D{i}"] = AppCulture.ShortDay(days[i]);
        }
        table.Columns.Add("Total", typeof(string));
        _weekHeaders["Total"] = "Total";

        foreach (var task in ExportService.TotalsByTask(entries))
        {
            var row = table.NewRow();
            row[0] = AggregateMarker(entries, task.TaskName) + task.TaskName;
            for (int i = 0; i < days.Count; i++)
            {
                double seconds = entries
                    .Where(e => e.TaskName == task.TaskName && e.StartedAt.Date == days[i])
                    .Sum(e => e.Elapsed.TotalSeconds);
                row[i + 1] = seconds > 0 ? ExportService.FormatHm(TimeSpan.FromSeconds(seconds)) : "";
            }
            row[days.Count + 1] = ExportService.FormatHm(task.Total);
            table.Rows.Add(row);
        }

        if (entries.Count > 0)
        {
            var totalRow = table.NewRow();
            totalRow[0] = "TOTAL";
            for (int i = 0; i < days.Count; i++)
            {
                double seconds = entries.Where(e => e.StartedAt.Date == days[i]).Sum(e => e.Elapsed.TotalSeconds);
                totalRow[i + 1] = seconds > 0 ? ExportService.FormatHm(TimeSpan.FromSeconds(seconds)) : "";
            }
            totalRow[days.Count + 1] = ExportService.FormatHm(
                TimeSpan.FromSeconds(entries.Sum(e => e.Elapsed.TotalSeconds)));
            table.Rows.Add(totalRow);
        }

        var grand = TimeSpan.FromSeconds(entries.Sum(e => e.Elapsed.TotalSeconds));
        WeekTotalText.Text = $"Total semaine : {ExportService.FormatHm(grand)}";
        UpdateGoal(grand);
        UpdateStatus(entries.Count);

        // La semaine affichée fait partie de l'empreinte : deux semaines vides ne doivent pas
        // être confondues, sinon les en-têtes de colonnes resteraient sur les dates précédentes.
        var signature = _weekStart.ToString("O") + "|" + string.Join("|",
            table.Rows.Cast<DataRow>().Select(r => string.Join(";", r.ItemArray)));
        if (signature == _weekSignature) return;

        _weekSignature = signature;
        WeekGrid.ItemsSource = table.DefaultView;
    }

    /// <summary>Avancement de la semaine par rapport à cinq fois l'objectif journalier (0 = masqué).</summary>
    private void UpdateGoal(TimeSpan weekTotal)
    {
        double goalHours = _settings.WeeklyHoursGoal;
        if (goalHours <= 0) { GoalPanel.Visibility = Visibility.Collapsed; return; }

        var goal = TimeSpan.FromHours(goalHours);
        double percent = Math.Min(100, weekTotal.TotalSeconds / goal.TotalSeconds * 100);
        GoalBar.Value = percent;
        GoalText.Text = weekTotal >= goal
            ? $"Objectif {ExportService.FormatHm(goal)} atteint ✓ (+{ExportService.FormatHm(weekTotal - goal)})"
            : $"{percent:0} % de l'objectif {ExportService.FormatHm(goal)} — reste {ExportService.FormatHm(goal - weekTotal)}";
        GoalPanel.Visibility = Visibility.Visible;
    }

    private void WeekGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        e.Column.Header = _weekHeaders.TryGetValue(e.PropertyName, out var header) ? header : e.PropertyName;

        if (e.Column is DataGridTextColumn col && e.PropertyName != "Tache")
        {
            col.CellStyle = new Style(typeof(DataGridCell));
            // Qualifié : dans une Window, « HorizontalAlignment » désigne la propriété d'instance.
            col.CellStyle.Setters.Add(
                new Setter(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Right));
            col.MinWidth = 74;
        }
        else
        {
            e.Column.MinWidth = 160;
            e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }
    }

    private void PrevWeek_Click(object sender, RoutedEventArgs e) { _weekStart = _weekStart.AddDays(-7); LoadWeek(); }

    private void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        if (_weekStart >= StartOfWeek(DateTime.Now)) return;
        _weekStart = _weekStart.AddDays(7);
        LoadWeek();
    }

    private void ThisWeek_Click(object sender, RoutedEventArgs e)
    {
        _weekStart = StartOfWeek(DateTime.Now);
        LoadWeek();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Tabs)) return;
        RefreshActiveTab(onlyIfLive: false);
    }

    // ----------------------------------------------------- Édition / suppression

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedEntry();

    private void EditEntry_Click(object sender, RoutedEventArgs e) => EditSelectedEntry();

    private void EditSelectedEntry()
    {
        if (EntriesList.SelectedItem is not EntryRow row) return;
        if (RefuseIfOpen(row)) return;

        var entry = _db.GetEntriesForDay(_day).FirstOrDefault(x => x.Id == row.Id);
        if (entry is null) { LoadDay(); return; }

        _refreshTimer.Stop();
        try
        {
            if (EntryEditWindow.Edit(this, _db, entry)) { LoadDay(); LoadWeek(); }
        }
        finally { _refreshTimer.Start(); }
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not EntryRow row) return;
        if (RefuseIfOpen(row)) return;

        var answer = MessageBox.Show(this,
            $"Supprimer définitivement cette entrée ?\n\n{row.Start} – {row.End}   {row.Name}   ({row.Duration})",
            "Supprimer une entrée", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _db.DeleteEntry(row.Id);
        Logger.Info($"Entrée {row.Id} supprimée depuis le tableau de bord.");
        LoadDay();
        LoadWeek();
    }

    /// <summary>
    /// L'entrée en cours est pilotée par le TimerService : la modifier ici la désynchroniserait.
    /// On renvoie l'utilisateur vers la correction à chaud.
    /// </summary>
    private bool RefuseIfOpen(EntryRow row)
    {
        if (!row.IsOpen) return false;
        MessageBox.Show(this,
            $"« {row.Name} » est la tâche en cours.\n\n" +
            $"Utilise la correction à chaud ({_settings.HotkeyEdit}) pour la renommer, " +
            "ajuster son heure de début ou l'annuler.",
            "Tâche en cours", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    // ------------------------------------------------------------------ Export

    private void ExportCsv_Click(object sender, RoutedEventArgs e) =>
        RunExport("csv", "Fichier CSV (*.csv)|*.csv");

    private void ExportXlsx_Click(object sender, RoutedEventArgs e) =>
        RunExport("xlsx", "Classeur Excel (*.xlsx)|*.xlsx");

    /// <summary>Période libre (un mois, des congés…) : le jour ou la semaine affichés ne suffisent pas toujours.</summary>
    private void ExportRange_Click(object sender, RoutedEventArgs e)
    {
        var (from, to) = ActiveRange;
        _refreshTimer.Stop();
        try
        {
            var choice = ExportRangeWindow.Ask(this, from, to);
            if (choice is null) return;
            RunExport(choice.Format,
                      choice.Format == "csv" ? "Fichier CSV (*.csv)|*.csv" : "Classeur Excel (*.xlsx)|*.xlsx",
                      (choice.From, choice.ToInclusive));
        }
        finally { _refreshTimer.Start(); }
    }

    private void RunExport(string extension, string filter, (DateTime From, DateTime ToInclusive)? range = null)
    {
        var (from, to) = range ?? ActiveRange;
        var entries = _db.GetEntriesForRange(from, to.AddDays(1));
        if (entries.Count == 0)
        {
            MessageBox.Show(this, "Aucune entrée à exporter sur cette période.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = Directory.Exists(_settings.ExportFolder)
            ? _settings.ExportFolder
            : AppSettings.DefaultExportFolder();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter le relevé de temps",
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            InitialDirectory = folder,
            FileName = $"{ExportService.SuggestedFileName(from, to)}.{extension}"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (extension == "csv") ExportService.ExportCsv(entries, dialog.FileName);
            else ExportService.ExportXlsx(entries, dialog.FileName, from, to);

            // Mémorise le dossier choisi comme défaut pour les prochains exports.
            var chosen = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(chosen) && chosen != _settings.ExportFolder)
            {
                _settings.ExportFolder = chosen;
                _db.SaveSettings(_settings);
            }

            Logger.Info($"Export {extension} : {entries.Count} entrées → {dialog.FileName}");
            StatusText.Text = $"Exporté : {Path.GetFileName(dialog.FileName)} ({entries.Count} entrées)";
        }
        catch (Exception ex)
        {
            Logger.Error("Export", ex);
            MessageBox.Show(this,
                $"L'export a échoué.\n\n{ex.Message}\n\n" +
                "Si le fichier est déjà ouvert dans Excel, ferme-le puis réessaie.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateStatus(int entryCount)
    {
        var (from, to) = ActiveRange;
        var period = from == to ? from.ToString("dd/MM/yyyy") : $"{from:dd/MM} – {to:dd/MM/yyyy}";
        StatusText.Text = $"{entryCount} entrée(s) — {period}";
    }
}
