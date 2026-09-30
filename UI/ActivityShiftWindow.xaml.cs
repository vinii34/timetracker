using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TimeTracker.Core.Models;
using TimeTracker.Core.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TimeTracker.UI;

public enum ShiftAnswerKind { Existing, New, Stay, Expired }

/// <summary>Réponse à une proposition de changement.</summary>
/// <param name="Origin">
/// D'où vient ce qui a été choisi — « local », « IA », « modifié », « tapé » — ou, pour
/// <see cref="ShiftAnswerKind.Expired"/>, pourquoi la fenêtre s'est fermée. Journalisé.
/// </param>
public sealed record ShiftAnswer(ShiftAnswerKind Kind, TaskItem? Task, string? NewName, bool Backdate, string Origin);

/// <summary>
/// « Tu as changé de tâche ? » — proposée quand les fenêtres ne ressemblent plus à la tâche en
/// cours (<see cref="ActivityShiftDetector"/>). Une tâche existante, ou un nom nouveau
/// pré-rempli et modifiable (tiré du titre sur le poste, puis remplacé par celui de l'IA si elle
/// répond), et l'heure où le changement a commencé : pour la timesheet, c'est le plus précieux.
///
/// ⚠️ <b>Jamais de bascule sans clic</b>, et jamais de modalité : la fenêtre est dans la barre
/// des tâches et dans Alt-Tab, s'ouvre sans prendre le focus (il est peut-être en train de
/// taper ailleurs), et se retire d'elle-même au bout de <see cref="Lifetime"/>. Ne pas répondre
/// est un cas normal — journalisé « expirée », jamais compté comme un refus.
/// </summary>
public partial class ActivityShiftWindow : Window
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    private readonly DispatcherTimer _expiry;
    private readonly DispatcherTimer _hintDelay;
    private readonly Func<string, NameReview.Hint?>? _checkName;
    private TaskItem? _existing;
    private string _existingOrigin = "local";
    private string? _prefilled;
    private string _prefilledOrigin = "local";
    private bool _settingText;
    private bool _userEdited;
    private bool _userChose;
    private string? _aiName;
    private NameReview.Hint? _hint;
    private ShiftAnswer? _answer;

    /// <summary>Instant d'ouverture : au réveil de veille, une proposition trop ancienne est retirée.</summary>
    public DateTime OpenedAt { get; } = DateTime.Now;

    /// <summary>Levé une fois, à la fermeture, avec la réponse (ou <see cref="ShiftAnswerKind.Expired"/>).</summary>
    public event Action<ShiftAnswer>? Answered;

    internal ActivityShiftWindow(string currentTask, DateTime since, TimeSpan evidence,
                                 string dominantTitle, TimeSpan dominantTime,
                                 TaskItem? existing, string? existingReason, string? localName,
                                 Func<string, NameReview.Hint?>? checkName, bool playSound)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        WindowFit.LimitToWorkArea(this);
        _checkName = checkName;
        // Avant tout SetName : remplir le champ déclenche TextChanged, qui s'en sert.
        _hintDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hintDelay.Tick += (_, _) => { _hintDelay.Stop(); UpdateHint(); };
        _expiry = new DispatcherTimer { Interval = Lifetime };
        _expiry.Tick += (_, _) => Expire("expirée");

        DetailText.Text = $"Depuis {since:HH:mm} ({Minutes(evidence)}), tes fenêtres ne ressemblent plus à « {currentTask} ».";
        DominantText.Text = $"Surtout : « {Shorten(dominantTitle, 70)} » ({Minutes(dominantTime)})";
        StayButton.Content = $"Non, je reste sur « {Shorten(currentTask, 26)} »";
        BackdateCheck.Content = $"Compter depuis {since:HH:mm} (sinon : à partir de maintenant)";

        SetExisting(existing, existingReason, "local");
        if (localName != null) SetName(localName, "local");
        (existing != null ? ExistingRadio : NewRadio).IsChecked = true;
        ExistingRadio.Click += (_, _) => _userChose = true;
        NewRadio.Click += (_, _) => _userChose = true;

        Loaded += (_, _) =>
        {
            PositionBottomRight();
            if (playSound) SystemSounds.Asterisk.Play();
            _expiry.Start();
            UpdateHint();
        };
        Closed += (_, _) =>
        {
            _expiry.Stop();
            _hintDelay.Stop();
            Answered?.Invoke(_answer ?? new ShiftAnswer(ShiftAnswerKind.Expired, null, null, false, "fermée"));
        };
    }

    // ------------------------------------------------------------------ IA

    /// <summary>L'IA est interrogée : on le dit, avec ce qui est parti.</summary>
    internal void ShowAiPending(string providerName, string sent)
    {
        AiText.Text = $"✨ {providerName} réfléchit…";
        AiButton.IsEnabled = false;
        AiButton.Visibility = Visibility.Visible;
        SentText.Text = sent;
        SentText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Réponse de l'IA. Elle remplace la proposition locale tant que l'utilisateur n'a rien
    /// touché ; sinon elle reste affichée à côté, à reprendre d'un clic.
    /// </summary>
    internal void ShowAiAnswer(TaskItem? existing, string? newName, string why)
    {
        if (!IsLoaded) return;
        AiButton.IsEnabled = false;
        var reason = why.Length > 0 ? $" — {why}" : "";
        if (existing != null)
        {
            SetExisting(existing, why, "IA");
            if (!_userChose) ExistingRadio.IsChecked = true;
            AiText.Text = $"✨ L'IA reconnaît la tâche « {existing.Name} »{reason}";
        }
        else if (newName != null)
        {
            _aiName = newName;
            if (!_userEdited) SetName(newName, "IA");
            if (!_userChose) NewRadio.IsChecked = true;
            AiButton.IsEnabled = _userEdited;
            AiText.Text = $"✨ Nom proposé par l'IA : « {newName} »{reason}" + (_userEdited ? " (clic pour le reprendre)" : "");
        }
        else
        {
            AiText.Text = "✨ L'IA ne voit pas de tâche évidente — proposition locale ci-dessus.";
        }
    }

    internal void ShowAiFailed()
    {
        if (!IsLoaded) return;
        AiButton.IsEnabled = false;
        AiText.Text = "✨ L'IA n'a pas répondu — proposition locale seulement.";
    }

    /// <summary>Retire la proposition sans réponse (tâche changée ailleurs, réunion, délai…).</summary>
    internal void Expire(string reason)
    {
        if (_answer != null) return;
        _answer = new ShiftAnswer(ShiftAnswerKind.Expired, null, null, false, reason);
        Close();
    }

    // ------------------------------------------------------------ Réponses

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        bool backdate = BackdateCheck.IsChecked == true;
        if (ExistingRadio.IsChecked == true && _existing != null)
        {
            Answer(new ShiftAnswer(ShiftAnswerKind.Existing, _existing, null, backdate, _existingOrigin));
            return;
        }
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0) { NewNameBox.Focus(); return; }
        var origin = _prefilled != null && name == _prefilled.Trim() ? _prefilledOrigin
                     : _prefilled != null ? "modifié" : "tapé";
        Answer(new ShiftAnswer(ShiftAnswerKind.New, null, name, backdate, origin));
    }

    private void Stay_Click(object sender, RoutedEventArgs e) =>
        Answer(new ShiftAnswer(ShiftAnswerKind.Stay, null, null, false, "refusée"));

    private void Answer(ShiftAnswer answer)
    {
        _answer = answer;
        Close();
    }

    // ------------------------------------------------------------ Champ du nom

    private void SetExisting(TaskItem? task, string? reason, string origin)
    {
        if (task is null) return;
        _existing = task;
        _existingOrigin = origin;
        ExistingText.Text = $"Tâche existante : « {task.Name} »"
                            + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n{reason}");
        ExistingRadio.Visibility = Visibility.Visible;
    }

    private void SetName(string name, string origin)
    {
        _settingText = true;
        NewNameBox.Text = name;
        _settingText = false;
        _prefilled = name;
        _prefilledOrigin = origin;
        UpdateHint();
    }

    private void NewNameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => NewRadio.IsChecked = true;

    private void NewNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_settingText) { _userEdited = true; _userChose = true; }
        HintButton.Visibility = Visibility.Collapsed;
        _hintDelay.Stop();
        if (_checkName != null && IsLoaded) _hintDelay.Start();
    }

    private void NewNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None
            && HintButton.Visibility == Visibility.Visible && _hint != null)
        {
            ApplyHint();
            e.Handled = true;
        }
    }

    private void UpdateHint()
    {
        var text = NewNameBox.Text.Trim();
        _hint = text.Length == 0 ? null : _checkName?.Invoke(text);
        if (_hint is null || _hint.Suggested == text) { HintButton.Visibility = Visibility.Collapsed; return; }
        HintText.Text = $"{_hint.Message} — Tab pour {(_hint.IsExisting ? "la reprendre" : "corriger")}";
        HintButton.Visibility = Visibility.Visible;
    }

    private void ApplyHint()
    {
        if (_hint is null) return;
        SetName(_hint.Suggested, _hint.IsExisting ? "existante" : "corrigé");
        NewRadio.IsChecked = true;
        NewNameBox.CaretIndex = NewNameBox.Text.Length;
    }

    private void Hint_Click(object sender, RoutedEventArgs e) => ApplyHint();

    private void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_aiName is null) return;
        SetName(_aiName, "IA");
        NewRadio.IsChecked = true;
    }

    // ------------------------------------------------------------------ Divers

    /// <summary>Coin bas-droit, borné à la zone de travail (même piège que ReminderPopup).</summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        double height = ActualHeight > 0 ? ActualHeight : 320;
        Left = Math.Clamp(area.Right - Width - 12, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(area.Bottom - height - 12, area.Top, Math.Max(area.Top, area.Bottom - height));
    }

    private static string Minutes(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
