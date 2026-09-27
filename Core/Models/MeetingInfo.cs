namespace TimeTracker.Core.Models;

/// <summary>
/// Une réunion détectée, du premier indice jusqu'à sa disparition.
/// Mutable : le titre exploitable arrive souvent après le début (l'onglet Meet ne prend son
/// nom qu'une fois la réunion rejointe), et la fin n'est connue qu'au dernier moment.
/// </summary>
public sealed class MeetingInfo
{
    /// <summary>
    /// Premier instant où la réunion a été vue — pas l'instant de la bascule : le délai de
    /// confirmation est antidaté pour ne pas perdre la première minute de réunion.
    /// </summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Instant où les indices ont disparu (hors délai de grâce), null tant qu'elle dure.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Meilleur titre relevé pendant la réunion, s'il y en a eu un.</summary>
    public string? Title { get; set; }

    /// <summary>Applications ayant motivé la détection, pour le journal et le diagnostic.</summary>
    public string Apps { get; set; } = "";

    /// <summary>
    /// Réunion d'agenda correspondante, si l'agenda en connaissait une sur ce créneau.
    /// Sert à deux choses dans <c>App</c> : antidater le début à l'heure prévue, et nommer.
    /// </summary>
    public CalendarMeeting? Calendar { get; set; }

    /// <summary>Libellé affichable : le titre si on en a un, sinon les applications détectées.</summary>
    public string Label => string.IsNullOrWhiteSpace(Title) ? Apps : Title!;

    public TimeSpan Duration => (EndedAt ?? DateTime.Now) - StartedAt;
}
