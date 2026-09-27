using System.Windows;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace TimeTracker.UI;

/// <summary>
/// Correction à chaud (raccourci global, Ctrl+Alt+E par défaut) : corriger la tâche en cours,
/// antidater son heure de début, l'annuler, ou supprimer la dernière entrée terminée.
/// </summary>
public partial class QuickEditWindow : Window
{
    private readonly DatabaseService _db;
    private readonly TimerService _timer;
    private TimeEntry? _lastClosed;

    internal QuickEditWindow(DatabaseService db, TimerService timer)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _db = db;
        _timer = timer;

        TaskBox.ItemsSource = db.GetAllTasks().Select(t => t.Name).ToList();
        LoadCurrent();
        LoadLastClosed();

        StartBox.TextChanged += (_, _) => UpdateDuration();
        Loaded += (_, _) =>
        {
            // Ouverte depuis un raccourci global : une autre appli a le focus.
            Activate();
            if (_timer.CurrentTask != null) StartBox.Focus();
        };
    }

    public static void Show(DatabaseService db, TimerService timer)
    {
        var win = new QuickEditWindow(db, timer);
        win.ShowDialog();
    }

    private void LoadCurrent()
    {
        var task = _timer.CurrentTask;
        if (task is null)
        {
            SubtitleText.Text = "Aucune tâche en cours.";
            CurrentPanel.Visibility = Visibility.Collapsed;
            SaveButton.Visibility = Visibility.Collapsed;
            return;
        }

        SubtitleText.Text = _timer.IsPaused
            ? "Suivi en pause — les corrections portent sur la tâche sélectionnée."
            : "Corrige la tâche en cours sans casser le chronomètre.";
        TaskBox.Text = task.Name;
        StartBox.Text = _timer.CurrentStart.ToString("HH:mm");
        CancelEntryButton.IsEnabled = _timer.CurrentEntryId.HasValue;
        UpdateDuration();
    }

    private void LoadLastClosed()
    {
        var last = _db.GetLastEntry();
        // La plus récente peut être l'entrée en cours : on remonte alors d'un cran.
        if (last is { EndedAt: null }) last = _db.GetEntryBefore(last.Id);
        _lastClosed = last is { EndedAt: not null } ? last : null;

        if (_lastClosed is null)
        {
            LastEntryText.Text = "Aucune entrée terminée pour l'instant.";
            DeleteLastButton.IsEnabled = false;
            return;
        }

        LastEntryText.Text =
            $"{AppCulture.ShortDay(_lastClosed.StartedAt)}  {_lastClosed.StartedAt:HH:mm} – " +
            $"{_lastClosed.EndedAt:HH:mm}   « {_lastClosed.TaskName} »   " +
            $"({ExportService.FormatHm(_lastClosed.Elapsed)})";
        DeleteLastButton.IsEnabled = true;
    }

    /// <summary>Heure de début saisie, ramenée sur la bonne date (jamais dans le futur).</summary>
    private bool TryReadStart(out DateTime start, out string error)
    {
        start = default;
        error = "";

        if (!TimeInput.TryParse(StartBox.Text, out var time))
        {
            error = "Heure de début invalide (format attendu : hh:mm).";
            return false;
        }

        // On vise la dernière occurrence de cette heure : aujourd'hui si elle est déjà passée,
        // hier sinon (cas d'une correction faite après minuit).
        var now = DateTime.Now;
        start = TimeInput.CombineNotAfter(now.Date, time, now);

        if (now - start > TimeSpan.FromHours(23))
        {
            error = "Plus de 23 h de suivi continu : vérifie l'heure saisie.";
            return false;
        }
        return true;
    }

    private void UpdateDuration()
    {
        if (_timer.CurrentTask is null) return;

        if (TryReadStart(out var start, out _))
        {
            DurationText.Text = $"Durée recalculée : {ExportService.FormatHm(DateTime.Now - start)}"
                                + (start.Date != DateTime.Now.Date ? $"  (démarrée le {start:dd/MM})" : "");
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DurationText.Text = "";
        }
    }

    private void Minus5_Click(object sender, RoutedEventArgs e) => ShiftStart(-5);

    private void Minus15_Click(object sender, RoutedEventArgs e) => ShiftStart(-15);

    private void ShiftStart(int minutes)
    {
        if (!TryReadStart(out var start, out var error)) { ShowError(error); return; }
        StartBox.Text = start.AddMinutes(minutes).ToString("HH:mm");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.CurrentTask is null) { Close(); return; }

        var name = TaskBox.Text.Trim();
        if (name.Length == 0) { ShowError("Le nom de la tâche ne peut pas être vide."); return; }
        if (!TryReadStart(out var start, out var error)) { ShowError(error); return; }

        try
        {
            if (!string.Equals(name, _timer.CurrentTask.Name, StringComparison.OrdinalIgnoreCase))
            {
                var task = _db.GetOrCreateTask(name);
                _timer.ReassignCurrentTask(task);
                Logger.Info($"Correction : tâche en cours → « {name} ».");
            }

            if (Math.Abs((start - _timer.CurrentStart).TotalSeconds) >= 30)
            {
                _timer.AdjustCurrentStart(start);
                Logger.Info($"Correction : début de la tâche en cours → {start:HH:mm}.");
            }

            Close();
        }
        catch (Exception ex)
        {
            Logger.Error("QuickEditWindow.Save", ex);
            ShowError($"Enregistrement impossible : {ex.Message}");
        }
    }

    private void CancelEntry_Click(object sender, RoutedEventArgs e)
    {
        var task = _timer.CurrentTask;
        if (task is null) return;

        var answer = MessageBox.Show(this,
            $"Supprimer l'entrée en cours sur « {task.Name} » ({ExportService.FormatHm(_timer.Elapsed)}) " +
            "et arrêter le suivi ?",
            "Annuler l'entrée en cours", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _timer.CancelCurrentEntry();
        Logger.Info($"Correction : entrée en cours annulée (« {task.Name} »).");
        Close();
    }

    private void DeleteLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClosed is null) return;

        var answer = MessageBox.Show(this,
            $"Supprimer définitivement cette entrée ?\n\n{LastEntryText.Text}",
            "Supprimer la dernière entrée", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _db.DeleteEntry(_lastClosed.Id);
        Logger.Info($"Correction : entrée {_lastClosed.Id} supprimée (« {_lastClosed.TaskName} »).");
        LoadLastClosed();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

