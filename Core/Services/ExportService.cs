using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Export des entrées de temps vers CSV (ouvrable directement dans Excel FR) et vers
/// un vrai classeur .xlsx (détail + récapitulatif par tâche + croisé jour×tâche).
/// </summary>
public static class ExportService
{
    /// <summary>Une ligne agrégée « tâche → durée » du récapitulatif.</summary>
    public record TaskTotal(string TaskName, TimeSpan Total);

    /// <summary>Durée au format <c>7h05</c>.</summary>
    public static string FormatHm(TimeSpan t) => $"{(int)t.TotalHours}h{t.Minutes:00}";

    /// <summary>Durée en heures décimales, arrondie au centième (format timesheet).</summary>
    public static double DecimalHours(TimeSpan t) => Math.Round(t.TotalHours, 2);

    public static List<TaskTotal> TotalsByTask(IEnumerable<TimeEntry> entries) =>
        entries
            .GroupBy(e => e.TaskName)
            .Select(g => new TaskTotal(g.Key, TimeSpan.FromSeconds(g.Sum(e => e.Elapsed.TotalSeconds))))
            .OrderByDescending(t => t.Total)
            .ToList();

    /// <summary>Nom de fichier suggéré, sans extension (ex. <c>TimeTracker_2026-07-20_2026-07-26</c>).</summary>
    public static string SuggestedFileName(DateTime from, DateTime toInclusive) =>
        from.Date == toInclusive.Date
            ? $"TimeTracker_{from:yyyy-MM-dd}"
            : $"TimeTracker_{from:yyyy-MM-dd}_{toInclusive:yyyy-MM-dd}";

    // ------------------------------------------------------------------- CSV

