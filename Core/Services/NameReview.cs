using TimeTracker.Core.Models;
using TimeTracker.Core.Services.Ai;

namespace TimeTracker.Core.Services;

/// <summary>
/// « Noms à vérifier » : les fautes de frappe et les doublons que le poste sait voir seul
/// (<see cref="TaskSimilarity"/>), présentés là où l'utilisateur regarde — un bandeau du
/// tableau de bord — et non plus seulement au fond de « Gérer les tâches », où il n'allait pas.
///
/// Rien n'est appliqué sans une case cochée. Ce qu'il décoche en validant ne revient plus
/// (<see cref="KeyDismissed"/>) : un bandeau qui ressasse les mêmes refus n'est plus lu.
/// </summary>
public static class NameReview
{
    /// <summary>Une proposition : fusion ou renommage, sa clé de refus, et si elle est cochée d'office.</summary>
    public sealed record Item(TaskCleanupAssistant.Merge? Merge, TaskCleanupAssistant.Rename? Rename,
                              string Key, bool Recommended);

    /// <summary>Clé de la table <c>settings</c> : propositions refusées, une par ligne.</summary>
    public const string KeyDismissed = "name_review_dismissed";

    /// <summary>
    /// Propositions locales. Le nom qui <b>survit</b> à une fusion est, dans l'ordre : celui
    /// sans faute de frappe (sur les vrais noms, la faute portait souvent le plus d'entrées :
    /// « Onborading » 6, « Onboarding » 3), puis celui au format des réunions nommées
    /// automatiquement (« Réunion — sujet » : sinon la prochaine réunion recréerait le doublon),
    /// puis celui qui porte le plus d'entrées. Jamais la tâche en cours ne part. Une fusion entre
    /// une réunion et du travail hors réunion n'est pas cochée d'office : c'est souvent le même
    /// sujet, pas toujours le même temps.
    /// </summary>
    public static List<Item> Find(IReadOnlyList<TaskUsage> usage, long? currentTaskId,
                                  string meetingName, IReadOnlySet<string> dismissed)
    {
        var tasks = usage.Select(u => u.Task).ToList();
        var byId = usage.ToDictionary(u => u.Task.Id);
        var items = new List<Item>();
        var merging = new HashSet<long>();
        var vocabulary = TaskSimilarity.Vocabulary.Of(tasks.Select(t => t.Name));
        var corrections = tasks.ToDictionary(t => t.Id,
            t => TaskSimilarity.CorrectTypos(t.Name, vocabulary, maxRareCount: 1, includesName: true));
        var autoPrefix = $"{meetingName} — ";

        // Rang du nom qui doit survivre : moins de fautes, format automatique, plus d'entrées.
        (int, int, int) Keep(TaskUsage u) => (
            -(corrections[u.Task.Id]?.Words.Count ?? 0),
            u.Task.Name.StartsWith(autoPrefix, StringComparison.CurrentCultureIgnoreCase) ? 1 : 0,
            u.EntryCount);

        void AddMerge(TaskUsage a, TaskUsage b, string why)
        {
            var (source, target) = Keep(a).CompareTo(Keep(b)) < 0 ? (a, b) : (b, a);
            // La tâche en cours ne peut pas partir ; inverser le sens fondrait le bon nom dans la
            // faute. La proposition reviendra quand elle ne sera plus en cours.
            if (source.Task.Id == currentTaskId) return;
            // Une tâche ne part que dans une autre, et jamais vers une tâche qui part elle-même.
            if (merging.Contains(source.Task.Id) || merging.Contains(target.Task.Id)) return;
            var key = $"m:{Math.Min(a.Task.Id, b.Task.Id)}:{Math.Max(a.Task.Id, b.Task.Id)}";
            if (dismissed.Contains(key)) return;

            bool oneSidedMeeting = IsMeeting(source.Task.Name, meetingName) != IsMeeting(target.Task.Name, meetingName);
            if (oneSidedMeeting) why = $"même sujet, avec et sans « {meetingName} »";
            items.Add(new Item(new TaskCleanupAssistant.Merge(source.Task.Name, target.Task.Name, why), null,
                               key, Recommended: !oneSidedMeeting));
            merging.Add(source.Task.Id);
        }

        foreach (var d in TaskSimilarity.FindDuplicates(tasks, meetingName))
            AddMerge(byId[d.A.Id], byId[d.B.Id],
                     d.Similarity >= 0.99 ? "même nom à la casse, aux accents ou à la ponctuation près"
                                          : "faute de frappe probable");

        foreach (var u in usage)
        {
            if (merging.Contains(u.Task.Id)) continue;
            var correction = corrections[u.Task.Id];
            if (correction is null) continue;

            var why = "faute de frappe probable : "
                      + string.Join(", ", correction.Words.Select(w => $"« {w.From} » → « {w.To} »"));
            // Le nom corrigé existe déjà : c'est une fusion, pas un renommage.
            var clash = tasks.FirstOrDefault(t => t.Id != u.Task.Id
                                                  && string.Equals(t.Name, correction.Name, StringComparison.CurrentCultureIgnoreCase));
            if (clash != null) { AddMerge(u, byId[clash.Id], why); continue; }

            var key = $"r:{u.Task.Id}:{TaskSimilarity.Normalize(correction.Name)}";
            if (dismissed.Contains(key)) continue;
            items.Add(new Item(null, new TaskCleanupAssistant.Rename(u.Task.Name, correction.Name, why), key, true));
        }

        return items;
    }

