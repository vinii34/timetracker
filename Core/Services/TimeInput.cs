using System.Globalization;

namespace TimeTracker.Core.Services;

/// <summary>
/// Lecture des heures saisies à la main dans les fenêtres de correction.
/// Tolère les formes courantes : <c>9:05</c>, <c>09:05</c>, <c>9h05</c>, <c>0905</c>.
/// </summary>
public static class TimeInput
{
    private static readonly string[] Formats = { @"h\:mm", @"hh\:mm", @"h\:m", @"hh\:m" };

    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = text.Trim().Replace('h', ':').Replace('H', ':').TrimEnd(':');
        if (normalized.Length == 4 && normalized.All(char.IsDigit))
            normalized = normalized[..2] + ":" + normalized[2..];
        else if (normalized.Length is 1 or 2 && normalized.All(char.IsDigit))
            normalized += ":00";

        return TimeSpan.TryParseExact(normalized, Formats, CultureInfo.InvariantCulture, out time)
               && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    /// <summary>
    /// Combine une heure saisie avec une date de référence. Si le résultat tombe dans le futur
    /// par rapport à <paramref name="notAfter"/>, on recule d'un jour (saisie faite après minuit).
    /// </summary>
    public static DateTime CombineNotAfter(DateTime referenceDate, TimeSpan time, DateTime notAfter)
    {
        var candidate = referenceDate.Date + time;
        if (candidate > notAfter) candidate = candidate.AddDays(-1);
        return candidate;
    }
}
