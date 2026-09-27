namespace TimeTracker.Core.Models;

/// <summary>Pourquoi on demande à l'utilisateur de trancher entre des réunions d'agenda.</summary>
public enum MeetingChoiceKind
{
    /// <summary>Aucune réunion en cours : un ou plusieurs créneaux commencent, faut-il pointer ?</summary>
    Start,

    /// <summary>
    /// Une réunion est en cours (le micro a mordu) sans réunion d'agenda attachée, et plusieurs
    /// créneaux la couvrent : laquelle est-ce ? Ne sert qu'à nommer, jamais à basculer.
    /// </summary>
    Name,

    /// <summary>
    /// Une réunion d'agenda est en cours et d'autres créneaux se présentent : y passer, ou
    /// rester ? Posé quand les créneaux se chevauchent, ou quand plusieurs se suivent à la fois.
    /// </summary>
    Switch
}

/// <summary>
/// Question posée à l'utilisateur quand l'agenda ne suffit pas à décider seul. Émise par
/// <c>MeetingDetector</c>, affichée par <c>App</c>, et rendue au détecteur avec la réponse
/// (<c>MeetingDetector.Resolve</c>) — c'est lui qui en tire les conséquences.
/// </summary>
/// <param name="Kind">Situation qui motive la question.</param>
/// <param name="Candidates">Créneaux entre lesquels choisir, dans l'ordre de préférence de l'agenda.</param>
/// <param name="Current">Réunion d'agenda en cours, pour <see cref="MeetingChoiceKind.Switch"/> ; sinon null.</param>
/// <param name="CurrentOver">
/// Vrai si <paramref name="Current"/> est terminée à l'agenda alors que la réunion continue :
/// on ne demande plus « y passer ? » mais « laquelle est-ce maintenant ? ».
/// </param>
public sealed record MeetingChoice(MeetingChoiceKind Kind,
                                   IReadOnlyList<CalendarMeeting> Candidates,
                                   CalendarMeeting? Current = null,
                                   bool CurrentOver = false);
