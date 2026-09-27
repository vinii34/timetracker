using System.IO;
using Microsoft.Data.Sqlite;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Accès SQLite (création du schéma + CRUD). 100 % local, fichier dans %APPDATA%\TimeTracker.
/// </summary>
public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(string? dbPath = null)
    {
        dbPath ??= DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Cache privé + WAL : lecteurs et écrivains ne se bloquent pas mutuellement
            // (le tableau de bord lit pendant que le service écrit).
            Cache = SqliteCacheMode.Default
        }.ToString();
    }

    public static string DefaultDbPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TimeTracker");
        return Path.Combine(dir, "timetracker.db");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText =
            "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS tasks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    last_used DATETIME,
    is_favorite INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS time_entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id INTEGER NOT NULL REFERENCES tasks(id),
    started_at DATETIME NOT NULL,
    ended_at DATETIME,
    duration_seconds INTEGER,
    is_meeting INTEGER DEFAULT 0,
    notes TEXT
);
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT
);
CREATE INDEX IF NOT EXISTS idx_entries_started ON time_entries(started_at);
-- Mots de titres de fenêtres vus pendant qu'une tâche tournait, en secondes cumulées :
-- c'est l'apprentissage des suggestions (décision de l'utilisateur, 2026-09-17).
CREATE TABLE IF NOT EXISTS task_hints (
    task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    word TEXT NOT NULL,
    seconds INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (task_id, word)
);";
        cmd.ExecuteNonQuery();

        // Bases créées avant les favoris : la colonne est ajoutée après coup.
        EnsureColumn(conn, "tasks", "is_favorite", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>Ajoute une colonne si elle manque (migration de schéma sur une base existante).</summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name;";
            check.Parameters.AddWithValue("$name", column);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
        Logger.Info($"Migration : colonne {table}.{column} ajoutée.");
    }

    // ---------------------------------------------------------------- Tasks

    /// <summary>Les N dernières tâches distinctes, triées par date de dernière utilisation.</summary>
    public List<TaskItem> GetRecentTasks(int limit = 10)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, last_used, is_favorite FROM tasks
ORDER BY (last_used IS NULL), last_used DESC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadTasks(cmd);
    }

    /// <summary>Trouve une tâche par nom (insensible à la casse) ou la crée.</summary>
    public TaskItem GetOrCreateTask(string name)
    {
        name = name.Trim();
        using var conn = Open();

        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT id, name, last_used, is_favorite FROM tasks WHERE name = $name COLLATE NOCASE LIMIT 1;";
            find.Parameters.AddWithValue("$name", name);
            var existing = ReadTasks(find);
            if (existing.Count > 0) return existing[0];
        }

        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO tasks (name, last_used) VALUES ($name, $now); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$now", DateTime.Now);
        var id = (long)insert.ExecuteScalar()!;
        return new TaskItem { Id = id, Name = name, LastUsed = DateTime.Now };
    }

    public TaskItem? GetTask(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, last_used, is_favorite FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadTasks(cmd).FirstOrDefault();
    }

    public void UpdateTaskName(long taskId, string newName)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$name", newName.Trim());
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    public void TouchTask(long taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET last_used = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", DateTime.Now);
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Tâches épinglées, par ordre alphabétique (tête du sélecteur).</summary>
    public List<TaskItem> GetFavoriteTasks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, last_used, is_favorite FROM tasks
WHERE is_favorite = 1
ORDER BY name COLLATE NOCASE ASC;";
        return ReadTasks(cmd);
    }

    public void SetFavorite(long taskId, bool favorite)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET is_favorite = $fav WHERE id = $id;";
        cmd.Parameters.AddWithValue("$fav", favorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Tâche portant ce nom (insensible à la casse), ou null. Sert à détecter les doublons.</summary>
    public TaskItem? FindTaskByName(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, last_used, is_favorite FROM tasks WHERE name = $name COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name.Trim());
        return ReadTasks(cmd).FirstOrDefault();
    }

    /// <summary>
    /// Toutes les tâches avec leur usage, pour la fenêtre de gestion. Le total ignore
    /// l'entrée en cours (durée non encore calculée) : elle est comptée dans EntryCount.
    /// </summary>
    public List<TaskUsage> GetTaskUsage()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT t.id, t.name, t.last_used, t.is_favorite,
       COUNT(e.id), COALESCE(SUM(e.duration_seconds), 0), MAX(e.started_at)
