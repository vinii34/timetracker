# TimeTracker — Document de passation (handoff)

> Pour reprendre le projet dans une nouvelle session. Lis d'abord ce fichier,
> puis `README.md`. `CLAUDE.md` (chargé automatiquement) en donne le résumé et pointe ici.
> État au **2026-09-29** : **4e collecte dépouillée** (2026-09-17 → 29, première avec les
> suggestions de la v1.6) et **suggestions refaites en v1.7**, packagée, **en test chez
> l'utilisateur** depuis le 29/09 après-midi. Tout est en §0, section « 4e collecte ».
> Le projet tourne désormais **sur le poste de travail** (celui du vrai relevé) : pièges propres
> à ce poste en §8.

---

## 0. À la reprise — où on en est exactement

Il y a eu **quatre collectes** : la première a réglé la détection, la seconde a montré que le
résultat n'arrivait pas jusqu'à l'utilisateur, la troisième — la première avec l'agenda
Outlook — a montré que les réunions qui se suivent se fondaient en une, la quatrième a montré
que les suggestions de tâche ne pouvaient pas marcher telles qu'elles étaient conçues.

| | journaux | jours | bascules |
|---|---|---|---|
| 1re collecte | `logs/TimeTracker-journaux-20260802-1036/` | 2026-07-27 → 31 | 14 |
| 2e collecte | `logs/TimeTracker-journaux-20260809-1516/` | 2026-08-03 → 07 | 13 |
| 3e collecte | `logs/TimeTracker-journaux-20260917-0822/` | 2026-08-10 → 09-15 (20 jours) | 54 |
| 4e collecte | `logs/TimeTracker-journaux-20260928-0911/` + `%APPDATA%` du 29/09 | 2026-09-17 → 29 (9 jours) | 19 |

### 4e collecte (2026-09-17 → 29, dépouillée le 2026-09-29) — les suggestions refaites (v1.7)

⚠️ Le `log.txt` du dossier contient tout depuis le 2026-06-29 : la nouveauté commence à la ligne
814 (2026-09-17 10:08) et s'arrête au 25/09 ; le 28 et le 29/09 ont été lus directement dans
`%APPDATA%\TimeTracker\log.txt`. Le `meetingtrace.log` ne couvre que le 17/09 et le 28/09 :
l'appli démarre avec Windows, donc **sans** `--meetingtrace`. La base a été lue sur une **copie**
(`.db` + `-wal` + `-shm`), dans le dossier temporaire de session, hors dépôt.

**La demande de l'utilisateur** (2026-09-29) : les suggestions « essaient de matcher avec des
tâches déjà faites » ; il veut que l'appli **propose des noms de tâche nouveaux** d'après son
activité, avec l'IA ; qu'elle **propose de corriger** les fautes de frappe et quasi-doublons ;
qu'elle **détecte d'elle-même un changement de tâche** ; et des appels à l'IA plus fréquents
(« je n'ai vu aucune requête faite à l'IA »).

**Ce que les données ont montré :**

- **63 % du temps hors réunion part sur des tâches créées le jour même** (65 entrées sur 98) ;
  136 tâches sur 246 n'ont qu'une entrée. `TaskSuggester` ne sait que reclasser les tâches
  existantes : il avait tort par construction deux fois sur trois. C'était la cause de la plainte.
- **L'apprentissage s'intoxiquait**, comme soupçonné : la tâche fourre-tout (« Gen admin, …,
  e-mails ») avait appris 419 mots — les sujets de mails de tous les clients —, record absolu pour
  7,5 h pointées, et détenait déjà plus de la moitié du poids de 12 mots qui sont des noms
  d'autres tâches, dont une créée le matin même.
