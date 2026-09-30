# TimeTracker

Suivi du temps de travail par tâche pour Windows.
Tourne en arrière-plan (system tray), 100 % local par défaut — la seule fonction qui envoie
quelque chose hors du poste (l'assistant IA) est désactivée par défaut et explicite : des noms de
tâches sur un clic, et, seulement si on coche une seconde case, des mots-clés de fenêtres (jamais
un titre entier) pour proposer des noms de tâche.

## État d'avancement

**v1.1** — application complète en saisie clavier : suivi, correction, restitution et export,
plus la gestion de la bibliothèque de tâches et le pilotage depuis le tableau de bord.
La reconnaissance vocale a été **abandonnée** (précision insuffisante) et retirée du code.

**v1.7** (2026-09-29) — les suggestions refaites après deux semaines d'usage : TimeTracker
**propose de lui-même de changer de tâche** quand les fenêtres ne ressemblent plus à la tâche en
cours (tâche existante ou nom nouveau, « compter depuis 10:42 »), **« Tu voulais dire … ? »** à la
création d'une tâche, un bandeau **« N noms à vérifier »** (fautes de frappe, doublons) dans le
tableau de bord, des doublons comparés **mot à mot**, un apprentissage qui suit les corrections,
et, en option, **l'IA qui propose des noms** d'après des mots-clés des fenêtres.

**v1.6** (2026-09-17) — les suggestions **apprennent** quelles fenêtres accompagnent chaque tâche
(mots de titres comptés par tâche, en base, sur le poste ; « Oublier » dans les Paramètres), et un
**assistant IA optionnel** propose fusions et renommages de tâches à cocher — Gemini, tout
fournisseur compatible OpenAI (dont Ollama en local) ou Anthropic, avec bouton de test.

**v1.5** (2026-09-17) — **objectif d'heures hebdomadaire** avec avancement dans le tableau de
bord, **export d'une période libre** (un mois, des congés), **raccourci global pause / reprise**
(`Ctrl+Alt+P`), **suggestion de tâche d'après les fenêtres utilisées** (💡 en tête du sélecteur,
encart dans le rappel — rien n'est enregistré), et **doublons probables** proposés à la fusion
dans « Gérer les tâches ». Tout se calcule sur le poste.

**v1.4** (2026-09-17) — cinq semaines d'usage avec l'**agenda Outlook** dépouillées : les
**réunions qui se suivent** sont désormais coupées à la frontière des créneaux au lieu de se
cumuler sur une seule entrée, **deux réunions aux mêmes horaires** se départagent par une
question à un bouton par réunion, la tâche en cours est **clôturée à l'heure du départ** quand le
poste reste verrouillé ou en veille au-delà d'un seuil (fini l'entrée qui court toute la nuit),
et la réunion est **nommée pendant qu'elle dure**, pas seulement à sa fin.

**v1.3** — lecture de l'**agenda Outlook** (COM sur l'Outlook classique, rien ne sort du poste) :
le sujet nomme la réunion — y compris Zoom —, et une question au début du créneau permet de
pointer une réunion que le micro ne verrait pas.

**v1.2** — ajoute la **détection automatique de réunion**, validée sur une semaine d'usage réel
(2026-07-27 → 31 : 14 réunions détectées, aucun faux positif, aucune réunion ratée) et réglée
d'après le journal de diagnostic de cette collecte. Voir `HANDOFF.md` §0 pour les mesures.

| # | Étape | État |
|---|-------|------|
| 1 | Tray + raccourci global + popup texte + timer + SQLite + rappel | ✅ Fait |
| 2 | ~~Vocal (Whisper.NET, NAudio)~~ | ❌ Abandonné / retiré |
| 3 | Détection de réunion (micro + fenêtres) | ✅ Fait (v1.2), validé sur sept semaines réelles |
| 4 | Intégration Outlook (lecture calendrier) | ✅ Fait (v1.3, COM), enchaînements et créneaux simultanés en v1.4 |
| 5 | Tableau de bord complet + export CSV/Excel | ✅ Fait |
| 6 | Démarrage de journée | ✅ popup au lancement (désactivable) |
| 7 | Correction à chaud | ✅ Fait |
| 8 | Installeur | ⬜ (package autonome dispo, voir plus bas) |
| 9 | Paramètres, son, démarrage Windows | ✅ Fait |
| 10 | Gérer les tâches, favoris, barre de suivi, rappel de pause | ✅ Fait (v1.1) |

