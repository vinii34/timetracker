using System.Text.RegularExpressions;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Devine sur quelle tâche l'utilisateur travaille d'après les fenêtres qu'il a eues au premier
/// plan : si « Orvane » apparaît dans le titre du classeur ouvert depuis vingt minutes et qu'une
/// tâche s'appelle « Config Orvane », c'est probablement elle.
///
/// Deux sources de rapprochement, additionnées :
///
/// — <b>les noms des tâches</b> : les mots des titres contre les mots des noms. Tous les mots ne
///   se valent pas : « config », « tests », « réunion » figurent dans la moitié des tâches et ne
///   désignent rien, alors que « Orvane », « Velmora » ou « Kestrio » n'en désignent qu'une.
///   Plutôt qu'une liste de mots vides à entretenir, chaque mot pèse l'inverse de sa fréquence
///   parmi les noms de tâches (idf), et seuls les mots présents dans peu de tâches comptent ;
/// — <b>ce qui a été appris</b> (<c>task_hints</c>, depuis le 2026-09-17) : les mots des titres
///   vus pendant qu'une tâche tournait, en secondes. Un mot vu dix minutes avec « Kestrio
///   Siren seul » désigne cette tâche, même si son nom n'y est pas — c'est ce qui rattrape un
///   portail dont le titre ne cite jamais le client. Même garde-fou : un mot appris sur trop de
///   tâches ne désigne rien.
///
/// Ce n'est qu'une <i>suggestion</i> : elle passe en tête du sélecteur avec sa raison, et le
/// rappel la mentionne si elle contredit la tâche en cours. Elle ne bascule jamais toute seule.
/// </summary>
public static class TaskSuggester
{
    /// <summary>Une tâche suggérée, le temps qui la soutient, et pourquoi (lisible par l'utilisateur).</summary>
    public sealed record Suggestion(TaskItem Task, TimeSpan Evidence, double Score, string Reason);

    /// <summary>Profondeur d'observation par défaut.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(20);

    /// <summary>Temps minimal derrière une suggestion : en dessous, c'est du bruit.</summary>
    public static readonly TimeSpan MinEvidence = TimeSpan.FromMinutes(3);

    /// <summary>Un mot appris ne compte pour une tâche qu'à partir d'une minute de présence.</summary>
    private const int MinLearnedSeconds = 60;

    /// <summary>Dix minutes de présence apprises valent une confiance pleine.</summary>
    private const double FullConfidenceSeconds = 600;

