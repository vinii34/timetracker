using System.Text;
using System.Text.Json;

namespace TimeTracker.Core.Services.Ai;

/// <summary>
/// Demande à un modèle de langage <b>sur quoi l'utilisateur travaille</b> d'après les mots de ses
/// fenêtres : une tâche existante, ou un nom de tâche nouveau. Demande du 2026-09-29 : 63 % de son
/// temps hors réunion part sur des tâches créées le jour même, que le suggéreur local — qui ne
/// sait que reclasser les tâches existantes — ne pouvait pas proposer.
///
/// Ce qui part, décidé par l'utilisateur le 2026-09-29 : des <b>mots-clés</b> filtrés
/// (<see cref="ActivityShiftDetector.Keywords"/>, jamais un titre entier), les noms des
/// applications, le nom de la tâche en cours et les noms des tâches existantes hors réunions.
/// <see cref="DescribeSent"/> le dit à l'écran, dans la fenêtre qui montre la réponse.
/// Le modèle <b>propose</b> ; rien ne bascule sans un clic.
/// </summary>
public static class TaskSuggestionAssistant
{
    /// <param name="FromSelector">Vrai quand l'utilisateur ouvre lui-même le sélecteur.</param>
    public sealed record Request(string? CurrentTask, IReadOnlyList<string> Keywords,
                                 IReadOnlyList<string> Processes, IReadOnlyList<string> TaskNames,
                                 bool FromSelector);

    /// <summary>Tâche existante (nom exact en base) ou nom nouveau ; les deux null = pas d'avis.</summary>
    public sealed record Answer(string? Existing, string? NewName, string Why);

    /// <summary>Au-delà, le prompt grossit sans rien apporter : les plus récentes suffisent.</summary>
    public const int MaxTaskNames = 150;

    public static string BuildPrompt(Request request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tu aides à nommer les tâches d'un outil de suivi du temps (timesheet).");
        sb.AppendLine(request.FromSelector
            ? "L'utilisateur ouvre la liste pour choisir sa prochaine tâche."
            : "L'utilisateur semble avoir changé d'activité.");
        sb.AppendLine(request.CurrentTask is null
            ? "Aucune tâche en cours."
            : $"Tâche en cours : « {request.CurrentTask} ».");
        sb.AppendLine($"Mots-clés relevés dans les titres de ses fenêtres ces dernières minutes, les plus fréquents d'abord : {string.Join(", ", request.Keywords)}");
        if (request.Processes.Count > 0)
            sb.AppendLine($"Applications : {string.Join(", ", request.Processes)}");
        sb.AppendLine();
        sb.AppendLine("Tâches existantes, les plus récentes d'abord :");
        foreach (var name in request.TaskNames.Take(MaxTaskNames)) sb.AppendLine($"- {name}");
        sb.AppendLine();
        sb.AppendLine("Règles :");
        sb.AppendLine("- Si l'activité correspond clairement à une tâche existante (autre que la tâche en cours), mets son nom EXACT dans \"existing\" et laisse \"new_name\" vide.");
        sb.AppendLine("- Sinon laisse \"existing\" vide et propose dans \"new_name\" un nom de tâche court (2 à 6 mots), dans le même style que les tâches existantes : même langue, même façon de citer le client ou le sujet.");
        sb.AppendLine("- N'invente aucun nom de client ou de projet absent des mots-clés et des tâches existantes.");
        sb.AppendLine("- Si les mots-clés ne permettent pas de deviner, laisse les deux vides.");
        sb.AppendLine();
        sb.AppendLine("Réponds UNIQUEMENT avec ce JSON, sans commentaire ni balise de code :");
        sb.AppendLine("{\"existing\":\"\",\"new_name\":\"\",\"why\":\"raison courte\"}");
        return sb.ToString();
    }

    /// <summary>
    /// Lit la réponse. Tolère balises et texte autour. Une tâche « existante » inconnue est
    /// écartée (les modèles en inventent) ; un nom nouveau qui est en fait une tâche existante,
    /// ou son quasi-doublon, devient cette tâche — la liste ne doit pas gagner un doublon de plus.
    /// La tâche en cours n'est jamais proposée.
    /// </summary>
    public static Answer Parse(string response, IReadOnlyCollection<string> knownNames, string? currentTask,
                               string meetingName = TaskSimilarity.DefaultMeetingName)
    {
        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("La réponse ne contient pas de JSON.");

        using var doc = JsonDocument.Parse(response[start..(end + 1)]);
        var root = doc.RootElement;
        var existing = Clean(Text(root, "existing"));
        var newName = Clean(Text(root, "new_name"));
        var why = Text(root, "why");

        string? Known(string name) =>
            knownNames.FirstOrDefault(n => string.Equals(n.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
            ?? knownNames.Select(n => (n, ok: TaskSimilarity.IsDuplicate(name, n, out var s, meetingName), s))
                         .Where(x => x.ok).OrderByDescending(x => x.s).Select(x => x.n).FirstOrDefault();

        bool IsCurrent(string? name) =>
            name != null && currentTask != null && string.Equals(name, currentTask, StringComparison.CurrentCultureIgnoreCase);

        var knownNew = newName.Length > 0 ? Known(newName) : null;
        var chosen = (existing.Length > 0 ? Known(existing) : null) ?? knownNew;
        if (IsCurrent(chosen)) chosen = null;

        string? fresh = chosen is null && newName.Length > 0 && knownNew is null ? newName : null;
        return new Answer(chosen, fresh, why);
    }

    /// <summary>Ce qui part, en clair, pour l'afficher à l'utilisateur à côté de la réponse.</summary>
    public static string DescribeSent(Request request, string providerName) =>
        $"Envoyé à {providerName} : {request.Keywords.Count} mots-clés ({string.Join(", ", request.Keywords)})"
        + (request.Processes.Count > 0 ? $", applis {string.Join(", ", request.Processes)}" : "")
        + (request.CurrentTask is null ? "" : ", le nom de la tâche en cours")
        + $" et {Math.Min(request.TaskNames.Count, MaxTaskNames)} noms de tâches. Jamais un titre entier.";

    public static async Task<Answer> RunAsync(IAiProvider provider, Request request, string meetingName,
                                              CancellationToken cancellation)
    {
        var response = await provider.CompleteAsync(BuildPrompt(request), cancellation);
        return Parse(response, request.TaskNames, request.CurrentTask, meetingName);
    }

    /// <summary>Nom lisible : sans guillemets autour, espaces réduites, 80 caractères au plus.</summary>
    private static string Clean(string name)
    {
        var s = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                      .Trim('"', '«', '»', '“', '”', '\'', ' ', '.');
        return s.Length <= 80 ? s : s[..80].TrimEnd();
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
