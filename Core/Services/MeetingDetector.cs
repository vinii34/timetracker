using System.Windows.Threading;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Décide, à partir des indices des sondes, qu'une réunion commence puis se termine.
///
/// Toute l'intelligence est dans les deux temporisations, parce qu'un indice brut est trop
/// nerveux pour piloter une timesheet :
/// — <b>délai de confirmation</b> : un appel de trente secondes ne mérite pas une bascule.
///   Le début retenu reste malgré tout le <i>premier</i> indice, pas la fin du délai, pour ne
///   pas perdre la première minute de la réunion.
/// — <b>délai de grâce</b> : le micro se libère un instant quand on coupe son micro, qu'on
///   change de périphérique ou qu'on passe d'une salle à l'autre. Sans grâce, une réunion
///   d'une heure se hacherait en dix entrées. La fin retenue est l'instant où l'indice a
///   disparu, pas la fin de la grâce.
///
/// <b>Réunions qui se suivent.</b> Pendant une réunion, le micro reste capté d'un créneau à
/// l'autre : sans l'agenda, rien ne distingue deux réunions enchaînées d'une seule longue. La
/// collecte du 2026-08-10 au 09-14 en a compté neuf fondues en une seule entrée (jusqu'à 3h19,
/// cinq créneaux). D'où <see cref="MeetingSwitched"/> : quand la réunion d'agenda en cours est
/// terminée et qu'un seul autre créneau couvre l'instant, la réunion est <b>coupée</b> à la
/// frontière des créneaux, sans rien demander. Dès qu'il y a un doute — plusieurs créneaux, ou
/// un chevauchement avec une réunion qui n'est pas finie —, c'est une question
/// (<see cref="ChoiceNeeded"/>), jamais une décision à l'aveugle.
///
/// Ce service ne touche ni la base ni le chronomètre : il se contente d'émettre des événements,
/// que <c>App</c> traduit en bascules de tâche. Cette frontière est ce qui permet de le tester
/// avec des sondes scriptées.
/// </summary>
public sealed class MeetingDetector
{
    /// <summary>Cadence d'interrogation des sondes. Assez court pour ne pas rater une réunion,
    /// assez long pour que l'énumération des fenêtres reste invisible.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly IReadOnlyList<IMeetingProbe> _probes;
    private readonly CalendarProbe? _calendar;
    private readonly DispatcherTimer _timer;

    private MeetingInfo? _current;      // réunion en cours (confirmée)
    private DateTime? _pendingSince;    // premier indice, en attente de confirmation
    private DateTime? _missingSince;    // dernier indice vu, en attente de la fin de la grâce
    private bool _ignored;              // l'utilisateur a annulé cette réunion-ci

    /// <summary>Créneaux que l'agenda vient d'annoncer pendant ce tour, à traiter une fois les
    /// indices de toutes les sondes connus (voir <see cref="HandleCalendar"/>).</summary>
    private IReadOnlyList<CalendarMeeting>? _freshCalendar;

    /// <summary>Questions déjà posées (clé = nature + créneau) : jamais deux fois la même.</summary>
    private readonly HashSet<string> _asked = new();

    /// <summary>Émis une fois le délai de confirmation écoulé. <c>StartedAt</c> est antidaté.</summary>
    public event Action<MeetingInfo>? MeetingStarted;

    /// <summary>Émis une fois le délai de grâce écoulé. <c>EndedAt</c> est l'instant réel de fin.</summary>
    public event Action<MeetingInfo>? MeetingEnded;

    /// <summary>
    /// La réunion en cours a changé de titre ou s'est vu attacher un créneau d'agenda. Sert à
    /// nommer l'entrée <i>pendant</i> la réunion : l'utilisateur l'a renommée à la main 15 fois
    /// en cinq semaines parce que rien ne lui montrait que le titre était déjà connu.
    /// </summary>
    public event Action<MeetingInfo>? MeetingUpdated;

    /// <summary>
    /// Une réunion en remplace une autre sans interruption (créneaux qui se suivent) :
    /// la première est terminée à l'instant où la seconde commence. <c>App</c> clôture
    /// l'entrée et en ouvre une autre, sans repasser par la tâche d'avant.
    /// </summary>
    public event Action<MeetingInfo, MeetingInfo>? MeetingSwitched;

    /// <summary>L'agenda ne permet pas de décider seul : voir <see cref="MeetingChoice"/>.
    /// La réponse revient par <see cref="Resolve"/>.</summary>
    public event Action<MeetingChoice>? ChoiceNeeded;

