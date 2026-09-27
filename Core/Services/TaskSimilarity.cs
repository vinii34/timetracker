using System.Globalization;
using System.Text;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Repère les tâches qui sont probablement la même sous deux noms : coquille (« Conflig Flow »
/// / « Config Flow »), majuscule ou accent (« Debug Dornac » / « Débug Dornac »), espace ou
/// ponctuation en plus (« ORVANE Groupe / Contoso » / « ORVANE Groupe/Contoso », les deux venus de deux
/// sujets d'agenda). Tout se fait sur le poste, sans envoyer un seul nom de tâche nulle part :
/// la promesse « 100 % local » tient, et un modèle de langage n'apporterait rien ici.
///
/// Deux tâches sont jugées doublons si leurs noms <b>normalisés</b> (minuscules, sans accents,
/// sans ponctuation, espaces réduits, chiffres détachés) sont égaux, ou si leur distance
/// d'édition rapportée à la longueur reste sous un seuil. Le seuil est volontairement serré :
/// un faux doublon proposé coûte une fusion à refuser, un vrai raté ne coûte rien.
/// </summary>
public static class TaskSimilarity
{
    /// <summary>Deux tâches vraisemblablement identiques, et la ressemblance (0 → 1).</summary>
    public sealed record Duplicate(TaskItem A, TaskItem B, double Similarity);

    /// <summary>Ressemblance minimale pour proposer la fusion.</summary>
    public const double Threshold = 0.85;

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

    /// <summary>Ressemblance entre deux noms (1 = identiques après normalisation).</summary>
    public static double Similarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;
        int longest = Math.Max(na.Length, nb.Length);
        return 1.0 - (double)Levenshtein(na, nb) / longest;
    }

    /// <summary>Paires de doublons probables, les plus ressemblantes d'abord.</summary>
    public static List<Duplicate> FindDuplicates(IReadOnlyList<TaskItem> tasks, double threshold = Threshold)
    {
        var result = new List<Duplicate>();
        for (int i = 0; i < tasks.Count; i++)
        {
            for (int j = i + 1; j < tasks.Count; j++)
            {
                double s = Similarity(tasks[i].Name, tasks[j].Name);
                if (s >= threshold) result.Add(new Duplicate(tasks[i], tasks[j], s));
            }
        }
        return result.OrderByDescending(d => d.Similarity).ToList();
    }

    private static int Levenshtein(string s, string t)
    {
        var prev = new int[t.Length + 1];
        var cur = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) prev[j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[t.Length];
    }
}
