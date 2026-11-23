using System.Globalization;
using System.Text.Json;
using SandUp.Application;
using SandUp.Domain;
using Microsoft.Data.Sqlite;

namespace SandUp.Infrastructure;

// Indexed columns support bounded queries; JSON retains the complete versioned DTO.
// No monotonic clock anchors are serialized by the persistence contracts.
public sealed partial class SqliteWorkClockStore : IWorkClockStore, IWorkClockRecordReader, IWorkClockOperationReader
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool ready;
    private static readonly HashSet<string> Activities = ["walk", "look_away", "relax_shoulders_hands", "other"];
    public SqliteWorkClockStore(string databasePath) => path = Path.GetFullPath(databasePath);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HealthyWorkClock", "healthy-work.db");
    private SqliteConnection Connect()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 2 }.ToString());
        c.Open(); return c;
    }
    private async Task<T> Run<T>(Func<SqliteConnection, T> action, bool opening = false)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => { if (!opening && !ready) throw new InvalidOperationException("Store is not initialized."); using var c = Connect(); return action(c); }).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        return cmd;
    }
    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values) { using var cmd = Cmd(c, tx, sql, values); cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values) { using var cmd = Cmd(c, tx, sql, values); return cmd.ExecuteScalar(); }
    private static List<T> Read<T>(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        using var cmd = Cmd(c, tx, sql, values); using var r = cmd.ExecuteReader(); var list = new List<T>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<T>(r.GetString(0)) ?? throw new InvalidDataException("Invalid saved payload."));
        return list;
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Utc(DateTimeOffset date) => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static long Revision(SqliteConnection c, SqliteTransaction? tx = null) => Convert.ToInt64(Scalar(c, tx, "SELECT revision FROM metadata WHERE id=1"));
    private static void SaveValue<T>(SqliteConnection c, SqliteTransaction tx, string key, T value) => Exec(c, tx, "INSERT INTO app_values(key,payload) VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload", key, Json(value));
    private static T? Value<T>(SqliteConnection c, SqliteTransaction? tx, string key) => Read<T>(c, tx, "SELECT payload FROM app_values WHERE key=$p0", key).FirstOrDefault();

    public async Task<MigrationReport> OpenAndMigrateAsync()
    {
        int previous = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return await Run(c =>
            {
                ready = false;
                previous = Convert.ToInt32(Scalar(c, null, "PRAGMA user_version"));
                if (previous > 1) return new MigrationReport(previous, previous, false, "Database schema is newer than this application.");
                if (Convert.ToString(Scalar(c, null, "PRAGMA quick_check")) != "ok") return new MigrationReport(previous, previous, false, "Database integrity check failed.");
                using var tx = c.BeginTransaction();
                Exec(c, tx, """
                    CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL);
                    INSERT OR IGNORE INTO metadata VALUES(1,0);
                    CREATE TABLE IF NOT EXISTS app_values(key TEXT PRIMARY KEY,payload TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY,started_utc TEXT NOT NULL,start_date TEXT NOT NULL,end_date TEXT,outcome INTEGER NOT NULL,work_ms INTEGER NOT NULL CHECK(work_ms>=0),rest_ms INTEGER NOT NULL CHECK(rest_ms>=0),payload TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS sessions_by_date ON sessions(start_date,started_utc,session_id);
                    CREATE UNIQUE INDEX IF NOT EXISTS one_active_session ON sessions(outcome) WHERE outcome=0;
                    CREATE TABLE IF NOT EXISTS time_slices(slice_id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,sequence_no INTEGER NOT NULL,local_date TEXT NOT NULL,kind INTEGER NOT NULL,duration_ms INTEGER NOT NULL CHECK(duration_ms>=0),finalized INTEGER NOT NULL,payload TEXT NOT NULL,UNIQUE(session_id,sequence_no));
                    CREATE INDEX IF NOT EXISTS slices_by_date ON time_slices(local_date,session_id);
                    CREATE TABLE IF NOT EXISTS session_activities(session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,activity_id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(session_id,activity_id));
                    CREATE TABLE IF NOT EXISTS operation_receipts(operation_id TEXT PRIMARY KEY,command_id TEXT,session_id TEXT REFERENCES sessions(session_id) ON DELETE CASCADE,revision INTEGER NOT NULL,payload TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """);
                if (Value<StoredSettings>(c, tx, "settings") is null)
                    SaveValue(c, tx, "settings", new StoredSettings(new SettingsSnapshot(1500000, 300000, 20000, new long[] { 180000, 300000 }, 1), DateTimeOffset.UtcNow, 1));
                if (Value<WorkspaceDraft>(c, tx, "draft") is null) SaveValue(c, tx, "draft", new WorkspaceDraft("", DateTimeOffset.UtcNow));
                InstallReminderInvalidation(c, tx);
                ResetReminderCache();
                tx.Commit(); ready = true; return new MigrationReport(previous, 1, true, null);
            }, true);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        { ready = false; return new MigrationReport(previous, previous, false, e.GetType().Name); }
    }

    public Task<PersistedAppState> LoadAsync() => Run(c =>
    {
        using var tx = c.BeginTransaction();
        var cp = Value<RuntimeCheckpoint>(c, tx, "checkpoint");
        var current = cp?.SessionId is Guid id ? Read<SessionRecord>(c, tx, "SELECT payload FROM sessions WHERE session_id=$p0", id.ToString()).SingleOrDefault() : null;
        var slices = current is null ? new List<TimeSlice>() : Read<TimeSlice>(c, tx, "SELECT payload FROM time_slices WHERE session_id=$p0 ORDER BY sequence_no", current.SessionId.ToString());
        return new PersistedAppState(Revision(c, tx), Value<StoredSettings>(c, tx, "settings")!, Value<WorkspaceDraft>(c, tx, "draft")!, cp, current, slices, cp?.PendingCheckInSessionId);
    });

    public Task<CommitResult> CommitAsync(AtomicWriteBatch batch) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        try
        {
            var existing = Scalar(c, tx, "SELECT revision FROM operation_receipts WHERE operation_id=$p0", batch.OperationId.ToString());
            if (existing is not null) return new CommitResult(CommitStatus.AlreadyCommitted, Convert.ToInt64(existing), null);
            long revision = Revision(c, tx);
            if (revision != batch.ExpectedStorageRevision) return new CommitResult(CommitStatus.Conflict, revision, null);
            if (batch.OperationId != batch.Receipt.OperationId) throw new InvalidDataException("Receipt operation mismatch.");
            foreach (var s in batch.Sessions)
                Exec(c, tx, "INSERT INTO sessions VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7) ON CONFLICT(session_id) DO UPDATE SET end_date=excluded.end_date,outcome=excluded.outcome,work_ms=excluded.work_ms,rest_ms=excluded.rest_ms,payload=excluded.payload", s.SessionId.ToString(), Utc(s.StartedAtUtc), Date(s.StartedLocalDate), s.EndedLocalDate is { } d ? Date(d) : null, (int)s.Outcome, s.WorkMs, s.RestMs, Json(s));
            foreach (var s in batch.Slices)
            {
                var old = Read<TimeSlice>(c, tx, "SELECT payload FROM time_slices WHERE slice_id=$p0", s.SliceId.ToString()).SingleOrDefault();
                if (old is not null && (old.SessionId != s.SessionId || old.SequenceNo != s.SequenceNo || old.Kind != s.Kind || old.WorkSubtype != s.WorkSubtype || old.LocalDate != s.LocalDate || old.TimeZoneId != s.TimeZoneId || old.StartedAtUtc != s.StartedAtUtc || old.Finalized && old != s || s.DurationMs < old.DurationMs)) throw new InvalidDataException("Invalid time slice update.");
                Exec(c, tx, "INSERT INTO time_slices VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7) ON CONFLICT(slice_id) DO UPDATE SET duration_ms=excluded.duration_ms,finalized=excluded.finalized,payload=excluded.payload", s.SliceId.ToString(), s.SessionId.ToString(), s.SequenceNo, Date(s.LocalDate), (int)s.Kind, s.DurationMs, s.Finalized, Json(s));
            }
            foreach (var replacement in batch.ActivityReplacements)
            {
                Exec(c, tx, "DELETE FROM session_activities WHERE session_id=$p0", replacement.SessionId.ToString());
                foreach (var activity in replacement.Activities.DistinctBy(a => a.ActivityId))
                {
                    if (activity.SessionId != replacement.SessionId || !Activities.Contains(activity.ActivityId)) throw new InvalidDataException("Invalid activity.");
                    Exec(c, tx, "INSERT INTO session_activities VALUES($p0,$p1,$p2)", activity.SessionId.ToString(), activity.ActivityId, Json(activity));
                }
            }
            foreach (var id in batch.Sessions.Select(s => s.SessionId).Concat(batch.Slices.Select(s => s.SessionId)).Concat(batch.ActivityReplacements.Select(s => s.SessionId)).Distinct())
            {
                var session = Read<SessionRecord>(c, tx, "SELECT payload FROM sessions WHERE session_id=$p0", id.ToString()).Single();
                var slices = Read<TimeSlice>(c, tx, "SELECT payload FROM time_slices WHERE session_id=$p0", id.ToString());
                if (slices.Where(s => s.Kind == SliceKind.Work).Sum(s => s.DurationMs) != session.WorkMs || slices.Where(s => s.Kind == SliceKind.Rest).Sum(s => s.DurationMs) != session.RestMs) throw new InvalidDataException("Session totals differ from slices.");
                bool reported = Convert.ToInt64(Scalar(c, tx, "SELECT count(*) FROM session_activities WHERE session_id=$p0", id.ToString())) > 0;
                if (reported != (session.CheckInStatus == CheckInStatus.Reported)) throw new InvalidDataException("Check-in status mismatch.");
            }
            if (batch.Settings is { } settings) SaveValue(c, tx, "settings", settings);
            if (batch.Draft is { } draft) SaveValue(c, tx, "draft", draft);
            if (batch.Checkpoint is SetCheckpoint cp) SaveValue(c, tx, "checkpoint", cp.Value with { StorageRevision = revision + 1 });
            else if (batch.Checkpoint is ClearCheckpoint) Exec(c, tx, "DELETE FROM app_values WHERE key='checkpoint'");
            var receipt = batch.Receipt with { CommittedRevision = revision + 1 };
            Exec(c, tx, "INSERT INTO operation_receipts VALUES($p0,$p1,$p2,$p3,$p4)", receipt.OperationId.ToString(), receipt.CommandId?.ToString(), receipt.SessionId?.ToString(), receipt.CommittedRevision, Json(receipt));
            Exec(c, tx, "UPDATE metadata SET revision=$p0 WHERE id=1", revision + 1);
            tx.Commit(); return new CommitResult(CommitStatus.Committed, revision + 1, null);
        }
        catch (Exception e) when (e is SqliteException or InvalidDataException or InvalidOperationException or OverflowException)
        { return new CommitResult(CommitStatus.Failed, null, e.GetType().Name); }
    });

    public Task<SessionRecord?> FindSessionAsync(Guid sessionId) => Run(c => Read<SessionRecord>(c, null, "SELECT payload FROM sessions WHERE session_id=$p0", sessionId.ToString()).SingleOrDefault());
    public Task<OperationReceipt?> FindReceiptByCommandAsync(Guid commandId) => Run(c => Read<OperationReceipt>(c, null, "SELECT payload FROM operation_receipts WHERE command_id=$p0 ORDER BY revision DESC LIMIT 1", commandId.ToString()).SingleOrDefault());
    public Task<IReadOnlyList<ActivityReport>> ReadActivitiesAsync(Guid sessionId) => Run<IReadOnlyList<ActivityReport>>(c => Read<ActivityReport>(c, null, "SELECT payload FROM session_activities WHERE session_id=$p0 ORDER BY activity_id", sessionId.ToString()));
    private static void Validate(DateRange range) { if (range.From > range.To) throw new ArgumentException("Invalid date range."); }
    public Task<SessionPage> QuerySessionsAsync(SessionQuery query) => Run(c =>
    {
        Validate(query.Range); if (query.PageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        var cursor = query.Cursor is null ? null : JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(query.Cursor));
        var items = Read<SessionRecord>(c, null, "SELECT payload FROM sessions WHERE start_date >= $p0 AND start_date <= $p1 AND ($p2 IS NULL OR started_utc < $p2 OR (started_utc=$p2 AND session_id<$p3)) ORDER BY started_utc DESC,session_id DESC LIMIT $p4", Date(query.Range.From), Date(query.Range.To), cursor?[0], cursor?[1], query.PageSize + 1);
        string? next = null;
        if (items.Count > query.PageSize) { items.RemoveAt(items.Count - 1); var last = items[^1]; next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { Utc(last.StartedAtUtc), last.SessionId.ToString() })); }
        return new SessionPage(items, next, DateTimeOffset.UtcNow);
    });
    public Task<IReadOnlyList<TimeSlice>> QuerySlicesAsync(DateRange range) => Run<IReadOnlyList<TimeSlice>>(c => { Validate(range); return Read<TimeSlice>(c, null, "SELECT payload FROM time_slices WHERE local_date >= $p0 AND local_date <= $p1 ORDER BY local_date,session_id,sequence_no", Date(range.From), Date(range.To)); });
    public Task<int> ClearHistoryAsync(ClearHistoryRequest request) => Run(c =>
    {
        Validate(request.Range); if (!request.Confirmed) throw new InvalidOperationException("ConfirmationRequired");
        using var tx = c.BeginTransaction();
        using var cmd = Cmd(c, tx, "DELETE FROM sessions WHERE outcome<>0 AND end_date >= $p0 AND end_date <= $p1", Date(request.Range.From), Date(request.Range.To));
        int count = cmd.ExecuteNonQuery();
        var cp = Value<RuntimeCheckpoint>(c, tx, "checkpoint");
        if (cp?.PendingCheckInSessionId is { } pending && Scalar(c, tx, "SELECT 1 FROM sessions WHERE session_id=$p0", pending.ToString()) is null)
            SaveValue(c, tx, "checkpoint", cp with { PendingCheckInSessionId = null, SessionId = cp.SessionId == pending ? null : cp.SessionId, SavedPhase = cp.SavedPhase == SessionPhase.AwaitingCheckIn ? SessionPhase.Idle : cp.SavedPhase });
        if (request.IncludeDraft) SaveValue(c, tx, "draft", new WorkspaceDraft("", DateTimeOffset.UtcNow));
        Exec(c, tx, "UPDATE metadata SET revision=revision+1 WHERE id=1"); tx.Commit(); return count;
    });

    // A single SQLite transaction supplies both CSV files and daily summaries.
    internal Task<(List<SessionRecord> Sessions, List<TimeSlice> Slices, List<ActivityReport> Activities, ReminderRates Rates)> ReadReportAsync(DateRange range) => Run(c =>
    {
        Validate(range); using var tx = c.BeginTransaction();
        return ReadReport(c, tx, range);
    });

    private static (List<SessionRecord> Sessions, List<TimeSlice> Slices, List<ActivityReport> Activities, ReminderRates Rates) ReadReport(SqliteConnection c, SqliteTransaction tx, DateRange range)
    {
        var slices = Read<TimeSlice>(c, tx, "SELECT payload FROM time_slices WHERE local_date >= $p0 AND local_date <= $p1 ORDER BY session_id,sequence_no", Date(range.From), Date(range.To));
        var sessions = Read<SessionRecord>(c, tx, "SELECT payload FROM sessions WHERE session_id IN (SELECT session_id FROM time_slices WHERE local_date >= $p0 AND local_date <= $p1) OR (end_date >= $p0 AND end_date <= $p1) ORDER BY started_utc,session_id", Date(range.From), Date(range.To));
        var activities = Read<ActivityReport>(c, tx, "SELECT a.payload FROM session_activities a JOIN sessions s ON s.session_id=a.session_id WHERE s.session_id IN (SELECT session_id FROM time_slices WHERE local_date >= $p0 AND local_date <= $p1) OR (s.end_date >= $p0 AND s.end_date <= $p1) ORDER BY a.session_id,a.activity_id", Date(range.From), Date(range.To));
        var rates = Value<StoredSettings>(c, tx, "settings")?.Settings.ReminderRates;
        return (sessions, slices, activities, rates is { IsValid: true } ? rates : ReminderRates.Default);
    }
}
