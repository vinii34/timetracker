using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>Ce qu'une lecture d'agenda a donné, y compris quand elle a échoué.</summary>
/// <param name="Available">Faux si l'agenda n'a pas pu être lu du tout.</param>
/// <param name="Detail">Explication lisible, journalisée et affichée par <c>--outlookprobe</c>.</param>
/// <param name="Meetings">Réunions de la fenêtre demandée, vide si <paramref name="Available"/> est faux.</param>
public sealed record CalendarSnapshot(bool Available, string Detail,
                                      IReadOnlyList<CalendarMeeting> Meetings)
{
    public static CalendarSnapshot Unavailable(string detail) =>
        new(false, detail, Array.Empty<CalendarMeeting>());
}

/// <summary>
/// Source d'agenda. L'interface existe pour deux raisons concrètes :
///
/// — <b>remplacer la façon de lire sans toucher au reste</b> : on commence par le COM de
///   l'Outlook classique (aucune connexion, rien ne sort du poste), et le jour où ce poste
///   passe au « nouveau Outlook », qui n'expose plus de COM, une source Microsoft Graph prend
///   sa place derrière la même interface ;
/// — <b>tester la machine à états</b> : le poste de développement n'a pas de profil Outlook
///   configuré, exactement comme il n'a ni Teams ni Zoom. Une source scriptée permet de dérouler
///   toute la logique dans <c>--selftest</c>, comme <c>ScriptedMeetingProbe</c> pour les réunions.
/// </summary>
public interface ICalendarSource
{
    /// <summary>Nom court, journalisé (« Outlook (COM) »).</summary>
    string Name { get; }

    /// <summary>
    /// Lit les réunions dont le créneau croise l'intervalle demandé.
    /// <b>Peut être lent</b> (appel COM) : à n'appeler que rarement, jamais à chaque tour
    /// d'interrogation. Ne doit jamais lever.
    /// </summary>
    CalendarSnapshot Read(DateTime from, DateTime to);
}
