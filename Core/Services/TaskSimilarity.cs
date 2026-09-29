using System.Globalization;
using System.Text;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Repère les tâches qui sont probablement la même sous deux noms, et les fautes de frappe dans
/// un nom. Tout se fait sur le poste, sans envoyer un seul nom de tâche nulle part.
///
/// <b>La comparaison se fait mot à mot</b>, plus sur le nom entier (refait le 2026-09-29).
/// Rejouée sur les 246 vrais noms de l'utilisateur, la distance d'édition du nom complet
/// proposait des paires fausses et ratait les vraies, pour la même raison : ses noms suivent des
/// gabarits (« Suivi projet facturation électronique &lt;client&gt;/Contoso », « Adaptation
/// mapping EDI &lt;client&gt; »), si bien que deux clients différents ne diffèrent
/// que de quelques lettres sur soixante — alors qu'une faute de frappe, elle, tient dans <i>un</i>
/// mot (« Onborading », « Conflig », « Paramettrage »). D'où trois règles :
///
/// — chaque mot de l'un doit se retrouver dans l'autre, tel quel ou à une faute près : un mot
///   entièrement différent (« Velmora » / « Kestrio », « Outgoing » / « Incoming ») fait deux
///   tâches ; un nombre doit être identique (« Flow1 » / « Flow2 ») ;
/// — le préfixe de réunion (« Réunion — ») est ignoré quand le reste est assez long pour être un
///   sujet (« Réunion — Point Hebdo PRJ » ≈ « Point Hebdo PRJ »), pas quand il ne reste qu'un
///   nom de client (« Réunion Orvane » est une réunion, « Orvane » du travail) ;
/// — une faute se juge selon la longueur du mot : une lettre de plus ou de moins, ou deux lettres
///   inversées, sur un mot court ; une substitution à partir de six lettres ; deux fautes à partir
///   de dix. « data » / « date » ne sont pas une faute.
/// </summary>
public static class TaskSimilarity
{
    /// <summary>Deux tâches vraisemblablement identiques, et la ressemblance (0 → 1, pour le classement).</summary>
    public sealed record Duplicate(TaskItem A, TaskItem B, double Similarity);

    /// <summary>Un nom corrigé mot à mot, et ce qui a été corrigé (« Onborading » → « Onboarding »).</summary>
    public sealed record Correction(string Name, IReadOnlyList<(string From, string To)> Words);

    /// <summary>Préfixe de réunion par défaut (réglage <c>MeetingTaskName</c>).</summary>
    public const string DefaultMeetingName = "Réunion";

    /// <summary>
    /// Forme canonique d'un nom : c'est sur elle que se comparent les tâches. Publique parce que
    /// <c>--selftest</c> la vérifie sur les cas réellement rencontrés.
    /// </summary>
    public static string Normalize(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;   // accents
            if (char.IsLetterOrDigit(ch))
            {
                // « Flow1 » et « Flow 1 » sont la même chose : un chiffre collé se détache.
                if (sb.Length > 0 && !pendingSpace
                    && char.IsDigit(ch) != char.IsDigit(sb[^1]) && sb[^1] != ' ')
                    sb.Append(' ');
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;   // espace, ponctuation, symbole : un seul séparateur
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Les deux noms désignent-ils vraisemblablement la même tâche ? <paramref name="similarity"/>
    /// ne sert qu'à classer les paires : 1 pour deux noms égaux à la ponctuation près.
    /// </summary>
    public static bool IsDuplicate(string a, string b, out double similarity,
                                   string meetingName = DefaultMeetingName)
    {
        var meeting = MeetingWords(meetingName);
        return IsDuplicate(Prepare(a, meeting), Prepare(b, meeting), out similarity);
    }

    /// <summary>Un nom prêt à comparer : fait une fois par tâche, pas une fois par paire.</summary>
    private sealed record Prepared(string Normalized, List<string> Words, bool Meeting);

    private static string[] MeetingWords(string meetingName) =>
        Normalize(meetingName).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static Prepared Prepare(string name, string[] meeting)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0) return new Prepared("", new List<string>(), false);
        var (words, isMeeting) = StripMeeting(normalized.Split(' '), meeting);
        return new Prepared(normalized, words, isMeeting);
    }

