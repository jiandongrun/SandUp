using SandUp.Application;
using SandUp.Domain;
using Microsoft.Data.Sqlite;

namespace SandUp.Infrastructure;

public sealed partial class SqliteWorkClockStore
{
    // Cache only recorded dates. All access is serialized by gate, and all inputs and
    // invalidation versions are read in the same SQLite snapshot as the requested report.
    private readonly SortedList<DateOnly, ReminderDayState> reminderPrefix = new();
    private DateOnly? reminderThrough;
    private long reminderVersion = -1;
    private void ResetReminderCache()
    {
        reminderPrefix.Clear(); reminderThrough = null; reminderVersion = -1;
    }

    private static void InstallReminderInvalidation(SqliteConnection c, SqliteTransaction tx)
    {
        Exec(c, tx, """
            CREATE INDEX IF NOT EXISTS sessions_by_end_date ON sessions(end_date);
            CREATE TABLE IF NOT EXISTS reminder_change_clock(id INTEGER PRIMARY KEY CHECK(id=1),version INTEGER NOT NULL,reset_version INTEGER NOT NULL);
            INSERT OR IGNORE INTO reminder_change_clock VALUES(1,0,0);
            CREATE TABLE IF NOT EXISTS reminder_dirty_dates(local_date TEXT PRIMARY KEY,version INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS reminder_dirty_by_version ON reminder_dirty_dates(version);
            """);
        // SQLite triggers also cover raw SQL edits/deletes and writes by other store instances.
        // Per-date version rows are bounded by recorded dates, rather than checkpoint count.
        foreach (var (table, dateColumn, predicate) in new[]
        {
            ("time_slices", "local_date", "1"),
            ("sessions", "end_date", "{0}.end_date IS NOT NULL"),
            ("session_activities", "(SELECT end_date FROM sessions WHERE session_id={0}.session_id)", "1"),
            ("app_values", "'0001-01-01'", "{0}.key='settings'")
        })
        {
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                string Dirty(string row)
                {
                    string date = dateColumn.Contains("{0}") ? string.Format(dateColumn, row) :
                        dateColumn.StartsWith("'") ? dateColumn : row + "." + dateColumn;
                    return $"INSERT INTO reminder_dirty_dates SELECT {date},version FROM reminder_change_clock WHERE id=1 AND ({string.Format(predicate, row)}) AND {date} IS NOT NULL ON CONFLICT(local_date) DO UPDATE SET version=excluded.version;";
                }
                var rows = operation == "INSERT" ? Dirty("NEW") : operation == "DELETE"
                    ? "UPDATE reminder_change_clock SET reset_version=version WHERE id=1; DELETE FROM reminder_dirty_dates;"
                    : Dirty("OLD") + Dirty("NEW");
                // Batches may upsert already-finalized slices from earlier dates. Those
                // identical rows must not invalidate historical prefixes on each checkpoint.
                string when = operation == "UPDATE" ? " WHEN OLD.payload IS NOT NEW.payload" : "";
                Exec(c, tx, $"CREATE TRIGGER IF NOT EXISTS reminder_{table}_{operation.ToLowerInvariant()} AFTER {operation} ON {table}{when} BEGIN UPDATE reminder_change_clock SET version=version+1 WHERE id=1; {rows} END;");
            }
        }
    }

    private int PrefixIndex(DateOnly date)
    {
        int low = 0, high = reminderPrefix.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (reminderPrefix.Keys[mid] < date) low = mid + 1; else high = mid;
        }
        return low;
    }
    private static ReminderLevels Roll(ReminderLevels previous, ReminderLevels actual) => new(
        .8 * previous.Eyes + .2 * actual.Eyes, .8 * previous.Shoulders + .2 * actual.Shoulders,
        .8 * previous.Back + .2 * actual.Back, .8 * previous.Abdomen + .2 * actual.Abdomen,
        .8 * previous.Legs + .2 * actual.Legs, .8 * previous.Pelvic + .2 * actual.Pelvic);

    internal Task<((List<SessionRecord> Sessions, List<TimeSlice> Slices, List<ActivityReport> Activities, ReminderRates Rates) Data,
        IReadOnlyDictionary<DateOnly, ReminderDayState> Reminders)> ReadSummaryReportAsync(DateRange range) => Run(c =>
    {
        Validate(range);
        using var tx = c.BeginTransaction();
        long version = Convert.ToInt64(Scalar(c, tx, "SELECT version FROM reminder_change_clock WHERE id=1"));
        DateOnly? from = reminderThrough is null ? DateOnly.MinValue :
            reminderThrough.Value == DateOnly.MaxValue ? null : reminderThrough.Value.AddDays(1);
        if (reminderVersion >= 0 && version != reminderVersion)
        {
            long reset = Convert.ToInt64(Scalar(c, tx, "SELECT reset_version FROM reminder_change_clock WHERE id=1"));
            if (reset > reminderVersion) from = DateOnly.MinValue;
            var dirty = Scalar(c, tx, "SELECT MIN(local_date) FROM reminder_dirty_dates INDEXED BY reminder_dirty_by_version WHERE version > $p0", reminderVersion);
            if (dirty is string value)
            {
                var date = DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                if (from is null || date < from) from = date;
            }
        }
        var through = reminderThrough is { } old && old > range.To ? old : range.To;
        var data = ReadReport(c, tx, range);
        if (from is { } start && start <= through)
        {
            int index = PrefixIndex(start);
            // Build a replacement suffix before mutating the cache, so an unsuccessful
            // read/replay leaves the previous version usable for the next retry.
            var history = index == 0 ? ReminderLevels.Full : Roll(reminderPrefix.Values[index - 1].History, reminderPrefix.Values[index - 1].Levels);
            int count = index == 0 ? 0 : reminderPrefix.Values[index - 1].HistoryDays + 1;
            var facts = start == range.From && through == range.To ? data : ReadReport(c, tx, new(start, through));
            var slices = facts.Slices.ToLookup(s => s.LocalDate);
            var ended = facts.Sessions.Where(s => s.Outcome != SessionOutcome.Active && s.EndedLocalDate >= start && s.EndedLocalDate <= through).ToLookup(s => s.EndedLocalDate!.Value);
            var activities = facts.Activities.ToLookup(a => a.SessionId);
            var days = slices.Select(g => g.Key).Concat(ended.Select(g => g.Key)).Distinct().Order().ToArray();
            var suffix = new List<KeyValuePair<DateOnly, ReminderDayState>>();
            foreach (var day in days)
            {
                var state = SqliteHistoryService.SummarizeReminder(slices[day], ended[day], activities, data.Rates, history)
                    with { History = history, HistoryDays = count };
                if (!state.HasRecords) continue;
                suffix.Add(new(day, state));
                history = Roll(history, state.Levels); count++;
            }
            while (reminderPrefix.Count > index) reminderPrefix.RemoveAt(reminderPrefix.Count - 1);
            foreach (var pair in suffix) reminderPrefix.Add(pair.Key, pair.Value);
        }
        reminderThrough = through; reminderVersion = version;
        var result = new Dictionary<DateOnly, ReminderDayState>();
        for (int offset = 0; offset <= range.To.DayNumber - range.From.DayNumber; offset++)
        {
            var day = range.From.AddDays(offset);
            int index = PrefixIndex(day);
            if (index < reminderPrefix.Count && reminderPrefix.Keys[index] == day) result.Add(day, reminderPrefix.Values[index]);
            else
            {
                var previous = index == 0 ? null : reminderPrefix.Values[index - 1];
                result.Add(day, new(ReminderLevels.Full, false)
                {
                    History = previous is null ? ReminderLevels.Full : Roll(previous.History, previous.Levels),
                    HistoryDays = previous is null ? 0 : previous.HistoryDays + 1
                });
            }
        }
        return (data, (IReadOnlyDictionary<DateOnly, ReminderDayState>)result);
    });
}
