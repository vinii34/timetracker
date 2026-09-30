using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Attribue les mots des fenêtres relevées aux tâches <b>d'après ce que dit la base après coup</b>,
/// et non d'après la tâche qui tournait à l'instant du relevé (refait le 2026-09-29).
///
/// L'ancienne règle comptait chaque minute les mots vus pour la tâche en cours. Dépouillement du
/// 17 au 29/09 : la tâche fourre-tout (« Gen admin, e-mails ») avait appris 419 mots — les sujets
/// de mails de tous les clients — et détenait déjà l'essentiel du poids des mots de tâches créées
/// le matin même. Une bascule oubliée puis antidatée (« depuis 15 min ») laissait aussi ces
/// minutes apprises pour la mauvaise tâche.
///
/// D'où : l'apprentissage attend <see cref="Delay"/> (les relevés vivent 30 min en mémoire), puis
/// demande à la base quelle entrée couvrait chaque relevé — une correction ou un antidatage fait
/// entre-temps est donc pris en compte. Rien n'est appris pendant une réunion : ses tâches
/// (« Réunion — sujet ») ne resservent presque jamais, et leurs mots prenaient le poids d'autres.
/// </summary>
public static class ActivityLearning
{
    /// <summary>Recul avant d'apprendre : laisse le temps de corriger ou d'antidater une bascule.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(15);

    /// <summary>Secondes par mot et par tâche, pour les relevés couverts par une entrée hors réunion.</summary>
    public static Dictionary<long, Dictionary<string, int>> Attribute(IEnumerable<ActivityProbe.Sample> samples,
                                                                      IReadOnlyList<TimeEntry> entries,
                                                                      int secondsPerSample = 5)
    {
        var result = new Dictionary<long, Dictionary<string, int>>();
        foreach (var sample in samples)
        {
            if (sample.Process.Equals("TimeTracker", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = entries.FirstOrDefault(e => e.StartedAt <= sample.At && (e.EndedAt ?? DateTime.MaxValue) > sample.At);
            if (entry is null || entry.IsMeeting) continue;

            if (!result.TryGetValue(entry.TaskId, out var words)) result[entry.TaskId] = words = new();
            foreach (var word in TaskSuggester.Words(sample.Title))
                words[word] = words.GetValueOrDefault(word) + secondsPerSample;
        }
        return result;
    }

    /// <summary>Une tâche reprise : ses fenêtres reviennent au premier plan.</summary>
    /// <param name="LastSeen">Dernier relevé de ces fenêtres pendant une entrée de cette tâche.</param>
    public sealed record Resumed(long TaskId, TimeSpan Evidence, DateTime LastSeen);

    /// <summary>Un mot ne rattache une fenêtre à une tâche qu'après 30 s de présence avec elle.</summary>
    private const int ResumedWordSeconds = 30;

    /// <summary>
    /// La tâche que la nouvelle activité <b>reprend</b> : ses fenêtres ont déjà été au premier plan,
    /// dans la demi-heure gardée en mémoire, pendant une entrée d'une autre tâche que celle en
    /// cours. Le cas du 29/09 : de retour sur les fenêtres de la tâche quittée 13 min plus tôt, la
    /// fenêtre de changement a proposé une réunion sans rapport (refusée), et l'utilisateur a
    /// rebasculé à la main onze minutes après, antidaté de quinze. Les mots appris, eux, ne
    /// pouvaient pas le voir : la tâche était née l'après-midi même.
    ///
    /// Une fenêtre étrangère désigne une tâche si son titre a été vu avec elle, ou si elle partage
    /// un mot-clé de la nouvelle activité (<paramref name="keywords"/>, déjà débarrassés des mots
    /// génériques) vu au moins 30 s avec elle. Il faut que la moitié des fenêtres étrangères la
    /// désignent ; à égalité, la plus récente l'emporte. Rien n'est gardé : tout vit en mémoire.
    /// </summary>
    /// <param name="foreign">Relevés de la nouvelle activité (<c>ActivityShiftDetector.Shift.Samples</c>).</param>
    /// <param name="recent">Relevés gardés en mémoire (<see cref="ActivityProbe.Retention"/>).</param>
    /// <param name="excluded">Tâches à ne jamais proposer (réunions).</param>
    public static Resumed? ResumedTask(IReadOnlyList<ActivityProbe.Sample> foreign, IReadOnlyList<string> keywords,
                                       IEnumerable<ActivityProbe.Sample> recent, IReadOnlyList<TimeEntry> entries,
                                       long currentTaskId, Func<long, bool> excluded, int secondsPerSample = 5)
    {
        if (foreign.Count == 0) return null;
        var keywordSet = keywords.ToHashSet(StringComparer.Ordinal);
        var titles = new Dictionary<long, HashSet<string>>();
        var words = new Dictionary<long, Dictionary<string, int>>();
        var lastSeen = new Dictionary<long, DateTime>();
        foreach (var sample in recent)
        {
            if (sample.Process.Equals("TimeTracker", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = entries.FirstOrDefault(e => e.StartedAt <= sample.At && (e.EndedAt ?? DateTime.MaxValue) > sample.At);
            if (entry is null || entry.IsMeeting || entry.TaskId == currentTaskId || excluded(entry.TaskId)) continue;

            if (!titles.TryGetValue(entry.TaskId, out var seen)) titles[entry.TaskId] = seen = new(StringComparer.Ordinal);
            seen.Add(sample.Title);
            if (!words.TryGetValue(entry.TaskId, out var counts)) words[entry.TaskId] = counts = new(StringComparer.Ordinal);
            foreach (var word in TaskSuggester.Words(sample.Title).Where(keywordSet.Contains))
                counts[word] = counts.GetValueOrDefault(word) + secondsPerSample;
            lastSeen[entry.TaskId] = sample.At;
        }
        if (titles.Count == 0) return null;

        var best = titles.Keys
            .Select(id =>
            {
                var strong = words[id].Where(w => w.Value >= ResumedWordSeconds).Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
                int hits = foreign.Count(s => titles[id].Contains(s.Title) || TaskSuggester.Words(s.Title).Overlaps(strong));
                return (Id: id, Hits: hits, Last: lastSeen[id]);
            })
            .OrderByDescending(x => x.Hits).ThenByDescending(x => x.Last)
            .First();
        if (best.Hits == 0 || best.Hits * 2 < foreign.Count) return null;
        return new Resumed(best.Id, TimeSpan.FromSeconds(best.Hits * secondsPerSample), best.Last);
    }
}
