using System.Globalization;
using System.Runtime.InteropServices;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Lit l'agenda dans l'Outlook <b>classique</b> déjà installé, par automation COM.
///
/// Choisi contre OAuth/Microsoft Graph pour deux raisons : la promesse « 100 % local » reste
/// vraie (aucune requête ne sort du poste, ça marche même hors ligne), et surtout il n'y a
/// <b>aucune application à déclarer</b> dans l'Azure AD de l'entreprise — validation que
/// l'utilisateur ne peut pas obtenir seul. ⚠️ En contrepartie, le « nouveau Outlook » n'expose
/// plus de COM : le jour où ce poste y bascule, c'est une source Graph qu'il faudra brancher
/// derrière <see cref="ICalendarSource"/>. Rien d'autre ne bougera.
///
/// ⚠️ <b>On s'attache à un Outlook déjà lancé, on ne le lance jamais.</b>
/// <c>CreateInstance</c> démarrerait Outlook dans le dos de l'utilisateur — plusieurs secondes,
/// une fenêtre qui s'ouvre, et un Outlook qui reste ouvert à cause de nous. S'il est fermé,
/// l'agenda est simplement indisponible jusqu'au prochain essai.
/// </summary>
public sealed class OutlookComCalendarSource : ICalendarSource
{
    /// <summary>Garde-fou : une récurrence mal bornée peut se déplier à l'infini.</summary>
    private const int MaxItems = 200;

    private const int FolderCalendar = 9;   // olFolderCalendar

    public string Name => "Outlook (COM)";

    /// <summary>Dernier filtre utilisé, exposé pour le diagnostic (voir le format ci-dessous).</summary>
    public string LastFilter { get; private set; } = "";

    public CalendarSnapshot Read(DateTime from, DateTime to)
    {
        object? application = null;
        try
        {
            application = AttachToRunningOutlook();
            if (application is null)
                return CalendarSnapshot.Unavailable(
                    "Outlook classique n'est pas en cours d'exécution (on ne le démarre jamais).");

            dynamic app = application;
            dynamic calendar = app.Session.GetDefaultFolder(FolderCalendar);
            dynamic items = calendar.Items;

            // L'ordre puis IncludeRecurrences puis Restrict : c'est la seule séquence qui rend
            // les occurrences récurrentes visibles. Sans Restrict, la collection est infinie.
            items.Sort("[Start]");
            items.IncludeRecurrences = true;

            // ⚠️ Outlook attend ici les dates dans le format court de la CULTURE COURANTE, pas
            // un format ISO. C'est le même piège que l'export Excel (voir AppCulture) : ne pas
            // « harmoniser » vers InvariantCulture, Outlook ne le comprendrait pas. Le filtre
            // exact est exposé par --outlookprobe, parce que c'est ici que ça cassera d'abord.
            LastFilter = $"[Start] < '{to.ToString("g", CultureInfo.CurrentCulture)}' " +
                         $"AND [End] > '{from.ToString("g", CultureInfo.CurrentCulture)}'";
            dynamic restricted = items.Restrict(LastFilter);

            var meetings = new List<CalendarMeeting>();
            int cancelled = 0, appointments = 0;
            foreach (dynamic item in restricted)
            {
                if (meetings.Count >= MaxItems) break;
                // Cast explicite : un argument dynamic rendrait l'appel lui-même dynamique,
                // et les paramètres ref ne survivent pas toujours à la liaison tardive.
                var meeting = Convert((object)item, ref cancelled, ref appointments);
                if (meeting != null) meetings.Add(meeting);
            }

            meetings.Sort((a, b) => a.Start.CompareTo(b.Start));
            var detail = $"{meetings.Count} réunion(s) lue(s)";
            if (cancelled > 0) detail += $", {cancelled} annulée(s) ignorée(s)";
            if (appointments > 0) detail += $", {appointments} rendez-vous sans invité ignoré(s)";
            return new CalendarSnapshot(true, detail + ".", meetings);
        }
        catch (Exception ex)
        {
            // Ne jamais laisser une erreur COM remonter : l'agenda est un confort, le suivi du
            // temps ne doit pas en dépendre.
            Logger.Error("OutlookComCalendarSource.Read", ex);
            return CalendarSnapshot.Unavailable($"Lecture impossible : {ex.Message}");
        }
        finally
        {
            if (application != null && Marshal.IsComObject(application))
                try { Marshal.ReleaseComObject(application); } catch { /* déjà relâché */ }
        }
    }

