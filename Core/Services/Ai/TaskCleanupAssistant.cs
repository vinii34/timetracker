using System.Text;
using System.Text.Json;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services.Ai;

/// <summary>
/// Demande à un modèle de langage ce que <see cref="TaskSimilarity"/> ne sait pas voir : deux
/// tâches qui désignent la même chose sans mot commun (« Aide Emma pour Brunel » et
/// « Brunel »), un nom mal orthographié, une convention de nommage à unifier. Le modèle
/// <b>propose</b>, l'utilisateur coche, TimeTracker applique — jamais l'inverse.
///
/// Ce qui part : les <b>noms</b> des tâches et leur nombre d'entrées. Ni les heures, ni les
/// dates, ni les titres de fenêtres. ~10 tokens par tâche : une centaine de tâches tient
/// largement dans le niveau gratuit de n'importe quel fournisseur.
/// </summary>
public static class TaskCleanupAssistant
{
    public sealed record Merge(string From, string Into, string Why);

    public sealed record Rename(string From, string To, string Why);

    /// <param name="Ignored">Propositions écartées parce qu'elles citent une tâche inconnue.</param>
    public sealed record Proposal(IReadOnlyList<Merge> Merges, IReadOnlyList<Rename> Renames,
                                  IReadOnlyList<string> Ignored);

    public static string BuildPrompt(IReadOnlyList<TaskUsage> tasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tu aides à nettoyer la liste des tâches d'un outil de suivi du temps (timesheet).");
        sb.AppendLine("Voici les tâches existantes, avec leur nombre d'entrées de temps :");
        sb.AppendLine();
        foreach (var t in tasks.OrderBy(t => t.Task.Name, StringComparer.CurrentCultureIgnoreCase))
            sb.AppendLine($"- {t.Task.Name} ({t.EntryCount})");
        sb.AppendLine();
        sb.AppendLine("Propose :");
        sb.AppendLine("1. des FUSIONS : deux tâches qui désignent manifestement le même travail (doublon, faute de frappe, même client et même sujet formulés différemment). « into » doit être un nom existant, de préférence celui qui a le plus d'entrées. Ne fusionne jamais deux clients ou deux sujets différents, et ne fusionne pas les réunions avec le travail hors réunion.");
        sb.AppendLine("2. des RENOMMAGES : orthographe, majuscules, cohérence de nommage (même forme pour les tâches du même type). Garde les noms courts. Ne renomme pas ce qui est déjà correct.");
        sb.AppendLine();
        sb.AppendLine("Réponds UNIQUEMENT avec ce JSON, sans commentaire ni balise de code, au plus 15 éléments par liste, listes vides si rien à proposer :");
        sb.AppendLine("{\"merges\":[{\"from\":\"nom\",\"into\":\"nom existant\",\"why\":\"raison courte\"}],\"renames\":[{\"from\":\"nom\",\"to\":\"nouveau nom\",\"why\":\"raison courte\"}]}");
        return sb.ToString();
    }

    /// <summary>
    /// Lit la réponse du modèle. Tolère les balises ```json et du texte autour ; écarte toute
    /// proposition qui cite un nom inconnu (les modèles en inventent), les auto-fusions et les
    /// renommages sans effet. Les noms sont ramenés à leur casse exacte en base.
    /// </summary>
    public static Proposal Parse(string response, IReadOnlyCollection<string> knownNames)
    {
        var canonical = knownNames.GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);
        var merges = new List<Merge>();
        var renames = new List<Rename>();
        var ignored = new List<string>();

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("La réponse ne contient pas de JSON.");

        using var doc = JsonDocument.Parse(response[start..(end + 1)]);
        var root = doc.RootElement;

        if (root.TryGetProperty("merges", out var mergesJson) && mergesJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mergesJson.EnumerateArray())
            {
                var from = Text(m, "from");
                var into = Text(m, "into");
                if (!canonical.TryGetValue(from, out var fromName) || !canonical.TryGetValue(into, out var intoName))
                {
                    ignored.Add($"fusion « {from} » → « {into} » (tâche inconnue)");
                    continue;
                }
                if (fromName == intoName) continue;
                merges.Add(new Merge(fromName, intoName, Text(m, "why")));
            }
        }

        if (root.TryGetProperty("renames", out var renamesJson) && renamesJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in renamesJson.EnumerateArray())
            {
                var from = Text(r, "from");
                var to = Text(r, "to").Trim();
                if (!canonical.TryGetValue(from, out var fromName))
                {
                    ignored.Add($"renommage « {from} » (tâche inconnue)");
                    continue;
                }
                if (to.Length == 0 || to == fromName) continue;
                renames.Add(new Rename(fromName, to, Text(r, "why")));
            }
        }

        return new Proposal(merges, renames, ignored);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    /// <summary>Interroge le fournisseur et lit sa réponse.</summary>
    public static async Task<Proposal> RunAsync(IAiProvider provider, IReadOnlyList<TaskUsage> tasks,
                                                CancellationToken cancellation)
    {
        var response = await provider.CompleteAsync(BuildPrompt(tasks), cancellation);
        return Parse(response, tasks.Select(t => t.Task.Name).ToList());
    }
}
