namespace TimeTracker.Core.Models;

/// <summary>
/// Réglages de l'application, persistés dans la table <c>settings</c> (clé/valeur).
/// Modifiables depuis la fenêtre Paramètres (<c>UI/SettingsWindow</c>).
/// </summary>
public class AppSettings
{
    /// <summary>Intervalle entre deux rappels « tu travailles toujours sur… ? » (minutes).</summary>
    public int ReminderIntervalMinutes { get; set; } = 30;

    /// <summary>Lancer TimeTracker automatiquement au démarrage de Windows.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Jouer un son lors des rappels.</summary>
    public bool ReminderSound { get; set; } = true;

    /// <summary>Dossier proposé par défaut pour les exports CSV/Excel.</summary>
    public string ExportFolder { get; set; } = DefaultExportFolder();

    /// <summary>Raccourci global d'ouverture du sélecteur de tâche.</summary>
    public string HotkeyTask { get; set; } = "Ctrl+Alt+T";

    /// <summary>Raccourci global de correction à chaud.</summary>
    public string HotkeyEdit { get; set; } = "Ctrl+Alt+E";

    /// <summary>Raccourci global de pause / reprise (sans tâche en cours : ouvre le sélecteur).</summary>
    public string HotkeyPause { get; set; } = "Ctrl+Alt+P";

    /// <summary>
    /// Objectif d'heures pointées <b>par jour</b> — valeur canonique, la semaine en déduit cinq
    /// jours. Zéro = pas d'objectif. L'utilisateur le saisit dans l'unité de son choix
    /// (<see cref="GoalPerWeek"/>) : il a d'abord tapé 7,5 sur un réglage « par semaine » en
    /// pensant à sa journée (2026-09-17), puis demandé à avoir le choix.
    /// </summary>
    public double DailyHoursGoal { get; set; } = 0;

    /// <summary>Unité d'affichage et de saisie dans les Paramètres : semaine (vrai) ou jour.</summary>
    public bool GoalPerWeek { get; set; } = false;

    /// <summary>Objectif hebdomadaire dérivé (cinq jours ouvrés).</summary>
    public double WeeklyHoursGoal => DailyHoursGoal * 5;

    /// <summary>
    /// Suggérer la tâche d'après les fenêtres au premier plan (mots du titre ↔ mots des tâches).
    /// Rien n'est enregistré : le relevé vit en mémoire et meurt avec l'application.
    /// </summary>
    public bool ActivitySuggestions { get; set; } = true;

    /// <summary>
    /// Apprendre quelles fenêtres accompagnent chaque tâche : les mots des titres vus pendant
    /// qu'une tâche tourne sont comptés en base (<c>task_hints</c>). ⚠️ Ce sont des mots de
    /// titres de fenêtres stockés sur le poste — accepté par l'utilisateur le 2026-09-17, à
    /// condition que ce soit visible et débrayable. Sans effet si les suggestions sont coupées.
    /// </summary>
    public bool ActivityLearning { get; set; } = true;

    // --- Assistant IA (optionnel, coupé par défaut) ---

    /// <summary>
    /// Autoriser l'envoi des <b>noms de tâches</b> à un modèle de langage, à la demande
    /// (bouton dans « Gérer les tâches »). Jamais les heures, jamais les titres de fenêtres.
    /// C'est la seule fonction qui fait sortir quelque chose du poste : coupée par défaut.
    /// </summary>
    public bool AiEnabled { get; set; } = false;

    /// <summary>« gemini », « openai » (et tout ce qui parle le même protocole : Mistral, Ollama…), « anthropic ».</summary>
    public string AiProvider { get; set; } = "gemini";

    public string AiApiKey { get; set; } = "";

    /// <summary>Modèle ; vide = celui par défaut du fournisseur.</summary>
    public string AiModel { get; set; } = "";

    /// <summary>Adresse de l'API pour un fournisseur compatible OpenAI ; vide = celle du fournisseur.</summary>
    public string AiBaseUrl { get; set; } = "";

    /// <summary>Ouvrir le sélecteur de tâche au lancement quand aucune entrée n'est en cours.</summary>
    public bool AskTaskOnStartup { get; set; } = true;

