using System.Windows;
using TimeTracker.Core.Services.Ai;

namespace TimeTracker.UI;

/// <summary>
/// Ce que l'assistant propose, à cocher. La fenêtre ne touche pas à la base : elle renvoie la
/// sélection, « Gérer les tâches » applique et journalise.
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
    }

    private readonly List<Row> _rows;
    private bool _applied;

    internal AiSuggestionsWindow(string providerName, TaskCleanupAssistant.Proposal proposal)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        WindowFit.LimitToWorkArea(this);

        _rows = proposal.Merges.Select(m => new Row
            {
                Title = $"Fusionner « {m.From} » dans « {m.Into} »", Why = m.Why, Merge = m
            })
            .Concat(proposal.Renames.Select(r => new Row
            {
                Title = $"Renommer « {r.From} » en « {r.To} »", Why = r.Why, Rename = r
            }))
            .ToList();

        HeaderText.Text = _rows.Count == 0
            ? $"{providerName} n'a rien trouvé à proposer."
            : $"{providerName} propose {proposal.Merges.Count} fusion(s) et {proposal.Renames.Count} renommage(s)";
        ProposalList.ItemsSource = _rows;
        ApplyButton.IsEnabled = _rows.Count > 0;

        if (proposal.Ignored.Count > 0)
        {
            IgnoredText.Text = "Écarté (tâche inconnue) : " + string.Join(" ; ", proposal.Ignored);
            IgnoredText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => Activate();
    }

    /// <summary>Ouvre en modal ; renvoie les propositions cochées, ou une liste vide.</summary>
    public static IReadOnlyList<Row> Show(Window? owner, string providerName, TaskCleanupAssistant.Proposal proposal)
    {
        var win = new AiSuggestionsWindow(providerName, proposal);
        if (owner != null && owner.IsLoaded) win.Owner = owner;
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
        return win._applied ? win._rows.Where(r => r.Selected).ToList() : Array.Empty<Row>();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { _applied = true; Close(); }
}
