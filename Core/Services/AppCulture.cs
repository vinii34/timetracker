using System.Globalization;

namespace TimeTracker.Core.Services;

/// <summary>
/// Sépare deux usages de la culture, qui n'ont pas les mêmes contraintes :
/// <list type="bullet">
/// <item>les <b>libellés</b> (noms de jours et de mois) suivent la langue de l'application,
/// le français, quel que soit le réglage Windows ;</item>
/// <item>les <b>nombres</b> exportés restent sur <see cref="CultureInfo.CurrentCulture"/>,
/// car Excel lit les décimales selon le même réglage régional — forcer le français
/// produirait « 1,5 » dans un Excel anglais, qui le prendrait pour du texte.</item>
/// </list>
/// </summary>
public static class AppCulture
{
    /// <summary>Culture utilisée pour tout ce qui est affiché en toutes lettres.</summary>
    public static readonly CultureInfo Display = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Ex. « lundi 20 juillet 2026 ».</summary>
    public static string LongDate(DateTime d) => d.ToString("dddd d MMMM yyyy", Display);

    /// <summary>Ex. « 20 juillet 2026 ».</summary>
    public static string MediumDate(DateTime d) => d.ToString("d MMMM yyyy", Display);

    /// <summary>Ex. « 20 juillet ».</summary>
    public static string DayMonth(DateTime d) => d.ToString("d MMMM", Display);

    /// <summary>Ex. « lun. 20/07 » (en-têtes de colonnes du tableau croisé).</summary>
    public static string ShortDay(DateTime d) => d.ToString("ddd dd/MM", Display);
}