    private static bool IsDuplicate(Prepared a, Prepared b, out double similarity)
    {
        similarity = 0;
        if (a.Normalized.Length == 0 || b.Normalized.Length == 0) return false;
        if (a.Normalized == b.Normalized) { similarity = 1; return true; }

        var (ta, ma) = (a.Words, a.Meeting);
        var (tb, mb) = (b.Words, b.Meeting);
        if (ta.Count == 0 || tb.Count == 0) return false;
        if (Math.Abs(ta.Count - tb.Count) > 1) return false;
        // Réunion d'un côté seulement : il faut un vrai sujet derrière, pas un nom de client seul.
        if (ma != mb && Math.Min(ta.Count, tb.Count) < 3) return false;
        // « EuroVelmora » / « Euro Velmora » : mêmes lettres, découpage différent.
        if (string.Concat(ta) == string.Concat(tb)) { similarity = 0.99; return true; }

        var (shorter, longer) = ta.Count <= tb.Count ? (ta, tb) : (tb, ta);
        // Un sujet d'agenda long gagne parfois un mot (« AR: Point hebdo… ») : toléré à
        // partir de cinq mots, jamais sur un nom court où ce mot serait le client ou le sens.
        int extraAllowed = shorter.Count >= 5 ? 1 : 0;
        if (longer.Count - shorter.Count > extraAllowed) return false;

        var used = new bool[longer.Count];
        var pending = new List<string>();
        foreach (var word in shorter)
        {
            int i = FindUnused(longer, used, w => w == word);
            if (i >= 0) used[i] = true; else pending.Add(word);
        }

        int edits = 0;
        foreach (var word in pending)
        {
            int best = -1, bestDistance = int.MaxValue;
            for (int i = 0; i < longer.Count; i++)
            {
                if (used[i] || !IsNearWord(word, longer[i], allowPlural: true)) continue;
                int d = Distance(word, longer[i]);
                if (d < bestDistance) { best = i; bestDistance = d; }
            }
            if (best < 0) return false;
            used[best] = true;
            edits += bestDistance;
        }

        int unmatched = used.Count(u => !u);
        if (unmatched > extraAllowed) return false;

        int letters = Math.Max(1, shorter.Sum(w => w.Length));
        similarity = Math.Max(0.5, 0.98 - (double)edits / letters - 0.05 * unmatched);
        return true;
    }

    /// <summary>Paires de doublons probables, les plus ressemblantes d'abord.</summary>
    public static List<Duplicate> FindDuplicates(IReadOnlyList<TaskItem> tasks,
                                                 string meetingName = DefaultMeetingName)
    {
        var meeting = MeetingWords(meetingName);
        var prepared = tasks.Select(t => Prepare(t.Name, meeting)).ToList();
        var result = new List<Duplicate>();
        for (int i = 0; i < tasks.Count; i++)
            for (int j = i + 1; j < tasks.Count; j++)
                if (IsDuplicate(prepared[i], prepared[j], out var s))
                    result.Add(new Duplicate(tasks[i], tasks[j], s));
        return result.OrderByDescending(d => d.Similarity).ToList();
    }

    /// <summary>
    /// Corrige les fautes de frappe évidentes d'un nom d'après le vocabulaire des autres tâches :
    /// un mot rare (au plus <paramref name="maxRareCount"/> tâches) à une faute d'un mot employé
    /// dans au moins trois tâches — et deux fois plus que lui — est remplacé par celui-ci, dans
    /// l'orthographe la plus fréquente (« Onborading » → « Onboarding »). Un pluriel n'est pas
    /// une faute. Null si rien à corriger.
    /// </summary>
    /// <param name="others">Noms des autres tâches (sans celle qu'on corrige).</param>
    /// <param name="maxRareCount">
    /// Nombre de tâches au-delà duquel un mot est « connu » et n'est plus corrigé : 0 pour un nom
    /// en cours de saisie (le mot n'existe encore nulle part), 1 ou 2 pour une tâche existante.
    /// </param>
    public static Correction? CorrectTypos(string name, IEnumerable<string> others, int maxRareCount = 1) =>
        CorrectTypos(name, Vocabulary.Of(others), maxRareCount, includesName: false);