    private static readonly Regex WordSplit = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    /// <summary>
    /// Suggestions classées, la plus probable d'abord. <paramref name="sampleInterval"/> est le
    /// pas du relevé, pour convertir un nombre d'observations en durée. <paramref name="hints"/>
    /// est ce qui a été appris (tâche → mot → secondes), ou null.
    /// </summary>
    public static List<Suggestion> Suggest(IReadOnlyList<TaskItem> tasks,
                                           IReadOnlyList<ActivityProbe.Sample> samples,
                                           IReadOnlyDictionary<long, Dictionary<string, int>>? hints = null,
                                           TimeSpan? sampleInterval = null, int max = 2)
    {
        if (tasks.Count == 0 || samples.Count == 0) return new List<Suggestion>();
        var step = sampleInterval ?? TimeSpan.FromSeconds(5);

        // Poids des mots de noms : idf sur les noms de tâches. Un mot ne compte que s'il désigne
        // peu de tâches — au plus 15 % d'entre elles : « orvane » (8 tâches sur 80) désigne un
        // client, « config » ou « réunion » (des dizaines) ne désignent rien. Avec quatre tâches,
        // seul un mot propre à une tâche compte : c'est ce que vérifie --selftest.
        var taskWords = tasks.ToDictionary(t => t.Id, t => Words(t.Name));
        var df = new Dictionary<string, int>();
        foreach (var words in taskWords.Values)
            foreach (var w in words) df[w] = df.GetValueOrDefault(w) + 1;
        int maxDf = Math.Max(1, (int)Math.Floor(tasks.Count * 0.15));
        double NameWeight(string w) =>
            df.GetValueOrDefault(w) > maxDf ? 0 : Math.Log((tasks.Count + 1.0) / (df.GetValueOrDefault(w) + 0.5));

        // Mots appris : pour chaque mot, quelles tâches l'ont vu assez longtemps, et la part de
        // chacune. Un mot vu sur trop de tâches (le navigateur, « Microsoft ») ne désigne rien.
        var learnedTasks = new Dictionary<string, List<(long Task, int Seconds)>>();
        if (hints != null)
        {
            foreach (var (taskId, words) in hints)
                foreach (var (word, seconds) in words)
                {
                    if (seconds < MinLearnedSeconds) continue;
                    if (!learnedTasks.TryGetValue(word, out var list)) learnedTasks[word] = list = new();
                    list.Add((taskId, seconds));
                }
        }
        int maxLearnedDf = Math.Max(1, (int)Math.Floor(Math.Max(1, hints?.Count ?? 0) * 0.15));
        double LearnedWeight(long taskId, string word)
        {
            if (!learnedTasks.TryGetValue(word, out var list) || list.Count > maxLearnedDf) return 0;
            int own = list.FirstOrDefault(x => x.Task == taskId).Seconds;
            if (own == 0) return 0;
            double share = (double)own / list.Sum(x => x.Seconds);
            double confidence = Math.Min(1, own / FullConfidenceSeconds);
            return share * confidence;
        }

        // Titres regroupés : le même titre revu 40 fois n'a pas à être redécoupé 40 fois.
        var titles = samples.GroupBy(s => s.Title)
                            .Select(g => (Title: g.Key, Words: Words(g.Key), Count: g.Count()))
                            .ToList();

        var result = new List<Suggestion>();
        foreach (var task in tasks)
        {
            var words = taskWords[task.Id];
            double score = 0;
            int hits = 0;
            (string Title, int Count, double Weight, bool Learned) best = ("", 0, 0, false);
            foreach (var t in titles)
            {
                double byName = words.Where(word => Matches(word, t.Words)).Sum(NameWeight);
                double byLearning = t.Words.Sum(word => LearnedWeight(task.Id, word));
                double w = byName + byLearning;
                if (w <= 0) continue;
                score += w * t.Count;
                hits += t.Count;
                if (w * t.Count > best.Weight) best = (t.Title, t.Count, w * t.Count, byLearning > byName);
            }
            if (hits == 0) continue;

            var evidence = TimeSpan.FromSeconds(hits * step.TotalSeconds);
            if (evidence < MinEvidence) continue;

            var reason = $"{Math.Round(TimeSpan.FromSeconds(best.Count * step.TotalSeconds).TotalMinutes)} min sur « {Shorten(best.Title, 50)} »"
                         + (best.Learned ? " (fenêtre déjà vue avec cette tâche)" : "");
            result.Add(new Suggestion(task, evidence, score, reason));
        }

        // À score égal (plusieurs tâches portent le même mot rare), la plus récemment utilisée.
        return result.OrderByDescending(s => s.Score)
                     .ThenByDescending(s => s.Task.LastUsed ?? DateTime.MinValue)
                     .Take(max).ToList();
    }

    /// <summary>
    /// Un mot de tâche est reconnu dans un titre s'il y figure tel quel, ou comme début d'un mot
    /// plus long (« orvane » dans « orvanes »), pour les mots d'au moins quatre lettres.
    /// </summary>
    private static bool Matches(string word, HashSet<string> titleWords) =>
        titleWords.Contains(word)
        || (word.Length >= 4 && titleWords.Any(t => t.Length > word.Length && t.StartsWith(word, StringComparison.Ordinal)));

    /// <summary>Mots normalisés d'un texte (minuscules sans accents, trois caractères au moins, pas de nombre seul).</summary>
    public static HashSet<string> Words(string text) =>
        WordSplit.Split(TaskSimilarity.Normalize(text))
                 .Where(w => w.Length >= 3 && !w.All(char.IsDigit))
                 .ToHashSet(StringComparer.Ordinal);

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