    /// <summary>Constantes <c>OlMeetingStatus</c> : rendez-vous sans invité, et annulations.</summary>
    private const int MeetingStatusNonMeeting = 0;
    private const int MeetingStatusCanceled = 5;
    private const int MeetingStatusReceivedAndCanceled = 7;

    /// <summary>
    /// Traduit un élément d'agenda, ou null s'il n'a pas à compter comme réunion :
    ///
    /// — <b>annulée</b> par l'organisateur : Outlook garde l'élément, préfixé « Canceled: ».
    ///   La collecte du 2026-09-07 a vu la question posée pour deux réunions annulées ;
    /// — <b>rendez-vous sans invité</b> : un bloc personnel (« Back to home », trajet) n'est pas
    ///   une réunion. Il a nommé deux huddles Slack le 2026-08-14 et posé la question le 09-11.
    ///   Les vraies réunions qu'on organise soi-même ont des invités, donc un autre statut.
    /// </summary>
    private static CalendarMeeting? Convert(dynamic item, ref int cancelled, ref int appointments)
    {
        try
        {
            // La collection peut contenir autre chose qu'un rendez-vous ; on lit défensivement.
            DateTime start = item.Start;
            DateTime end = item.End;
            bool allDay = item.AllDayEvent;
            string subject = (item.Subject as string ?? "").Trim();

            int meetingStatus = -1;
            try { meetingStatus = (int)item.MeetingStatus; } catch { /* absent sur certains items */ }
            if (meetingStatus is MeetingStatusCanceled or MeetingStatusReceivedAndCanceled
                || subject.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase)
                || subject.StartsWith("Annulé", StringComparison.OrdinalIgnoreCase))
            {
                cancelled++;
                return null;
            }
            if (meetingStatus == MeetingStatusNonMeeting)
            {
                appointments++;
                return null;
            }

            string organizer = "";
            try { organizer = item.Organizer as string ?? ""; } catch { /* absent sur certains items */ }

            string id;
            try { id = item.GlobalAppointmentID as string ?? ""; }
            catch { id = ""; }
            if (id.Length == 0) { try { id = item.EntryID as string ?? ""; } catch { id = ""; } }
            // Une occurrence de série porte l'identifiant de la SÉRIE : sans la date, accepter
            // le point hebdo une fois vaudrait acceptation pour toutes les semaines suivantes.
            id = $"{id}@{start:yyyyMMddHHmm}";

            int response = 0;
            try { response = (int)item.ResponseStatus; } catch { /* rendez-vous sans invitation */ }

            return new CalendarMeeting
            {
                Id = id,
                Subject = subject,
                Start = start,
                End = end,
                Organizer = organizer,
                AllDay = allDay,
                Response = TranslateResponse(response)
            };
        }
        catch (Exception ex)
        {
            Logger.Error("OutlookComCalendarSource.Convert", ex);
            return null;
        }
    }

    /// <summary>Constantes <c>OlResponseStatus</c> d'Outlook.</summary>
    private static CalendarResponse TranslateResponse(int status) => status switch
    {
        1 => CalendarResponse.Organisateur,
        2 => CalendarResponse.Provisoire,
        3 => CalendarResponse.Acceptee,
        4 => CalendarResponse.Refusee,
        5 => CalendarResponse.SansReponse,
        _ => CalendarResponse.Inconnue
    };

    /// <summary>
    /// Récupère l'instance d'Outlook déjà en cours, ou null. Passe par la table des objets
    /// actifs (ROT) : <c>Marshal.GetActiveObject</c> n'existe plus depuis .NET Core, d'où
    /// l'appel direct à oleaut32.
    /// </summary>
    private static object? AttachToRunningOutlook()
    {
        try
        {
            CLSIDFromProgID("Outlook.Application", out Guid clsid);
            GetActiveObject(ref clsid, IntPtr.Zero, out object instance);
            return instance;
        }
        catch (COMException)
        {
            return null;   // MK_E_UNAVAILABLE : Outlook n'est pas lancé
        }
        catch (Exception ex)
        {
            Logger.Error("OutlookComCalendarSource.Attach", ex);
            return null;
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId,
        out Guid clsid);
}