    /// <param name="includesName">
    /// Le vocabulaire a été construit sur toutes les tâches, celle-ci comprise : ses propres mots
    /// sont décomptés (un vocabulaire pour toute la liste plutôt qu'un par tâche).
    /// </param>
    public static Correction? CorrectTypos(string name, Vocabulary vocabulary, int maxRareCount, bool includesName)
    {
        var own = includesName
            ? Split(name).Where(t => t.IsWord).Select(t => Normalize(t.Token)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var fixes = new List<(string From, string To)>();
        var sb = new StringBuilder();
        foreach (var (token, isWord) in Split(name))
        {
            var replacement = isWord ? FixWord(token, vocabulary, maxRareCount, own) : null;
            if (replacement != null) fixes.Add((token, replacement));
            sb.Append(replacement ?? token);
        }
        return fixes.Count == 0 ? null : new Correction(sb.ToString(), fixes);
    }

    // ------------------------------------------------------------------ Mots

    /// <summary>Un mot du vocabulaire : dans combien de tâches, et son orthographe la plus fréquente.</summary>
    internal sealed record Known(int Count, string Spelling);

    /// <summary>Les mots des noms de tâches : dans combien de tâches chacun, et comment il s'écrit le plus souvent.</summary>
    public sealed class Vocabulary
    {
        internal Dictionary<string, Known> Words { get; }

        private Vocabulary(Dictionary<string, Known> words) => Words = words;

        public static Vocabulary Of(IEnumerable<string> names)
        {
            var tasks = new Dictionary<string, int>(StringComparer.Ordinal);
            var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (token, isWord) in Split(name))
                {
                    if (!isWord) continue;
                    var key = Normalize(token);
                    if (key.Length == 0 || key.Contains(' ')) continue;
                    if (seen.Add(key)) tasks[key] = tasks.GetValueOrDefault(key) + 1;
                    if (!spellings.TryGetValue(key, out var forms)) spellings[key] = forms = new();
                    forms[token] = forms.GetValueOrDefault(token) + 1;
                }
            }
            return new Vocabulary(tasks.ToDictionary(t => t.Key,
                t => new Known(t.Value, spellings[t.Key].OrderByDescending(f => f.Value).First().Key),
                StringComparer.Ordinal));
        }
    }

    private static string? FixWord(string token, Vocabulary vocabulary, int maxRareCount, HashSet<string> own)
    {
        var key = Normalize(token);
        if (key.Length < 4 || key.Contains(' ') || key.Any(char.IsDigit)) return null;
        int Count(string word, Known known) => known.Count - (own.Contains(word) ? 1 : 0);
        int rare = vocabulary.Words.TryGetValue(key, out var k) ? Count(key, k) : 0;
        if (rare > maxRareCount) return null;

        Known? best = null;
        int bestDistance = int.MaxValue, bestCount = 0;
        foreach (var (word, known) in vocabulary.Words)
        {
            int count = Count(word, known);
            if (count < 3 || count < 2 * Math.Max(1, rare)) continue;
            if (!IsNearWord(key, word, allowPlural: false)) continue;
            int d = Distance(key, word);
            if (d < bestDistance || (d == bestDistance && count > bestCount))
            {
                best = known;
                bestDistance = d;
                bestCount = count;
            }
        }
        return best is null ? null : MatchCase(token, best.Spelling);
    }

    /// <summary>
    /// La correction garde la casse de ce qui a été tapé : « suppor » → « support » même si le
    /// mot s'écrit le plus souvent « Support » ailleurs ; « Onborading » → « Onboarding ».
    /// </summary>
    private static string MatchCase(string typed, string spelling)
    {
        if (typed.All(c => !char.IsLetter(c) || char.IsLower(c))) return spelling.ToLowerInvariant();
        if (typed.Length > 1 && typed.All(c => !char.IsLetter(c) || char.IsUpper(c))) return spelling.ToUpperInvariant();
        return char.IsUpper(typed[0]) ? char.ToUpperInvariant(spelling[0]) + spelling[1..] : spelling;
    }

    /// <summary>Découpe un nom en mots et séparateurs, pour le recomposer à l'identique hors des corrections.</summary>
    private static IEnumerable<(string Token, bool IsWord)> Split(string name)
    {
        int i = 0;
        while (i < name.Length)
        {
            bool word = char.IsLetterOrDigit(name[i]);
            int j = i;
            while (j < name.Length && char.IsLetterOrDigit(name[j]) == word) j++;
            yield return (name[i..j], word);
            i = j;
        }
    }

    /// <summary>
    /// Deux mots normalisés à une faute de frappe près. Les nombres et les mots de moins de
    /// quatre lettres doivent être identiques ; « data » / « date » (substitution sur un mot court)
    /// ne sont pas une faute, « Tesal » / « Tessal » et « Ovrane » / « Orvane » en sont.
    /// </summary>
    public static bool IsNearWord(string a, string b, bool allowPlural)
    {
        if (a == b) return true;
        if (a.Length < 4 || b.Length < 4) return false;
        if (a.Any(char.IsDigit) || b.Any(char.IsDigit)) return false;
        if (!allowPlural && (IsPlural(a, b) || IsEPrefixed(a, b))) return false;

        int shortest = Math.Min(a.Length, b.Length);
        int d = Distance(a, b);
        // Deux fautes seulement sur un mot long : à huit lettres, un nom propre (« Inferbal »)
        // peut n'être qu'à deux substitutions de « internal ».
        if (shortest >= 10) return d <= 2;
        if (shortest >= 6) return d <= 1;
        if (d != 1) return false;
        // Mot court : une lettre en plus ou en moins, ou deux lettres inversées — pas une
        // substitution, qui change trop souvent le mot (« test » / « text »).
        return a.Length != b.Length || a.OrderBy(c => c).SequenceEqual(b.OrderBy(c => c));
    }

    private static bool IsPlural(string a, string b)
    {
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        return l.Length == s.Length + 1 && l.StartsWith(s, StringComparison.Ordinal) && (l[^1] == 's' || l[^1] == 'x');
    }

    /// <summary>« ereporting » est « e-reporting » écrit d'un bloc, pas une faute de « reporting ».</summary>
    private static bool IsEPrefixed(string a, string b) =>
        (a.Length == b.Length + 1 && a[0] == 'e' && a.EndsWith(b, StringComparison.Ordinal))
        || (b.Length == a.Length + 1 && b[0] == 'e' && b.EndsWith(a, StringComparison.Ordinal));

    private static (List<string> Words, bool Meeting) StripMeeting(string[] words, string[] meeting)
    {
        bool prefixed = meeting.Length > 0 && words.Length >= meeting.Length
                        && meeting.Select((w, i) => words[i] == w).All(x => x);
        return (prefixed ? words.Skip(meeting.Length).ToList() : words.ToList(), prefixed);
    }

    private static int FindUnused(List<string> words, bool[] used, Func<string, bool> match)
    {
        for (int i = 0; i < words.Count; i++)
            if (!used[i] && match(words[i])) return i;
        return -1;
    }

    /// <summary>Distance d'édition avec inversion de deux lettres voisines (« Onborading » → 1).</summary>
    private static int Distance(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= t.Length; j++) d[0, j] = j;
        for (int i = 1; i <= s.Length; i++)
        {
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[s.Length, t.Length];
    }
}