    /// <summary>Ce qu'on suggère à la place d'un nom tapé, et comment le dire.</summary>
    /// <param name="IsExisting">Vrai si <paramref name="Suggested"/> est une tâche existante (sinon une correction).</param>
    public sealed record Hint(string Suggested, string Message, bool IsExisting);

    /// <summary>
    /// « Tu voulais dire … ? » au moment où l'utilisateur crée une tâche — là où il regarde,
    /// avant que la faute n'entre en base (une tâche nouvelle était créée sans aucun contrôle).
    /// Une tâche existante à la faute près prime sur une correction mot à mot. Null si le nom
    /// est déjà exactement celui d'une tâche, ou si rien ne cloche.
    /// </summary>
    /// <param name="vocabulary">Vocabulaire des noms, construit une fois par l'appelant ; null = construit ici.</param>
    public static Hint? CheckNewName(string typed, IReadOnlyList<TaskItem> tasks, string meetingName,
                                     TaskSimilarity.Vocabulary? vocabulary = null)
    {
        typed = typed.Trim();
        if (typed.Length < 4) return null;
        if (tasks.Any(t => string.Equals(t.Name.Trim(), typed, StringComparison.CurrentCultureIgnoreCase))) return null;

        var existing = tasks
            .Select(t => (Task: t, Ok: TaskSimilarity.IsDuplicate(typed, t.Name, out var s, meetingName), Score: s))
            .Where(x => x.Ok)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Task.LastUsed ?? DateTime.MinValue)
            .Select(x => x.Task).FirstOrDefault();
        if (existing != null) return new Hint(existing.Name, $"« {existing.Name} » existe déjà", true);

        var correction = TaskSimilarity.CorrectTypos(typed, vocabulary ?? TaskSimilarity.Vocabulary.Of(tasks.Select(t => t.Name)),
                                                     maxRareCount: 0, includesName: false);
        return correction is null ? null : new Hint(correction.Name, $"Tu voulais dire « {correction.Name} » ?", false);
    }

    public static HashSet<string> LoadDismissed(DatabaseService db) =>
        (db.GetSetting(KeyDismissed) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    public static void Dismiss(DatabaseService db, IEnumerable<string> keys)
    {
        var all = LoadDismissed(db);
        int before = all.Count;
        foreach (var k in keys) if (k.Length > 0) all.Add(k);
        if (all.Count != before) db.SetSetting(KeyDismissed, string.Join("\n", all));
    }

    /// <summary>
    /// Applique les fusions puis les renommages choisis. Jamais la tâche en cours en source de
    /// fusion ; un renommage vers un nom déjà pris est écarté plutôt que transformé en fusion
    /// déguisée. Renvoie le nombre appliqué et ce qui a été écarté (lisible).
    /// </summary>
    public static (int Applied, List<string> Skipped) Apply(DatabaseService db,
        IEnumerable<(TaskCleanupAssistant.Merge? Merge, TaskCleanupAssistant.Rename? Rename)> chosen,
        long? currentTaskId, string origin)
    {
        int applied = 0;
        var skipped = new List<string>();
        var ordered = chosen.OrderBy(c => c.Merge is null).ToList();   // fusions d'abord
        foreach (var (merge, rename) in ordered)
        {
            try
            {
                if (merge is { } m)
                {
                    var source = db.FindTaskByName(m.From);
                    var target = db.FindTaskByName(m.Into);
                    if (source is null || target is null || source.Id == target.Id) { skipped.Add(m.From); continue; }
                    if (source.Id == currentTaskId) { skipped.Add($"{m.From} (tâche en cours)"); continue; }
                    db.MergeTasks(source.Id, target.Id);
                    Logger.Info($"Tâches fusionnées ({origin}) : « {m.From} » → « {m.Into} ».");
                    applied++;
                }
                else if (rename is { } r)
                {
                    var task = db.FindTaskByName(r.From);
                    if (task is null) { skipped.Add(r.From); continue; }
                    var clash = db.FindTaskByName(r.To);
                    if (clash != null && clash.Id != task.Id) { skipped.Add($"{r.From} → {r.To} (nom déjà pris)"); continue; }
                    db.UpdateTaskName(task.Id, r.To);
                    Logger.Info($"Tâche renommée ({origin}) : « {r.From} » → « {r.To} ».");
                    applied++;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"NameReview.Apply ({origin})", ex);
                skipped.Add(merge?.From ?? rename?.From ?? "?");
            }
        }
        return (applied, skipped);
    }

    /// <summary>Le nom commence-t-il par le préfixe de réunion (« Réunion », « Réunion — sujet ») ?</summary>
    public static bool IsMeeting(string name, string meetingName)
    {
        var n = TaskSimilarity.Normalize(name);
        var m = TaskSimilarity.Normalize(meetingName);
        return m.Length > 0 && (n == m || n.StartsWith(m + " ", StringComparison.Ordinal));
    }
}