    /// <summary>
    /// Au-delà de cette durée de verrouillage ou de veille, la tâche en cours est clôturée à
    /// l'instant où le poste a été quitté, et le suivi passe en pause. Zéro désactive.
    /// Motivé par cinq semaines d'usage : l'utilisateur ferme son portable le soir sans arrêter
    /// sa tâche, et corrige le lendemain une entrée qui a couru toute la nuit.
    /// </summary>
    public int AbsenceCutoffMinutes { get; set; } = 30;

    // --- Détection de réunion ---

    /// <summary>Basculer automatiquement sur la tâche de réunion quand une réunion est détectée.</summary>
    public bool MeetingDetection { get; set; } = true;

    /// <summary>Tâche sur laquelle les réunions détectées sont pointées.</summary>
    public string MeetingTaskName { get; set; } = "Réunion";

    /// <summary>
    /// Durée pendant laquelle la réunion doit rester visible avant la bascule : évite de pointer
    /// un appel de trente secondes. Le début pointé reste antidaté au premier indice.
    /// </summary>
    public int MeetingStartDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Tolérance à la disparition des indices avant de conclure la fin : couper son micro ou
    /// changer de périphérique ne doit pas hacher la réunion en plusieurs entrées.
    /// </summary>
    public int MeetingEndGraceSeconds { get; set; } = 120;

    /// <summary>
    /// Applications dont l'usage du micro compte comme une réunion (motifs séparés par « ; »).
    /// Sans ce filtre, Discord ou la dictée Windows déclencheraient une bascule. Réglage avancé :
    /// modifiable seulement en base (table <c>settings</c>).
    /// </summary>
    public string MeetingApps { get; set; } = "teams;zoom;cpthost;slack;webex;msedge;chrome;firefox";

    /// <summary>Motifs de <see cref="MeetingApps"/>, nettoyés.</summary>
    public IReadOnlyList<string> MeetingAppList() =>
        MeetingApps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // --- Agenda ---

    /// <summary>
    /// Lire l'agenda Outlook pour nommer les réunions et proposer de basculer.
    /// Sans effet si Outlook classique n'est pas lancé : l'agenda est un confort, jamais une
    /// dépendance du suivi du temps.
    /// </summary>
    public bool OutlookCalendar { get; set; } = true;

    /// <summary>
    /// Poser la question « tu bascules ? » au début d'une réunion de l'agenda. Décoché, l'agenda
    /// ne sert plus qu'à <b>nommer</b> les réunions que le micro détecte — utile si les créneaux
    /// où l'utilisateur n'assiste pas sont nombreux.
    /// </summary>
    public bool OutlookAsk { get; set; } = true;

    public static string DefaultExportFolder() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // --- Clés de stockage dans la table settings ---
    public const string KeyReminderInterval = "reminder_interval_minutes";
    public const string KeyStartWithWindows = "start_with_windows";
    public const string KeyReminderSound = "reminder_sound";
    public const string KeyExportFolder = "export_folder";
    public const string KeyHotkeyTask = "hotkey_task";
    public const string KeyHotkeyEdit = "hotkey_edit";
    public const string KeyHotkeyPause = "hotkey_pause";
    public const string KeyDailyHoursGoal = "daily_hours_goal";
    public const string KeyGoalPerWeek = "goal_per_week";
    /// <summary>Ancienne clé (v1.5 / v1.6) : valeur hebdomadaire, migrée en jour ÷ 5 avec l'unité « semaine ».</summary>
    public const string KeyWeeklyHoursGoal = "weekly_hours_goal";
    public const string KeyActivitySuggestions = "activity_suggestions";
    public const string KeyActivityLearning = "activity_learning";
    public const string KeyAiEnabled = "ai_enabled";
    public const string KeyAiProvider = "ai_provider";
    public const string KeyAiApiKey = "ai_api_key";
    public const string KeyAiModel = "ai_model";
    public const string KeyAiBaseUrl = "ai_base_url";
    public const string KeyAskTaskOnStartup = "ask_task_on_startup";
    public const string KeyAbsenceCutoff = "absence_cutoff_minutes";
    public const string KeyMeetingDetection = "meeting_detection";
    public const string KeyMeetingTaskName = "meeting_task_name";
    public const string KeyMeetingStartDelay = "meeting_start_delay_seconds";
    public const string KeyMeetingEndGrace = "meeting_end_grace_seconds";
    public const string KeyMeetingApps = "meeting_apps";
    public const string KeyOutlookCalendar = "outlook_calendar";
    public const string KeyOutlookAsk = "outlook_ask";
}