FROM tasks t LEFT JOIN time_entries e ON e.task_id = t.id
GROUP BY t.id
ORDER BY t.name COLLATE NOCASE ASC;";

        var list = new List<TaskUsage>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TaskUsage
            {
                Task = new TaskItem
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    LastUsed = r.IsDBNull(2) ? null : r.GetDateTime(2),
                    IsFavorite = !r.IsDBNull(3) && r.GetInt32(3) != 0
                },
                EntryCount = r.GetInt32(4),
                Total = TimeSpan.FromSeconds(r.GetInt64(5)),
                LastEntry = r.IsDBNull(6) ? null : r.GetDateTime(6)
            });
        }
        return list;
    }

    public int CountEntriesForTask(long taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM time_entries WHERE task_id = $id;";
        cmd.Parameters.AddWithValue("$id", taskId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Supprime une tâche de la bibliothèque. Refusé si des entrées y sont rattachées :
    /// la clé étrangère protège l'historique, il faut passer par <see cref="MergeTasks"/>.
    /// </summary>
    public void DeleteTask(long taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Fusionne <paramref name="sourceId"/> dans <paramref name="targetId"/> : les entrées
    /// changent de tâche, puis la source disparaît. C'est la sortie propre pour un doublon
    /// ou une faute de frappe qui porte déjà des heures.
    /// </summary>
    public void MergeTasks(long sourceId, long targetId)
    {
        if (sourceId == targetId) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var move = conn.CreateCommand())
        {
            move.Transaction = tx;
            move.CommandText = "UPDATE time_entries SET task_id = $target WHERE task_id = $source;";
            move.Parameters.AddWithValue("$target", targetId);
            move.Parameters.AddWithValue("$source", sourceId);
            move.ExecuteNonQuery();
        }

        // La cible hérite de la date d'usage la plus récente et du statut favori des deux.
        using (var merge = conn.CreateCommand())
        {
            merge.Transaction = tx;
            merge.CommandText = @"
UPDATE tasks SET
    last_used = MAX(COALESCE(last_used, '0001-01-01'),
                    COALESCE((SELECT last_used FROM tasks WHERE id = $source), '0001-01-01')),
    is_favorite = MAX(is_favorite, (SELECT is_favorite FROM tasks WHERE id = $source))
WHERE id = $target;";
            merge.Parameters.AddWithValue("$target", targetId);
            merge.Parameters.AddWithValue("$source", sourceId);
            merge.ExecuteNonQuery();
        }

        // Ce que la source avait appris des fenêtres passe à la cible (secondes additionnées).
        using (var hints = conn.CreateCommand())
        {
            hints.Transaction = tx;
            hints.CommandText = @"
INSERT INTO task_hints(task_id, word, seconds)
    SELECT $target, word, seconds FROM task_hints WHERE task_id = $source
ON CONFLICT(task_id, word) DO UPDATE SET seconds = seconds + excluded.seconds;
DELETE FROM task_hints WHERE task_id = $source;";
            hints.Parameters.AddWithValue("$target", targetId);
            hints.Parameters.AddWithValue("$source", sourceId);
            hints.ExecuteNonQuery();
        }

        using (var drop = conn.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = "DELETE FROM tasks WHERE id = $source;";
            drop.Parameters.AddWithValue("$source", sourceId);
            drop.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ------------------------------------------------ Apprentissage des fenêtres

    /// <summary>Ajoute des secondes de présence à des mots de titres pour une tâche (upsert).</summary>
    public void AddTaskHints(long taskId, IReadOnlyDictionary<string, int> secondsByWord)
    {
        if (secondsByWord.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO task_hints(task_id, word, seconds) VALUES ($task, $word, $seconds)
ON CONFLICT(task_id, word) DO UPDATE SET seconds = seconds + excluded.seconds;";
        var pTask = cmd.Parameters.Add("$task", SqliteType.Integer);
        var pWord = cmd.Parameters.Add("$word", SqliteType.Text);
        var pSeconds = cmd.Parameters.Add("$seconds", SqliteType.Integer);
        pTask.Value = taskId;
        foreach (var (word, seconds) in secondsByWord)
        {
            if (seconds <= 0 || word.Length > 40) continue;
            pWord.Value = word;
            pSeconds.Value = seconds;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Tout ce qui a été appris : tâche → mot → secondes.</summary>
    public Dictionary<long, Dictionary<string, int>> GetTaskHints()
    {
        var result = new Dictionary<long, Dictionary<string, int>>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT task_id, word, seconds FROM task_hints;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long task = r.GetInt64(0);
            if (!result.TryGetValue(task, out var words)) result[task] = words = new Dictionary<string, int>();
            words[r.GetString(1)] = r.GetInt32(2);
        }
        return result;
    }

    /// <summary>Oublie tout ce qui a été appris des fenêtres. Renvoie le nombre de lignes effacées.</summary>
    public int ClearTaskHints()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_hints;";
        return cmd.ExecuteNonQuery();
    }

    private static List<TaskItem> ReadTasks(SqliteCommand cmd)
    {
        var list = new List<TaskItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TaskItem
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                LastUsed = r.IsDBNull(2) ? null : r.GetDateTime(2),
                IsFavorite = !r.IsDBNull(3) && r.GetInt32(3) != 0
            });
        }
        return list;
    }

    // -------------------------------------------------------------- Entries

    /// <summary>Démarre une nouvelle entrée de temps et renvoie son id.</summary>
    public long StartEntry(long taskId, DateTime startedAt, bool isMeeting = false)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO time_entries (task_id, started_at, is_meeting)
VALUES ($task, $start, $meeting);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$start", startedAt);
        cmd.Parameters.AddWithValue("$meeting", isMeeting ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Clôture une entrée : renseigne ended_at et duration_seconds.</summary>
    public void EndEntry(long entryId, DateTime endedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE time_entries
SET ended_at = $end,
    duration_seconds = CAST((julianday($end) - julianday(started_at)) * 86400 AS INTEGER)
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$end", endedAt);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ajuste l'heure de début d'une entrée et recalcule la durée si clôturée.</summary>
    public void UpdateEntryStart(long entryId, DateTime startedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE time_entries
SET started_at = $start,
    duration_seconds = CASE WHEN ended_at IS NULL THEN NULL
        ELSE CAST((julianday(ended_at) - julianday($start)) * 86400 AS INTEGER) END
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$start", startedAt);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteEntry(long entryId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM time_entries WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Entrée non clôturée (ended_at NULL) la plus récente, pour reprise au lancement.</summary>
    public TimeEntry? GetOpenEntry()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id, e.task_id, e.started_at, e.ended_at, e.duration_seconds, e.is_meeting, e.notes, t.name
FROM time_entries e JOIN tasks t ON t.id = e.task_id
WHERE e.ended_at IS NULL
ORDER BY e.started_at DESC LIMIT 1;";
        return ReadEntries(cmd).FirstOrDefault();
    }

    /// <summary>Toutes les entrées d'un jour donné (jointes au nom de tâche).</summary>
    public List<TimeEntry> GetEntriesForDay(DateTime day) =>
        GetEntriesForRange(day.Date, day.Date.AddDays(1));

    /// <summary>
    /// Entrées dont le début tombe dans [from, toExclusive[ (jointes au nom de tâche).
    /// Le filtre porte sur <c>started_at</c> : une entrée à cheval sur minuit est comptée
    /// dans son jour de démarrage, comme dans la vue « Aujourd'hui ».
    /// </summary>
    public List<TimeEntry> GetEntriesForRange(DateTime from, DateTime toExclusive)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id, e.task_id, e.started_at, e.ended_at, e.duration_seconds, e.is_meeting, e.notes, t.name
FROM time_entries e JOIN tasks t ON t.id = e.task_id
WHERE e.started_at >= $start AND e.started_at < $end
ORDER BY e.started_at ASC;";
        cmd.Parameters.AddWithValue("$start", from);
        cmd.Parameters.AddWithValue("$end", toExclusive);
        return ReadEntries(cmd);
    }

    /// <summary>Entrée la plus récente, clôturée ou non (pour « supprimer la dernière »).</summary>
    public TimeEntry? GetLastEntry()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id, e.task_id, e.started_at, e.ended_at, e.duration_seconds, e.is_meeting, e.notes, t.name
FROM time_entries e JOIN tasks t ON t.id = e.task_id
ORDER BY e.started_at DESC, e.id DESC LIMIT 1;";
        return ReadEntries(cmd).FirstOrDefault();
    }

    /// <summary>Une entrée par son identifiant (jointe au nom de tâche), ou null si supprimée.</summary>
    public TimeEntry? GetEntry(long entryId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id, e.task_id, e.started_at, e.ended_at, e.duration_seconds, e.is_meeting, e.notes, t.name
FROM time_entries e JOIN tasks t ON t.id = e.task_id
WHERE e.id = $id;";
        cmd.Parameters.AddWithValue("$id", entryId);
        return ReadEntries(cmd).FirstOrDefault();
    }

    /// <summary>
    /// Entrée précédant chronologiquement <paramref name="entryId"/>. Sert à rogner l'entrée
    /// d'avant quand on antidate le début de la tâche en cours (sinon les deux se chevauchent).
    /// </summary>
    public TimeEntry? GetEntryBefore(long entryId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id, e.task_id, e.started_at, e.ended_at, e.duration_seconds, e.is_meeting, e.notes, t.name
FROM time_entries e JOIN tasks t ON t.id = e.task_id
WHERE e.id <> $id
  AND e.started_at <= (SELECT started_at FROM time_entries WHERE id = $id)
ORDER BY e.started_at DESC, e.id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", entryId);
        return ReadEntries(cmd).FirstOrDefault();
    }

    /// <summary>Réécrit début et fin d'une entrée et recalcule la durée.</summary>
    public void UpdateEntryTimes(long entryId, DateTime startedAt, DateTime? endedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE time_entries
SET started_at = $start,
    ended_at = $end,
    duration_seconds = CASE WHEN $end IS NULL THEN NULL
        ELSE CAST((julianday($end) - julianday($start)) * 86400 AS INTEGER) END
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$start", startedAt);
        cmd.Parameters.AddWithValue("$end", (object?)endedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rattache une entrée à une autre tâche (correction de saisie).</summary>
    public void UpdateEntryTask(long entryId, long taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE time_entries SET task_id = $task WHERE id = $id;";
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Écrit le commentaire d'une entrée. Sert au titre de réunion relevé : la collecte du
    /// 2026-08-03 au 07 a montré que l'appli connaissait un titre exploitable pour 8 réunions
    /// sur 13 et n'en faisait rien — il n'apparaissait que dans une bulle de notification que
    /// l'utilisateur n'a jamais vue.
    /// </summary>
    public void UpdateEntryNotes(long entryId, string? notes)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE time_entries SET notes = $notes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Toutes les tâches, par ordre alphabétique (listes déroulantes d'édition).</summary>
    public List<TaskItem> GetAllTasks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, last_used, is_favorite FROM tasks ORDER BY name COLLATE NOCASE ASC;";
        return ReadTasks(cmd);
    }

    public DateTime? GetLastEntryDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(started_at) FROM time_entries;";
        var result = cmd.ExecuteScalar();
        return result is null || result is DBNull ? null : Convert.ToDateTime(result);
    }

    private static List<TimeEntry> ReadEntries(SqliteCommand cmd)
    {
        var list = new List<TimeEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TimeEntry
            {
                Id = r.GetInt64(0),
                TaskId = r.GetInt64(1),
                StartedAt = r.GetDateTime(2),
                EndedAt = r.IsDBNull(3) ? null : r.GetDateTime(3),
                DurationSeconds = r.IsDBNull(4) ? null : r.GetInt32(4),
                IsMeeting = !r.IsDBNull(5) && r.GetInt32(5) != 0,
                Notes = r.IsDBNull(6) ? null : r.GetString(6),
                TaskName = r.GetString(7)
            });
        }
        return list;
    }

    // ------------------------------------------------------------- Settings

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO settings (key, value) VALUES ($key, $value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Efface un réglage (sert aux tests de migration : simuler une base plus ancienne).</summary>
    public void DeleteSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public AppSettings LoadSettings()
    {
        var s = new AppSettings();
        if (int.TryParse(GetSetting(AppSettings.KeyReminderInterval), out var interval) && interval > 0)
            s.ReminderIntervalMinutes = interval;
        if (bool.TryParse(GetSetting(AppSettings.KeyStartWithWindows), out var start))
            s.StartWithWindows = start;
        if (bool.TryParse(GetSetting(AppSettings.KeyReminderSound), out var sound))
            s.ReminderSound = sound;
        if (bool.TryParse(GetSetting(AppSettings.KeyAskTaskOnStartup), out var ask))
            s.AskTaskOnStartup = ask;
        // Zéro est légitime (fonction coupée), un négatif ne l'est pas.
        if (int.TryParse(GetSetting(AppSettings.KeyAbsenceCutoff), out var absence) && absence >= 0)
            s.AbsenceCutoffMinutes = absence;

        var folder = GetSetting(AppSettings.KeyExportFolder);
        if (!string.IsNullOrWhiteSpace(folder)) s.ExportFolder = folder;

        var hkTask = GetSetting(AppSettings.KeyHotkeyTask);
        if (!string.IsNullOrWhiteSpace(hkTask)) s.HotkeyTask = hkTask;

        var hkEdit = GetSetting(AppSettings.KeyHotkeyEdit);
        if (!string.IsNullOrWhiteSpace(hkEdit)) s.HotkeyEdit = hkEdit;

        var hkPause = GetSetting(AppSettings.KeyHotkeyPause);
        if (!string.IsNullOrWhiteSpace(hkPause)) s.HotkeyPause = hkPause;

        // Stocké en culture invariante : la virgule décimale française ne doit pas dépendre du
        // réglage Windows du jour (même famille de piège que l'export, voir AppCulture).
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (double.TryParse(GetSetting(AppSettings.KeyDailyHoursGoal), System.Globalization.NumberStyles.Float,
                            invariant, out var daily) && daily >= 0)
        {
            s.DailyHoursGoal = daily;
            if (bool.TryParse(GetSetting(AppSettings.KeyGoalPerWeek), out var perWeek)) s.GoalPerWeek = perWeek;
        }
        // Base d'avant la v1.6.2 : la valeur était hebdomadaire. Elle devient un objectif par
        // jour (÷ 5), affiché en semaine pour que l'utilisateur retrouve son chiffre.
        else if (double.TryParse(GetSetting(AppSettings.KeyWeeklyHoursGoal), System.Globalization.NumberStyles.Float,
                                 invariant, out var weekly) && weekly >= 0)
        {
            s.DailyHoursGoal = weekly / 5;
            s.GoalPerWeek = true;
        }

        if (bool.TryParse(GetSetting(AppSettings.KeyActivitySuggestions), out var activity))
            s.ActivitySuggestions = activity;
        if (bool.TryParse(GetSetting(AppSettings.KeyActivityLearning), out var learning))
            s.ActivityLearning = learning;

        if (bool.TryParse(GetSetting(AppSettings.KeyAiEnabled), out var ai))
            s.AiEnabled = ai;
        var aiProvider = GetSetting(AppSettings.KeyAiProvider);
        if (!string.IsNullOrWhiteSpace(aiProvider)) s.AiProvider = aiProvider;
        s.AiApiKey = GetSetting(AppSettings.KeyAiApiKey) ?? "";
        s.AiModel = GetSetting(AppSettings.KeyAiModel) ?? "";
        s.AiBaseUrl = GetSetting(AppSettings.KeyAiBaseUrl) ?? "";

        if (bool.TryParse(GetSetting(AppSettings.KeyMeetingDetection), out var meeting))
            s.MeetingDetection = meeting;

        var meetingTask = GetSetting(AppSettings.KeyMeetingTaskName);
        if (!string.IsNullOrWhiteSpace(meetingTask)) s.MeetingTaskName = meetingTask;

        // Un délai à zéro est légitime (bascule immédiate), un délai négatif ne l'est pas.
        if (int.TryParse(GetSetting(AppSettings.KeyMeetingStartDelay), out var startDelay) && startDelay >= 0)
            s.MeetingStartDelaySeconds = startDelay;
        if (int.TryParse(GetSetting(AppSettings.KeyMeetingEndGrace), out var endGrace) && endGrace >= 0)
            s.MeetingEndGraceSeconds = endGrace;

        var meetingApps = GetSetting(AppSettings.KeyMeetingApps);
        if (!string.IsNullOrWhiteSpace(meetingApps)) s.MeetingApps = meetingApps;

        if (bool.TryParse(GetSetting(AppSettings.KeyOutlookCalendar), out var outlook))
            s.OutlookCalendar = outlook;
        if (bool.TryParse(GetSetting(AppSettings.KeyOutlookAsk), out var outlookAsk))
            s.OutlookAsk = outlookAsk;

        return s;
    }

    public void SaveSettings(AppSettings s)
    {
        SetSetting(AppSettings.KeyReminderInterval, s.ReminderIntervalMinutes.ToString());
        SetSetting(AppSettings.KeyStartWithWindows, s.StartWithWindows.ToString());
        SetSetting(AppSettings.KeyReminderSound, s.ReminderSound.ToString());
        SetSetting(AppSettings.KeyAskTaskOnStartup, s.AskTaskOnStartup.ToString());
        SetSetting(AppSettings.KeyAbsenceCutoff, s.AbsenceCutoffMinutes.ToString());
        SetSetting(AppSettings.KeyExportFolder, s.ExportFolder);
        SetSetting(AppSettings.KeyHotkeyTask, s.HotkeyTask);
        SetSetting(AppSettings.KeyHotkeyEdit, s.HotkeyEdit);
        SetSetting(AppSettings.KeyHotkeyPause, s.HotkeyPause);
        SetSetting(AppSettings.KeyDailyHoursGoal,
                   s.DailyHoursGoal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetSetting(AppSettings.KeyGoalPerWeek, s.GoalPerWeek.ToString());
        SetSetting(AppSettings.KeyActivitySuggestions, s.ActivitySuggestions.ToString());
        SetSetting(AppSettings.KeyActivityLearning, s.ActivityLearning.ToString());
        SetSetting(AppSettings.KeyAiEnabled, s.AiEnabled.ToString());
        SetSetting(AppSettings.KeyAiProvider, s.AiProvider);
        SetSetting(AppSettings.KeyAiApiKey, s.AiApiKey);
        SetSetting(AppSettings.KeyAiModel, s.AiModel);
        SetSetting(AppSettings.KeyAiBaseUrl, s.AiBaseUrl);
        SetSetting(AppSettings.KeyMeetingDetection, s.MeetingDetection.ToString());
        SetSetting(AppSettings.KeyMeetingTaskName, s.MeetingTaskName);
        SetSetting(AppSettings.KeyMeetingStartDelay, s.MeetingStartDelaySeconds.ToString());
        SetSetting(AppSettings.KeyMeetingEndGrace, s.MeetingEndGraceSeconds.ToString());
        SetSetting(AppSettings.KeyMeetingApps, s.MeetingApps);
        SetSetting(AppSettings.KeyOutlookCalendar, s.OutlookCalendar.ToString());
        SetSetting(AppSettings.KeyOutlookAsk, s.OutlookAsk.ToString());
    }
}
