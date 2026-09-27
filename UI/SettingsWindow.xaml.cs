using System.IO;
using System.Windows;
using System.Windows.Input;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
using TimeTracker.Core.Services.Ai;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextBox = System.Windows.Controls.TextBox;

namespace TimeTracker.UI;

/// <summary>
/// Fenêtre de réglages. Les valeurs sont écrites dans l'instance partagée d'<see cref="AppSettings"/>
/// (jamais remplacée) puis persistées ; l'appelant applique ensuite ce qui doit l'être à chaud.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] IntervalPresets = { 10, 15, 20, 30, 45, 60, 90, 120 };

    private readonly AppSettings _settings;
    private readonly DatabaseService _db;
    private readonly Func<Hotkey, Hotkey, Hotkey, string?> _applyHotkeys;
    private bool _saved;

    internal SettingsWindow(AppSettings settings, DatabaseService db,
        Func<Hotkey, Hotkey, Hotkey, string?> applyHotkeys)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        WindowFit.LimitToWorkArea(this);
        _settings = settings;
        _db = db;
        _applyHotkeys = applyHotkeys;

        IntervalBox.ItemsSource = IntervalPresets;
        IntervalBox.Text = settings.ReminderIntervalMinutes.ToString();
        SoundCheck.IsChecked = settings.ReminderSound;
        AskOnStartupCheck.IsChecked = settings.AskTaskOnStartup;
        AbsenceBox.Text = settings.AbsenceCutoffMinutes.ToString();
        GoalUnitBox.SelectedIndex = settings.GoalPerWeek ? 1 : 0;
        GoalBox.Text = FormatHours(settings.GoalPerWeek ? settings.WeeklyHoursGoal : settings.DailyHoursGoal);
        ActivityCheck.IsChecked = settings.ActivitySuggestions;
        LearningCheck.IsChecked = settings.ActivityLearning;

        AiCheck.IsChecked = settings.AiEnabled;
        AiProviderBox.ItemsSource = AiProviderFactory.Providers.Select(p => p.Label).ToList();
        AiProviderBox.SelectedIndex = Math.Max(0,
            AiProviderFactory.Providers.ToList().FindIndex(p => p.Id == settings.AiProvider));
        AiKeyBox.Password = settings.AiApiKey;
        AiModelBox.Text = settings.AiModel.Length > 0 ? settings.AiModel : AiProviderFactory.DefaultModel(SelectedProvider);
        AiUrlBox.Text = settings.AiBaseUrl.Length > 0 ? settings.AiBaseUrl : AiProviderFactory.DefaultBaseUrl(SelectedProvider);
        AiUrlBox.IsEnabled = SelectedProvider == AiProviderFactory.OpenAi;
        StartupCheck.IsChecked = settings.StartWithWindows;
        ExportFolderBox.Text = settings.ExportFolder;
        HotkeyTaskBox.Text = settings.HotkeyTask;
        HotkeyEditBox.Text = settings.HotkeyEdit;
        HotkeyPauseBox.Text = settings.HotkeyPause;
        MeetingCheck.IsChecked = settings.MeetingDetection;
        OutlookCheck.IsChecked = settings.OutlookCalendar;
        OutlookAskCheck.IsChecked = settings.OutlookAsk;
        MeetingTaskBox.Text = settings.MeetingTaskName;

        Loaded += (_, _) => Activate();
    }

    /// <summary>
    /// Ouvre les réglages en modal. <paramref name="applyHotkeys"/> tente d'enregistrer les
    /// raccourcis et renvoie un message d'erreur si l'un d'eux est déjà pris (null si tout va bien).
    /// Renvoie true si les réglages ont été enregistrés.
    /// </summary>
    public static bool Show(AppSettings settings, DatabaseService db,
        Func<Hotkey, Hotkey, Hotkey, string?> applyHotkeys)
    {
        var win = new SettingsWindow(settings, db, applyHotkeys);
        win.ShowDialog();
        return win._saved;
    }

    // ------------------------------------------------------------ Objectif

    private static string FormatHours(double hours) =>
        hours.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    private static bool TryParseHours(string text, out double hours) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out hours);

    private bool GoalPerWeek => GoalUnitBox.SelectedIndex == 1;

    /// <summary>Changer d'unité convertit la valeur affichée (× 5 ou ÷ 5) : le chiffre suit le sens.</summary>
    private void GoalUnit_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !TryParseHours(GoalBox.Text, out var value)) return;
        GoalBox.Text = FormatHours(GoalPerWeek ? value * 5 : value / 5);
    }

    // ------------------------------------------------------------ Assistant IA

    private string SelectedProvider =>
        AiProviderBox.SelectedIndex >= 0 && AiProviderBox.SelectedIndex < AiProviderFactory.Providers.Count
            ? AiProviderFactory.Providers[AiProviderBox.SelectedIndex].Id
            : AiProviderFactory.Gemini;

    private string _previousProvider = "";

    /// <summary>Changer de fournisseur remplace le modèle et l'adresse s'ils étaient ceux par défaut.</summary>
    private void AiProvider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) { _previousProvider = SelectedProvider; return; }
        var provider = SelectedProvider;
        if (AiModelBox.Text.Trim().Length == 0 || AiModelBox.Text.Trim() == AiProviderFactory.DefaultModel(_previousProvider))
            AiModelBox.Text = AiProviderFactory.DefaultModel(provider);
        if (AiUrlBox.Text.Trim().Length == 0 || AiUrlBox.Text.Trim() == AiProviderFactory.DefaultBaseUrl(_previousProvider))
            AiUrlBox.Text = AiProviderFactory.DefaultBaseUrl(provider);
        AiUrlBox.IsEnabled = provider == AiProviderFactory.OpenAi;
        _previousProvider = provider;
        AiTestText.Text = "";
    }

    /// <summary>Un aller-retour minimal avec le fournisseur, pour valider clé et modèle avant d'enregistrer.</summary>
    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        AiTestButton.IsEnabled = false;
        AiTestText.Text = "Interrogation en cours…";
        try
        {
            var provider = AiProviderFactory.Create(SelectedProvider, AiKeyBox.Password, AiModelBox.Text, AiUrlBox.Text);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var answer = await provider.CompleteAsync("Réponds uniquement par le mot OK.", cts.Token);
            AiTestText.Text = $"✓ {provider.Name} répond : « {answer.Trim().Replace('\n', ' ')[..Math.Min(40, answer.Trim().Length)]} »";
            Logger.Info($"IA : test de connexion réussi ({provider.Name}).");
        }
        catch (Exception ex)
        {
            AiTestText.Text = $"✗ {ex.Message}";
            Logger.Info($"IA : test de connexion échoué — {ex.Message}");
        }
        finally
        {
            AiTestButton.IsEnabled = true;
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Effacer tout ce que TimeTracker a appris des fenêtres (mots de titres comptés par tâche) ?\n\n" +
            "Les suggestions repartiront des seuls noms de tâches.",
            "Oublier", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        int removed = _db.ClearTaskHints();
        Logger.Info($"Apprentissage des fenêtres effacé ({removed} ligne(s)).");
        MessageBox.Show(this, $"{removed} association(s) mot ↔ tâche effacée(s).", "Oublier",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------- Capture raccourci

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        // Alt masque la vraie touche derrière Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;   // en attente de la touche principale
            return;
        }

        // Sans modificateur, on laisse Tab / Échap faire leur travail habituel.
        if (Keyboard.Modifiers == ModifierKeys.None) return;

        e.Handled = true;
        var hotkey = new Hotkey(Keyboard.Modifiers, key);
        if (!hotkey.IsValid) return;

        box.Text = hotkey.ToString();
        HideError();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Dossier d'export par défaut",
            InitialDirectory = Directory.Exists(ExportFolderBox.Text)
                ? ExportFolderBox.Text
                : AppSettings.DefaultExportFolder()
        };
        if (dialog.ShowDialog(this) == true) ExportFolderBox.Text = dialog.FolderName;
    }

    // ---------------------------------------------------------------- Validation

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text.Trim(), out var interval) || interval < 1 || interval > 480)
        {
            ShowError("L'intervalle de rappel doit être un nombre de minutes entre 1 et 480.");
            return;
        }
        if (!int.TryParse(AbsenceBox.Text.Trim(), out var absence) || absence < 0 || absence > 1440)
        {
            ShowError("La durée d'absence doit être un nombre de minutes entre 0 et 1440.");
            return;
        }
        if (!Hotkey.TryParse(HotkeyTaskBox.Text, out var hkTask))
        {
            ShowError("Raccourci « Changer de tâche » invalide.");
            return;
        }
        if (!Hotkey.TryParse(HotkeyEditBox.Text, out var hkEdit))
        {
            ShowError("Raccourci « Correction à chaud » invalide.");
            return;
        }
        if (!Hotkey.TryParse(HotkeyPauseBox.Text, out var hkPause))
        {
            ShowError("Raccourci « Pause / reprise » invalide.");
            return;
        }
        if (hkTask == hkEdit || hkTask == hkPause || hkEdit == hkPause)
        {
            ShowError("Les trois raccourcis doivent être différents.");
            return;
        }
        // Virgule ou point : l'utilisateur tape « 37,5 » sur un clavier français.
        double goalMax = GoalPerWeek ? 168 : 24;
        if (!TryParseHours(GoalBox.Text, out var goalEntered) || goalEntered < 0 || goalEntered > goalMax)
        {
            ShowError($"L'objectif doit être un nombre d'heures entre 0 et {goalMax}.");
            return;
        }
        double goal = GoalPerWeek ? goalEntered / 5 : goalEntered;   // canonique : par jour

        var folder = ExportFolderBox.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            ShowError("Le dossier d'export n'existe pas.");
            return;
        }
        if (folder.Length == 0) folder = AppSettings.DefaultExportFolder();

        var meetingTask = MeetingTaskBox.Text.Trim();
        if (MeetingCheck.IsChecked == true && meetingTask.Length == 0)
        {
            ShowError("Donne un nom à la tâche des réunions (par exemple « Réunion »).");
            return;
        }

        // Les raccourcis d'abord : si Windows refuse la combinaison, rien n'est enregistré.
        var hotkeyError = _applyHotkeys(hkTask, hkEdit, hkPause);
        if (hotkeyError != null)
        {
            ShowError(hotkeyError);
            return;
        }

        try
        {
            _settings.ReminderIntervalMinutes = interval;
            _settings.ReminderSound = SoundCheck.IsChecked == true;
            _settings.AskTaskOnStartup = AskOnStartupCheck.IsChecked == true;
            _settings.AbsenceCutoffMinutes = absence;
            _settings.StartWithWindows = StartupCheck.IsChecked == true;
            _settings.ExportFolder = folder;
            _settings.HotkeyTask = hkTask.ToString();
            _settings.HotkeyEdit = hkEdit.ToString();
            _settings.HotkeyPause = hkPause.ToString();
            _settings.DailyHoursGoal = goal;
            _settings.GoalPerWeek = GoalPerWeek;
            _settings.ActivitySuggestions = ActivityCheck.IsChecked == true;
            _settings.ActivityLearning = LearningCheck.IsChecked == true;
            _settings.AiEnabled = AiCheck.IsChecked == true;
            _settings.AiProvider = SelectedProvider;
            _settings.AiApiKey = AiKeyBox.Password.Trim();
            _settings.AiModel = AiModelBox.Text.Trim();
            _settings.AiBaseUrl = AiUrlBox.Text.Trim();
            _settings.MeetingDetection = MeetingCheck.IsChecked == true;
            _settings.OutlookCalendar = OutlookCheck.IsChecked == true;
            _settings.OutlookAsk = OutlookAskCheck.IsChecked == true;
            // Un champ vidé alors que la détection est coupée ne doit pas effacer le nom retenu.
            if (meetingTask.Length > 0) _settings.MeetingTaskName = meetingTask;

            _db.SaveSettings(_settings);
            StartupService.SetEnabled(_settings.StartWithWindows);

            Logger.Info($"Réglages enregistrés : rappel={interval}min, absence={absence}min, son={_settings.ReminderSound}, " +
                        $"démarrageWindows={_settings.StartWithWindows}, " +
                        $"raccourcis={_settings.HotkeyTask}/{_settings.HotkeyEdit}/{_settings.HotkeyPause}, " +
                        $"objectif={goalEntered}h/{(GoalPerWeek ? "semaine" : "jour")}, suggestions={_settings.ActivitySuggestions}/apprentissage={_settings.ActivityLearning}, " +
                        $"IA={_settings.AiEnabled}/{_settings.AiProvider}/{_settings.AiModel}, " +
                        $"réunions={_settings.MeetingDetection}/« {_settings.MeetingTaskName} », " +
                        $"agenda={_settings.OutlookCalendar}/question={_settings.OutlookAsk}");
            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error("SettingsWindow.Save", ex);
            ShowError($"Enregistrement impossible : {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

