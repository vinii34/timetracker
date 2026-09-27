using System.IO;
using System.Text;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Journal de diagnostic continu de la détection de réunion, destiné à être relu après
/// plusieurs jours d'usage réel pour ajuster les heuristiques (les motifs de titres ne peuvent
/// pas être devinés sans avoir vu de vraies réunions).
///
/// Trois précautions dictent la forme du fichier :
///
/// — <b>Volume</b> : les sondes sont interrogées toutes les 5 s. Écrire chaque tour ferait des
///   milliers de lignes par jour pour rien. Une ligne n'est écrite que quand le relevé
///   <i>change</i>, plus une respiration périodique qui prouve que l'appli tournait toujours.
///
/// — <b>Confidentialité</b> : ce fichier est fait pour être <i>envoyé</i>, alors que TimeTracker
///   promet que rien ne quitte le poste. Il ne contient donc que les fenêtres des applications
///   de réunion, et les titres de navigateur y sont masqués sauf s'ils ressemblent à une réunion
///   ou que le navigateur capte le micro (voir <see cref="MeetingWindowProbe.CandidateWindows"/>).
///   Le fichier reste du texte brut, relisible et corrigeable avant envoi.
///
/// — <b>Innocuité</b> : rien ici ne doit jamais interrompre le suivi du temps. Toute erreur
///   d'écriture est avalée, et le fichier est plafonné puis recyclé.
/// </summary>
public sealed class MeetingTrace
{
    /// <summary>Ligne écrite même sans changement, pour distinguer « rien ne bouge » de « appli fermée ».</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(15);

    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly object _lock = new();
    private string? _lastSignature;
    private DateTime _lastWrite = DateTime.MinValue;

    public MeetingTrace(string path)
    {
        Path = path;
        Append($"=== Journal de détection de réunion ouvert le {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==="
               + Environment.NewLine
               + "  (une ligne à chaque changement de relevé ; titres de navigateur masqués"
               + " sauf s'ils ressemblent à une réunion)");
    }

    public string Path { get; }

    /// <summary>Note une décision : bascule, fin de réunion, annulation par l'utilisateur.</summary>
    public void Event(string text) => Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] >>> {text}");

    /// <summary>
    /// Relève l'état des sondes. N'écrit que si quelque chose a changé depuis le dernier tour,
    /// ou si la respiration périodique est due.
    /// </summary>
    public void Record(bool verdict, IReadOnlyList<MeetingSignal> signals,
                       IReadOnlyList<MeetingWindowProbe.WindowInfo> windows)
    {
        var mic = signals.Where(s => s.Source == MeetingSource.Microphone)
                         .Select(s => s.App)
                         .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                         .ToList();

        var lines = windows.Select(w => $"    fen | {w.Process} | {w.ClassName} | {w.Title}")
                           .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                           .ToList();

        // Slack fait clignoter un 🔊 dans le titre de sa fenêtre principale quand quelqu'un
        // parle : la signature changeait toutes les 5 s, et un huddle de 18 min a pesé 69
        // relevés (collecte du 2026-08-03). Les pictogrammes ne disent rien sur la réunion :
        // ils sortent de la signature, pas du relevé.
        var signature = $"{verdict}|{string.Join(",", mic)}|" +
                        string.Join("\n", lines.Select(StripSymbols));

        lock (_lock)
        {
            bool changed = signature != _lastSignature;
            if (!changed && DateTime.Now - _lastWrite < Heartbeat) return;
            _lastSignature = signature;
            _lastWrite = DateTime.Now;
        }

        var text = new StringBuilder();
        text.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ")
            .Append(verdict ? "RÉUNION " : "rien    ")
            .Append("micro=[").Append(mic.Count == 0 ? "-" : string.Join(", ", mic)).Append(']');
        foreach (var line in lines) text.Append(Environment.NewLine).Append(line);

        Append(text.ToString());
    }

    /// <summary>Retire les pictogrammes (émojis, symboles) d'une ligne, espaces compris.</summary>
    public static string StripSymbols(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.OtherSymbol
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.Format)
                continue;
            result.Append(rune.ToString());
        }
        return result.ToString().TrimEnd();
    }

    private void Append(string text)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            lock (_lock)
            {
                Recycle();
                // UTF-8 avec BOM : le fichier s'ouvre correctement dans Notepad. StreamWriter
                // n'écrit la marque qu'en tête de fichier, pas à chaque ajout.
                File.AppendAllText(Path, text + Environment.NewLine, new UTF8Encoding(true));
            }
        }
        catch { /* le diagnostic ne doit jamais gêner le suivi du temps */ }
    }

    /// <summary>Repart d'un fichier neuf quand le journal devient gros, en gardant le précédent.</summary>
    private void Recycle()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < MaxBytes) return;

        var previous = Path + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(Path, previous);
    }
}
