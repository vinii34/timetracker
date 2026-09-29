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
}
