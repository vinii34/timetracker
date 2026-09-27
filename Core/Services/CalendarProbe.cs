using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Troisième sonde de <see cref="MeetingDetector"/>, à côté du micro et des fenêtres.
///
/// Le pari d'origine était « le micro déclenche, la fenêtre nomme ». L'agenda s'y insère sans
/// le défaire : <b>le micro déclenche, l'agenda nomme mieux que la fenêtre</b> — et il déclenche
/// lui aussi, mais <b>seulement quand l'utilisateur l'a dit</b>.
///
/// D'où deux natures d'indice pour une même réunion d'agenda :
///
/// — <b>concluant</b>, si l'utilisateur a répondu « oui » à la question posée au début : la
///   réunion est pointée même sans micro (il écoute, il est au téléphone, il a rejoint muet).
/// — <b>qualifiant</b> sinon — refus, absence de réponse, ou question jamais posée. L'agenda ne
///   déclenche alors rien du tout, mais si le micro mord pendant le créneau, c'est le sujet de
///   l'agenda qui nomme la réunion. C'est ce qui rattrape les deux cas que l'utilisateur a
///   décrits : avoir cliqué « non » par erreur, et avoir décidé d'y aller finalement.
///
/// Une conséquence voulue : une réunion <b>acceptée</b> court jusqu'à la fin prévue à l'agenda
/// même si l'utilisateur quitte avant, alors qu'une réunion seulement <b>nommée</b> par l'agenda
/// se termine quand le micro se tait. On ne change pas la règle de fin qui a été validée sur
/// deux semaines d'usage : seule une acceptation explicite la remplace.
///
/// <b>Plusieurs créneaux à la fois.</b> La sonde renvoie <i>tous</i> les créneaux qui couvrent
/// l'instant (collecte du 2026-08-10 au 09-14 : deux réunions aux mêmes horaires « arrivent très
/// souvent »), classés par préférence — acceptés d'abord, refusés en dernier, et entre les deux
/// d'après la réponse donnée dans Outlook. Elle ne tranche pas elle-même : c'est le détecteur qui
/// décide s'il faut poser la question, parce que lui seul sait si une réunion est déjà en cours.
/// </summary>
public sealed class CalendarProbe : IMeetingProbe
{
    /// <summary>
    /// Cadence de relecture de l'agenda. Le détecteur interroge les sondes toutes les 5 s ;
    /// relire Outlook aussi souvent serait absurde (appel COM) et inutile — un agenda ne bouge
    /// pas à la seconde. Entre deux lectures, <see cref="Poll"/> répond depuis le cache.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>Fenêtre lue autour de maintenant : de quoi couvrir la journée de travail.</summary>
    private static readonly TimeSpan LookBehind = TimeSpan.FromHours(4);
    private static readonly TimeSpan LookAhead = TimeSpan.FromHours(8);

    private readonly ICalendarSource _source;
    private readonly TimeSpan _refreshInterval;
    private readonly HashSet<string> _accepted = new();
    private readonly HashSet<string> _refused = new();
    private readonly HashSet<string> _asked = new();

    private IReadOnlyList<CalendarMeeting> _cache = Array.Empty<CalendarMeeting>();
    private DateTime _lastRead = DateTime.MinValue;

    /// <param name="refreshInterval">
    /// Cadence de relecture ; <see cref="RefreshInterval"/> par défaut. Zéro relit à chaque tour,
    /// ce dont <c>--selftest</c> a besoin pour dérouler plusieurs états dans la même seconde.
    /// </param>
    public CalendarProbe(ICalendarSource source, TimeSpan? refreshInterval = null)
    {
        _source = source;
        _refreshInterval = refreshInterval ?? RefreshInterval;
    }

    public string Name => "agenda";

    /// <summary>Dernier état de la lecture, pour le diagnostic et les réglages.</summary>
    public string LastDetail { get; private set; } = "jamais lu";

    /// <summary>
    /// Des réunions de l'agenda viennent de commencer et l'utilisateur n'a rien dit sur elles.
    /// Émis <b>une seule fois</b> par occurrence, groupées quand plusieurs commencent ensemble.
    ///
    /// ⚠️ C'est <c>MeetingDetector</c> qui décide s'il y a lieu de poser une question, et
    /// laquelle : si une réunion est déjà en cours (détectée au micro parce qu'il s'est connecté
    /// en avance), il ne faut rien demander du tout — l'agenda se contente alors de la nommer.
    /// </summary>
    public event Action<IReadOnlyList<CalendarMeeting>>? DecisionNeeded;

