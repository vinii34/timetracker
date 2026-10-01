# TimeTracker — instructions projet

## À lire en premier

1. **`HANDOFF.md`** — document de passation : état d'avancement, architecture, décisions,
   pièges rencontrés, ce qui reste à faire. **C'est la source de vérité pour reprendre le projet.**
2. **`README.md`** — fonctionnalités, build, options de ligne de commande, structure.

Ne demande pas à l'utilisateur de réexpliquer le projet : tout est dans ces deux fichiers.

## But du projet

Application **Windows** de suivi du temps de travail **par tâche**, tournant en arrière-plan
(icône system tray), **100 % locale par défaut** — seul l'assistant IA (opt-in) envoie quelque
chose : des noms de tâches sur un clic (v1.6), et, derrière un second interrupteur coupé par
défaut, des mots-clés de fenêtres pour proposer des noms (v1.7). Finalité : produire
des données exploitables pour **remplir une timesheet interne**. À terme : intégration Outlook
et détection automatique des réunions.

**État : v1.1 éprouvée** (suivi, correction à chaud, tableau de bord Jour/Semaine + barre de
suivi, export CSV/Excel, paramètres, bibliothèque de tâches avec favoris/fusion/renommage, rappel
de pause), **plus la détection de réunion** (étape 3).

✅ **Deux semaines d'usage réel dépouillées** (journaux bruts dans `logs/`, 2026-07-27 → 31 puis
2026-08-03 → 07) : 27 bascules, 0 faux positif, 0 réunion ratée. **La détection est validée ;
c'est le chemin du titre jusqu'à l'utilisateur qui a été refait le 2026-08-09** — la bulle de la
barre système n'arrivait jamais (9 propositions, 0 clic), le titre relevé est désormais écrit dans
l'entrée et le nom de la tâche. **Lis `HANDOFF.md` §0 avant toute chose** : il donne les chiffres
motif par motif, et ce serait du gâchis de les réajuster à l'aveugle par-dessus.

Le 2026-08-09 aussi : **icône posée sur les 7 fenêtres** (elles affichaient un cadre vide) et
**fin du gel du tableau de bord au lendemain d'une veille** — le rappel périodique s'ouvrait au
réveil, en modal, dans une fenêtre absente de la barre des tâches et d'Alt-Tab. Voir `HANDOFF.md`
§8 : ⚠️ ne jamais faire un `ShowDialog()` sur une fenêtre que l'utilisateur ne peut pas retrouver,
et ne rien déclencher tout seul au réveil de veille.

