namespace TimeTracker.Core.Services;

/// <summary>
/// Repère un <b>changement de tâche probable</b> : les fenêtres des dernières minutes ne
/// ressemblent plus à celles de la tâche en cours. Demande de l'utilisateur (2026-09-17 puis
/// 09-29) : que l'appli le voie d'elle-même au lieu d'attendre le sélecteur ou le rappel.
///
/// Une fenêtre « ressemble » à la tâche en cours si son titre a déjà été vu depuis le début de
/// la tâche, ou si elle partage un mot significatif avec ce qui a été vu depuis, ce qui a été
/// appris pour cette tâche (<c>task_hints</c>), ou son nom. Les mots présents partout
/// (« microsoft », « outlook », le nom de l'employeur) ne disent rien : l'appelant fournit le
/// filtre, calculé comme celui de <see cref="TaskSuggester"/>. Une fenêtre sans mot significatif
/// (nouvel onglet, boîte de réception) ne compte ni pour ni contre.
///
/// Garde-fous, parce qu'une proposition à tort coûte un clic et, répétée, la confiance :
/// — au moins <see cref="MinMismatch"/> de fenêtres étrangères sur les <see cref="RecentWindow"/>
///   dernières minutes, et <see cref="MismatchRatio"/> du temps ;
/// — la condition doit tenir <see cref="HoldEvaluations"/> évaluations de suite (une par minute) :
///   un coup d'œil à un mail ne déclenche rien ;
/// — une seule proposition par changement : le même ensemble de mots n'est pas reproposé tant que
///   la tâche n'a pas changé, et rien pendant <see cref="Cooldown"/> après une proposition ;
/// — « je reste » range l'activité proposée du côté de la tâche en cours.
/// Les silences liés au contexte (réunion, pause, juste après une bascule ou un réveil) sont
/// décidés par l'appelant, qui ne demande alors rien et appelle <see cref="Silence"/>.
///
/// Rien ne quitte cette classe vers le journal : elle manipule des titres de fenêtres.
/// </summary>
public sealed class ActivityShiftDetector
{
    /// <summary>Ce que la tâche en cours est censée ressembler.</summary>
    /// <param name="LearnedWords">Mots appris pour cette tâche (au moins une minute).</param>
    /// <param name="IsGeneric">Vrai pour un mot qui ne désigne rien (vu sur trop de tâches).</param>
    /// <param name="NamesOtherTask">
    /// Vrai pour un mot rare du nom d'une <b>autre</b> tâche (« velmora » dans « Onboarding
    /// Velmora ») : il ne décrit pas la tâche en cours, même vu ou appris avec elle. Sans cette
    /// règle, la tâche fourre-tout (« Gen admin, e-mails »), qui apprend les sujets de mails de
    /// tous les clients, « ressemblait » à tout et ne voyait jamais de changement.
    /// </param>
    public sealed record Context(long TaskId, string TaskName, DateTime TaskStart,
                                 IReadOnlySet<string> LearnedWords, Func<string, bool> IsGeneric,
                                 Func<string, bool>? NamesOtherTask = null);

    /// <summary>Un changement probable.</summary>
    /// <param name="Since">Premier instant de la nouvelle activité (antidatage proposé).</param>
    /// <param name="Keywords">Mots significatifs de la nouvelle activité, les plus présents d'abord.</param>
    /// <param name="Samples">Relevés de la nouvelle activité, pour <see cref="TaskSuggester"/>.</param>
    public sealed record Shift(DateTime Since, TimeSpan Evidence, string DominantTitle, TimeSpan DominantTime,
                               IReadOnlyList<string> Keywords, IReadOnlyList<string> Processes,
                               IReadOnlyList<ActivityProbe.Sample> Samples);

    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinMismatch = TimeSpan.FromMinutes(3);
    public const double MismatchRatio = 0.8;
    public const int HoldEvaluations = 2;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    /// <summary>Retour d'au moins une minute sur la tâche en cours : le changement repart de zéro.</summary>
    private static readonly TimeSpan FitStreakResets = TimeSpan.FromMinutes(1);

    /// <summary>Mots vus au moins 30 s depuis le début de la tâche : ils la décrivent.</summary>
    private static readonly TimeSpan BaselineMinimum = TimeSpan.FromSeconds(30);

    private const int MaxKeywords = 20;