    /// <summary>« Oui, je suis en réunion » : l'indice devient concluant jusqu'à la fin prévue.</summary>
    public void Accept(string meetingId)
    {
        _refused.Remove(meetingId);
        _accepted.Add(meetingId);
    }

    /// <summary>
    /// « Non » — ou pas de réponse. L'agenda cesse de déclencher, mais continue de nommer :
    /// s'il participe malgré tout, le micro déclenchera et la réunion portera le bon nom.
    /// </summary>
    public void Decline(string meetingId)
    {
        _accepted.Remove(meetingId);
        _refused.Add(meetingId);
    }

    public bool IsAccepted(string meetingId) => _accepted.Contains(meetingId);

    public bool IsDeclined(string meetingId) => _refused.Contains(meetingId);

    /// <summary>Réunion d'agenda préférée couvrant cet instant, ou null. Exposé pour le diagnostic.</summary>
    public CalendarMeeting? MeetingAt(DateTime at) => MeetingsAt(at).FirstOrDefault();

    /// <summary>
    /// Toutes les réunions d'agenda couvrant cet instant, la plus plausible d'abord : celles que
    /// l'utilisateur a acceptées ici, puis celles qu'il n'a pas refusées classées d'après sa
    /// réponse dans Outlook (organisateur, acceptée, provisoire, sans réponse…), les refusées en
    /// dernier. À rang égal, la plus ancienne commencée, puis l'ordre alphabétique — pour que deux
    /// tours consécutifs donnent le même ordre.
    /// </summary>
    public IReadOnlyList<CalendarMeeting> MeetingsAt(DateTime at) =>
        _cache.Where(m => !m.AllDay && m.Covers(at))
              .OrderBy(m => _accepted.Contains(m.Id) ? 0 : _refused.Contains(m.Id) ? 2 : 1)
              .ThenBy(m => ResponseRank(m.Response))
              .ThenBy(m => m.Start)
              .ThenBy(m => m.Subject, StringComparer.CurrentCultureIgnoreCase)
              .ToList();

    private static int ResponseRank(CalendarResponse response) => response switch
    {
        CalendarResponse.Organisateur => 0,
        CalendarResponse.Acceptee => 1,
        CalendarResponse.Provisoire => 2,
        CalendarResponse.SansReponse => 3,
        CalendarResponse.Inconnue => 4,
        CalendarResponse.Refusee => 5,
        _ => 6
    };

    public IReadOnlyList<MeetingSignal> Poll()
    {
        var now = DateTime.Now;
        Refresh(now);

        var meetings = MeetingsAt(now);
        if (meetings.Count == 0) return Array.Empty<MeetingSignal>();

        // La question n'est posée qu'une fois par occurrence, jamais pour une réunion déjà
        // tranchée, et en un seul groupe quand plusieurs commencent ensemble : c'est ce groupe
        // que l'utilisateur doit départager. Le détecteur reste libre de ne pas la poser.
        var fresh = meetings.Where(m => !_accepted.Contains(m.Id) && !_refused.Contains(m.Id)
                                        && _asked.Add(m.Id)).ToList();
        if (fresh.Count > 0) DecisionNeeded?.Invoke(fresh);

        return meetings.Select(m => new MeetingSignal(MeetingSource.Calendar, _source.Name, m.Subject,
                                                      Conclusive: _accepted.Contains(m.Id), Calendar: m))
                       .ToList();
    }

    /// <summary>
    /// Relecture de l'agenda, au plus une fois par <see cref="RefreshInterval"/>.
    ///
    /// Volontairement <b>synchrone</b>, sur le fil du détecteur : Outlook est un serveur COM
    /// mono-thread cloisonné (STA), et le déporter sur un thread de fond ferait passer chaque
    /// appel par un marshalling qui coûte plus cher que la lecture elle-même. La lecture porte
    /// sur le magasin local déjà synchronisé, elle se compte en dizaines de millisecondes.
    /// </summary>
    private void Refresh(DateTime now)
    {
        if (now - _lastRead < _refreshInterval) return;
        _lastRead = now;

        var snapshot = _source.Read(now - LookBehind, now + LookAhead);
        if (snapshot.Detail != LastDetail)
        {
            // Journalisé au changement seulement : une lecture toutes les 5 min pendant huit
            // heures ferait cent lignes identiques. Mais sans aucune trace, impossible de dire
            // après coup si l'agenda était lu ou pas — c'est la leçon des bulles et du rappel.
            Logger.Info($"Agenda ({_source.Name}) : {snapshot.Detail}");
            LastDetail = snapshot.Detail;
        }
        if (snapshot.Available) _cache = snapshot.Meetings;
    }
}
