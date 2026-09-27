using System.Media;
using System.Windows;
using System.Windows.Controls;
using TimeTracker.Core.Models;
using Button = System.Windows.Controls.Button;

namespace TimeTracker.UI;

/// <summary>
/// « Une réunion commence à ton agenda — tu bascules ? », et ses deux variantes : « tu es en
/// réunion, laquelle ? » et « une autre commence, tu y passes ? ». Un bouton par créneau, pour
/// que deux réunions aux mêmes horaires (l'une facultative, l'autre non) se départagent d'un
/// clic — c'est le cas que l'utilisateur a décrit le 2026-09-17.
///
/// ⚠️ Affichée <b>sans modalité</b>, comme le rappel périodique depuis le 2026-08-09 : une
/// fenêtre absente de la barre des tâches et d'Alt-Tab ne doit jamais bloquer le reste de
/// l'application (voir HANDOFF §8). Ne pas répondre est un cas normal et parfaitement géré —
/// l'agenda continue alors de nommer la réunion sans la déclencher, et une réunion en cours
/// n'est jamais coupée sans réponse.
/// </summary>
public partial class CalendarPromptWindow : Window
{
    private readonly MeetingChoice _choice;
    private CalendarMeeting? _chosen;

    /// <summary>Ligne de bouton : ce qu'on propose, et de quoi reconnaître le créneau.</summary>
    private sealed record Candidate(CalendarMeeting Meeting, string Prompt, string Detail);

    internal CalendarPromptWindow(MeetingChoice choice, bool playSound)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _choice = choice;

        bool several = choice.Candidates.Count > 1;
        string current = choice.Current is null ? "" : Subject(choice.Current);

        switch (choice.Kind)
        {
            case MeetingChoiceKind.Start:
                HeaderText.Text = several ? "Plusieurs réunions prévues à l'agenda" : "Réunion prévue à l'agenda";
                HintText.Text = several
                    ? "Choisis celle à laquelle tu participes. Si tu ne réponds pas, rien ne bascule — la réunion sera nommée d'après l'agenda si elle est détectée."
                    : "Si tu ne réponds pas, rien ne bascule — mais la réunion sera quand même nommée avec ce sujet si elle est détectée.";
                NoButton.Content = several ? "Aucune, je n'y suis pas" : "Non, je n'y suis pas";
                break;

            case MeetingChoiceKind.Name:
                HeaderText.Text = "Tu es en réunion — laquelle ?";
                HintText.Text = "Plusieurs créneaux couvrent cette réunion : le nom retenu dépend de ta réponse. Sans réponse, elle garde le titre de sa fenêtre.";
                NoButton.Content = "Aucune de celles-ci";
                break;

            default:
                HeaderText.Text = choice.CurrentOver
                    ? "Cette réunion est terminée à l'agenda — laquelle maintenant ?"
                    : (several ? "D'autres réunions commencent" : "Une autre réunion commence");
                CurrentText.Text = choice.CurrentOver
                    ? $"« {current} » s'est terminée à {choice.Current!.End:HH:mm}, mais tu es toujours en réunion."
                    : $"En cours : « {current} » jusqu'à {choice.Current!.End:HH:mm}.";
                CurrentText.Visibility = Visibility.Visible;
                HintText.Text = "Y passer clôture la réunion en cours à l'heure du nouveau créneau. Sans réponse, rien ne bouge.";
                NoButton.Content = $"Rester sur « {Shorten(current, 28)} »";
                break;
        }

        CandidateList.ItemsSource = choice.Candidates.Select(m => new Candidate(m,
            choice.Kind switch
            {
                MeetingChoiceKind.Start when !several => "Oui, pointer la réunion",
                MeetingChoiceKind.Start => $"Pointer « {Subject(m)} »",
                MeetingChoiceKind.Name => $"C'est « {Subject(m)} »",
                _ => $"Passer à « {Subject(m)} »"
            },
            Describe(m))).ToList();

        Loaded += (_, _) =>
        {
            PositionBottomRight();
            if (playSound) SystemSounds.Asterisk.Play();
            Activate();
        };
    }

    private static string Subject(CalendarMeeting m) => m.Subject.Length > 0 ? m.Subject : "(sans objet)";

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>
    /// Horaire, organisateur, et ce que l'utilisateur avait répondu dans Outlook — affiché mais
    /// ne décidant de rien : il assiste parfois à des réunions refusées, et refuse par erreur.
    /// </summary>
    private static string Describe(CalendarMeeting m)
    {
        var text = $"{m.Start:HH:mm} – {m.End:HH:mm}";
        if (m.Organizer.Length > 0) text += $" · {m.Organizer}";
        var response = m.Response switch
        {
            CalendarResponse.Organisateur => "tu organises",
            CalendarResponse.Acceptee => "acceptée",
            CalendarResponse.Provisoire => "peut-être",
            CalendarResponse.Refusee => "refusée",
            CalendarResponse.SansReponse => "sans réponse",
            _ => ""
        };
        return response.Length > 0 ? $"{text} · {response}" : text;
    }

    /// <summary>Affiche la question et rappelle <paramref name="answered"/> à la fermeture,
    /// avec le créneau choisi ou null (« non », « aucune », « je reste », ou fenêtre fermée).</summary>
    public static void Ask(MeetingChoice choice, bool playSound, Action<CalendarMeeting?> answered)
    {
        var win = new CalendarPromptWindow(choice, playSound);
        win.Closed += (_, _) => answered(win._chosen);
        win.Show();
    }

    /// <summary>Coin bas-droit, borné à la zone de travail (même piège que ReminderPopup).</summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        double height = ActualHeight > 0 ? ActualHeight : 200;
        Left = Math.Clamp(area.Right - Width - 12, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(area.Bottom - height - 12, area.Top, Math.Max(area.Top, area.Bottom - height));
    }

    private void Candidate_Click(object sender, RoutedEventArgs e)
    {
        _chosen = ((sender as Button)?.Tag as Candidate)?.Meeting;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e) { _chosen = null; Close(); }
}