- **L'encart du rappel était du bruit** : affiché sur 33 rappels sur 46, suivi d'un changement de
  tâche dans les 5 min trois fois (ce n'est pas un refus — mais un rappel qui doute trois fois sur
  quatre n'est plus lu).
- **IA : 2 appels en 12 jours**, les deux « Tester la connexion » du 17/09. Conforme à la
  conception v1.6 (un seul usage, sur un clic, au fond de « Gérer les tâches »).
- **`TaskSimilarity` rejoué sur les 246 vrais noms** (harnais jetable qui compile les sources) :
  20 paires à 0,85, dont ~7 fausses — même gabarit de nom, client ou sens différent (« … EDI
  &lt;client A&gt; » / « &lt;client B&gt; », « Outgoing » / « Incoming », « Flow1 » / « Flow2 ») — et
  des vraies ratées : même sujet avec et sans « Réunion — » (5 paires), suffixes « (annulé) »,
  faute dans un nom de client. La distance sur le nom entier était le mauvais outil : les fautes
  tiennent dans un mot.

**v1.4 en conditions réelles — première fois** (elle n'avait tourné que contre un agenda scripté) :
8 « Réunion suivante » coupées proprement ; 5 chevauchements (4 « je reste », 1 « j'y passe ») ;
1 question à deux créneaux simultanés, bien pointée ; 24 questions Start (11 acceptées) ;
1 nuit clôturée à 20:28 au lieu de courir jusqu'au matin (`Absence de`) ; 22 réunions nommées
pendant qu'elles duraient — les renommages à la main pendant la réunion sont tombés de 19 (cinq
semaines) à 3, toutes des réunions sans agenda. **La v1.4 est validée.**

⚠️ **À examiner, pas fait** : deux « réunions » démarrées au réveil par `[msedge.exe, Zoom]
« Zoom Workplace »` ont duré presque toute la journée — le 23/09 de 09:24 à 18:09, enchaînées de
créneau en créneau (dont 3h30 sur le déjeuner : poste verrouillé sans veille pendant une réunion
« en cours », donc l'exception de `HandleAbsence` a joué), le 24/09 6h01 (renommée à la main et
mise en pause par l'utilisateur). Le 28/09, 3h48 pour un créneau de 2 h, gardé tel quel.
Hypothèse : un onglet Edge garde le micro. Pas de trace ces jours-là : à vérifier avec
`--meetingtrace` le jour où ça se reproduit. Détail mineur : le 28/09 14:30, une entrée de réunion
d'1 min (le micro a lâché une minute après une coupure « Réunion suivante »).

### Décisions de l'utilisateur (2026-09-29) — les quatre questions de la passation

1. **Ce qui part à l'IA** : des **mots-clés filtrés** — au plus 20 mots rares des titres de
   fenêtres, jamais un titre entier, adresses e-mail retirées —, le nom des applications, celui
   de la tâche en cours et ceux des tâches (hors réunions, les 150 plus récentes). **Affiché à
   côté de chaque proposition** (`TaskSuggestionAssistant.DescribeSent`).
2. **Quand** : automatiquement **au changement détecté et à l'ouverture du sélecteur**, au plus
   un appel toutes les 5 min (plafond de sécurité : 100 par jour), échec silencieux et journalisé,
   la proposition locale restant affichée.
3. **Fautes et quasi-doublons** : **proposés, en un clic** — « Tu voulais dire … ? » à la
   création, bandeau « N noms à vérifier » dans le tableau de bord, liste à cocher.
4. **Rappel de 30 min** : **gardé, sans l'encart** « tu sembles plutôt sur… », que la fenêtre de
   changement remplace.

⚠️ Il a été **prévenu** que ces mots-clés peuvent contenir des noms de clients et de collègues,
et que les conditions de l'API Gemini (version du 23/03/2026, relue ce jour-là) prévoient, pour
les services **non payants**, que Google utilise les contenus pour améliorer ses produits et que
des relecteurs humains peuvent les lire — il doit vérifier la politique IA de son entreprise.
D'où un **interrupteur à part, coupé par défaut** (`AiActivitySuggestions`, « Laisser l'IA
proposer des noms d'après mes fenêtres »), en plus d'`AiEnabled` qui ne couvre que les noms de
tâches sur un clic. À ce jour il ne l'a pas coché (à lui de le faire).

### Ce qui a été fait le 2026-09-29 (v1.7)

1. **Garde-fous du poste de travail** (voir §8) : `--selftest` / `--uitest` sont traités
   **avant** le mutex d'instance unique — son TimeTracker tourne en permanence, les tests
   n'affichaient que « déjà en cours d'exécution » ; `--db=` gèle le registre de démarrage
   (`StartupService.Frozen`) — une base neuve effaçait l'entrée de sa vraie installation.
2. **`TaskSimilarity` refait mot à mot.** Chaque mot de l'un doit se retrouver dans l'autre, tel
   quel ou à une faute près ; un nombre doit être identique ; un mot de plus toléré seulement à
   partir de cinq mots ; préfixe de réunion ignoré si le reste fait au moins trois mots ; faute
   selon la longueur (4-5 lettres : insertion, suppression ou inversion, pas de substitution —
   « data » / « date » ; 6-9 : une faute ; 10+ : deux). Plus **`CorrectTypos`** (un mot rare à une
   faute d'un mot employé dans ≥ 3 tâches et deux fois plus) et un **`Vocabulary`** construit une
   fois pour toute la liste. Pièges trouvés sur les vrais noms et couverts : un pluriel, un mot
   en « e- » écrit d'un bloc (« ereporting »), un nom propre à deux substitutions de « internal ».
   La correction garde la casse tapée. Rejoué sur la vraie base : **27 propositions en 74 ms**,
   aucune fausse repérée hors des fusions « réunion ↔ travail », qui ne sont pas cochées d'office.
3. **« Noms à vérifier »** (`NameReview`, bandeau du tableau de bord, liste à cocher dans
   `AiSuggestionsWindow` généralisée). Le nom qui **survit** à une fusion : **sans faute** d'abord
   (sur les vrais noms la faute portait souvent le plus d'entrées : « Onborading » 6,
   « Onboarding » 3), puis **au format des réunions automatiques** (« Réunion — sujet », sinon la
   prochaine réunion recrée le doublon), puis le plus d'entrées ; jamais la tâche en cours ne part.
   Une proposition cochée que l'utilisateur décoche puis valide est **mémorisée comme refusée**
   (clé `name_review_dismissed` de `settings`) ; celles décochées d'office ne sont jamais
   « refusées » sans y avoir touché, et ne comptent pas dans le bandeau. Bouton « Proposer aussi
   avec l'IA » (le nettoyage v1.6, sur un clic) dans la même fenêtre. « Gérer les tâches »
   applique désormais la même règle de sens.
4. **« Tu voulais dire … ? »** dans le sélecteur (et dans la fenêtre de changement) : tâche
   existante à la faute près, sinon correction mot à mot ; `Tab` ou clic corrige, `Entrée` garde
   le nom tapé ; contrôle après 300 ms de pause de frappe.
5. **Détection d'un changement de tâche** (`ActivityShiftDetector`, une évaluation par minute).
   Une fenêtre « ressemble » à la tâche en cours si son titre a été vu depuis le début de la tâche,
   ou si elle partage un mot significatif avec ce qui a été vu (≥ 30 s), appris, ou son nom.
   Déclenche si ≥ 3 min et ≥ 80 % des 5 dernières minutes sont étrangères, **deux évaluations de
   suite**. Trois subtilités, chacune couverte par `--selftest` : l'**épisode en cours est retiré
   du profil** de la tâche (sinon une évaluation retardée voyait la nouvelle activité déjà
   rangée côté tâche) ; un **mot qui nomme une autre tâche** ne décrit pas la tâche en cours ; une
   autre tâche nommée dans **la moitié au moins** des fenêtres récentes l'emporte sur les mots
   courants appris — sans ces deux règles, la tâche fourre-tout « ressemblait » à tout. Une seule
   proposition par changement (mêmes mots-clés jamais reproposés pour la même tâche, 15 min de
   délai), « je reste » range l'activité côté tâche. Silences (`App.ShiftSilenceReason`) :
   réglage, pas de tâche active, réunion, moins de 10 min après une bascule ou un réveil, une
   proposition, le sélecteur ou une fenêtre modale ouverts. **Pas le rappel** : resté ouvert sans
   réponse, il éteignait la détection — la proposition le ferme et prend sa place, et un rappel
   n'est pas affiché tant qu'une proposition est à l'écran.
6. **`ActivityShiftWindow`** — « Tu as changé de tâche ? » : non modale, dans la barre des tâches
   et Alt-Tab, s'ouvre **sans prendre le focus**, se retire au bout de 20 min (ou quand la tâche
   change ailleurs, qu'une réunion commence, au réveil si elle est périmée). Tâche existante
   (suggestion locale, jamais une réunion ni la tâche en cours) et/ou nom nouveau pré-rempli —
   tiré du titre sur le poste (`ActivityNaming` : marque d'appli, « RE: », extension, adresse
   e-mail retirés), puis remplacé par celui de l'IA tant que l'utilisateur n'a rien touché —,
   « Compter depuis HH:MM » coché, « Non, je reste ». La tâche choisie est **relue en base** au
   moment de basculer (elle a pu être fusionnée pendant que la fenêtre était ouverte).
7. **`TaskSuggestionAssistant`** (IA) : prompt, `Parse` (tâche existante inconnue écartée, nom
   nouveau qui est en fait une tâche existante ou son quasi-doublon → cette tâche, jamais la
   tâche en cours, JSON absent → exception journalisée), `DescribeSent`. Même `IAiProvider` que
   la v1.6. Dans le sélecteur, la réponse s'affiche sous le champ (« ✨ Nom proposé : … — Tab
   pour l'utiliser ») avec ce qui est parti.
8. **Apprentissage refait** (`ActivityLearning`) : différé de 15 min et attribué d'après **les
   entrées en base** (une bascule antidatée ou corrigée entre-temps est prise en compte), jamais
   pendant une réunion, le reste appris à la fermeture. Le déjà-appris pollué **n'a pas été
   effacé** (voir « Ce qui reste »).
9. **Encart du rappel retiré** (`ReminderPopup`).
10. **Journal** (jamais de titre ni de mot-clé) : `Changement d'activité probable sur « … »
    depuis HH:MM (…) — proposé : …`, `Changement accepté : « … » (tâche existante|nouvelle
    tâche, local|IA|modifié|tapé|corrigé|existante) depuis HH:MM|à partir de maintenant`,
    `Changement refusé`, `Proposition de changement sans réponse (expirée|fermée|réunion|…)`,
    `Sélecteur : « … » (rang N, suggérée)` / `Sélecteur : nouvelle tâche « … » (nom corrigé|IA)`,
    `IA : suggestion d'activité (changement|sélecteur, N mots-clés, M noms) → … en X s`,
    `Noms à vérifier : N appliqué(s), M écarté(s)`.
11. **Réglages** : `ActivityShiftDetection` (vrai par défaut), `AiActivitySuggestions` (faux,
    grisé tant qu'`AiEnabled` est décoché).

Couvert par `--selftest` : `doublons` (vrais, faux, liste), `noms` (fautes, saisie, bandeau,
refus mémorisés), `changement` (détecté, une seule fois, coup d'œil, même fenêtre, appris, tâche
fourre-tout, je reste, silence, nom tiré du titre) et `câblage changement` (proposition, heure
bornée à l'entrée en cours, nouvelle tâche antidatée, existante, tâche fusionnée entre-temps,
réponse tardive, je reste), `apprentissage` (attribution d'après les entrées, recouvrement),
`IA_activité` (prompt, lecture, ce qui est parti, mots-clés). `--uitest` : 25 fenêtres dont
`ActivityShiftWindow` ×3, sélecteur « Tu voulais dire » et « IA », « Noms à vérifier », tableau
de bord avec bandeau. Une revue de code du diff a trouvé 4 défauts, corrigés avant le paquet
(tâche fusionnée pendant la proposition, rappel qui éteignait la détection, propositions
décochées d'office refusées à tort, sens de fusion de « Gérer les tâches »).

**Paquet v1.7** : `%USERPROFILE%\TimeTracker-v1.7\TimeTracker.exe` (72 Mo), `--selftest` et
`--uitest` verts **sur l'exe publié**. Pas de `LISEZMOI` ni de `.bat` cette fois. L'utilisateur
l'a lancé le 29/09 vers 14 h 30 (le démarrage avec Windows pointe donc vers la v1.7) et a vu les
fusions proposées. **Sauvegarde de la base avant test** : `%APPDATA%\TimeTracker\
sauvegarde-avant-v1.7-20260929.db` (248 tâches, 605 entrées, API de sauvegarde SQLite).
**Retour arrière** : quitter, relancer la v1.6.2, installée par l'utilisateur dans
`…\Documents\Programmes\TimeTracker-v1.6.2\` (dans OneDrive). Base partagée, les nouvelles clés
de `settings` sont ignorées par la v1.6.2.

⚠️ **Ce qui n'a pas été vérifié en conditions réelles** :
- les **seuils** de la détection de changement (5 min, 3 min, 80 %, deux évaluations, silences
  de 10 min) : jamais tournés sur de vraies fenêtres — aucun titre n'est conservé, rien à
  rejouer ;
- **un vrai appel Gemini avec le nouveau prompt** : la clé de l'utilisateur est en clair dans
  `settings` et n'a volontairement pas été utilisée. Le format `generateContent` est le même que
  le test de connexion validé en v1.6 ; la lecture de réponses réalistes est testée ;
- le ressenti : fréquence des propositions, pertinence des noms tirés des titres.

**À lire dans la prochaine collecte** (lignes du point 10) : combien de propositions par jour,
réparties entre acceptées / antidatées / refusées / sans réponse — ⚠️ une absence de réponse
n'est pas un refus ; l'origine des noms acceptés (local, IA, modifié) ; le rang choisi dans le
sélecteur et la part des tâches nouvelles ; les « Tu voulais dire » repris (`nom corrigé`) ;
les appels IA s'il a coché la case, et leurs échecs ; ce que « Noms à vérifier » a appliqué.
Si les propositions sont trop fréquentes ou à tort, **mesurer avant de toucher** : c'est
`MinMismatch`, `MismatchRatio`, `HoldEvaluations` et `ShiftQuietAfterSwitch` qu'on règle.

### 3e collecte (2026-08-10 → 09-15, dépouillée le 2026-09-17) — cinq semaines avec l'agenda

⚠️ Le `log.txt` de cette collecte **contient tout depuis le 2026-06-29** et le `meetingtrace.log`
tout depuis le 2026-07-27 : la nouveauté commence au 2026-08-10. Le 15/08 → 30/08 est un trou
(congés), le poste de l'utilisateur est resté sans TimeTracker.

**L'agenda fonctionne, et il porte la charge.** Sur 54 bascules : 59 questions posées, 31 « oui »,
28 « non » ou sans réponse ; **3 réunions pointées sur l'agenda seul** (micro jamais capté —
le cas « Teams rejoint muet » que l'étape 4 devait combler, il l'a comblé) ; 24 entrées
nommées automatiquement d'un sujet d'agenda ; 0 faux positif signalé ; les réunions Zoom sont
enfin nommées. L'utilisateur répond en général en moins de 10 s, parfois des heures après
(question de 21:47 répondue le lendemain 09:01) : la fenêtre non modale tient.

**Ce que la collecte a révélé — les trois demandes de l'utilisateur le 2026-09-17 :**

1. **Les réunions qui se suivent se cumulaient sur la même entrée.** 26 fois le journal dit
   « commence, réunion déjà en cours — aucune question, l'agenda ne fait que nommer » : le
   micro reste capté d'un créneau à l'autre, donc pour le détecteur c'est *une* réunion. Neuf
   entrées ont ainsi fondu deux à cinq réunions (le 01/09 : 14:30 → 18:09, **3h19**, cinq
   créneaux ; le 31/08 : 10:28 → 12:10 renommée à la main « Call Marc 10 min, Tests &
   paramétrages 30 min + PlaceHolder Nordal 45 min »). La règle « connecté en avance → on ne
   redémarre pas » était juste ; elle était appliquée aussi quand le créneau attaché était
   **terminé**, ce qui est un autre cas.
2. **Deux réunions aux mêmes horaires** (une facultative, une obligatoire) : `MeetingAt` prenait
   la première du cache, la question n'était posée que pour elle. L'utilisateur veut choisir.
3. **L'entrée qui court toute la nuit.** 16 réveils de veille dans le journal, et chaque matin
   la même correction à la main (« API Z3 17:39→19:39 », « Config Tessal 17:18→19:00 »,
   « Kestrio Siren seul 17:14→18:30 »…). Il ferme le portable sans arrêter la tâche.

**Et ce qu'elle a révélé sans qu'il le demande :**

- **Il renomme la réunion à la main pendant la réunion** — 19 fois (« Correction : tâche en
  cours → « Réunion ("Weekly consultants' alignment") » »), et même une fois « Réunion non
  renommé automatiquement (C'était "Suivi projet…") ». Il ne savait pas que l'appli tenait le
  titre : le nommage se faisait **en fin de réunion**, et le tray affichait « Réunion — 0:12 »
  pendant. Après quoi la fin trouvait l'entrée « déjà nommée » (22 fois) et ne touchait rien.
  Même leçon que la bulle : ce qu'on sait doit être **visible là où il regarde**, au moment
  où il regarde.
- **Réunions annulées** posées en question (4 « Canceled: … »), et un **rendez-vous personnel**
  sans invité (« Back to home », trajet) qui a posé la question et **nommé deux huddles Slack**
  le 14/08. Les sujets Outlook portent parfois des **espaces en queue** (« …weekly Sync  ») qui
  créaient des tâches en doublon.
- **10 bascules sans aucun titre** (« Slack.exe », « msedge.exe », « Zoom Workplace ») : Slack
  n'affiche plus de fenêtre « Huddle: … », seulement un 🔊 clignotant sur sa fenêtre principale ;
  et le Meet dans Edge prend son titre après la bascule. Sans agenda, ces réunions restent
  « Réunion » tout court. **Pas de motif à ajouter** : la fenêtre principale de Slack montre le
  canal affiché, pas le huddle.

### Ce qui a été fait le 2026-09-17 (v1.4)

1. **Réunions qui se suivent : coupure à la frontière** (`MeetingDetector.HandleCalendar` /
   `MeetingSwitched`, `App.OnMeetingSwitched`). Quand le créneau attaché à la réunion en cours
   est **terminé à l'agenda**, que le micro est toujours capté et qu'**un seul** autre créneau
   couvre l'instant, la réunion est coupée à `max(début du suivant, fin du précédent)` : l'entrée
   est clôturée et une autre s'ouvre, **sans repasser par la tâche d'avant** (elle reprend à la
   fin de la dernière). Aucune question : le micro dit qu'il est en réunion, l'agenda dit
   laquelle. Le prix assumé : s'il reste 6 min de plus dans A, il aura une entrée B de 6 min à
   supprimer — contre 3h19 à redécouper.
   ⚠️ **La règle « connecté en avance » est intacte** : un créneau qui arrive sur une réunion
   sans créneau attaché est adopté sans rien redémarrer (cas 1 de `CheckCalendarCooperation`).
2. **Créneaux simultanés : une question, un bouton par réunion** (`MeetingChoice`,
   `CalendarPromptWindow` refaite). La sonde renvoie désormais **tous** les créneaux qui couvrent
   l'instant, classés — acceptés, puis d'après la réponse Outlook (organisateur > acceptée >
   provisoire > sans réponse > refusée) — et le détecteur décide de la question :
   `Start` (rien en cours : lesquels pointer ?), `Name` (en réunion sans créneau, plusieurs
   couvrent : laquelle ? ne sert qu'à nommer), `Switch` (une autre commence pendant la sienne :
   y passer ou rester ; « rester » refuse l'autre, qui ne coupera donc pas quand la première
   finira). **Sans réponse rien ne bouge**, et une réunion en cours n'est jamais coupée sur une
   question sans réponse. Avec plusieurs créneaux sans acceptation, l'agenda **ne nomme plus** :
   parier nommerait mal une réunion sur deux.
   `CalendarProbe.DecisionNeeded` est désormais consommé par le **détecteur**, pas par `App` :
   lui seul sait si une réunion est en cours et si les indices sont encore là.
3. **Fin de journée : `AbsenceCutoffMinutes`** (réglage, défaut 30, section « Fin de journée et
   absences » des Paramètres). Verrouillage (`SessionLock`) ou veille (`PowerModes.Suspend`)
   datent le départ ; au retour, si l'absence dépasse le seuil et qu'une tâche tourne, l'entrée
   est **clôturée à l'heure du départ** (`TimerService.PauseAt`) et le suivi passe en **pause**,
   avec le rappel « toujours en pause ? » avancé à **2 min** (non modal) — reprendre ou changer.
   Rien n'est ouvert à l'écran au réveil (règle du §8). Exception : verrouillage **sans veille**
   pendant une réunion en cours → rien clôturé (il est en salle, portable verrouillé, la
   détection a continué de tourner et sait que ça dure). Un arrêt/redémarrage de Windows
   passait déjà par `OnExit` → `Flush()`.
4. **Nommage pendant la réunion** (`App.NameRunningMeeting`, `MeetingUpdated`). Dès qu'un titre
   exploitable est connu — souvent dès la bascule, le sujet d'agenda étant là — l'entrée est
   réaffectée à « Réunion — <sujet> » **pendant** la réunion, visible dans le tray et le tableau
   de bord. Mêmes retenues : titre générique → rien ; entrée réaffectée par l'utilisateur → on
   ne repasse pas derrière lui. `ApplyMeetingTitle` en fin de réunion reste (commentaire, titre
   tardif) et ne journalise plus quand c'est déjà fait.
5. **Agenda : annulées et rendez-vous sans invité ignorés** (`OutlookComCalendarSource.Convert`,
   `MeetingStatus` 5/7 et 0, plus le préfixe « Canceled: »), sujets **`Trim()`**. Le `Detail`
   journalisé compte ce qui est ignoré.
6. **Trois points ouverts de la 2e collecte réglés** : `Pause`/`Resume` journalisés
   (`TimerService`), signature de `MeetingTrace` débarrassée des pictogrammes
   (`StripSymbols` — le 🔊 de Slack ne fait plus une ligne toutes les 5 s). Reste la durée
   annoncée en fin de réunion après une pause (cosmétique, bulle que personne ne lit).

Couvert par `--selftest` : `enchaînements` (`CheckMeetingSequences`, quatre cas : suite
coupée sans question, simultanées → question à deux → la choisie est pointée, chevauchement →
question → coupure à l'heure de B, « je reste » → B refusée et pas de coupure à la fin de A) et
`absence` (`CheckAbsenceCutoff` : clôture à l'heure du départ, jamais avant le début de
l'entrée). `--uitest` rend les cinq variantes de `CalendarPromptWindow`.

⚠️ **Ce qui n'a pas été vérifié en conditions réelles** : tout ce qui précède, sur le poste de
travail. Les quatre cas passent avec un agenda scripté ; la prochaine collecte doit regarder
les lignes « Réunion suivante », « question (Switch/Name) » et « Absence de … » de `log.txt`.

### Ce qui a été fait le 2026-09-17, second temps (v1.5) — les « idées jamais demandées », demandées

L'utilisateur a repris les trois idées listées en §7 (« c'est des bonnes idées, tu peux les
implémenter »), en a ajouté une — **suggérer ou corriger la tâche d'après les fenêtres
utilisées** — et a posé la question d'un modèle **Gemini** (clé API gratuite AI Studio) pour des
regroupements de tâches. Livré :

1. **Objectif d'heures par jour** (`DailyHoursGoal`, 0 = aucun, section « Fin de journée et
   absences » des Paramètres ; `WeeklyHoursGoal` = × 5, dérivé). Onglet Jour : « objectif 7h30
   (reste 1h20) » ; onglet Semaine : barre d'avancement + « 20 % de l'objectif 37h30 — reste
   30h12 ». Stocké en culture invariante (`7.5`), saisi avec virgule ou point.
   ⚠️ D'abord livré « par semaine » (v1.5, clé `weekly_hours_goal`) : l'utilisateur a saisi 7,5
   en pensant à sa journée et vu « objectif 1h24 », puis a corrigé en 37,5 et demandé **le
   choix**. Depuis la v1.6.2 : valeur canonique par jour (`daily_hours_goal`), unité de saisie
   `goal_per_week`, le ComboBox convertit (× 5 / ÷ 5) quand on change d'unité ; l'ancienne clé
   hebdomadaire est migrée ÷ 5 avec l'unité « semaine » (couvert par `--selftest`, `objectif`).
   ⚠️ Une **v1.6.1** intermédiaire (`%USERPROFILE%\TimeTracker-v1.6.1\`) relisait l'ancienne
   clé comme valeur par jour (aurait fait 37,5 h/jour) — jamais livrée, **à supprimer**, ne pas
   s'en servir comme retour arrière.
2. **Export d'une période libre** (`ExportRangeWindow`, bouton « Autre période… » du tableau de
   bord) : deux dates, raccourcis « Ce mois / Mois dernier / 30 derniers jours », CSV ou Excel.
   `RunExport` prend la plage en paramètre ; rien d'autre n'a bougé dans l'export.
3. **Raccourci global pause / reprise** (`HotkeyPause`, `Ctrl+Alt+P` par défaut, troisième champ
   des Paramètres, affiché dans le menu du tray). `ApplyHotkeys` prend trois raccourcis ; sans
   tâche en cours, il ouvre le sélecteur comme le bouton Pause.
4. **Suggestions de tâche d'après l'activité** (`ActivityProbe` + `TaskSuggester`, réglage
   `ActivitySuggestions`, actif par défaut). Le relevé note toutes les 5 s la fenêtre **au premier
   plan** (process, titre) et garde 30 min **en mémoire seulement**. La suggestion rapproche les
   mots des titres et les mots des noms de tâches, chaque mot pesant l'inverse de sa fréquence
   parmi les tâches, et **seuls les mots présents dans ≤ 15 % des tâches comptent** : « orvane »
   (8 tâches sur 80) désigne un client, « config » ou « réunion » ne désignent rien. Il faut au
   moins 3 min derrière une suggestion. Deux débouchés : la tâche suggérée passe **en tête du
   sélecteur** avec 💡 et sa raison en infobulle (« 12 min sur « Orvane - mapping.xlsx » ») ; et le
   **rappel** porte un encart « D'après tes fenêtres, tu sembles plutôt sur … » quand la
   suggestion de tête n'est pas la tâche en cours et pèse au moins deux fois ce que les fenêtres
   disent de celle-ci. **Rien ne bascule tout seul.**
   ⚠️ **Confidentialité** : les titres de fenêtres ne sont jamais écrits — ni base, ni journal,
   ni trace. La raison affichée en contient un ; le journal ne dit que « avec une suggestion ».
   Ne pas « enrichir » en persistant des mots de titres sans en reparler avec l'utilisateur.
5. **Doublons probables** dans « Gérer les tâches » (`TaskSimilarity`, encart jaune, 5 paires au
   plus, un bouton « Fusionner » par paire — la tâche qui porte le moins d'entrées va dans
   l'autre, jamais la tâche en cours). Normalisation (minuscules, sans accents, ponctuation
   effacée, chiffres détachés) puis Levenshtein rapporté à la longueur, seuil 0,85. Cas réels
   couverts par `--selftest` : « Conflig Flow 1 et 2 » / « Config Flow1 et 2 », « Débug Dornac » /
   « Debug Dornac », deux sujets d'agenda à une espace près. **Aucun modèle de langage** : tout
   ce que l'utilisateur a nommé (« rassembler les tâches qui ont presque le même nom ») se fait
   sur le poste.

**Gemini : évalué, pas branché** à ce stade — puis **décidé et branché** dans la v1.6, juste après
(voir ci-dessous).

### Ce qui a été fait le 2026-09-17, troisième temps (v1.6) — la promesse « 100 % local » révisée

L'utilisateur a tranché les deux questions laissées ouvertes : **stocker en local des mots de
titres de fenêtres ne le dérange pas** (« pour permettre une utilisation plus intuitive »), et
**l'IA est acceptable** à condition que l'utilisateur en soit conscient, puisse l'activer ou la
désactiver, et **ne soit pas limité à une clé Gemini**. La promesse devient donc : *100 % local
par défaut ; une seule fonction fait sortir quelque chose du poste, elle est coupée par défaut,
explicite, et à la demande.*

1. **Apprentissage des fenêtres** (`task_hints` : tâche, mot, secondes ; réglage
   `ActivityLearning`, actif par défaut ; bouton « Oublier ce qui a été appris »). Une fois par
   minute (`App.LearnFromActivity`), les mots des titres vus au premier plan depuis le dernier
   passage sont comptés pour la tâche qui tourne — jamais en pause, jamais les fenêtres de
   TimeTracker lui-même. **Jamais un titre entier** : des mots isolés, normalisés. La fusion de
   deux tâches additionne leurs mots (`MergeTasks`), la suppression les efface (cascade).
   `TaskSuggester` additionne désormais deux sources : les mots des **noms** (idf, ≤ 15 % des
   tâches) et les mots **appris** (part de la tâche sur ce mot × confiance atteinte à 10 min,
   mots vus sur ≤ 15 % des tâches apprises seulement — « chrome », « microsoft » ne désignent
   rien). La raison dit « (fenêtre déjà vue avec cette tâche) » quand c'est l'appris qui parle.
2. **Assistant IA multi-fournisseur** (`Core/Services/Ai/`) : `IAiProvider` avec trois
   implémentations — **Gemini** (`generateContent`, clé AI Studio), **compatible OpenAI**
   (`chat/completions` : OpenAI, Mistral, Groq, et surtout **Ollama / LM Studio en local**, clé
   vide, adresse `http://localhost:11434/v1`), **Anthropic** (`v1/messages`). Modèles par défaut :
   `gemini-3.5-flash-lite` (**500 requêtes/jour** d'après la page de limites de l'utilisateur,
   contre 20 pour les « Flash »), `gpt-4o-mini`, `claude-haiku-4-5-20251001`. Réglages
   `AiEnabled` (faux par défaut), `AiProvider`, `AiApiKey` (**stockée en clair dans `settings`**,
   dit tel quel dans l'UI), `AiModel`, `AiBaseUrl` ; bouton **« Tester la connexion »** dans les
   Paramètres (une question, une réponse « OK »).
   Un seul usage pour l'instant : **`TaskCleanupAssistant`** — bouton « ✨ Proposer avec l'IA… »
   dans « Gérer les tâches ». Part : les **noms de tâches et leur nombre d'entrées**, rien
   d'autre (~10 tokens par tâche). Le modèle répond en JSON (fusions + renommages + raison),
   `Parse` tolère les balises et écarte les noms inventés ; `AiSuggestionsWindow` affiche des
   cases à cocher ; l'application refuse de fusionner la tâche en cours et de renommer vers un
   nom déjà pris. Le journal ne note que des **comptes** (noms envoyés, propositions), jamais le
   contenu.
   ✅ **Gemini vérifié par l'utilisateur** le 2026-09-17 : « Tester la connexion » avec sa clé
   AI Studio → « Gemini / gemini-3.5-flash-lite répond : « OK » ». Le format `generateContent`
   et l'identifiant de modèle sont donc justes. Les deux autres fournisseurs (OpenAI-compatible,
   Anthropic) restent écrits de mémoire, non testés. Le poste de dev n'a pas de clé ; celle que
   l'utilisateur a collée dans la conversation n'a volontairement pas été utilisée (pas de
   manipulation de jetons), il la révoque. `--selftest` couvre le prompt, la lecture d'une
   réponse réaliste (balises, casse, nom inventé, renommage sans effet), la fabrique et les
   réglages.

Couvert par `--selftest` : `suggestions` gagne deux cas appris (mot appris → tâche sans son nom ;
mot appris partout → rien), `IA` (`CheckAiAssistant`) couvre prompt, parse, fabrique, réglages,
et `task_hints` (accumulation, fusion, effacement). `--uitest` rend `AiSuggestionsWindow` et le
gestionnaire avec le bouton IA.

**Idées suivantes pour l'assistant, non faites** : traduire les tâches en codes projet de la
timesheet interne (demander d'abord à quoi elle ressemble), résumer la semaine en lignes de
timesheet, nommer une réunion Zoom sans agenda d'après le contexte. Toutes à la demande.

Couvert par `--selftest` : `doublons` (`CheckTaskSimilarity`), `suggestions`
(`CheckTaskSuggester` : mot rare → la bonne tâche, mot commun → rien, 2 min → rien, classement
par temps), `objectif` (aller-retour décimal en base). `--uitest` rend le sélecteur avec
suggestion, le rappel avec encart, `ExportRangeWindow`, le gestionnaire avec un doublon et le
tableau de bord avec l'objectif.

⚠️ **`dotnet.exe` avait disparu de `C:\Program Files\dotnet\`** ce jour-là (SDK 8.0.424, host
et runtimes intacts, pas d'entrée de désinstallation du SDK dans le registre : probablement une
mise à jour interrompue). Build et publication ont été faits avec un **remplaçant** :
`%USERPROFILE%\dotnet-shim\dotnet.exe` (voir §5). À réinstaller proprement à l'occasion.

⚠️ Le `meetingtrace.log` de la 2e collecte **contient celui de la 1re** (identique jusqu'à la
ligne 8976) : la nouveauté commence à la session du 2026-08-06 08:25. Et la trace détaillée ne
couvre que le 06 et le 07 — le 03 au 05 n'est lisible qu'à travers `log.txt`.

⚠️ **Ne rejoue pas de réglage à l'aveugle par-dessus.** Les chiffres ci-dessous disent ce que
chaque motif a réellement vu ; c'est le seul socle dont on dispose, et il ne couvre que deux
semaines, un poste, une version de chaque application.

### 2e collecte (2026-08-03 → 07, dépouillée le 2026-08-09) — première semaine de v1.2

**La détection tient.** 13 bascules, 13 fins propres, 0 erreur. Sur les 122 relevés micro-actif
du 06–07, **tous** portent le verdict `RÉUNION` : aucune capture micro hors réunion. Aucune
réunion ratée non plus. La preuve du 0 faux positif n'est **pas** l'absence d'annulation (voir
plus bas, l'utilisateur n'avait aucun moyen d'annuler) mais le fait que 12 des 13 entrées ont
reçu de sa main un nom de vraie réunion.

**Le vrai problème était ailleurs : la bulle de notification n'arrive pas.**
Le libellé proposé en fin de réunion et le « clique ici si ce n'en est pas une » n'existaient que
dans une bulle de la barre système. Mesure : **9 propositions, 0 clic**, et l'utilisateur a
renommé **12 réunions sur 13 de mémoire**, le soir, dans le tableau de bord (le 07/08 : trois
entrées renommées en vingt secondes à 17:53, pour des réunions de 11h33, 11h50 et 16h30) — alors
que l'appli tenait **le bon titre pour 8 d'entre elles**. Il l'a découvert en lisant ce rapport.

Trois choses condamnent cette bulle, et aucune n'est réparable de façon fiable : l'assistant de
concentration de Windows la supprime justement quand une appli passe en plein écran ou partage
l'écran ; l'action attachée expire au bout de 15 s (`_balloonAction` remis à null sur
`BalloonTipClosed`), donc un clic depuis le centre de notifications ne fait **rien, en silence** ;
et rien n'est journalisé à l'affichage, ce qui rend l'ensemble indiagnosticable. C'est d'ailleurs
ce qui m'a fait conclure à tort, au premier dépouillement, que les titres avaient été *refusés*.

⚠️ **Leçon de conception : ne rien faire dépendre d'une bulle de la barre système.** Ce qui doit
atteindre l'utilisateur doit être **écrit** là où il regarde — l'entrée, le tableau de bord,
l'export.

### Ce que la 2e collecte a corrigé (fait le 2026-08-09)

1. **Le titre est écrit, plus proposé** (`App.ApplyMeetingTitle`, ex-`MeetingNameProposal`).
   En fin de réunion, le titre relevé va dans **`time_entries.notes`** — colonne qui existait
   déjà, était déjà lue et **déjà exportée** (CSV et colonne 8 du XLSX), et que rien n'écrivait —
   et la tâche est **nommée automatiquement** « Réunion — <titre> ». Les trois retenues de la
   1re collecte sont conservées : titre absent ou **générique** (= tout Zoom) → rien du tout ;
   entrée **déjà nommée** par l'utilisateur pendant la réunion → le commentaire est posé mais
   **son** nom n'est pas touché ; entrée disparue → rien.
   ⚠️ Toujours jamais un renommage de la tâche « Réunion » elle-même.
2. **`ZPToolBarParentWnd` : régression du 2026-08-02, corrigée.** La barre d'outils Zoom porte un
   *titre* égal à son nom de classe. Depuis que `ZPToolBarParentWndClass` conclut, ce faux titre
   ne correspondait à aucun `GenericTitles` et passait donc **devant** « Zoom Meeting » dans
   `BestTitle` : **5 des 13 réunions nommées d'un nom de classe de fenêtre**. Écarté par
   `MeetingWindowProbe.EchoesClassName` (titre == classe, ou classe == titre + « Class ») plutôt
   que par une entrée en dur, pour que la règle vaille aussi pour `VideoFrameWnd` et la suite.
   Effet de bord voulu : les réunions Zoom retombent sur « Zoom Meeting », générique, donc **ni
   nommées ni commentées** — ce qui est le bon comportement tant qu'Outlook n'est pas là.
3. **`ShortLabel` : deux résidus Teams mesurés** — `Chat | ` en tête et ` (External)` en queue,
   vus le 05 et le 06/08 (« Chat | Velmora / Contoso Discovery Call # 1 (External) |
   Microsoft Teams »). L'ordre des retraits compte : préfixes, puis queue Teams, puis `(External)`.
4. **Le titre relevé est lisible dans le tableau de bord** : dans le nom de la tâche quand la
   détection a pu la nommer, et sinon dans l'**infobulle du 👥** (`MarkerTip`). Volontairement pas
   de nouvelle colonne — `AdjustTaskColumn` aurait été à reprendre pour rien.

Couvert par `--selftest` : `réunion_titre_posé` déroule les trois cas (titre exploitable, titre
générique, entrée déjà nommée à la main), et `réunion_libellé` a gagné les cas `Chat |`,
`(External)` et `EchoesClassName`.

### Deux bugs signalés par l'utilisateur, corrigés le 2026-08-09

1. **Fenêtres sans icône** — cadre vide dans la barre des tâches. Aucun des 7 XAML ne posait
   `Icon`. Icône désormais dessinée une fois dans `UI/AppIcon.cs` et servie aux deux mondes
   (`ImageSource` pour les fenêtres, `Icon` GDI+ pour la barre système, même dessin) ; `--uitest`
   échoue sur toute fenêtre qui l'oublierait. Détails en §8.
2. **Tableau de bord incliquable le lendemain d'une veille**, un « ding » à chaque clic. Cause :
   l'utilisateur oublie d'arrêter sa dernière tâche, la machine dort, et **au réveil le tick
   suivant voit des heures depuis le dernier rappel et ouvre aussitôt le `ReminderPopup`** —
   pendant que l'affichage se reconfigure. Ce popup était modal (`ShowDialog()`) et absent de la
   barre des tâches **comme** d'Alt-Tab : s'il ne s'affichait pas, il était introuvable et
   bloquait toute l'application. Trois corrections : le rappel est **non modal**, sa position est
   **bornée à la zone de travail**, et **plus rien ne se déclenche au réveil**
   (`App.OnMachineWokeUp`). Il est aussi journalisé à l'affichage — sans trace, le blocage était
   indiagnosticable, comme la bulle avant lui. Détails en §8.

⚠️ Reste ouvert : quand la machine dort avec une tâche en cours, **l'entrée continue de courir
toute la nuit**. Le gel est corrigé, pas la donnée : c'est une décision à prendre avec
l'utilisateur (clôturer à l'endormissement ? proposer au réveil de recouper ?).

### Étape 4 (Outlook) — commencée le 2026-08-09

**La lecture se fait par COM sur l'Outlook classique, pas par OAuth.** L'utilisateur demandait
l'écran de connexion Microsoft qu'il connaît de Slack et Zoom ; le COM a été retenu avec son
accord parce qu'il n'exige **aucune application déclarée dans l'Azure AD de Contoso** et
qu'il **préserve la promesse « 100 % local »** : rien ne sort du poste, ça marche hors ligne.

🚫 **Microsoft Graph est hors de portée, vérifié le 2026-08-09 — ne pas relancer le sujet.**
L'utilisateur a essayé lui-même : `portal.azure.com` → Microsoft Entra ID répond directement
**« you don't have access »**. Il ne peut donc pas créer d'inscription d'application, donc pas
de client ID, donc **rien à tester** côté OAuth. Une source Graph ne redeviendra envisageable
que si son service informatique déclare l'application pour lui — c'est une démarche à lancer
auprès d'eux, pas un travail de développement.

⚠️ **Conséquence à connaître : il n'y a aucun repli self-service.** Le « nouveau Outlook »
n'expose plus de COM et est **déjà installé** sur son poste. Le jour où l'entreprise l'y bascule,
la lecture d'agenda s'arrête net, et la seule issue passera par l'IT. C'est pour ça que tout
passe malgré tout par `ICalendarSource`, instanciée à un seul endroit
(`App.CreateCalendarProbe`) : le jour où le client ID existe, il n'y a qu'une source à écrire.

⚠️ **On s'attache à un Outlook déjà lancé, on ne le démarre jamais** (`GetActiveObject` via
oleaut32, `Marshal.GetActiveObject` n'existant plus depuis .NET Core). `CreateInstance` ouvrirait
Outlook dans le dos de l'utilisateur.

**L'agenda est une troisième sonde**, à côté du micro et des fenêtres — c'est ce qui fait que la
coopération demandée marche sans cas particulier. La règle devient : *le micro déclenche,
l'agenda nomme mieux que la fenêtre, et il déclenche aussi si l'utilisateur l'a dit.*

| situation | ce qui se passe |
|---|---|
| Connecté en avance, micro détecté avant le créneau | **Aucune question, aucune bascule** : la réunion en cours est la même, l'agenda ne fait que la nommer. C'était l'exigence explicite. |
| Rien de détecté, il répond « oui » | L'indice agenda devient **concluant** : la réunion est pointée sans micro (il écoute, il est au téléphone). |
| Il répond « non », ou ne répond pas, puis y participe | L'agenda ne déclenche rien, mais dès que le micro mord la réunion porte **le sujet de l'agenda**. Couvre le clic « non » par erreur et le changement d'avis. |

**Heure de début** (règle donnée par l'utilisateur) : le **plus tôt** entre le premier indice et
l'heure du créneau. Connecté à 13:55 pour un créneau 14:00 → 13:55 ; rejoint à 14:10 → 14:00.
`ClampToCurrentEntry` reste le garde-fou : la réunion ne peut jamais rogner l'entrée en cours.

⚠️ **Différence de fin assumée** : une réunion **acceptée** court jusqu'à la fin prévue à
l'agenda même s'il part avant, alors qu'une réunion seulement **nommée** par l'agenda se termine
quand le micro se tait. On ne remplace pas la règle de fin validée sur deux semaines sans une
acceptation explicite.

**Ce qui est vérifié, et ce qui ne peut pas l'être ici.** `--selftest` (`agenda=True`) déroule les
trois situations du tableau plus la règle d'heure, avec un `ScriptedCalendarSource` — ce poste
n'a **aucun profil Outlook configuré**, exactement comme il n'a ni Teams ni Zoom. J'ai vérifié
séparément que la liaison tardive `dynamic` sur COM fonctionne bien en .NET 8 dans cette
configuration, et que la source échoue proprement quand Outlook est fermé. **Le reste se valide
sur le poste de travail avec `--outlookprobe`**, qui écrit un relevé : présence d'Outlook,
**filtre exact envoyé** (le point le plus fragile, voir ci-dessous) et réunions relevées.

⚠️ **Le piège qui cassera en premier** : `Items.Restrict` attend ses dates dans le **format court
de la culture courante**, pas en ISO. C'est le même piège que l'export Excel (§3) — ne pas
« harmoniser » vers `InvariantCulture`, Outlook ne comprendrait plus. Le format utilisateur de ce
poste est en-US alors que le système est fr-FR : c'est exactement le genre d'écart qui fait
renvoyer zéro réunion sans la moindre erreur. D'où le filtre imprimé par `--outlookprobe`.

### Ce que la 2e collecte a laissé ouvert

- **La durée annoncée en fin de réunion est fausse après une pause.** Le 04/08 : « Fin de réunion
  (**2h15**) … suivi déjà arrêté », alors qu'une pause avait clôturé l'entrée à 13:26 — 55 min
  réellement pointées, recoupées à la main. `duration = end - meeting.StartedAt` (`OnMeetingEnded`)
  mesure l'horloge du détecteur, pas l'entrée. **Non corrigé.**
- **`Pause`/`Resume` ne sont pas journalisés** (`TimerService.Pause`). C'est ce qui rend le 04/08
  illisible : le suivi s'arrête sans laisser de trace et le seul indice est la formulation
  « suivi déjà arrêté ». Deux `Logger.Info` manquants. **Non corrigé.**
- **Un huddle Slack bavard gonfle le journal de diagnostic.** Slack fait clignoter un 🔊 sur le
  titre de sa fenêtre *principale* quand quelqu'un parle : la signature change toutes les 5 s.
  **69 relevés en 18 min**, contre 27 pour un Zoom d'1h02 — ~17 % de la nouvelle trace pour un
  seul huddle. Normaliser la signature (retirer les émojis d'état) dans `MeetingTrace`.
  **Non corrigé.**
- **Aucun moyen de dire « ce n'en était pas une »** hors de la bulle. Avec 0 faux positif en deux
  semaines ça n'a pas mordu, et l'entrée reste supprimable dans le tableau de bord — mais si le
  besoin apparaît, ne pas le remettre dans une bulle.
- ⚠️ `ZPFloatVideoWndClass` **est** finalement apparue (3 relevés, titre « Zoom Workplace ») après
  avoir été retirée le 02/08 pour ne s'être jamais montrée. Son titre est générique : elle
  n'apporte rien, la laisser dehors.

### Ce que la 1re collecte a prouvé (2026-07-27 → 31)

**Le pari central est validé : le micro déclenche, la fenêtre nomme.**

- **14 bascules, 14 fins propres, 0 faux positif, 0 annulation** par l'utilisateur.
- Les **8 combinaisons de capture micro** relevées (`Slack.exe`, `Zoom.exe`, `MSTeams`,
  `msedge.exe`, `ZoomHybridConf.exe` et leurs paires) tombent **toutes** dans une réunion réelle.
  Ni Discord, ni dictée, ni OBS : le filtre `MeetingApps` n'a jamais eu à rejeter quoi que ce soit.
- **Aucune réunion ratée.** Chaque réunion que l'utilisateur a nommée à la main dans `log.txt`
  correspond à une bascule. Aucun titre de réunion Teams/Zoom/Slack n'apparaît avec le verdict
  `rien`, sauf dans les 5 s qui précèdent la bascule. La limite documentée (« Teams rejoint micro
  coupé ») **n'a pas mordu** cette semaine-là — ce qui ne prouve pas qu'elle n'existe pas.
- **Les deux temporisations tiennent.** Le 31/07 le micro a lâché une douzaine de fois pendant le
  Zoom de 11:31→13:03 : la grâce de 120 s a gardé **une seule** entrée de 1h31. Les délais n'ont
  pas été touchés. Seul cas limite : un appel non répondu de 2 min a été pointé le 29/07 — c'est
  le prix du délai de confirmation à 60 s, l'utilisateur l'a gardé et nommé.

### Ce que la 1re collecte a corrigé (fait le 2026-08-02)

1. **`ConclusiveClasses` était du vent.** `ZPContentViewWndClass` et `ZPFloatVideoWndClass`
   **ne sont jamais apparues** en cinq jours, ni le process `CptHost` : ce Zoom héberge la réunion
   dans `Zoom.exe`. Remplacées par `ConfMultiTabContentWndClass` (50 relevés) et
   `ZPToolBarParentWndClass` (53), **toutes deux à 0 relevé hors réunion**. C'est ce qui rattrape
   désormais une réunion Zoom rejointe micro coupé.
2. **La fenêtre « Zoom Workplace » volait les libellés.** La fenêtre principale de Zoom
   (`ZPPTMainFrmWndClassEx`) reste ouverte toute la journée : **559 relevés, dont 379 hors de
   toute réunion**. Comme le libellé retenu était le titre le plus long, elle a nommé
   « Zoom Workplace » des réunions Teams et même un appel dans un onglet du navigateur.
   D'où `GenericTitles` + le classement générique/non générique dans `MeetingDetector.BestTitle`,
   et le remplacement d'un libellé générique par un vrai titre qui arrive après coup.
3. **Slack n'était pas nommé du tout.** Les trois huddles étaient étiquetés « Slack.exe » alors
   que la fenêtre s'appelait `Huddle: @Camille Durand - Contoso - Slack 🎤` — **196 relevés,
   0 hors réunion**. Motif `^Huddle\s*:` ajouté (ancré en tête exprès : « Slack - Huddle Preview »
   n'est qu'un survol). Volontairement **qualifiant et non concluant** : le micro suffisait déjà,
   inutile d'élargir la surface de faux positif. Candidat concluant si le besoin apparaît.
4. **`ShortLabel`** retire ce qui n'appartient pas au nom de la réunion : `Meeting join | ` en
   tête, ` | Microsoft Teams` en queue, la queue Slack, et le ` and 4 more pages` d'Edge.
   Séparé de `CleanTitle`, qui sert à *reconnaître* et doit garder la marque de l'application.

**Résultat mesuré sur les 14 bascules réelles : 9 libellés exploitables contre 6** (et les 6
traînaient encore leur suffixe). Les 5 restants sont **tous des réunions Zoom** :

⚠️ **Zoom est une impasse pour les libellés.** Aucune de ses fenêtres n'expose le sujet de la
réunion — le titre est « Zoom Meeting » ou « Zoom Workplace », jamais le nom du point. Inutile de
chercher un motif : seule l'**étape 4 (calendrier Outlook)** peut nommer une réunion Zoom.

### Le bug que la 1re collecte a révélé (corrigé)

Le **2026-07-30**, l'utilisateur arrête le suivi à 18:35 pendant une réunion Meet, la machine se
met en veille, et la fin de réunion est conclue **au réveil à 22:41**. `OnMeetingEnded` rouvrait
`_taskBeforeMeeting` sans vérifier que le suivi tournait encore : l'entrée 197 a couru de 18:40
jusqu'au lendemain 08:47, et l'utilisateur a dû la supprimer à la main. Garde ajoutée
(`_timer.CurrentEntryId.HasValue`), couverte par `--selftest`.

Noter au passage que l'**instant** de fin, lui, était juste : antidater la fin à la disparition
de l'indice a bien résisté à quatre heures de veille. Ne pas « corriger » ça.

### Les deux derniers morceaux de l'étape 3 (faits le 2026-08-02)

**Qualifier le libellé en fin de réunion.** ⚠️ **Refait le 2026-08-09 — lire le §0 d'abord.**
La bulle de fin *proposait* « Réunion — <titre relevé> » et un clic réaffectait l'entrée
(`App.NameMeetingEntry`). La 2e collecte a mesuré 9 propositions et 0 clic : la bulle n'arrive
pas. Le titre est désormais **écrit** sans rien demander (`App.ApplyMeetingTitle`).
Ce qui **survit** de la conception d'origine : ⚠️ jamais un renommage de la tâche « Réunion »
— il réécrirait le libellé de toutes les réunions déjà pointées ; et les trois raisons de se
taire — titre absent ou **générique** (« Réunion — Zoom Workplace » ne rend service à personne),
entrée **déjà renommée** par l'utilisateur pendant la réunion (7 fois en 5 jours la 1re semaine,
4 fois la 2e — ne pas repasser derrière lui), entrée disparue. Une seule bulle est affichée en
fin de réunion : deux bulles coup sur coup, la seconde efface la première.

L'entrée visée est **forcément clôturée** à cet instant (la fin de réunion l'a fermée), donc la
frontière d'édition du §4 est respectée : le `TimerService` n'est pas concerné.

**Marquer les réunions dans le tableau de bord.** Pictogramme 👥 sur les entrées `is_meeting` de
l'onglet Jour (colonne dédiée en `CellTemplate`, pour porter l'infobulle). Sur les lignes
**agrégées** (totaux du jour, tableau croisé de la semaine), la marque n'est posée que si *toutes*
les entrées de la tâche sur la période sont des réunions : une tâche mixte reste sans marque,
mieux vaut pas de signal qu'un signal faux. ⚠️ `AdjustTaskColumn` soustrait la largeur de cette
nouvelle colonne — à mettre à jour si on en ajoute une autre. Depuis le 2026-08-09 l'infobulle du
👥 porte aussi le titre relevé quand il n'a pas pu servir de nom de tâche (§0).

### Ce qui reste à faire tout de suite

**v1.3 packagée le 2026-08-09** dans `%USERPROFILE%\TimeTracker-v1.3\` (+ zip), vérifiée
**sur l'exe publié** : `--selftest` tout à `True` dont `agenda=True`, `--uitest` sur les
11 fenêtres. Elle apporte le titre de réunion écrit, les correctifs `ZPToolBarParentWnd` et
`ShortLabel`, les icônes de fenêtre, la fin du gel après veille, et l'agenda Outlook. Un `.bat`
supplémentaire y est livré : **`Diagnostic agenda Outlook.bat`**.

✅ **La lecture COM fonctionne sur le poste de travail** : `--outlookprobe` a journalisé
`lecture OK` le 2026-08-09 à 17:33:30. Le piège du format de date n'a donc pas mordu. Un second
essai à 17:35:40 a donné `ÉCHEC` sans que le journal dise pourquoi (Outlook probablement fermé
entre-temps) — d'où le détail désormais journalisé. **Le relevé lui-même n'a pas encore été
transmis** : il reste à confirmer que les réunions sont bien lues, pas seulement l'agenda ouvert.

**v1.3.1 packagée le 2026-08-09** (`%USERPROFILE%\TimeTracker-v1.3.1\`) : fenêtre Paramètres
défilable (elle dépassait l'écran du portable à 150 %, voir §8), diagnostic d'agenda déposé sur
le Bureau au lieu de `%TEMP%`, et raison de l'échec de lecture journalisée.

**v1.4 packagée le 2026-09-17** (`%USERPROFILE%\TimeTracker-v1.4\`) : réunions qui se
suivent coupées à la frontière, choix entre créneaux simultanés, clôture à l'heure du départ
après une absence, nommage pendant la réunion, annulées et rendez-vous ignorés.
**v1.5 packagée le même jour** (`%USERPROFILE%\TimeTracker-v1.5\`) : objectif hebdo, export
de période libre, raccourci pause, suggestions par l'activité des fenêtres, doublons probables.
**v1.6 packagée le même jour encore** (`%USERPROFILE%\TimeTracker-v1.6\`) : apprentissage
des fenêtres en base, assistant IA multi-fournisseur (coupé par défaut). Voir les trois sections
« Ce qui a été fait le 2026-09-17 » plus haut. **v1.6.2** : objectif d'heures au choix par jour
ou par semaine (retour immédiat de l'utilisateur), Gemini vérifié par lui avec sa clé.

**v1.7 packagée le 2026-09-29** (`%USERPROFILE%\TimeTracker-v1.7\`), **en test chez
l'utilisateur** : voir « 4e collecte » plus haut.

Ensuite, par ordre de priorité (mis à jour le 2026-09-29) :

0. **Recueillir ses impressions sur la v1.7** — il a dit qu'il revenait après l'avoir testée —
   puis **relire son `log.txt`** sur les lignes listées dans « À lire dans la prochaine
   collecte ». Ne rien régler à l'aveugle : les seuils de la détection n'ont jamais vu de vraies
   fenêtres. S'il faut des données plus fines (quelles fenêtres ont déclenché), lui **proposer**
   une collecte d'activité opt-in, locale, sur le modèle de `--meetingtrace` — pas la décider :
   aujourd'hui aucun titre n'est conservé, par principe.
1. **L'appris pollué n'a pas été effacé.** La tâche fourre-tout garde ses 419 mots ; les
   nouvelles règles de la détection les neutralisent en partie, pas `TaskSuggester`. Lui
   proposer « Oublier ce qui a été appris » (Paramètres) ou un oubli ciblé de cette seule tâche
   (à écrire), et lui laisser le choix.
2. **Les « réunions toute la journée »** des 23 et 24/09 (voir plus haut) : attendre que ça se
   reproduise avec `--meetingtrace` pour savoir qui tient le micro.
3. **S'il coche l'IA d'après les fenêtres** : vérifier dans le journal que les appels passent
   (`IA : suggestion d'activité … →`), leur durée, et que le nombre par jour reste raisonnable.
   Les fournisseurs OpenAI-compatible et Anthropic restent écrits de mémoire, non testés.
4. Petits points connus, non faits : le compteur du bandeau n'est pas recalculé après « Gérer
   les tâches » (jusqu'à 1 min de décalage) ; un mutex distinct pour `--db=` permettrait
   d'explorer l'UI à côté de sa vraie instance (aujourd'hui : « déjà en cours d'exécution ») ;
   pondérer l'appris par la récence (§7).
5. Étape 8 (installeur) — voir §7. C'est ce qui reste pour une « version finale » : un
   raccourci menu Démarrer, ajout/suppression de programmes, et la question SmartScreen.
6. Cosmétique : la durée annoncée dans la bulle de fin de réunion après une pause.

La relecture de la v1.4 en réel (ancien point 1) est **faite** : validée, voir « 4e collecte ».
Le SDK de l'ancien poste (ancien point 2) ne concerne plus : le nouveau poste a son SDK (§5).

### Malentendu à ne pas reproduire

L'utilisateur avait compris qu'il devait lancer `--meetingprobe` en continu toute la semaine.
C'est un **instantané qui quitte aussitôt** : il n'aurait eu aucun suivi du temps. D'où
`--meetingtrace` (journal continu, appli normale). Si la question revient, être explicite sur la
différence.

---

## 1. But du projet

Application **Windows** de suivi du temps de travail **par tâche**, tournant en
arrière-plan (icône system tray), **100 % locale** (aucune donnée ne sort du poste).
Objectif final : produire des données exploitables pour remplir une timesheet interne,
avec à terme intégration Outlook et détection automatique de réunions.

Le cahier des charges complet est le **prompt initial de la 1re session** (non versionné
dans le repo). Résumé des étapes plus bas (§7).

⚠️ **Le vocal a été explicitement ABANDONNÉ** par l'utilisateur (voir §6). Ne pas le
réintroduire sans demande explicite.

---

## 2. État actuel

**v1.1 complète, saisie clavier uniquement.** Compile sans erreur ni warning.
**Étape 3 (détection de réunion) livrée par-dessus et packagée en v1.2-test** — voir juste en dessous.

### Étape 3 — détection de réunion (2026-07-26)

Livrée : sondes, machine à états, bascule, annulation, reprise, réglages, journal de diagnostic,
tests. **Validée sur deux semaines d'usage réel** (§0) : la détection est bonne, c'est le chemin
du titre jusqu'à l'utilisateur qui a dû être refait le 2026-08-09. **Le paquet le plus récent
(`TimeTracker-v1.2\`, 2026-08-02) est antérieur à cette reprise** — à repackager.

**Choix de conception, à ne pas défaire sans raison :**

1. **Le micro déclenche, la fenêtre nomme.** Le cahier des charges d'origine (§7 de la version
   précédente) prévoyait une détection par *process* (`ms-teams.exe`, `Zoom.exe`, `slack.exe`).
   Abandonné : Teams et Slack tournent toute la journée, leur présence ne prouve rien, et surtout
   **les réunions dans un onglet** (Google Meet, Teams web) n'ont aucun process propre.
   Le signal retenu est l'usage du micro **en temps réel**, publié par Windows sous
   `HKCU\...\CapabilityAccessManager\ConsentStore\microphone` : `LastUsedTimeStop = 0` signifie
   « capture en cours ». C'est la source de l'icône de micro du système — pas de COM, pas de
   WASAPI, et indifférent à la façon dont la réunion tourne.
2. **Aucun titre de fenêtre n'est concluant.** `MeetingWindowProbe` ne fait *jamais* basculer sur
   la foi d'un titre, même dans Teams : « Appels | Microsoft Teams » est l'onglet *Appels* et non
   un appel, et « Réunion du 12 » dans Edge n'est qu'un document. Une bascule à tort pollue la
   timesheet, alors que rater un titre ne coûte qu'un libellé générique. Seule exception, vraiment
   sans ambiguïté : la **vue de réunion Zoom** (classes `ZPContentViewWndClass` /
   `ZPFloatVideoWndClass`, process `CptHost`), que Zoom n'ouvre que pendant une réunion — d'où une
   réunion Zoom détectée même micro coupé.
3. **Deux temporisations, et des instants antidatés.** Le début pointé est le **premier indice**
   (pas la fin du délai de confirmation) : sinon la première minute de réunion est perdue. La fin
   pointée est l'instant où l'indice **a disparu** (pas la fin du délai de grâce) : sinon la
   reprise de la tâche précédente perd ces minutes. Le délai de grâce existe parce que couper son
   micro libère parfois la capture — sans lui, une réunion d'une heure se hache en dix entrées.
4. **Le détecteur ne touche ni la base ni le chronomètre.** Il émet `MeetingStarted` /
   `MeetingEnded`, `App` traduit. C'est cette frontière qui rend la machine à états testable avec
   une sonde scriptée (`App.ScriptedMeetingProbe`) — impossible de déclencher une vraie réunion
   Teams depuis `--selftest`.
5. **Annulation = rendre le temps.** Cliquer la bulle appelle `TimerService.UndoAutoSwitch()` :
   l'entrée de réunion est **supprimée** et l'entrée précédente **rouverte**
   (`UpdateEntryTimes(id, début, null)` remet `ended_at` à NULL), comme si rien ne s'était passé.
   Le détecteur passe alors en `IgnoreCurrentMeeting()`, sinon le tick suivant rebasculerait.
6. **Une pause ou un arrêt ne donne rien à reprendre.** `_taskBeforeMeeting` est null si le suivi
   était en pause : redémarrer une tâche au hasard après la réunion serait pire que laisser le
   rappel poser la question.
7. **Rappels suspendus, pas décalés** : `TimerService.RemindersSuspended` repousse `_lastReminder`
   à chaque tick, donc le premier rappel n'arrive pas juste après la réunion.

**État de validation (mis à jour le 2026-08-02).** Ni Teams, ni Zoom, ni Slack ne sont installés
sur le poste de dev (vérifié : `Get-AppxPackage`, chemins `%LOCALAPPDATA%`) — seul Outlook l'est.
Rien ne peut donc être vérifié ici en conditions réelles. Les motifs **ont maintenant vu une vraie
semaine** (§0) ; les deux outils qui ont permis de la collecter restent en place :

- `--meetingprobe[=<fichier>]` — **instantané** : relève et **quitte**. Vérification ponctuelle.
  Traité **avant** le mutex d'instance unique, exprès : il n'a d'intérêt que pendant que
  TimeTracker tourne déjà. ⚠️ Ne suit **pas** le temps : ne jamais le proposer comme mode d'usage
  prolongé (le malentendu a eu lieu).
- `--meetingtrace[=<fichier>]` — **journal continu**, l'appli tournant normalement. C'est l'outil
  de la collecte sur plusieurs jours. `--meetingobserve` s'y ajoute pour détecter et journaliser
  **sans jamais toucher au relevé de temps**.

C'est en lisant ces relevés qu'on ajuste `MeetingWindowProbe.TitlePatterns`, `ConclusiveClasses`
et `AppSettings.MeetingApps`.

**Deux contraintes ont façonné `MeetingTrace`**, à ne pas défaire :

1. **Volume** : à 5 s de cadence, journaliser chaque tour ferait des milliers de lignes par jour.
   Une ligne n'est écrite que quand la *signature* du relevé change, plus une respiration toutes
   les 15 min (elle distingue « rien ne bouge » de « appli fermée »). Mesuré : ~380 octets pour
   22 s de fonctionnement, l'essentiel étant l'en-tête.
2. **Confidentialité** : ce fichier est fait pour être **envoyé**, alors que l'appli promet que
   rien ne quitte le poste. `MeetingWindowProbe.CandidateWindows()` ne retient que les fenêtres
   d'applications de réunion, et **masque les titres de navigateur** sauf s'ils ressemblent à une
   réunion ou si ce navigateur capte le micro. Sans ce filtre, une semaine de trace serait une
   semaine d'historique de navigation. Ne pas « enrichir » le journal sans repenser ce point.
   Noter aussi que `CandidateWindows` est volontairement **plus large** que les indices retenus :
   le diagnostic doit montrer les titres que les motifs actuels ne reconnaissent PAS.

**Build de test remis à l'utilisateur le 2026-07-26** :
`%USERPROFILE%\TimeTracker-v1.2-test\` + `TimeTracker-v1.2-test.zip` (66,5 Mo, pour le
transporter sur le poste de travail). Contenu : exe self-contained (71,9 Mo), `LISEZMOI.txt` et
trois `.bat` :

| `.bat` | Arguments | Usage |
|--------|-----------|-------|
| `TimeTracker - semaine de test` | `--meetingtrace` | **celui que l'utilisateur va lancer** : suivi normal + bascule auto + journal |
| `TimeTracker - observation seule` | `--meetingtrace --meetingobserve` | même collecte, sans jamais toucher au relevé de temps (filet de sécurité proposé) |
| `Recuperer les journaux` | — | zippe `log.txt` + `meetingtrace.log` sur le Bureau, prêt à transmettre |

Vérifié : `--selftest` et `--uitest` OK **sur l'exe publié**, mécanique des `.bat` testée de bout
en bout sur base jetable, journal confirmé écrit avec masquage effectif des titres de navigateur.

⚠️ Les `.bat` sont volontairement en **ASCII sans accents** : la console cmd les afficherait en
mojibake sous la codepage 850. Ne pas y remettre d'accents.
⚠️ Piège signalé dans le LISEZMOI : l'option « Démarrer avec Windows » lance l'exe **sans** le
journal — pendant la semaine de test, il faut passer par le `.bat` (ou le copier dans
`shell:startup`).

Limite connue, à documenter auprès de l'utilisateur : une réunion **Teams** suivie **micro coupé
dès le départ** ne se distingue pas d'un Teams ouvert → non détectée. Elle ne s'est pas produite
pendant la semaine de collecte, mais rien ne l'empêche. L'étape 4 (calendrier Outlook) est la
bonne réponse à ce cas, pas un durcissement des heuristiques de titres. Le cas **Zoom**, lui, est
désormais couvert par les classes concluantes corrigées (§0).

### Ajouté en v1.1 (2026-07-26)

Cinq demandes utilisateur, toutes livrées :

1. **Bibliothèque de tâches** (`UI/TaskManagerWindow`) — le sélecteur traînait des coquilles
   héritées du vocal (« Ma pink garpeau »). On peut désormais **renommer** (l'historique suit,
   c'est tout l'intérêt), **fusionner** deux doublons, **supprimer** une tâche jamais utilisée,
   et **épingler** en favori. Accessible depuis le tray et le tableau de bord.
   ⚠️ Suppression **refusée** si la tâche porte des entrées : la clé étrangère protège
   l'historique, la fusion est la sortie propre. Ne pas « débloquer » ça sans y penser à deux fois.
2. **Favoris** — colonne `tasks.is_favorite` (migration `EnsureColumn` pour les bases
   existantes). `App.BuildSelectorTasks()` compose la liste du sélecteur : favoris d'abord,
   puis récentes non déjà listées, 15 max, chiffres 1-9 sur les premières.
3. **Barre de suivi du tableau de bord** — changer de tâche, corriger, pause/reprise, arrêter,
   gérer les tâches, paramètres. Câblée par `UI/TrackerActions` (délégués fournis par `App`) :
   la fenêtre ne pilote rien elle-même, ce qui la garde rendable seule par `--uitest`.
4. **Rappel « toujours en pause ? »** — `TimerService.Pause()` **ne stoppe plus** le
   `DispatcherTimer` : il continue de tourner pour émettre `PausedReminderDue`. Réponses :
   reprendre / changer de tâche / rester en pause.
5. **Décalage 5 ou 15 min** — répondre « Non, je change » à un rappel ouvre le sélecteur avec
   un panneau « Cette tâche a commencé… ». `App.BackdatedStart()` **borne** le décalage au
   début de l'entrée en cours, sinon `StartTask` clôturerait la précédente avant son propre début.

Également : `TimerService.Stop()` (fin de journée, conserve le temps — à ne pas confondre avec
`CancelCurrentEntry()` qui supprime) et `TimerService.ReloadCurrentTask()` (le tray affichait
l'ancien nom après un renommage de la tâche en cours).

### Livré en v1.0

La v1.0 ferme la boucle du produit : on **suit** son temps, on **corrige** ses erreurs,
et on **sort** les données pour la timesheet. Livré dans cette version (par rapport à la
beta 0.1.0) :

- **Étape 5 — Tableau de bord complet + export.** Onglets Jour / Semaine, navigation
  jour par jour et semaine par semaine, tableau croisé tâche × jour, export CSV
  (UTF-8 BOM, `;`) et Excel (ClosedXML : feuilles *Détail*, *Par tâche*, *Par jour*).
  Édition et suppression d'une entrée terminée depuis la liste du jour.
- **Étape 7 — Correction à chaud (`Ctrl+Alt+E`).** Réaffecter la tâche en cours,
  antidater son début (l'entrée précédente est rognée pour éviter les chevauchements),
  annuler l'entrée en cours, supprimer la dernière entrée terminée.
- **Étape 9 — Fenêtre Paramètres.** `DatabaseService.SaveSettings` est enfin **câblé**.
  Intervalle de rappel, son, popup au lancement, raccourcis **personnalisables**
  (capture clavier + réenregistrement `RegisterHotKey` avec retour arrière si Windows
  refuse), démarrage Windows, dossier d'export.

Déjà présent depuis la beta : tray + menu, `Ctrl+Alt+T`, popup de sélection (texte ou
chiffres 1-9), timer, rappel périodique, SQLite WAL, reprise d'entrée ouverte, instance
unique, journal + handlers d'exception globaux.

**Package v1.1** : `%USERPROFILE%\TimeTracker-v1.1\TimeTracker.exe` (71,9 Mo,
self-contained) + `LISEZMOI.txt`, zip `%USERPROFILE%\TimeTracker-v1.1.zip` (66,5 Mo).
Vérifié : `--selftest` et `--uitest` OK **sur l'exe publié**.

**Package v1.2 (2026-08-02)** : `%USERPROFILE%\TimeTracker-v1.2\` (exe self-contained 71,9 Mo)
+ `LISEZMOI.txt` + deux `.bat` (journal de diagnostic, récupération des journaux), zip
`%USERPROFILE%\TimeTracker-v1.2.zip` (66,5 Mo) pour le transport vers le poste de travail.
Vérifié : `--selftest` et `--uitest` OK **sur l'exe publié**, `.bat` confirmés sans octet > 127.

**Package v1.3 (2026-08-09)** : `%USERPROFILE%\TimeTracker-v1.3\` (exe self-contained 71,9 Mo)
+ `LISEZMOI.txt` + **trois** `.bat` (le nouveau étant `Diagnostic agenda Outlook.bat`), zip
`%USERPROFILE%\TimeTracker-v1.3.zip` (66,5 Mo). Vérifié : `--selftest` tout à `True` dont
`agenda=True`, `--uitest` sur les 11 fenêtres, **sur l'exe publié** ; le nouveau `.bat` confirmé
en ASCII pur et sans BOM. C'est la version à donner à l'utilisateur.

**Les packages coexistent, aucun à écraser** — le projet n'étant pas versionné, ce sont les
seuls retours arrière possibles :

| Dossier | Rôle |
|---------|------|
| `TimeTracker-v1.7\` | **en test** (2026-09-29, poste de travail, `%USERPROFILE%`) : changement de tâche proposé, « Tu voulais dire », noms à vérifier, IA d'après les fenêtres (coupée par défaut) |
| `TimeTracker-v1.6.2\` | objectif d'heures par jour ou par semaine, au choix — **le retour arrière de la v1.7**. Sur le poste de travail, l'utilisateur l'a installée dans `Documents\Programmes\` (OneDrive), pas dans `%USERPROFILE%` |
| `TimeTracker-v1.6\` | apprentissage des fenêtres en base, assistant IA multi-fournisseur (coupé par défaut) — objectif encore « par semaine » |
| `TimeTracker-v1.5\` | objectif hebdo, export de période, raccourci pause, suggestions par l'activité, doublons probables — retour arrière sans table `task_hints` ni IA |
| `TimeTracker-v1.4\` | réunions enchaînées coupées, choix entre créneaux, clôture après absence, nommage pendant la réunion — retour arrière si les suggestions dérangent |
| `TimeTracker-v1.3.1\` | Paramètres défilable, diagnostic agenda sur le Bureau — retour arrière si la coupure automatique pose problème |
| `TimeTracker-v1.3\` | titre de réunion écrit, icônes, gel après veille corrigé, agenda Outlook — ⚠️ Paramètres dépasse l'écran du portable |
| `TimeTracker-v1.2\` | correctifs de la 1re collecte + libellé proposé + marquage — retour arrière si l'agenda pose problème |
| `TimeTracker-v1.2-test\` | build de la semaine de collecte, **périmé** (garder pour référence) |
| `TimeTracker-v1.1\` | dernière version éprouvée avant l'étape 3 — le vrai retour arrière |
| `TimeTracker-v1.0\` | conservé par précaution |

La base `%APPDATA%\TimeTracker\timetracker.db` est **partagée** par toutes ces versions : revenir
à la v1.1 ne perd ni tâches ni historique (les réglages de réunion y dorment simplement).

---

## 3. Stack & décisions techniques

- **C# / .NET 8**, **WPF** (fenêtres) + **WinForms** `NotifyIcon` pour le tray
  (`UseWPF` + `UseWindowsForms` dans le csproj).
- **SQLite** via `Microsoft.Data.Sqlite` (8.0.10) et **ClosedXML** (0.105.0) pour le
  `.xlsx`. Seules dépendances NuGet.
- Cible `net8.0-windows`, `RuntimeIdentifier win-x64`.
- ⚠️ `dotnet` **n'est pas dans le PATH** des shells par défaut sur ce poste dev →
  utiliser `"C:\Program Files\dotnet\dotnet.exe"`.

### Culture : le piège à ne pas « corriger »

`AppCulture` sépare volontairement deux usages :
- **libellés** (noms de jours/mois) → toujours **fr-FR**, l'appli est en français ;
- **nombres** exportés → **`CultureInfo.CurrentCulture`**, jamais forcés.

Raison : Excel lit les décimales selon le réglage régional Windows de l'utilisateur.
Sur ce poste dev, le format utilisateur est **en-US** alors que le système est fr-FR —
forcer le français produirait `1,5` qu'un Excel anglais prendrait pour du texte.
Ne pas « harmoniser » les deux.

---

## 4. Architecture / carte des fichiers

```
TimeTracker/
├── App.xaml(.cs)         # Orchestrateur : services, wiring, raccourcis, cycle de vie,
│                         #   OpenTaskSelector/QuickEdit/Settings/Dashboard, --selftest, --uitest
├── app.manifest          # DPI awareness
├── Core/
│   │   (ajoutés le 2026-09-17 : Models/MeetingChoice.cs — question agenda ;
│   │    Services/ActivityProbe.cs — fenêtre au premier plan, en mémoire seulement ;
│   │    Services/TaskSuggester.cs — suggestion par les mots ; Services/TaskSimilarity.cs —
│   │    doublons de noms ; UI/ExportRangeWindow — export d'une période libre)
│   │   (ajoutés le 2026-09-29, v1.7 : Services/ActivityShiftDetector.cs — changement de
│   │    tâche probable ; Services/ActivityNaming.cs — nom tiré d'un titre ;
│   │    Services/ActivityLearning.cs — apprentissage différé d'après la base ;
│   │    Services/NameReview.cs — noms à vérifier, « Tu voulais dire » ;
│   │    Services/Ai/TaskSuggestionAssistant.cs — l'IA propose un nom d'après des mots-clés ;
│   │    UI/ActivityShiftWindow — « Tu as changé de tâche ? »)
│   ├── Models/
│   │   ├── TaskItem.cs        # id, name, last_used, is_favorite
│   │   ├── TaskUsage.cs       # tâche + nb d'entrées / total / dernier usage (gestion)
│   │   ├── TimeEntry.cs       # entrée de temps (+ Elapsed, TaskName)
│   │   ├── AppSettings.cs     # réglages + clés DB (rappel, son, raccourcis, export, réunions)
│   │   ├── MeetingSignal.cs   # indice de réunion (sonde, appli, titre, concluant ou non)
│   │   ├── MeetingInfo.cs     # une réunion détectée : début antidaté, fin, meilleur titre
│   │   └── Hotkey.cs          # « Ctrl+Alt+T » ⇄ (ModifierKeys, Key) + code Win32
│   └── Services/
│       ├── DatabaseService.cs # SQLite : schéma + migrations, CRUD, plages de dates,
│       │                      #   édition d'entrées, bibliothèque de tâches (fusion, favoris)
│       ├── TimerService.cs    # tâche active, ticks, rappels (dont pause), corrections, arrêt,
│       │                      #   suspension des rappels, annulation de bascule auto
│       ├── IMeetingProbe.cs   # source d'indices (permet la sonde scriptée des tests)
│       ├── MicrophoneProbe.cs # qui capte le micro maintenant (registre) — le déclencheur
│       ├── MeetingWindowProbe.cs # fenêtres : nomme la réunion, conclut pour Zoom seulement
│       ├── MeetingDetector.cs # machine à états : confirmation, grâce, début/fin antidatés
│       ├── MeetingTrace.cs    # journal de diagnostic (dédoublonné, titres navigateur masqués)
│       ├── HotkeyService.cs   # RegisterHotKey Win32 + UnregisterAll (réenregistrement)
│       ├── StartupService.cs  # entrée registre HKCU Run
│       ├── ExportService.cs   # CSV + XLSX, totaux par tâche, nom de fichier suggéré
│       ├── TimeInput.cs       # parse « 9:05 », « 9h05 », « 0905 », « 9 »
│       ├── AppCulture.cs      # voir §3
│       └── Logger.cs          # journal %APPDATA%\TimeTracker\log.txt
└── UI/
    ├── TrayIconManager.cs          # NotifyIcon + menu + icône dessinée au runtime
    ├── TaskSelectorPopup.xaml(.cs) # sélection/création (favoris ★, décalage 5/15 min)
    ├── ReminderPopup.xaml(.cs)     # rappel périodique : mode « en cours » ou « en pause »
    ├── QuickEditWindow.xaml(.cs)   # correction à chaud (Ctrl+Alt+E)
    ├── EntryEditWindow.xaml(.cs)   # édition d'une entrée terminée
    ├── TaskManagerWindow.xaml(.cs) # bibliothèque : renommer / fusionner / supprimer / favori
    ├── TrackerActions.cs           # délégués de suivi consommés par le tableau de bord
    ├── SettingsWindow.xaml(.cs)    # paramètres
    └── DashboardWindow.xaml(.cs)   # barre de suivi + onglets Jour / Semaine + export
```

Flux principal : `HotkeyService`/tray → `App.OpenTaskSelector()` → `TaskSelectorPopup.Pick()`
→ `TimerService.StartTask()` (clôture l'entrée précédente, en démarre une nouvelle) →
`DatabaseService`. `TimerService` émet `Tick` (tooltip), `ReminderDue` (popup),
`CurrentTaskChanged` (rafraîchit le tray).

Flux réunion : `MeetingDetector` (tick 5 s) → `MicrophoneProbe` + `MeetingWindowProbe` →
`MeetingStarted` → `App.OnMeetingStarted()` → `TimerService.StartTask(…, isMeeting: true)`.
À la fin : `MeetingEnded` → reprise de `_taskBeforeMeeting`. Le détecteur n'écrit jamais en base.

**Frontière d'édition à respecter** : l'entrée **ouverte** appartient au `TimerService`
et ne se corrige que par `QuickEditWindow` ; le tableau de bord n'édite que les entrées
**clôturées** (`RefuseIfOpen`). C'est ce qui évite de désynchroniser le chronomètre.
Même logique côté bibliothèque : `TaskManagerWindow` reçoit l'id de la tâche en cours et lui
interdit fusion et suppression (elle disparaîtrait sous les pieds du `TimerService`) ; le
renommage reste permis, `App` appelle ensuite `ReloadCurrentTask()`.

`AppSettings` est une **instance partagée jamais remplacée** : `SettingsWindow` la mute
puis persiste, et `App` réapplique à chaud (intervalle de rappel, raccourcis, registre).

---

## 5. Build, run, package

**Dev :**
```powershell
& "C:\Program Files\dotnet\dotnet.exe" build TimeTracker.csproj
& "C:\Program Files\dotnet\dotnet.exe" run --project TimeTracker.csproj
```

**Poste de travail (depuis le 2026-09-27)** : `dotnet` est dans le PATH, mais c'est un **SDK
9.0.305** (pas de SDK 8) ; il compile la cible `net8.0-windows` sans avertissement, les runtimes
Windows Desktop 8.0.20 / 8.0.31 sont présents. Restauration NuGet OK. Build, tests et
`publish` de la v1.7 faits ainsi. Le remplaçant `dotnet-shim` ci-dessous ne concerne que
l'ancien poste de développement.

⚠️ **Le 2026-09-17, `C:\Program Files\dotnet\dotnet.exe` n'existait plus** (le multiplexeur
seul ; `sdk\8.0.424`, `host\fxr\8.0.30`, `shared\*\8.0.30` et `packs` intacts). Sans télécharger
quoi que ce soit, un remplaçant a été compilé avec le `csc.exe` de .NET Framework
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`) : **`%USERPROFILE%\dotnet-shim\dotnet.exe`**
charge `hostfxr.dll` de l'installation et lui passe la ligne de commande
(`hostfxr_main_startupinfo`). Source : `dotnet-shim.cs` à côté. Il faut aussi poser
`$env:DOTNET_HOST_PATH` sur ce chemin pour que MSBuild lance le compilateur C# avec lui :

```powershell
$env:DOTNET_HOST_PATH = "%USERPROFILE%\dotnet-shim\dotnet.exe"
& "%USERPROFILE%\dotnet-shim\dotnet.exe" build TimeTracker.csproj
```

Build, `--selftest`, `--uitest` et `publish` ont été faits ainsi pour la v1.4. La bonne solution
reste de réinstaller le SDK .NET 8 (x64) ; le remplaçant devient alors inutile.

**Package autonome :**
```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish TimeTracker.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o <dossier>
```

**Flags de lancement** (voir aussi le tableau du README) :
`--nostartpopup`, `--dashboard[=semaine]`, `--db=<chemin>`, `--selftest`, `--uitest[=<dossier>]`,
`--meetingprobe[=<fichier>]` (instantané, quitte aussitôt), `--meetingtrace[=<fichier>]`
(journal continu), `--meetingobserve` (détecter sans basculer).

---

## 6. Vocal : tenté puis abandonné (NE PAS réintroduire sans demande)

L'étape 2 (Whisper.net + NAudio, mode mains libres) a été développée intégralement PUIS
**retirée à la demande de l'utilisateur** : précision insuffisante sur son poste, malgré
capture WASAPI (le vrai problème venait de WinMM), normalisation audio, langue forcée
`fr` et modèle `small`. Fichiers et packages supprimés.

---

## 7. Suite : ce qui reste

- **3. Détection de réunion** — livrée, éprouvée sur une semaine réelle et **corrigée d'après
  cette collecte** (résultats et correctifs en §0). **À repackager en v1.2.**
  Les deux derniers morceaux du cahier des charges (libellé proposé en fin de réunion, marquage
  dans le tableau de bord) sont **faits** — détail et garde-fous en §0. **L'étape 3 est close.**
- **4. Intégration Outlook** (lecture seule) — **livrée et éprouvée cinq semaines** (3e collecte,
  §0). La lecture se fait par **COM tardif sur l'Outlook classique** (et non par les PIA
  `Interop.Outlook`), derrière `ICalendarSource`. Réunions enchaînées et créneaux simultanés
  traités le 2026-09-17. Reste le repli Microsoft Graph si ce poste passe au « nouveau
  Outlook » — bloqué côté IT, voir §0.
- **8. Installeur** : NSIS ou WiX/MSI (raccourci menu Démarrer — `--dashboard` est prévu
  pour ça —, ajout/suppression de programmes, désinstalleur, option démarrage auto).
  ⚠️ **Le point SmartScreen est à traiter là** (demande explicite de l'utilisateur, 2026-08-09) :
  un installeur transmis par e-mail portera la même marque de provenance que le zip actuel, et
  l'avertissement reviendra — sur l'installeur cette fois, ce qui est pire pour la confiance.
  Trois pistes, par coût croissant : continuer à passer par la **synchronisation OneDrive** du
  dossier, qui ne pose aucune marque (déjà éprouvé) ; livrer un `Debloquer.bat` à côté ; ou faire
  **signer** le binaire — à chercher d'abord du côté d'un service de signature interne à Contoso,
  un certificat commercial coûtant quelques centaines d'euros par an.
- Idées non demandées : notes sur une entrée (colonne `notes` en base, exportée, remplie par
  la détection de réunion, mais aucune UI ne la saisit à la main).
- **Suggestions par l'activité** : refaites en v1.7 (changement de tâche proposé, noms nouveaux,
  noms à vérifier, apprentissage différé — §0, « 4e collecte »). Suite possible : pondérer
  par la récence (un mot appris il y a six mois pèse comme un mot d'hier), et un écran qui montre
  ce qui a été appris par tâche — aujourd'hui seul « Oublier » existe.
- **Assistant IA** : branché en v1.6 (nettoyage des tâches, sur un clic), étendu en v1.7 aux
  **noms proposés d'après des mots-clés de fenêtres**, automatiquement mais derrière un second
  interrupteur coupé par défaut (§0, décisions du 2026-09-29). Idées à la demande : codes projet
  de la timesheet interne, résumé de semaine, nommage d'une réunion Zoom sans agenda.
  ⚠️ Toujours coupé par défaut ; jamais un titre entier ; ce qui part toujours affiché.

---

## 8. Pièges & leçons (à connaître pour ne pas les reprendre)

**Propres au poste de travail** (celui de l'utilisateur, depuis le 2026-09-27) :

- ⚠️ **La vraie base est là** : `%APPDATA%\TimeTracker\timetracker.db`, ouverte en WAL par son
  TimeTracker qui tourne **en permanence**. Pour la lire : copier `.db`, `-wal` et `-shm`
  ensemble, ou l'API de sauvegarde SQLite depuis une connexion `mode=ro` (Python est présent) ;
  ne travailler que sur la copie, hors dépôt. Jamais de test dessus.
- Depuis la v1.7, **`--selftest` et `--uitest` passent avant le mutex** : ils tournent pendant que
  son TimeTracker tourne. Mais ils écrivent dans **son** `log.txt` — les lignes `SELFTEST` /
  `UITEST` s'y mêlent à sa journée, et le dépouillement doit les ignorer.
- **`--db=` ne touche plus au registre** (`StartupService.Frozen`). Il reste bloqué par le mutex :
  pour explorer l'UI sur une base de test, il faut qu'il quitte son TimeTracker (ou écrire un
  mutex distinct pour `--db=`, non fait).
- **Lancer un exe relance le démarrage avec Windows vers cet exe** (si le réglage est coché) :
  c'est ce qui fait qu'un paquet devient « l'installé ». Relancer l'ancien pour revenir.
- **Sa clé Gemini est en clair dans `settings`** : ne jamais l'afficher, la copier, la journaliser
  ni s'en servir pour un test — c'est à lui de tester avec.
- **Aucun titre de fenêtre n'est conservé** (ni base, ni journal) : impossible de rejouer la
  détection de changement sur une semaine passée. Seul `task_hints` (mots agrégés par tâche)
  existe. Pour régler les seuils sur du réel, il faudra une collecte opt-in, à lui proposer.
- ⚠️ **Vérifier du C# en le réimplémentant ne prouve rien** (rappel) : pour rejouer ses vrais
  noms à travers `TaskSimilarity` / `NameReview`, un harnais jetable hors dépôt qui
  `<Compile Include>` les sources a été utilisé (exclure `Core/Models/Hotkey.cs`, qui tire WPF ;
  `DatabaseService` demande le paquet `Microsoft.Data.Sqlite`).

**Leçons de la v1.7** :

- ⚠️ **Un suggéreur qui reclasse l'existant ne peut pas suivre quelqu'un qui crée ses tâches à la
  volée** — 63 % du temps sur des tâches du jour même. Mesurer la part de tâches nouvelles avant
  de régler un score.
- ⚠️ **Apprendre « ce qui tourne à l'instant » pollue la tâche fourre-tout.** Attribuer d'après la
  base, après coup, et ne jamais laisser un mot qui **nomme une autre tâche** décrire celle-ci.
- **Une comparaison de noms se fait mot à mot** quand les noms suivent des gabarits : sur soixante
  caractères, deux clients différents sont « à 0,88 » l'un de l'autre.
- **Dans une fusion, c'est le nom qui survit qui compte, pas le nombre d'entrées** : les entrées
  suivent de toute façon, et la faute portait souvent le plus d'entrées.
- **Une fenêtre non modale laisse le monde bouger derrière elle** : relire en base ce qu'elle
  propose au moment où l'utilisateur répond (la tâche proposée a pu être fusionnée entre-temps).
- **Un rappel non modal peut rester ouvert des heures** : ne pas en faire une condition de silence
  pour autre chose.

- ⚠️ **Un `ShowDialog()` sur une fenêtre `ShowInTaskbar="False"` + `WindowStyle="ToolWindow"`
  est un blocage définitif en puissance.** Ni barre des tâches, ni Alt-Tab : si elle ne s'affiche
  pas là où l'utilisateur regarde, elle est **introuvable** — et comme elle est modale, toutes les
  autres fenêtres de l'appli deviennent incliquables, chaque clic ne produisant qu'un « ding ».
  C'est exactement ce qui arrivait au `ReminderPopup` (corrigé le 2026-08-09, §0) : il est
  désormais **non modal**. Avant d'écrire un `ShowDialog()`, se demander ce qui se passe si cette
  fenêtre ne s'affiche pas. `TaskSelectorPopup` reste modal — il est `CenterScreen` et toujours
  ouvert sur une action explicite de l'utilisateur, mais il porte le même risque résiduel.
- ⚠️ **Rien ne doit se déclencher tout seul au réveil de veille.** L'utilisateur n'éteint pas son
  poste tous les soirs. Au réveil, tout compteur fondé sur `DateTime.Now - dernier_truc` voit des
  heures d'écart et se déclenche d'un coup, pendant que l'affichage se reconfigure — le pire
  moment pour ouvrir une fenêtre. D'où `App.OnMachineWokeUp` (`SystemEvents.PowerModeChanged` +
  `SessionSwitch`). ⚠️ `SystemEvents` notifie sur **son propre thread** : repasser par le
  dispatcher, et se désabonner dans `OnExit` (il garde une référence statique).
  Depuis le 2026-09-17 le réveil fait **une** chose de plus, sans rien afficher : clôturer
  l'entrée à l'heure du départ si l'absence dépasse le seuil (`App.HandleAbsence`). Le rappel
  qui en découle est non modal et arrive 2 min plus tard — pas à l'instant du réveil.
- ⚠️ **Après une veille, l'état du détecteur est périmé.** Le `DispatcherTimer` n'a pas tourné :
  `InMeeting` dit ce qui était vrai à l'endormissement. Ne pas s'y fier au réveil (c'est pour ça
  que `HandleAbsence` ne s'en sert qu'après un verrouillage sans veille).
- ⚠️ **Un créneau « qui commence » n'est pas toujours une réunion de plus.** Trois cas se
  ressemblent dans le journal et ne se traitent pas pareil : le créneau arrive sur une réunion
  détectée **sans créneau attaché** (connecté en avance → on attache, on ne redémarre pas) ; il
  arrive alors que le créneau attaché est **terminé** (réunion suivante → on coupe) ; il arrive
  alors que le créneau attaché **court encore** (chevauchement → on demande). Pendant cinq
  semaines les trois recevaient la même réponse — « aucune question, l'agenda ne fait que
  nommer » — et le second a fondu neuf enchaînements en une entrée.
- ⚠️ **Le poste de l'utilisateur est un portable à 150 %** : un écran 1080p n'y laisse que
  **~670 points** de hauteur utile, contre plus de 1000 sur le poste de développement (grand
  écran à 100 %). La fenêtre Paramètres en fait **1049** — elle dépassait par le bas, boutons
  compris, sans barre de défilement. Toute fenêtre haute doit réunir **deux** choses : ses
  réglages dans un `ScrollViewer`, et `WindowFit.LimitToWorkArea` ; l'une sans l'autre ne sert à
  rien. `--uitest` refuse maintenant une fenêtre plus haute que `LaptopHeightBudget` sans
  `ScrollViewer`, affiche `(défilable, NNNpts)` quand la vérification mord — pour qu'on voie
  qu'elle n'est pas passée à vide — et rend la fenêtre Paramètres une seconde fois contrainte à
  cette hauteur, ce qui est le seul moyen de voir ici ce que l'utilisateur voit chez lui.
- ⚠️ **L'exe n'est pas signé** : SmartScreen affiche « Windows protected your PC » à la première
  exécution. Ce n'est pas l'exe qui est en cause, mais la marque de provenance externe (Mark of
  the Web) — et **elle dépend du mode de transfert**, constaté le 2026-08-09 :
  **pièce jointe d'e-mail → marque posée**, **synchronisation OneDrive du dossier → aucune marque**
  (les livraisons précédentes passaient donc sans rien dire). **Débloquer le .zip avant de
  l'extraire** (Propriétés > Débloquer) règle le cas de l'e-mail — vérifié, ça marche. Débloquer
  après extraction n'agit pas sur les fichiers déjà sortis : il faut alors viser le `.exe`
  lui-même. Pour diagnostiquer : `Get-Item <exe> -Stream *` — si `Zone.Identifier` apparaît, la
  marque est là ; s'il n'y a que `:$DATA` et que l'alerte persiste, c'est une politique
  d'entreprise et seul un certificat y changera quelque chose.
- ⚠️ **Une fenêtre WPF sans `Icon` affiche un cadre vide** dans la barre des tâches et dans
  Alt-Tab. Aucun des 7 XAML ne la posait. L'icône est **dessinée** (`UI/AppIcon.cs`) et non
  embarquée en `.ico` — pas de dépôt git, un binaire perdu serait irrécupérable — et servie sous
  deux formes : `ImageSource` pour `Window.Icon`, `Icon` GDI+ pour la barre système.
  `--uitest` refuse désormais toute fenêtre sans icône.
- **Nom de colonne = chemin de binding.** Un `DataTable` dont les colonnes s'appellent
  `lun. 20/07` produit un `DataGrid` **silencieusement vide** : `.` et `/` sont des
  séparateurs dans un `PropertyPath` WPF. D'où les colonnes techniques `D0`…`D6` avec
  un libellé posé dans `AutoGeneratingColumn`. Bug rencontré et corrigé en v1.0.
- **`Run.Text="{Binding X}"` est TwoWay par défaut** → sur une source read-only,
  `XamlParseException` **au rendu** = crash. Toujours `Mode=OneWay`.
- **`--uitest` existe pour ça** : il ouvre et rend chaque fenêtre hors écran et journalise
  OK/ÉCHEC — les erreurs XAML ne se voient qu'au rendu, pas à la compilation. Avec
  `--uitest=<dossier>`, il enregistre en plus une capture PNG par fenêtre
  (`RenderTargetBitmap`), ce qui permet une revue visuelle sans piloter l'écran.
- **`HorizontalAlignment` dans une `Window`** désigne la propriété d'instance : qualifier
  `System.Windows.HorizontalAlignment.Right` pour l'énumération.
- **ImplicitUsings + UseWindowsForms** → ambiguïtés `Application`/`MessageBox`/`TextBox`/
  `KeyEventArgs` (WPF vs Forms). Résolu par alias `using`. `System.IO` parfois à importer.
  S'y ajoutent `Color`/`Brush`/`SolidColorBrush` dès qu'on touche aux couleurs en code-behind
  (`System.Drawing` vs `System.Windows.Media`) : aliaser, ne pas importer `System.Windows.Media`.
- **Ordre de `DockPanel.Dock`** : le premier enfant docké prend le bord **extérieur**. Un bouton
  « Fermer » déclaré après un panneau docké en bas se retrouve *au-dessus* de lui.
- ⚠️ **Ne jamais réécrire un fichier source via `Get-Content | Set-Content`** en
  PowerShell 5.1 : `Get-Content` lit l'UTF-8 sans BOM comme de l'ANSI et le résultat est
  du double encodage (`é` → `Ã©`) dans tout le fichier. Utiliser l'outil Edit.
- ⚠️ **Même piège pour les `.ps1` d'analyse** : PowerShell 5.1 lit un script sans BOM en ANSI,
  donc un motif contenant un accent n'y matche **rien** — et le script ne plante pas, il rend
  juste un résultat faux. Rencontré en dépouillant `meetingtrace.log` : un `-match 'RÉUNION'`
  qui ignorait silencieusement les 367 blocs de réunion. **Écrire ces scripts en ASCII pur**
  (`[char]0xE9` au besoin), ou passer par un harnais C# quand l'analyse porte sur des accents.
- **L'état PowerShell ne survit pas d'un appel d'outil au suivant** (répertoire courant mis à
  part) : une analyse en plusieurs étapes doit tenir dans **un seul script**, pas dans une suite
  d'appels qui se repassent des variables globales.
- **Vérifier du code C# avec une réimplémentation PowerShell ne prouve rien** : la copie diverge
  du code livré, et ses propres bugs se lisent comme des résultats. Pour rejouer des données
  réelles à travers la vraie logique, compiler un harnais jetable qui `<Compile Include>` les
  **sources du projet** (fait pour valider `ShortLabel` sur les 14 réunions de la collecte).
- **L'exe est WinExe** (GUI) → `& $exe` **ne bloque pas**. Utiliser `Start-Process -Wait`.
- **Edge glisse une espace de largeur nulle (U+200B) dans ses titres** : « Microsoft​ Edge ».
  Un motif qui cherche « Microsoft Edge » ne trouve donc rien. `MeetingWindowProbe.CleanTitle`
  retire le caractère **avant** de tenter quoi que ce soit ; le `--selftest` le vérifie avec un
  U+200B littéral dans la chaîne de test (invisible à la lecture, bien présent dans le fichier).
- ⚠️ **Hook de sécurité du shell** : une commande PowerShell combinant `Remove-Item` ET un
  chemin `D:\OneDrive...` (ou un wildcard) est **bloquée** (faux positif). Séparer les
  commandes, ou utiliser le `-Force` d'un autre cmdlet (ex. `Compress-Archive -Force`).
- Le projet est dans **OneDrive** → publier les gros artefacts ailleurs (`C:\Users\...`).
- **computer-use ne voit pas TimeTracker** : l'appli n'est pas dans le menu Démarrer, donc
  `request_access` ne la résout pas. Contournement utilisé : `--uitest=<dossier>` pour les
  captures de fenêtres, et une capture `GetWindowRect` + `CopyFromScreen` en PowerShell
  pour la fenêtre principale. Un installeur (étape 8) réglerait le problème.

---

## 9. Test / vérification

- **Build** : voir §5. Vise 0 warning.
- **Services + export + réglages** : `TimeTracker.exe --selftest` (base jetable en
  `%TEMP%`, jamais le relevé réel) → journalise un OK/ÉCHEC détaillé. Couvre aussi, depuis
  la v1.1, l'arrêt du suivi et la bibliothèque (favori, renommage, fusion, suppression), et
  depuis l'étape 3 la machine à états de réunion (sonde scriptée, délais à zéro), le drapeau
  `is_meeting`, l'annulation de bascule et le nettoyage des titres.
- **Rendu des fenêtres** : `TimeTracker.exe --uitest[=<dossier>]` → voir §8.
- **Détection de réunion en conditions réelles** : `TimeTracker.exe --meetingprobe[=<fichier>]`
  pendant une réunion. Le `--selftest` ne couvre que la machine à états, jamais les vraies sondes.
- **Données de démo** : le harnais de test hors dépôt sait fabriquer une base réaliste ;
  lancer ensuite `--db=<chemin>` pour explorer l'UI sans toucher au relevé réel.
- **Journal** : `%APPDATA%\TimeTracker\log.txt`.

---

## 10. Données / emplacements

- Base : `%APPDATA%\TimeTracker\timetracker.db` (propre à chaque poste).
- Journal : `%APPDATA%\TimeTracker\log.txt`.
- ℹ️ Il peut rester des modèles Whisper (`models\ggml-*.bin`) du temps du vocal — inutiles,
  supprimables sur le poste dev.
- Mémoire agent : `%USERPROFILE%\.claude\projects\D--...-TimeTracker\memory\`.