    public MeetingDetector(AppSettings settings, IReadOnlyList<IMeetingProbe> probes)
    {
        _settings = settings;
        _probes = probes;
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => PollOnce();

        // La sonde d'agenda annonce les créneaux qui commencent au milieu d'un tour, avant que
        // les autres sondes aient parlé : on les met de côté jusqu'à la fin du tour.
        _calendar = probes.OfType<CalendarProbe>().FirstOrDefault();
        if (_calendar != null) _calendar.DecisionNeeded += fresh => _freshCalendar = fresh;
    }

    /// <summary>
    /// Sondes par défaut : micro (déclenche) et fenêtres (nomme), plus l'agenda quand il est
    /// disponible — lui nomme mieux que les fenêtres, et déclenche si l'utilisateur l'a accepté.
    /// </summary>
    public static MeetingDetector CreateDefault(AppSettings settings, CalendarProbe? calendar = null)
    {
        var probes = new List<IMeetingProbe>
        {
            new MicrophoneProbe(settings),
            new MeetingWindowProbe(settings)
        };
        if (calendar != null) probes.Add(calendar);
        return new MeetingDetector(settings, probes);
    }

    /// <summary>
    /// Journal de diagnostic, ou null (cas normal). Quand il est posé, chaque tour d'interrogation
    /// y consigne son relevé : c'est ce qui permet d'ajuster les heuristiques après une semaine
    /// d'usage réel, sans rien changer au comportement de la détection.
    /// </summary>
    public MeetingTrace? Trace { get; set; }

    public bool InMeeting => _current != null && !_ignored;

    /// <summary>Réunion en cours, ou null. Exposé pour le diagnostic et l'affichage.</summary>
    public MeetingInfo? Current => _ignored ? null : _current;

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        _current = null;
        _pendingSince = null;
        _missingSince = null;
        _ignored = false;
        _freshCalendar = null;
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>
    /// L'utilisateur a annulé la bascule : on reste muet jusqu'à la fin de <i>cette</i> réunion.
    /// Sans ça, le tick suivant rebasculerait aussitôt sur la tâche « Réunion ».
    /// </summary>
    public void IgnoreCurrentMeeting()
    {
        if (_current != null) _ignored = true;
        _pendingSince = null;
    }

    /// <summary>
    /// Un tour d'interrogation. Publique pour que <c>--selftest</c> déroule la machine à états
    /// sans attendre le temps réel.
    /// </summary>
    public void PollOnce()
    {
        var now = DateTime.Now;

        var signals = new List<MeetingSignal>();
        foreach (var probe in _probes)
        {
            try { signals.AddRange(probe.Poll()); }
            catch (Exception ex) { Logger.Error($"MeetingDetector/{probe.Name}", ex); }
        }

        bool detected = signals.Any(s => s.Conclusive);
        var fresh = _freshCalendar;
        _freshCalendar = null;

        if (Trace != null)
        {
            // Le relevé des fenêtres candidates est plus large que celui des indices retenus :
            // le diagnostic doit montrer les titres que les motifs actuels ne reconnaissent PAS.
            var micApps = signals.Where(s => s.Source == MeetingSource.Microphone)
                                 .Select(s => s.App).ToList();
            Trace.Record(detected, signals, MeetingWindowProbe.CandidateWindows(micApps));
        }

        if (_current is null)
        {
            // Des créneaux commencent et rien n'est en cours : faut-il pointer ? La question
            // vaut aussi pendant le délai de confirmation — l'utilisateur y répond plus vite
            // que le micro ne se confirme, et l'un n'empêche pas l'autre.
            if (fresh is { Count: > 0 })
                ChoiceNeeded?.Invoke(new MeetingChoice(MeetingChoiceKind.Start, fresh));

            if (!detected) { _pendingSince = null; return; }

            _pendingSince ??= now;
            if ((now - _pendingSince.Value).TotalSeconds < _settings.MeetingStartDelaySeconds) return;

            var calendarNow = CalendarSignals(signals);
            _current = new MeetingInfo
            {
                StartedAt = _pendingSince.Value,
                Calendar = AdoptableCalendar(calendarNow),
                Apps = DescribeApps(signals)
            };
            _current.Title = BestTitle(_current, signals);
            _pendingSince = null;
            _missingSince = null;
            MeetingStarted?.Invoke(_current);

            // Plusieurs créneaux couvrent la réunion qui vient de démarrer et aucun ne s'impose :
            // le titre attendra la réponse plutôt que de parier.
            if (_current.Calendar is null && calendarNow.Count > 1)
                Ask(new MeetingChoice(MeetingChoiceKind.Name, calendarNow));
            return;
        }

        if (detected)
        {
            _missingSince = null;
            HandleCalendar(signals, now);
            return;
        }

        // Les indices viennent de disparaître et des créneaux commencent : typiquement une
        // réunion acceptée qui vient de finir à l'heure, suivie d'une autre. Poser la question
        // maintenant, sans attendre la fin de la grâce, c'est ce qui permet — sur un « oui » —
        // de couper à la frontière plutôt que de fondre les deux réunions.
        if (fresh is { Count: > 0 })
            ChoiceNeeded?.Invoke(new MeetingChoice(MeetingChoiceKind.Start, fresh));

        _missingSince ??= now;
        if ((now - _missingSince.Value).TotalSeconds < _settings.MeetingEndGraceSeconds) return;

        var finished = _current;
        finished.EndedAt = _missingSince;
        _current = null;
        _missingSince = null;
        bool wasIgnored = _ignored;
        _ignored = false;

        if (!wasIgnored) MeetingEnded?.Invoke(finished);
    }

