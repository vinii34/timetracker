using System.Collections.ObjectModel;
using System.Windows;
using TimeTracker.Core.Services;
using TimeTracker.Core.Services.Ai;

namespace TimeTracker.UI;

/// <summary>
/// Propositions de fusion et de renommage, à cocher : celles de l'assistant IA (« Gérer les
/// tâches ») et, depuis le 2026-09-29, les « noms à vérifier » trouvés sur le poste (bandeau du
/// tableau de bord). La fenêtre ne touche pas à la base : elle renvoie la sélection, l'appelant
/// applique et journalise.
/// </summary>
public partial class AiSuggestionsWindow : Window
{
    /// <summary>Une proposition cochable. <c>Merge</c> ou <c>Rename</c> est posé, pas les deux.</summary>
    public sealed class Row
    {
        public bool Selected { get; set; } = true;
        public string Title { get; init; } = "";
        public string Why { get; init; } = "";
        public TaskCleanupAssistant.Merge? Merge { get; init; }
        public TaskCleanupAssistant.Rename? Rename { get; init; }

        /// <summary>Clé de refus d'une proposition locale (vide pour l'IA, qui ne revient que sur un clic).</summary>
        public string Key { get; init; } = "";

        /// <summary>Cochée d'office. Une proposition qui ne l'était pas n'est jamais « refusée » sans y avoir touché.</summary>
        public bool Recommended { get; init; } = true;
    }

    /// <summary>
    /// Cochées, et <b>décochées par l'utilisateur</b>, au moment de valider ; deux listes vides si
    /// fermée sans valider. Une proposition laissée décochée d'office n'est pas un refus.
    /// </summary>
    public sealed record Outcome(IReadOnlyList<Row> Chosen, IReadOnlyList<Row> Declined);

    private readonly ObservableCollection<Row> _rows;
    private readonly Func<CancellationToken, Task<(IReadOnlyList<Row> Rows, string Summary)>>? _askAi;
    private CancellationTokenSource? _aiCancel;
    private bool _applied;

    internal AiSuggestionsWindow(string providerName, TaskCleanupAssistant.Proposal proposal)
        : this(proposal.Merges.Count + proposal.Renames.Count == 0
                   ? $"{providerName} n'a rien trouvé à proposer."
                   : $"{providerName} propose {proposal.Merges.Count} fusion(s) et {proposal.Renames.Count} renommage(s)",
               FromProposal(proposal), proposal.Ignored)
    {
    }

    /// <param name="askAi">
    /// Si fourni, un bouton « Proposer aussi avec l'IA » interroge l'assistant et ajoute ses
    /// propositions à la liste (un appel, sur un clic).
    /// </param>
    internal AiSuggestionsWindow(string header, IEnumerable<Row> rows, IReadOnlyList<string>? ignored = null,
                                 string? title = null,
                                 Func<CancellationToken, Task<(IReadOnlyList<Row> Rows, string Summary)>>? askAi = null)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        WindowFit.LimitToWorkArea(this);
        if (title != null) Title = title;

        _rows = new ObservableCollection<Row>(rows);
        _askAi = askAi;
        HeaderText.Text = header;
        ProposalList.ItemsSource = _rows;
        ApplyButton.IsEnabled = _rows.Count > 0;
        AiButton.Visibility = askAi is null ? Visibility.Collapsed : Visibility.Visible;
        ShowIgnored(ignored);

        Loaded += (_, _) => Activate();
        Closed += (_, _) => _aiCancel?.Cancel();
    }

    internal static IReadOnlyList<Row> FromProposal(TaskCleanupAssistant.Proposal proposal) =>
        proposal.Merges.Select(m => new Row
            {
                Title = $"Fusionner « {m.From} » dans « {m.Into} »", Why = m.Why, Merge = m
            })
            .Concat(proposal.Renames.Select(r => new Row
            {
                Title = $"Renommer « {r.From} » en « {r.To} »", Why = r.Why, Rename = r
            }))
            .ToList();

    internal static Row FromReview(NameReview.Item item) => item.Merge is { } m
        ? new Row { Title = $"Fusionner « {m.From} » dans « {m.Into} »", Why = m.Why, Merge = m, Key = item.Key,
                    Selected = item.Recommended, Recommended = item.Recommended }
        : new Row { Title = $"Renommer « {item.Rename!.From} » en « {item.Rename.To} »", Why = item.Rename.Why,
                    Rename = item.Rename, Key = item.Key, Selected = item.Recommended, Recommended = item.Recommended };

    /// <summary>Ouvre en modal ; renvoie les propositions cochées, ou une liste vide.</summary>
    public static IReadOnlyList<Row> Show(Window? owner, string providerName, TaskCleanupAssistant.Proposal proposal)
    {
        var win = new AiSuggestionsWindow(providerName, proposal);
        return win.ShowModal(owner).Chosen;
    }

    internal Outcome ShowModal(Window? owner)
    {
        if (owner != null && owner.IsLoaded) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowDialog();
        return _applied
            ? new Outcome(_rows.Where(r => r.Selected).ToList(), _rows.Where(r => !r.Selected && r.Recommended).ToList())
            : new Outcome(Array.Empty<Row>(), Array.Empty<Row>());
    }

    private void ShowIgnored(IReadOnlyList<string>? ignored)
    {
        if (ignored is null || ignored.Count == 0) return;
        IgnoredText.Text = "Écarté (tâche inconnue) : " + string.Join(" ; ", ignored);
        IgnoredText.Visibility = Visibility.Visible;
    }

    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_askAi is null) return;
        AiButton.IsEnabled = false;
        AiStatusText.Text = "Interrogation de l'assistant…";
        _aiCancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var (rows, summary) = await _askAi(_aiCancel.Token);
            // Une proposition déjà listée (même fusion, même renommage) n'est pas doublée.
            var known = _rows.Select(r => r.Title).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            foreach (var row in rows.Where(r => known.Add(r.Title))) _rows.Add(row);
            AiStatusText.Text = summary;
            ApplyButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            Logger.Error("AiSuggestionsWindow.Ai", ex);
            AiStatusText.Text = $"L'assistant n'a pas répondu : {ex.Message}";
            AiButton.IsEnabled = true;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { _applied = true; Close(); }
}