    /// <summary>
    /// Noms d'applications et d'écrans génériques : ils ne désignent aucune tâche même quand
    /// rien n'a encore été appris (base neuve).
    /// </summary>
    private static readonly HashSet<string> BuiltInGeneric = new(StringComparer.Ordinal)
    {
        "microsoft", "edge", "google", "chrome", "firefox", "mozilla", "excel", "word", "outlook",
        "powerpoint", "onenote", "teams", "slack", "zoom", "workplace", "explorer", "explorateur",
        "fichiers", "inbox", "boite", "reception", "onglet", "nouvel", "new", "tab", "personnel",
        "profile", "profil", "work", "travail", "notepad", "bloc", "notes", "visual", "studio",
        "channel", "canal", "message", "messages", "mail", "mails", "email", "calendar", "calendrier"
    };

    private readonly TimeSpan _step;
    private long? _taskId;
    private int _consecutive;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private HashSet<string> _proposed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acceptedTitles = new(StringComparer.Ordinal);

    public ActivityShiftDetector(TimeSpan? sampleInterval = null)
    {
        _step = sampleInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Le contexte interdit de proposer (réunion, pause…) : l'hystérésis repart de zéro.</summary>
    public void Silence() => _consecutive = 0;

    /// <summary>La proposition a été affichée : plus rien pendant le délai, et plus jamais ces mots-là pour cette tâche.</summary>
    public void Proposed(Shift shift, DateTime now)
    {
        _cooldownUntil = now + Cooldown;
        _proposed = shift.Keywords.Take(10).ToHashSet(StringComparer.Ordinal);
        _consecutive = 0;
    }

    /// <summary>« Non, je reste » : cette activité fait partie de la tâche en cours.</summary>
    public void Stayed(Shift shift)
    {
        foreach (var w in shift.Keywords) _accepted.Add(w);
        foreach (var s in shift.Samples) _acceptedTitles.Add(s.Title);
    }

    /// <summary>Une évaluation (une par minute). Renvoie le changement à proposer, ou null.</summary>
    public Shift? Evaluate(DateTime now, IReadOnlyList<ActivityProbe.Sample> samples, Context context)
    {
        if (_taskId != context.TaskId) Reset(context.TaskId);
        if (now < _cooldownUntil) { _consecutive = 0; return null; }

        bool IsGeneric(string w) => BuiltInGeneric.Contains(w) || context.IsGeneric(w);

        var observed = samples
            .Where(s => s.At >= context.TaskStart && s.At <= now
                        && !s.Process.Equals("TimeTracker", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Sample: s, Words: TitleWords(s.Title).Where(w => !IsGeneric(w)).ToHashSet(StringComparer.Ordinal)))
            .Where(x => x.Words.Count > 0)
            .ToList();

        var recentFrom = now - RecentWindow;
        var recent = observed.Where(x => x.Sample.At >= recentFrom).ToList();
        if (recent.Count == 0) { _consecutive = 0; return null; }

        // Ce que la tâche « ressemble » : tout ce qui précède les dernières minutes, SAUF la fin
        // de l'épisode en cours. Sans ce retrait, une évaluation retardée (fenêtre ouverte,
        // réunion) verrait le début de la nouvelle activité déjà rangé du côté de la tâche.
        // L'épisode remonte tant que les fenêtres partagent un mot ou un titre dominant (un quart
        // au moins des relevés récents) ; s'il couvre toute la tâche, rien n'a changé.
        int quarter = Math.Max(1, recent.Count / 4);
        var recentWords = recent.SelectMany(x => x.Words).GroupBy(w => w)
                                .Where(g => g.Count() >= quarter).Select(g => g.Key)
                                .ToHashSet(StringComparer.Ordinal);
        var recentTitles = recent.GroupBy(x => x.Sample.Title).Where(g => g.Count() >= quarter)
                                 .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var before = observed.Where(x => x.Sample.At < recentFrom).ToList();
        int cut = before.Count;
        while (cut > 0 && (recentTitles.Contains(before[cut - 1].Sample.Title) || before[cut - 1].Words.Overlaps(recentWords)))
            cut--;
        var baseline = before.Take(cut).ToList();
        if (baseline.Count == 0) { _consecutive = 0; return null; }

        var baselineTitles = baseline.Select(x => x.Sample.Title).ToHashSet(StringComparer.Ordinal);
        int minSamples = (int)Math.Ceiling(BaselineMinimum / _step);
        var nameWords = TaskSuggester.Words(context.TaskName);
        var otherTask = context.NamesOtherTask ?? (_ => false);
        var fitWords = baseline.SelectMany(x => x.Words)
                               .GroupBy(w => w).Where(g => g.Count() >= minSamples).Select(g => g.Key)
                               .Concat(context.LearnedWords)
                               .Where(w => nameWords.Contains(w) || !otherTask(w))
                               .Concat(_accepted)
                               .ToHashSet(StringComparer.Ordinal);

        // Une autre tâche nommée dans la moitié au moins des fenêtres récentes (toujours le même
        // client) l'emporte sur les mots courants que la tâche en cours a pu apprendre
        // (« commande », « facture ») : c'est elle que ces fenêtres désignent. Des mails de
        // clients différents, eux, ne désignent personne — la tâche fourre-tout reste tranquille.
        var dominantOther = recent.SelectMany(x => x.Words.Where(w => otherTask(w) && !nameWords.Contains(w) && !_accepted.Contains(w)))
                                  .GroupBy(w => w)
                                  .Where(g => g.Count() * 2 >= recent.Count)
                                  .OrderByDescending(g => g.Count())
                                  .Select(g => g.Key).FirstOrDefault();

        bool Fits((ActivityProbe.Sample Sample, HashSet<string> Words) x) =>
            baselineTitles.Contains(x.Sample.Title)
            || _acceptedTitles.Contains(x.Sample.Title)
            || ((dominantOther is null || !x.Words.Contains(dominantOther))
                && (x.Words.Overlaps(fitWords)
                    || x.Words.Any(w => nameWords.Any(n => w == n || (n.Length >= 4 && w.StartsWith(n, StringComparison.Ordinal))))));

        int misses = recent.Count(x => !Fits(x));
        bool shifted = misses * _step >= MinMismatch
                       && (double)misses / recent.Count >= MismatchRatio;
        if (!shifted) { _consecutive = 0; return null; }
        if (++_consecutive < HoldEvaluations) return null;

        // Début du changement : la première fenêtre étrangère après le dernier retour d'au moins
        // une minute sur la tâche en cours.
        DateTime? since = null, fitStreak = null;
        foreach (var x in observed)
        {
            if (!Fits(x)) { since ??= x.Sample.At; fitStreak = null; }
            else
            {
                fitStreak ??= x.Sample.At;
                if (x.Sample.At - fitStreak.Value >= FitStreakResets) since = null;
            }
        }
        if (since is null) return null;

        var foreign = observed.Where(x => x.Sample.At >= since && !Fits(x)).ToList();
        var keywords = foreign.SelectMany(x => x.Words)
                              .Where(w => !fitWords.Contains(w))
                              .GroupBy(w => w)
                              .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                              .Select(g => g.Key).Take(MaxKeywords).ToList();

        // Même changement déjà proposé pour cette tâche : on ne le repropose pas.
        if (_proposed.Count > 0 && keywords.Take(10).Count(_proposed.Contains) * 2 >= Math.Min(10, keywords.Count))
            return null;

        var dominant = foreign.GroupBy(x => x.Sample.Title).OrderByDescending(g => g.Count()).First();
        var processes = foreign.GroupBy(x => x.Sample.Process, StringComparer.OrdinalIgnoreCase)
                               .OrderByDescending(g => g.Count())
                               .Select(g => g.Key).Where(p => p.Length > 0).Take(3).ToList();

        return new Shift(since.Value, foreign.Count * _step, dominant.Key, dominant.Count() * _step,
                         keywords, processes, foreign.Select(x => x.Sample).ToList());
    }

    /// <summary>
    /// Mots significatifs d'une activité, les plus présents d'abord : ce qui part à l'IA quand
    /// l'utilisateur ouvre le sélecteur. Jamais un titre entier.
    /// </summary>
    public static IReadOnlyList<string> Keywords(IEnumerable<ActivityProbe.Sample> samples, Func<string, bool> isGeneric) =>
        samples.Where(s => !s.Process.Equals("TimeTracker", StringComparison.OrdinalIgnoreCase))
               .SelectMany(s => TitleWords(s.Title))
               .Where(w => !BuiltInGeneric.Contains(w) && !isGeneric(w))
               .GroupBy(w => w)
               .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
               .Select(g => g.Key).Take(MaxKeywords).ToList();

    private static readonly System.Text.RegularExpressions.Regex EmailAddress =
        new(@"\S+@\S+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Mots d'un titre, adresses e-mail retirées : Outlook met celle de la boîte dans chaque
    /// titre, et une adresse est une donnée personnelle qui ne nomme aucune tâche — elle n'a
    /// rien à faire parmi les mots-clés qui peuvent partir à l'IA.
    /// </summary>
    private static HashSet<string> TitleWords(string title) => TaskSuggester.Words(EmailAddress.Replace(title, " "));

    private void Reset(long taskId)
    {
        _taskId = taskId;
        _consecutive = 0;
        _cooldownUntil = DateTime.MinValue;
        _proposed = new HashSet<string>(StringComparer.Ordinal);
        _accepted.Clear();
        _acceptedTitles.Clear();
    }
}
