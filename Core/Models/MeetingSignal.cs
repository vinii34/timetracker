namespace TimeTracker.Core.Models;

/// <summary>Sonde d'où provient un indice de réunion.</summary>
public enum MeetingSource
{
    /// <summary>Le micro est capté en ce moment par une application de réunion.</summary>
    Microphone,

    /// <summary>Une fenêtre visible ressemble à une réunion (vue Zoom, onglet Meet…).</summary>
    Window,

    /// <summary>L'agenda place une réunion sur ce créneau.</summary>
    Calendar
}

/// <summary>
/// Indice de réunion relevé à un instant donné par une sonde.
/// </summary>
/// <param name="Source">Sonde qui a produit l'indice.</param>
/// <param name="App">Application concernée (« ms-teams.exe », « Zoom.exe », « msedge.exe »…).</param>
/// <param name="Title">Titre de fenêtre exploitable comme libellé, ou null si la sonde n'en a pas.</param>
/// <param name="Conclusive">
/// Vrai si l'indice suffit à conclure « il y a réunion maintenant ». Un onglet « Meet » ouvert
/// ne conclut rien — il traîne souvent toute la journée — alors que le micro capté par Teams, si.
/// Les indices non concluants servent uniquement à <b>nommer</b> la réunion.
/// </param>
/// <param name="Calendar">
/// Réunion d'agenda à l'origine de l'indice, si c'en est un. Portée jusqu'à <c>App</c> parce que
/// l'heure de début inscrite à l'agenda sert à antidater l'entrée, et le sujet à la nommer.
/// </param>
public record MeetingSignal(MeetingSource Source, string App, string? Title, bool Conclusive,
                            CalendarMeeting? Calendar = null);