    /// <summary>
    /// Écrit un CSV séparé par <c>;</c> en UTF-8 avec BOM (Excel FR l'ouvre sans assistant
    /// d'import). Bloc détail, puis bloc récapitulatif par tâche.
    /// </summary>
    public static void ExportCsv(IReadOnlyList<TimeEntry> entries, string path)
    {
        var culture = CultureInfo.CurrentCulture;
        var sb = new StringBuilder();

        sb.AppendLine("Date;Début;Fin;Tâche;Durée;Heures;Réunion;Notes");
        foreach (var e in entries)
        {
            sb.Append(Csv(e.StartedAt.ToString("yyyy-MM-dd"))).Append(';')
              .Append(Csv(e.StartedAt.ToString("HH:mm"))).Append(';')
              .Append(Csv(e.EndedAt?.ToString("HH:mm") ?? "")).Append(';')
              .Append(Csv(e.TaskName)).Append(';')
              .Append(Csv(FormatHm(e.Elapsed))).Append(';')
              .Append(Csv(DecimalHours(e.Elapsed).ToString(culture))).Append(';')
              .Append(e.IsMeeting ? "oui" : "non").Append(';')
              .Append(Csv(e.Notes ?? ""))
              .AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Total par tâche;Durée;Heures");
        var totals = TotalsByTask(entries);
        foreach (var t in totals)
        {
            sb.Append(Csv(t.TaskName)).Append(';')
              .Append(Csv(FormatHm(t.Total))).Append(';')
              .Append(Csv(DecimalHours(t.Total).ToString(culture)))
              .AppendLine();
        }

        var grand = TimeSpan.FromSeconds(entries.Sum(e => e.Elapsed.TotalSeconds));
        sb.Append("TOTAL;")
          .Append(Csv(FormatHm(grand))).Append(';')
          .Append(Csv(DecimalHours(grand).ToString(culture)))
          .AppendLine();

        // BOM explicite : sans lui Excel lit l'UTF-8 comme de l'ANSI (accents cassés).
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Échappe un champ CSV (guillemets doublés si <c>;</c>, guillemet ou saut de ligne).</summary>
    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    // ------------------------------------------------------------------ XLSX

    /// <summary>
    /// Écrit un classeur Excel : feuille « Détail », feuille « Par tâche », et — si la période
    /// couvre plus d'un jour — feuille « Par jour » (croisé tâche × jour, en heures décimales).
    /// </summary>
    public static void ExportXlsx(IReadOnlyList<TimeEntry> entries, string path, DateTime from, DateTime toInclusive)
    {
        using var wb = new XLWorkbook();

        WriteDetailSheet(wb, entries, from, toInclusive);
        WriteTotalsSheet(wb, entries);
        if (from.Date != toInclusive.Date) WritePivotSheet(wb, entries, from, toInclusive);

        wb.SaveAs(path);
    }

    private static void WriteDetailSheet(XLWorkbook wb, IReadOnlyList<TimeEntry> entries,
        DateTime from, DateTime toInclusive)
    {
        var ws = wb.AddWorksheet("Détail");
        ws.Cell(1, 1).Value = from.Date == toInclusive.Date
            ? $"TimeTracker — {AppCulture.LongDate(from)}"
            : $"TimeTracker — du {AppCulture.MediumDate(from)} au {AppCulture.MediumDate(toInclusive)}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;

        var headers = new[] { "Date", "Début", "Fin", "Tâche", "Durée", "Heures", "Réunion", "Notes" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(3, c + 1).Value = headers[c];
        StyleHeader(ws.Row(3), headers.Length);

        int row = 4;
        foreach (var e in entries)
        {
            ws.Cell(row, 1).Value = e.StartedAt.Date;
            ws.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(row, 2).Value = e.StartedAt.ToString("HH:mm");
            ws.Cell(row, 3).Value = e.EndedAt?.ToString("HH:mm") ?? "en cours";
            ws.Cell(row, 4).Value = e.TaskName;
            ws.Cell(row, 5).Value = FormatHm(e.Elapsed);
            ws.Cell(row, 6).Value = DecimalHours(e.Elapsed);
            ws.Cell(row, 6).Style.NumberFormat.Format = "0.00";
            ws.Cell(row, 7).Value = e.IsMeeting ? "oui" : "non";
            ws.Cell(row, 8).Value = e.Notes ?? "";
            row++;
        }

        if (entries.Count > 0)
        {
            ws.Cell(row, 4).Value = "TOTAL";
            var grand = TimeSpan.FromSeconds(entries.Sum(e => e.Elapsed.TotalSeconds));
            ws.Cell(row, 5).Value = FormatHm(grand);
            ws.Cell(row, 6).Value = DecimalHours(grand);
            ws.Cell(row, 6).Style.NumberFormat.Format = "0.00";
            ws.Range(row, 1, row, headers.Length).Style.Font.Bold = true;
            ws.Range(row, 1, row, headers.Length).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        ws.SheetView.FreezeRows(3);
        ws.Columns(1, headers.Length).AdjustToContents();
    }

    private static void WriteTotalsSheet(XLWorkbook wb, IReadOnlyList<TimeEntry> entries)
    {
        var ws = wb.AddWorksheet("Par tâche");
        var headers = new[] { "Tâche", "Durée", "Heures", "Part" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        StyleHeader(ws.Row(1), headers.Length);

        var totals = TotalsByTask(entries);
        double grandSeconds = entries.Sum(e => e.Elapsed.TotalSeconds);

        int row = 2;
        foreach (var t in totals)
        {
            ws.Cell(row, 1).Value = t.TaskName;
            ws.Cell(row, 2).Value = FormatHm(t.Total);
            ws.Cell(row, 3).Value = DecimalHours(t.Total);
            ws.Cell(row, 3).Style.NumberFormat.Format = "0.00";
            ws.Cell(row, 4).Value = grandSeconds > 0 ? t.Total.TotalSeconds / grandSeconds : 0;
            ws.Cell(row, 4).Style.NumberFormat.Format = "0.0 %";
            row++;
        }

        if (totals.Count > 0)
        {
            var grand = TimeSpan.FromSeconds(grandSeconds);
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 2).Value = FormatHm(grand);
            ws.Cell(row, 3).Value = DecimalHours(grand);
            ws.Cell(row, 3).Style.NumberFormat.Format = "0.00";
            ws.Range(row, 1, row, headers.Length).Style.Font.Bold = true;
            ws.Range(row, 1, row, headers.Length).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents();
    }

    private static void WritePivotSheet(XLWorkbook wb, IReadOnlyList<TimeEntry> entries,
        DateTime from, DateTime toInclusive)
    {
        var ws = wb.AddWorksheet("Par jour");
        var days = EachDay(from, toInclusive).ToList();
        var tasks = TotalsByTask(entries).Select(t => t.TaskName).ToList();

        ws.Cell(1, 1).Value = "Tâche";
        for (int d = 0; d < days.Count; d++)
        {
            ws.Cell(1, d + 2).Value = AppCulture.ShortDay(days[d]);
            ws.Column(d + 2).Width = 11;
        }
        ws.Cell(1, days.Count + 2).Value = "Total";
        StyleHeader(ws.Row(1), days.Count + 2);

        int row = 2;
        foreach (var task in tasks)
        {
            ws.Cell(row, 1).Value = task;
            double taskTotal = 0;
            for (int d = 0; d < days.Count; d++)
            {
                double seconds = entries
                    .Where(e => e.TaskName == task && e.StartedAt.Date == days[d])
                    .Sum(e => e.Elapsed.TotalSeconds);
                taskTotal += seconds;
                if (seconds > 0)
                {
                    ws.Cell(row, d + 2).Value = Math.Round(seconds / 3600.0, 2);
                    ws.Cell(row, d + 2).Style.NumberFormat.Format = "0.00";
                }
            }
            ws.Cell(row, days.Count + 2).Value = Math.Round(taskTotal / 3600.0, 2);
            ws.Cell(row, days.Count + 2).Style.NumberFormat.Format = "0.00";
            ws.Cell(row, days.Count + 2).Style.Font.Bold = true;
            row++;
        }

        // Ligne des totaux journaliers.
        ws.Cell(row, 1).Value = "TOTAL";
        for (int d = 0; d < days.Count; d++)
        {
            double seconds = entries.Where(e => e.StartedAt.Date == days[d]).Sum(e => e.Elapsed.TotalSeconds);
            if (seconds <= 0) continue;              // jour sans activité : case laissée vide
            ws.Cell(row, d + 2).Value = Math.Round(seconds / 3600.0, 2);
            ws.Cell(row, d + 2).Style.NumberFormat.Format = "0.00";
        }
        ws.Cell(row, days.Count + 2).Value = Math.Round(entries.Sum(e => e.Elapsed.TotalSeconds) / 3600.0, 2);
        ws.Cell(row, days.Count + 2).Style.NumberFormat.Format = "0.00";
        ws.Range(row, 1, row, days.Count + 2).Style.Font.Bold = true;
        ws.Range(row, 1, row, days.Count + 2).Style.Border.TopBorder = XLBorderStyleValues.Thin;

        ws.SheetView.FreezeColumns(1);
        ws.Column(1).AdjustToContents();
    }

    private static void StyleHeader(IXLRow row, int columnCount)
    {
        var range = row.Cells(1, columnCount);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromArgb(0xE8, 0xEF, 0xF7);
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    public static IEnumerable<DateTime> EachDay(DateTime from, DateTime toInclusive)
    {
        for (var d = from.Date; d <= toInclusive.Date; d = d.AddDays(1))
            yield return d;
    }
}