## Fonctionnalités

### Suivi
- **Icône system tray** avec menu : tâche en cours, changer de tâche, pause/reprise,
  corriger, tableau de bord, paramètres, quitter. Double-clic = tableau de bord.
- **Raccourci global `Ctrl+Alt+T`** → popup de sélection de tâche.
- **Popup de sélection** : saisie d'une nouvelle tâche (texte) ou choix dans la liste —
  **favoris (★) en tête**, puis tâches récentes (clic, double-clic, `Entrée`, chiffres `1`-`9`).
- **Timer** : durée écoulée affichée dans le tooltip de l'icône.
- **Rappel** (défaut 30 min) : popup « Toujours sur cette tâche ? » + son système.
  - Répondre « Non, je change » propose de **décaler le début de 5 ou 15 min**, pour le cas
    fréquent où le changement a eu lieu avant qu'on pense à le déclarer.
  - Pendant une **pause**, le rappel devient « Toujours en pause ? » (reprendre / changer de
    tâche / rester en pause) : une pause oubliée ne passe plus inaperçue.
- **Arrêt du suivi** (fin de journée) : clôture l'entrée en cours en conservant le temps pointé.
- **Fin de journée oubliée** : si le poste reste verrouillé ou en veille plus de 30 min (réglable,
  section « Fin de journée et absences ») avec une tâche en cours, l'entrée est clôturée **à
  l'heure du départ** et le suivi passe en pause ; le rappel « Toujours en pause ? » propose de
  reprendre ou de changer de tâche. Un verrouillage pendant une réunion en cours n'est pas coupé.
- **Reprise** d'une entrée laissée ouverte après une fermeture non propre.
- **Raccourci `Ctrl+Alt+P`** : pause / reprise sans passer par le tray (sans tâche en cours,
  ouvre le sélecteur).
- **Objectif d'heures**, saisi par jour ou par semaine au choix (Paramètres, 0 = aucun ; 7,5 par
  jour = 37,5 par semaine) : reste à faire dans l'onglet Jour, barre d'avancement dans l'onglet
  Semaine.