    /// <summary>
    /// Ce que l'agenda change à une réunion en cours et détectée à cet instant :
    ///
    /// — pas de créneau attaché : on adopte celui qui couvre l'instant, ou on demande lequel
    ///   s'ils sont plusieurs (l'utilisateur s'est connecté en avance, le créneau arrive après —
    ///   la réunion en cours n'est <b>jamais</b> redémarrée, c'était l'exigence explicite) ;
    /// — le créneau attaché est terminé à l'agenda et un seul autre couvre l'instant : la réunion
    ///   suivante a commencé, on coupe à la frontière. Plusieurs autres : on demande lequel ;
    /// — le créneau attaché court encore et d'autres commencent (chevauchement) : on demande
    ///   s'il faut y passer. Rester est la réponse par défaut, rien ne bouge sans réponse.
    /// </summary>
    private void HandleCalendar(List<MeetingSignal> signals, DateTime now)
    {
        var current = _current!;
        var calendarNow = CalendarSignals(signals);

        if (current.Calendar is null)
        {
            current.Calendar = AdoptableCalendar(calendarNow);
            if (current.Calendar is null && calendarNow.Count > 1)
                Ask(new MeetingChoice(MeetingChoiceKind.Name, calendarNow));
        }
        else
        {
            var attached = current.Calendar;
            var others = calendarNow.Where(c => c.Id != attached.Id && !IsDeclined(c)).ToList();
            var accepted = others.FirstOrDefault(IsAccepted);

            if (accepted != null && !attached.Covers(now))
                SwitchTo(accepted, Later(accepted.Start, attached.End), signals);
            else if (others.Count == 1 && !attached.Covers(now))
                SwitchTo(others[0], Later(others[0].Start, attached.End), signals);
            else if (others.Count > 0)
                Ask(new MeetingChoice(MeetingChoiceKind.Switch, others, attached,
                                      CurrentOver: !attached.Covers(now)));
        }

        RefreshTitle(signals);
    }

    /// <summary>
    /// Le libellé arrive souvent en retard : l'onglet Meet ne prend son nom qu'une fois la
    /// réunion rejointe, et Teams renomme sa fenêtre en cours de route. Un libellé générique se
    /// laisse donc remplacer par un vrai titre, alors qu'un vrai titre ne bouge plus (sans quoi
    /// la fenêtre Zoom toujours ouverte finirait par le reprendre). Seul le sujet de l'agenda
    /// fait autorité : il remplace même un titre de fenêtre déjà retenu.
    /// </summary>
    private void RefreshTitle(List<MeetingSignal> signals)
    {
        var current = _current!;
        var better = BestTitle(current, signals);
        if (better is null || better == current.Title) return;

        bool authoritative = better == current.Calendar?.Subject;
        if (current.Title is null || authoritative
            || (MeetingWindowProbe.IsGenericTitle(current.Title) && !MeetingWindowProbe.IsGenericTitle(better)))
        {
            current.Title = better;
            MeetingUpdated?.Invoke(current);
        }
    }

    /// <summary>Coupe la réunion en cours à <paramref name="at"/> et en ouvre une autre sur ce créneau.</summary>
    private void SwitchTo(CalendarMeeting next, DateTime at, IEnumerable<MeetingSignal> signals)
    {
        var finished = _current!;
        finished.EndedAt = at;

        _current = new MeetingInfo
        {
            StartedAt = at,
            Calendar = next,
            Title = next.Subject.Length > 0 ? next.Subject : null,
            Apps = DescribeApps(signals)
        };
        _missingSince = null;
        MeetingSwitched?.Invoke(finished, _current);
    }

    /// <summary>Pose une question, une seule fois par créneau et par nature de question.</summary>
    private void Ask(MeetingChoice choice)
    {
        var unasked = choice.Candidates.Where(c => _asked.Add($"{choice.Kind}:{c.Id}")).ToList();
        if (unasked.Count == 0) return;
        ChoiceNeeded?.Invoke(choice with { Candidates = unasked });
    }

