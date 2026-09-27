using System.Windows;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;

namespace TimeTracker.UI;

/// <summary>
/// Édition d'une entrée déjà clôturée (tâche, date, heures de début et de fin).
/// L'entrée en cours n'est jamais éditée ici : elle appartient au TimerService
/// et se corrige via la fenêtre de correction à chaud.
/// </summary>
public partial class EntryEditWindow : Window
{
    private readonly DatabaseService _db;
    private readonly TimeEntry _entry;
    private bool _saved;

    internal EntryEditWindow(DatabaseService db, TimeEntry entry)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _db = db;
        _entry = entry;

        TaskBox.ItemsSource = db.GetAllTasks().Select(t => t.Name).ToList();
        TaskBox.Text = entry.TaskName;
        DateBox.SelectedDate = entry.StartedAt.Date;
        StartBox.Text = entry.StartedAt.ToString("HH:mm");
        EndBox.Text = (entry.EndedAt ?? entry.StartedAt).ToString("HH:mm");

        StartBox.TextChanged += (_, _) => UpdateDuration();
        EndBox.TextChanged += (_, _) => UpdateDuration();
        Loaded += (_, _) => { UpdateDuration(); TaskBox.Focus(); };
    }

    /// <summary>Ouvre l'éditeur en modal. Renvoie true si l'entrée a été modifiée.</summary>
    public static bool Edit(Window owner, DatabaseService db, TimeEntry entry)
    {
        var win = new EntryEditWindow(db, entry) { Owner = owner };
        win.ShowDialog();
        return win._saved;
    }

    /// <summary>Recompose début/fin depuis les champs. Fin ≤ début ⇒ la fin passe au lendemain.</summary>
    private bool TryBuildRange(out DateTime start, out DateTime end, out string error)
    {
        start = default; end = default; error = "";

        if (DateBox.SelectedDate is not DateTime date)
        {
            error = "Choisis une date.";
            return false;
        }
        if (!TimeInput.TryParse(StartBox.Text, out var startTime))
        {
            error = "Heure de début invalide (format attendu : hh:mm).";
            return false;
        }
        if (!TimeInput.TryParse(EndBox.Text, out var endTime))
        {
            error = "Heure de fin invalide (format attendu : hh:mm).";
            return false;
        }

        start = date.Date + startTime;
        end = date.Date + endTime;
        if (end <= start) end = end.AddDays(1);   // entrée à cheval sur minuit

        if (end - start > TimeSpan.FromHours(23))
        {
            error = "Durée supérieure à 23 h : vérifie les heures saisies.";
            return false;
        }
        return true;
    }

    private void UpdateDuration()
    {
        if (TryBuildRange(out var start, out var end, out _))
        {
            var span = end - start;
            DurationText.Text = $"Durée : {ExportService.FormatHm(span)}"
                                + (end.Date != start.Date ? "  (fin le lendemain)" : "");
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DurationText.Text = "";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildRange(out var start, out var end, out var error))
        {
            ShowError(error);
            return;
        }

        var name = TaskBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Le nom de la tâche ne peut pas être vide.");
            return;
        }

        try
        {
            var task = _db.GetOrCreateTask(name);
            if (task.Id != _entry.TaskId) _db.UpdateEntryTask(_entry.Id, task.Id);
            _db.UpdateEntryTimes(_entry.Id, start, end);
            Logger.Info($"Entrée {_entry.Id} modifiée : {name} {start:yyyy-MM-dd HH:mm}→{end:HH:mm}");
            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error("EntryEditWindow.Save", ex);
            ShowError($"Enregistrement impossible : {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