### Suggestions d'après les fenêtres
TimeTracker relève toutes les 5 s la fenêtre au premier plan et garde la dernière demi-heure
**en mémoire seulement** — rien n'est écrit en base ni dans le journal. Il compare les mots des
titres (« Orvane - mapping.xlsx ») aux mots des noms de tâches (« Config Orvane ») ; seuls les mots
qui désignent peu de tâches comptent (« Orvane » oui, « Config » ou « Réunion » non).
- Dans le **sélecteur**, la tâche suggérée passe en tête avec 💡 et sa raison en infobulle.
- **Changement de tâche probable** : si pendant plusieurs minutes les fenêtres ne ressemblent
  plus à la tâche en cours, une petite fenêtre en bas à droite (non modale, dans la barre des
  tâches, sans prendre le focus) demande « Tu as changé de tâche ? » : une tâche existante, ou un
  nom nouveau pré-rempli et modifiable (tiré du titre, ou proposé par l'IA si elle est activée),
  avec « Compter depuis 10:42 » et « Non, je reste ». Jamais en réunion, en pause, ni dans les
  10 min qui suivent une bascule ou un réveil. **Rien ne bascule sans un clic.**
- **« Tu voulais dire … ? »** : à la création d'une tâche, une faute de frappe évidente
  (« Onborading ») ou une tâche existante à la faute près est signalée sous le champ ; `Tab`
  corrige, `Entrée` garde le nom tapé.
- **Apprentissage** : les mots des titres vus sont comptés pour la tâche que la base désigne un
  quart d'heure plus tard — une bascule antidatée ou corrigée entre-temps est donc prise en compte
  —, jamais pendant une réunion (table `task_hints` : tâche, mot, secondes — jamais un titre
  entier). Une fenêtre déjà vue dix minutes avec « Kestrio Siren seul » la suggère même si son nom
  ne dit pas « Kestrio ». La raison précise alors « fenêtre déjà vue avec cette tâche ».
Désactivable dans Paramètres > « Suggestions de tâche » (suggestions, apprentissage et
proposition de changement séparément, plus « Oublier ce qui a été appris »).

### Assistant IA (optionnel, désactivé par défaut)
Paramètres > « Assistant IA » : fournisseur (**Gemini** avec une clé AI Studio, **compatible
OpenAI** — OpenAI, Mistral, ou **Ollama / LM Studio en local**, auquel cas rien ne sort du poste —,
**Anthropic**), clé, modèle, et un bouton « Tester la connexion ». Puis, dans « Gérer les
tâches », **« ✨ Proposer avec l'IA… »** envoie les **noms** des tâches (et leur nombre d'entrées,
rien d'autre : ni heures, ni titres de fenêtres) et affiche fusions et renommages proposés,
à cocher. La tâche en cours n'est jamais fusionnée. Modèle Gemini par défaut :
`gemini-3.5-flash-lite` (500 requêtes par jour sur le niveau gratuit).

Seconde case, **coupée par défaut** : « Laisser l'IA proposer des noms d'après mes fenêtres ».
Au changement de tâche détecté et à l'ouverture du sélecteur, elle envoie des **mots-clés** tirés
des titres (au plus 20 mots, adresses e-mail retirées, jamais un titre entier), le nom des
applications, celui de la tâche en cours et ceux des tâches (hors réunions). Au plus un appel
toutes les 5 minutes ; ce qui est parti est affiché à côté de la proposition. ⚠️ Ces mots peuvent
contenir des noms de clients : vérifier la politique IA de l'entreprise, et savoir qu'un niveau
gratuit autorise le fournisseur à réutiliser les données.

### Détection de réunion

Les réunions sont pointées sans rien avoir à déclarer.

- **Déclencheur** : une application de réunion **capte le micro**. Windows publie cette
  information en temps réel (`ConsentStore\microphone`), ce qui marche aussi bien pour Teams
  natif que pour Teams ou Google Meet **dans un onglet**, ou pour Zoom. La vue de réunion Zoom
  est en plus reconnue à sa fenêtre, donc détectée même micro coupé.
- **Filtre** : seules les applications de la liste comptent — Discord, la dictée Windows ou
  OBS captent le micro sans qu'il s'agisse d'une réunion.
- **Bascule automatique** sur la tâche « Réunion », **antidatée au premier indice** : la
  première minute de réunion n'est pas perdue.
- **Notification annulable** : un clic sur la bulle défait la bascule et **rend le temps** à la
  tâche précédente, comme si la détection n'avait pas eu lieu.
- **Reprise automatique** de la tâche d'avant à la fin de la réunion (si une tâche tournait —
  un suivi en pause ou arrêté n'en a aucune à reprendre).
- **Réunion nommée pendant qu'elle dure** : dès qu'un titre exploitable est connu (le sujet de
  l'agenda l'est dès la bascule), l'entrée est réaffectée à « Réunion — Point Hebdo PRJ - Velmora »
  — visible dans le tray et le tableau de bord. La tâche « Réunion » elle-même n'est jamais
  renommée, sans quoi tout l'historique des réunions changerait de libellé. Rien n'est fait si le
  titre n'apprend rien (« Zoom Workplace »), ou si tu as déjà réaffecté l'entrée toi-même. Le
  titre relevé est de toute façon écrit en commentaire de l'entrée (exporté).
- **Réunions marquées 👥 dans le tableau de bord**, en plus du drapeau déjà présent à l'export.
  Sur les lignes agrégées (totaux, semaine), la marque n'apparaît que si *toutes* les entrées de
  la tâche sur la période sont des réunions.
- **Rappels mis en sommeil** pendant la réunion : « Toujours sur cette tâche ? » n'arrive plus
  au milieu d'une prise de parole.
- **Temporisations** : la réunion doit durer un peu avant la bascule (60 s par défaut), et une
  disparition brève du micro — micro coupé, changement de périphérique — ne la coupe pas en
  plusieurs entrées (grâce de 120 s).

Réglages avancés, modifiables seulement dans la table `settings` de la base :
`meeting_apps` (liste des applications), `meeting_start_delay_seconds`,
`meeting_end_grace_seconds`.

> ⚠️ Une réunion Teams suivie **micro coupé dès le départ** ne se distingue pas d'un Teams
> simplement ouvert, et Zoom n'écrit jamais le sujet dans le titre de ses fenêtres. Dans les deux
> cas c'est l'**agenda** qui répond (ci-dessous) — pas un motif de titre supplémentaire.

### Agenda Outlook

Lu par automation COM dans l'**Outlook classique déjà ouvert** : aucune connexion, rien ne sort
du poste, ça marche hors ligne. Jamais démarré par TimeTracker ; s'il est fermé, l'agenda est
simplement indisponible. Le « nouveau Outlook » n'expose pas de COM.

- **Le micro déclenche, l'agenda nomme mieux que la fenêtre.** Une réunion détectée pendant un
  créneau porte le sujet de l'agenda — c'est ce qui nomme enfin les réunions Zoom.
- **Question au début du créneau** : « Oui, pointer la réunion » pointe même sans micro (tu
  écoutes, tu es au téléphone) jusqu'à la fin prévue ; « Non » ou pas de réponse ne bascule pas,
  mais si tu y participes quand même la détection prend le relais avec le bon nom.
- **Connecté en avance** : le créneau qui arrive sur une réunion déjà détectée la nomme sans la
  redémarrer. Le début pointé est le plus tôt entre le premier indice et l'heure du créneau.
- **Réunions qui se suivent** : quand le créneau en cours est terminé et qu'un seul autre le
  suit, la réunion est **coupée à la frontière** — une entrée par réunion, sans question.
- **Plusieurs réunions en même temps** : la question montre **un bouton par réunion** (horaire,
  organisateur, ta réponse Outlook). Si une autre commence pendant la tienne : « Passer à… » ou
  « Rester sur… » ; sans réponse rien ne bouge.
- Les réunions **annulées** et les rendez-vous **sans invité** (trajets, blocs personnels) sont
  ignorés.

`Diagnostic agenda Outlook.bat` (`--outlookprobe`) dépose sur le Bureau ce que l'agenda a
répondu : présence d'Outlook, filtre envoyé, réunions lues.

**Journal de diagnostic** (`--meetingtrace`) : les heuristiques de titres ne peuvent être réglées
qu'en ayant vu de vraies réunions — c'est lui qui a servi à les régler, et il reste là pour une
prochaine fois. Ce journal note, à chaque *changement* de relevé, qui capte le
micro, les fenêtres des applications de réunion (process, classe, titre) et chaque décision prise
— y compris les bascules annulées, qui signalent les faux positifs. Les titres de fenêtres de
navigateur y sont **masqués** sauf s'ils ressemblent à une réunion : un journal destiné à être
transmis n'a pas à contenir une semaine d'historique de navigation.

### Gérer les tâches
Fenêtre dédiée (menu du tray, ou bouton **★ Tâches** du tableau de bord) pour nettoyer la
liste proposée par le sélecteur :
- **Doublons probables** signalés en tête (coquille, accent, majuscule, espace : « Conflig Flow 1
  et 2 » ≈ « Config Flow1 et 2 ») avec un bouton **Fusionner** par paire. Comparés mot à mot :
  deux noms au même gabarit mais d'un client ou d'un sens différent ne sont pas des doublons. Le
  nom sans faute est celui qui reste. Calculé sur le poste.
- **Renommer** une tâche mal orthographiée — l'historique déjà enregistré suit.
- **Fusionner** deux doublons (« Regardez Youtube » → « Regarder Youtube ») : les entrées sont
  rattachées à la tâche cible, aucun temps n'est perdu.
- **Supprimer** une tâche jamais utilisée. Une tâche qui porte des heures n'est pas supprimable
  (l'historique serait amputé) : la fusion est proposée à la place.
- **Favoris (★)** : les tâches courantes remontent en tête du sélecteur.
- Filtre par nom et vue « favoris seulement » ; nombre d'entrées, total et dernière
  utilisation affichés pour chaque tâche.

### Correction à chaud — `Ctrl+Alt+E`
- Rattacher l'entrée en cours à une **autre tâche** (sans renommer l'historique).
- **Antidater** l'heure de début (saisie libre ou boutons −5 / −15 min) ; l'entrée
  précédente est automatiquement rognée pour éviter tout chevauchement.
- **Annuler** l'entrée en cours (et arrêter le suivi).
- **Supprimer** la dernière entrée terminée.

### Tableau de bord
- **Barre de suivi** en haut : tâche en cours et sa durée, puis *Changer de tâche*,
  *Corriger*, *Pause / Reprendre*, *Arrêter*, *★ Tâches*, *Paramètres* — plus besoin de
  repasser par l'icône du tray.
- Bandeau **« N noms à vérifier »** quand des fautes de frappe ou des doublons probables sont
  repérés : **Vérifier…** ouvre la liste à cocher (fusions et renommages), et « Proposer aussi
  avec l'IA » y ajoute, sur un clic, les propositions de l'assistant. Une proposition cochée
  qu'on décoche puis valide ne revient plus.
- Onglet **Jour** : liste des entrées, total par tâche, total du jour, navigation
  jour par jour. Rafraîchissement en temps réel.
- Onglet **Semaine** : tableau croisé tâche × jour, navigation de semaine en semaine.
- **Édition** d'une entrée terminée : double-clic (ou clic droit → Modifier) pour
  changer tâche, date, heure de début et de fin. Clic droit → Supprimer.
  L'entrée en cours se corrige via `Ctrl+Alt+E`.

### Export
- **CSV** : UTF-8 avec BOM, séparateur `;` — s'ouvre directement dans Excel.
  Bloc détail puis récapitulatif par tâche.
- **Excel (.xlsx)** : feuille *Détail*, feuille *Par tâche* (avec part en %), et
  feuille *Par jour* (croisé, en heures décimales) dès que la période dépasse un jour.
- Chaque export porte sur la période de l'onglet affiché (le jour ou la semaine) ; le bouton
  **Autre période…** exporte une plage libre (ce mois, le mois dernier, 30 derniers jours, ou
  deux dates au choix).
- Les durées sont fournies en `7h05` **et** en heures décimales (`7,08`) pour la timesheet.

### Paramètres
Intervalle de rappel, son, popup au lancement, seuil d'absence et objectif d'heures
hebdomadaire, suggestions d'après les fenêtres, réunions et agenda, **trois raccourcis
personnalisables** (capture de la combinaison au clavier), démarrage avec Windows, dossier
d'export par défaut.

> Les libellés de dates sont toujours en français ; les **nombres** exportés suivent le
> réglage régional Windows, pour qu'Excel les lise comme des nombres et non comme du texte.

## Prérequis dev

- .NET 8 SDK ou plus récent (`winget install Microsoft.DotNet.SDK.8`) — un SDK 9 compile aussi
  la cible `net8.0-windows`, à condition que le runtime Windows Desktop 8 soit présent.

## Build & run (dev)

```bash
dotnet build TimeTracker.csproj
```

```bash
dotnet run --project TimeTracker.csproj
```

L'exécutable de sortie : `bin\Debug\net8.0-windows\win-x64\TimeTracker.exe`.

### Options de ligne de commande

| Option | Effet |
|--------|-------|
| `--nostartpopup` | Démarre sans le popup de début de journée |
| `--dashboard[=semaine]` | Ouvre directement le tableau de bord (onglet Jour ou Semaine) |
| `--db=<chemin>` | Utilise une autre base (tests, sans toucher au relevé réel). Le démarrage avec Windows n'est alors jamais modifié |
| `--selftest` | Exerce services, export et réglages sur une base jetable, journalise, puis quitte. Fonctionne même si TimeTracker tourne déjà |
| `--uitest[=<dossier>]` | Rend chaque fenêtre hors écran (détecte les erreurs XAML au rendu) ; avec un dossier, enregistre une capture PNG par fenêtre. Fonctionne même si TimeTracker tourne déjà |
| `--meetingprobe[=<fichier>]` | **Instantané** : relève micro, fenêtres et verdict de détection de réunion, puis **quitte** (aucun suivi du temps). Pour une vérification ponctuelle pendant une réunion. Fonctionne même si TimeTracker tourne déjà |
| `--meetingtrace[=<fichier>]` | Suivi normal **plus** un journal de diagnostic continu (défaut : `%APPDATA%\TimeTracker\meetingtrace.log`). Une ligne à chaque changement de relevé, pas à chaque tour. C'est ce qu'il faut pour collecter plusieurs jours de données |
| `--meetingobserve` | Les réunions sont détectées et journalisées mais **aucune bascule** n'a lieu : le relevé de temps n'est jamais modifié. À combiner avec `--meetingtrace` |

## Package autonome

Produit un **exe unique self-contained** (runtime .NET inclus → aucun prérequis
sur le poste cible), à copier dans un dossier :

```bash
dotnet publish TimeTracker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o <dossier_sortie>
```

Le copier sur le poste cible et double-cliquer. Voir `LISEZMOI.txt` (inclus dans le package).

## Structure

```
TimeTracker/
├── App.xaml(.cs)              # Orchestrateur : services, wiring, raccourcis, cycle de vie, tests
├── Core/
│   ├── Models/                # TaskItem, TaskUsage, TimeEntry, AppSettings, Hotkey,
│   │                          #   MeetingSignal, MeetingInfo
│   └── Services/
│       ├── DatabaseService    # SQLite (schéma + CRUD + plages de dates)
│       ├── TimerService       # tâche active, ticks, rappels, corrections
│       ├── MeetingDetector    # décide du début et de la fin d'une réunion
│       ├── MicrophoneProbe    # qui capte le micro maintenant (déclencheur)
│       ├── MeetingWindowProbe # fenêtres de réunion (nomme, et conclut pour Zoom)
│       ├── ActivityProbe      # fenêtre au premier plan toutes les 5 s, en mémoire (30 min)
│       ├── TaskSuggester      # tâche existante suggérée d'après les mots des fenêtres
│       ├── ActivityShiftDetector # changement de tâche probable (hystérésis, silences)
│       ├── ActivityNaming     # nom de tâche tiré d'un titre de fenêtre (sur le poste)
│       ├── ActivityLearning   # apprentissage différé d'après les entrées en base
│       ├── TaskSimilarity     # doublons mot à mot, fautes de frappe (vocabulaire des noms)
│       ├── NameReview         # « noms à vérifier » et « Tu voulais dire … ? »
│       ├── Ai/                # fournisseurs IA, nettoyage des tâches, suggestion d'après l'activité
│       ├── HotkeyService      # RegisterHotKey Win32, (ré)enregistrement à chaud
│       ├── StartupService     # entrée registre démarrage Windows
│       ├── ExportService      # CSV et XLSX (ClosedXML)
│       ├── TimeInput          # lecture des heures saisies (9h05, 0905, 9:05…)
│       ├── AppCulture         # libellés en français, nombres en culture système
│       └── Logger             # journal %APPDATA%\TimeTracker\log.txt
└── UI/
    ├── TrayIconManager        # NotifyIcon + menu contextuel
    ├── TaskSelectorPopup      # sélection / création de tâche (favoris, décalage 5/15 min)
    ├── ReminderPopup          # rappel périodique (suivi en cours ou en pause)
    ├── ActivityShiftWindow    # « Tu as changé de tâche ? » (non modale, bas-droite)
    ├── AiSuggestionsWindow    # propositions à cocher (IA, noms à vérifier)
    ├── QuickEditWindow        # correction à chaud (Ctrl+Alt+E)
    ├── EntryEditWindow        # édition d'une entrée terminée
    ├── TaskManagerWindow      # bibliothèque des tâches (renommer / fusionner / favoris)
    ├── TrackerActions         # commandes de suivi câblées sur la barre du tableau de bord
    ├── SettingsWindow         # paramètres
    └── DashboardWindow        # barre de suivi + onglets Jour / Semaine + export
```

## Données

- Base : `%APPDATA%\TimeTracker\timetracker.db` (SQLite, mode WAL).
- Journal : `%APPDATA%\TimeTracker\log.txt`.