🧪 **v1.7.2 packagée le 2026-09-30 à 18:38, pas encore lancée par l'utilisateur**
(`%USERPROFILE%\TimeTracker-v1.7.2\` ; retour arrière = relancer sa v1.7.1). Seul changement :
dans le tableau de bord, les totaux par tâche **défilent** au lieu d'écraser le détail, avec une
**poignée** entre les deux (`FitTotals`, `TotalsSplitter`). Voir `HANDOFF.md` §0 et §8.

🧪 **v1.7.1 packagée le 2026-09-30, lancée par l'utilisateur le 30/09 à 09:20**
(`%USERPROFILE%\TimeTracker-v1.7.1\` ; retour arrière = relancer sa v1.7). Préfixe de réunion :
**« Meeting »** depuis le 30/09 (« Meetting » était une faute, corrigée par lui). Ses deux retours après une
demi-journée de v1.7 : **le focus volé** par les fenêtres qui s'ouvrent seules (rappel, agenda :
`Activate()`) et **les boutons hors écran** quand l'IA répond. Corrigés (`ShowActivated="False"`,
`WindowFit.KeepBottomRight`, vérifiés par `--uitest`), plus ce que son journal a montré : préfixe de
réunion changé en « Meetting » (les « Réunion — … » n'étaient plus des réunions), **reprise** de la
tâche dont les fenêtres reviennent, **tâche d'avant** juste après les favoris dans le sélecteur,
oubli ciblé de l'appris. **Lis `HANDOFF.md` §0, « Premiers retours sur la v1.7 ».** L'agent lit
`%APPDATA%\TimeTracker\log.txt` directement : pas besoin de script d'extraction.

🧪 **v1.7 packagée le 2026-09-29, en test chez l'utilisateur** (`%USERPROFILE%\TimeTracker-v1.7\`,
lancée par lui le 29/09 ; retour arrière = relancer sa v1.6.2). Après dépouillement de la 4e
collecte (2026-09-17 → 29) : les suggestions ne pouvaient pas marcher — **63 % de son temps
part sur des tâches créées le jour même**, et le suggéreur ne reclassait que l'existant. La v1.7
apporte, sur ses décisions explicites du 29/09 : **proposition de changement de tâche**
(`ActivityShiftDetector` + `ActivityShiftWindow`, jamais de bascule sans clic), **« Tu voulais
dire … ? »** à la création, bandeau **« Noms à vérifier »** (`NameReview`, doublons **mot à mot**),
**apprentissage différé** d'après la base, **encart du rappel retiré**, et l'**IA qui propose des
noms** d'après des mots-clés (`TaskSuggestionAssistant`, interrupteur à part coupé par défaut).
**Lis `HANDOFF.md` §0, section « 4e collecte »** : décisions, ce qui n'a pas tourné en réel (seuils
de détection, appel Gemini avec le nouveau prompt), lignes du journal à relire.

✅ **v1.6.2 packagée le 2026-09-17** (`%USERPROFILE%\TimeTracker-v1.6.2\`), vérifiée sur l'exe
publié ; **Gemini vérifié par l'utilisateur** avec sa clé (« répond OK »). La v1.5 du même jour a
apporté l'objectif d'heures (par jour ou par semaine au choix depuis la 1.6.2), l'export de
période libre,
raccourci pause `Ctrl+Alt+P`, **suggestions de tâche d'après les fenêtres au premier plan**
(`ActivityProbe`/`TaskSuggester`) et **doublons probables** (`TaskSimilarity`, local). La v1.6
ajoute, **sur décision explicite de l'utilisateur** : l'**apprentissage** des fenêtres en base
(`task_hints`, des mots, jamais des titres entiers, débrayable + « Oublier ») et un **assistant IA
multi-fournisseur** (`Core/Services/Ai/` : Gemini, compatible OpenAI dont Ollama local,
Anthropic), **coupé par défaut**, un seul usage à la demande (fusions/renommages de tâches à
cocher), seuls les noms de tâches envoyés. Gemini testé en réel par l'utilisateur ; OpenAI et
Anthropic écrits de mémoire, non testés. La promesse est désormais « 100 % local **par défaut** ».

✅ **v1.4 packagée le 2026-09-17** (`%USERPROFILE%\TimeTracker-v1.4\`), vérifiée sur l'exe
publié, après dépouillement de **cinq semaines avec l'agenda** (3e collecte,
`logs/TimeTracker-journaux-20260917-0822/`). Elle apporte : **réunions qui se suivent coupées à la
frontière** des créneaux (elles se cumulaient sur une entrée — 26 fois en cinq semaines),
**choix entre créneaux simultanés** (un bouton par réunion), **clôture à l'heure du départ** quand
le poste reste verrouillé/en veille plus de 30 min (`AbsenceCutoffMinutes`), **nommage pendant la
réunion**. **Lis `HANDOFF.md` §0** : ce qui a été fait et pourquoi, et ce qui n'a pas encore
tourné en conditions réelles. ⚠️ Ne jamais écraser un paquet existant : ce sont les seuls
retours arrière sur les exécutables publiés.

Depuis le 2026-09-27, le projet tourne sur **le poste de travail de l'utilisateur** — celui où
son TimeTracker tourne en permanence et où se trouve son **vrai relevé**. `dotnet` est dans le
PATH (SDK 9, qui compile la cible net8 ; le contournement `dotnet-shim` de `HANDOFF.md` §5 ne
concerne que l'ancien poste). Depuis la v1.7, `--selftest` / `--uitest` tournent **pendant** que
son appli tourne (ils passent avant le mutex) et `--db=` ne touche plus au registre de
démarrage. Pièges de ce poste : `HANDOFF.md` §8, en tête.

⚠️ **Le poste de l'utilisateur est un portable à 150 %** (~670 points de haut), le poste de dev a
un grand écran à 100 %. Une fenêtre haute doit avoir un `ScrollViewer` **et**
`WindowFit.LimitToWorkArea` — `--uitest` le vérifie. Détail en `HANDOFF.md` §8.

**Prochain sujet** : relire son `log.txt` (v1.7.1 depuis le 30/09 09:20) sur les lignes listées
en `HANDOFF.md` §0 (« À lire dans la prochaine collecte », v1.7 et v1.7.1) avant de toucher au
moindre seuil ; lui signaler l'oubli ciblé pour la tâche fourre-tout. Ensuite l'**installeur** (étape 8). La v1.4 est validée en réel (4e collecte). Points
ouverts en `HANDOFF.md` §0, « Ce qui reste à faire ».

✅ **Étape 4 (Outlook) livrée et éprouvée.** Lecture de l'agenda par **COM sur l'Outlook
classique**, derrière `ICalendarSource`. L'agenda est une **troisième sonde** du détecteur : il
nomme mieux que les fenêtres, et déclenche seulement si l'utilisateur a répondu « oui ». Trois
réunions ont été pointées sur l'agenda seul en cinq semaines. ⚠️ Un créneau qui « commence »
pendant une réunion se traite de trois façons (connecté en avance / réunion suivante /
chevauchement) — `HANDOFF.md` §8.

🚫 **Microsoft Graph / OAuth est hors de portée — ne pas relancer le sujet.** Vérifié par
l'utilisateur le 2026-08-09 : `portal.azure.com` → Microsoft Entra ID répond « you don't have
access ». Pas d'inscription d'application possible, donc pas de client ID, donc rien à tester.
Ça ne rouvrira que si son service informatique déclare l'application pour lui.

À faire ensuite : retours sur la v1.7 et collecte suivante, puis l'installeur (étape 8).

## Contraintes à ne pas oublier

- ❌ **La reconnaissance vocale a été abandonnée** après avoir été développée puis retirée
  (précision insuffisante). **Ne pas la réintroduire** sans demande explicite de l'utilisateur.
- ⚠️ **Dépôt git PUBLIC** (GitHub `vinii34/timetracker`). **Aucun vrai nom** de client,
  collègue, réunion ou employeur dans le code, les tests ou la doc : utiliser des noms fictifs
  (Orvane, Velmora, Kestrio, Contoso, Camille Durand…). Les journaux de `logs/` restent hors
  du dépôt (`.gitignore`) : ils contiennent de vrais titres de fenêtres.
- ⚠️ **Le projet est dans OneDrive** : publier les gros artefacts ailleurs (`%USERPROFILE%\...`).
- ⚠️ **Ne jamais réécrire un fichier source via `Get-Content | Set-Content`** en PowerShell 5.1 :
  double encodage UTF-8 garanti (`é` → `Ã©`) sur tout le fichier. Utiliser l'outil Edit.
- ⚠️ **Culture** : `AppCulture` sépare volontairement les libellés (toujours fr-FR) des nombres
  exportés (`CurrentCulture`, jamais forcés — Excel lit les décimales selon le réglage Windows).
  Ne pas « harmoniser » les deux. Explication complète dans `HANDOFF.md` §3.

## Vérifier son travail

Le build seul ne suffit pas : les erreurs XAML n'apparaissent **qu'au rendu**.

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build TimeTracker.csproj   # viser 0 warning
Start-Process .\bin\Debug\net8.0-windows\win-x64\TimeTracker.exe -ArgumentList "--selftest","--uitest" -Wait
Get-Content "$env:APPDATA\TimeTracker\log.txt" -Tail 5 -Encoding utf8
```

- `--selftest` : services, export CSV/XLSX et réglages, sur une base **jetable**.
- `--uitest[=<dossier>]` : ouvre et rend chaque fenêtre hors écran ; avec un dossier, écrit une
  capture PNG par fenêtre (revue visuelle sans piloter l'écran).
- `--db=<chemin>` : explorer l'UI sur une base de test sans toucher au relevé réel.
- `--meetingprobe[=<fichier>]` : **instantané** du micro + fenêtres + verdict, puis **quitte**
  (aucun suivi du temps — ne pas le proposer comme mode d'usage prolongé).
- `--outlookprobe[=<fichier>]` : idem pour l'agenda — présence d'Outlook, filtre exact envoyé,
  réunions relevées. Le poste de dev n'ayant **aucun profil Outlook**, c'est le seul moyen de
  vérifier la lecture COM là où elle doit marcher.
- `--meetingtrace[=<fichier>]` : suivi normal **plus** journal de diagnostic continu. C'est
  l'outil de collecte sur plusieurs jours. `--meetingobserve` détecte sans jamais basculer.

Ne jamais faire tourner un test sur `%APPDATA%\TimeTracker\timetracker.db` : c'est le vrai
relevé de temps de l'utilisateur.

## Style

Code et commentaires **en français**, comme l'existant. Commentaires réservés au « pourquoi »
non évident (contraintes WPF, pièges de culture, frontières de responsabilité), pas au « quoi ».