    /// <summary>
    /// Réponse de l'utilisateur à une question posée par <see cref="ChoiceNeeded"/>.
    /// <paramref name="chosen"/> null signifie « aucune de celles-ci » — ou « je reste ».
    /// Les créneaux écartés sont refusés : ils ne déclencheront plus rien, mais nommeront
    /// encore si le micro mord pendant qu'ils sont seuls (un « non » par erreur se rattrape).
    /// </summary>
    public void Resolve(MeetingChoice choice, CalendarMeeting? chosen)
    {
        if (_calendar is null) return;

        foreach (var candidate in choice.Candidates)
        {
            if (chosen != null && candidate.Id == chosen.Id) _calendar.Accept(candidate.Id);
            else _calendar.Decline(candidate.Id);
        }
        if (chosen is null || _current is null) return;   // sans réunion en cours, l'acceptation suffit

        switch (choice.Kind)
        {
            case MeetingChoiceKind.Name when _current.Calendar is null:
                _current.Calendar = chosen;
                if (chosen.Subject.Length > 0) _current.Title = chosen.Subject;
                MeetingUpdated?.Invoke(_current);
                break;

            case MeetingChoiceKind.Switch when _current.Calendar?.Id != chosen.Id:
                // Il y est passé au début du créneau (règle de l'heure de début : le plus tôt
                // entre l'indice et le créneau), sans jamais remonter avant la réunion coupée.
                SwitchTo(chosen, Later(chosen.Start, _current.StartedAt), Array.Empty<MeetingSignal>());
                break;
        }
    }

    private bool IsAccepted(CalendarMeeting m) => _calendar?.IsAccepted(m.Id) == true;

    private bool IsDeclined(CalendarMeeting m) => _calendar?.IsDeclined(m.Id) == true;

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>Créneaux couvrant l'instant, dans l'ordre de préférence de la sonde.</summary>
    private static List<CalendarMeeting> CalendarSignals(IEnumerable<MeetingSignal> signals) =>
        signals.Where(s => s.Calendar != null).Select(s => s.Calendar!)
               .DistinctBy(c => c.Id).ToList();

    /// <summary>
    /// Créneau à attacher sans poser de question : le seul qui couvre l'instant, ou celui que
    /// l'utilisateur a accepté. Plusieurs candidats sans acceptation → null, on demandera.
    /// </summary>
    private CalendarMeeting? AdoptableCalendar(List<CalendarMeeting> calendarNow) =>
        calendarNow.Count == 1 ? calendarNow[0] : calendarNow.FirstOrDefault(IsAccepted);

    /// <summary>
    /// Meilleur titre disponible.
    ///
    /// <b>Le créneau attaché passe avant les fenêtres</b>, et de loin : il donne le sujet tel que
    /// l'utilisateur l'a accepté, là où une fenêtre donne au mieux le titre que l'application a
    /// bien voulu afficher. C'est aussi la seule façon de nommer une réunion Zoom, dont aucune
    /// fenêtre n'expose jamais le sujet (constat des deux semaines de collecte). Tant qu'aucun
    /// créneau n'est attaché, l'agenda ne nomme rien : avec deux créneaux simultanés, parier
    /// sur l'un reviendrait à mal nommer une réunion sur deux.
    ///
    /// Pour les fenêtres, les libellés génériques sont relégués en dernier, puis le plus long
    /// d'abord — entre « Microsoft Teams » et « Point hebdo », le second est le libellé utile.
    /// Le classement par généricité n'est pas cosmétique : « Zoom Workplace » est plus long que
    /// « Zoom Meeting », et la fenêtre principale de Zoom reste ouverte pendant les réunions
    /// Teams. Sans lui, elle nomme des réunions auxquelles elle n'a pas participé (collecte du
    /// 2026-07-27 au 31). Un générique reste malgré tout préférable au nom du process.
    /// </summary>
    private static string? BestTitle(MeetingInfo meeting, IEnumerable<MeetingSignal> signals)
    {
        if (meeting.Calendar is { Subject.Length: > 0 } calendar) return calendar.Subject;

        return signals.Where(s => !string.IsNullOrWhiteSpace(s.Title) && s.Source == MeetingSource.Window)
                      .OrderBy(s => MeetingWindowProbe.IsGenericTitle(s.Title!) ? 1 : 0)
                      .ThenByDescending(s => s.Title!.Length)
                      .Select(s => s.Title)
                      .FirstOrDefault();
    }

    private static string DescribeApps(IEnumerable<MeetingSignal> signals) =>
        string.Join(", ", signals.Select(s => s.App).Distinct(StringComparer.OrdinalIgnoreCase));
}
