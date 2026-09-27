namespace TimeTracker.Core.Models;

/// <summary>Réponse de l'utilisateur à l'invitation, telle que l'agenda la connaît.</summary>
public enum CalendarResponse
{
    Inconnue,
    Organisateur,
    Acceptee,
    Provisoire,
    Refusee,
    SansReponse
}

/// <summary>
/// Une réunion lue dans l'agenda, indépendamment de la façon dont on l'a lue (COM Outlook
/// aujourd'hui, Microsoft Graph un jour) : c'est le seul type que le reste de l'appli connaît.
/// </summary>
public sealed class CalendarMeeting
{
    /// <summary>
    /// Identifiant stable d'une occurrence. Sert à se souvenir de ce que l'utilisateur a
    /// répondu : sans lui, on reposerait la question à chaque tour d'interrogation.
    /// ⚠️ Pour une réunion récurrente, l'identifiant de l'occurrence doit changer d'une semaine
    /// à l'autre, sinon accepter le point hebdo une fois vaudrait acceptation pour toujours.
    /// </summary>
    public required string Id { get; init; }

    public required string Subject { get; init; }

    public DateTime Start { get; init; }

    public DateTime End { get; init; }

    public string Organizer { get; init; } = "";

    /// <summary>
    /// Ce que l'utilisateur a répondu dans Outlook. <b>Volontairement non bloquant</b> : il
    /// assiste parfois à des réunions qu'il a refusées, et refuse parfois par erreur. Cette
    /// valeur sert à décider s'il faut <i>poser la question</i>, jamais à écarter une réunion
    /// que la détection automatique a bel et bien vue.
    /// </summary>
    public CalendarResponse Response { get; init; } = CalendarResponse.Inconnue;

    /// <summary>Les journées entières ne sont pas des réunions : congés, rappels, « focus time ».</summary>
    public bool AllDay { get; init; }

    public bool Covers(DateTime at) => at >= Start && at < End;

    public override string ToString() =>
        $"{Start:HH:mm}–{End:HH:mm} « {Subject} » ({Response}" + (AllDay ? ", journée" : "") + ")";
}
