using System.Globalization;
using System.Text;
using SandUp.Application;
using SandUp.Domain;

namespace SandUp.Infrastructure;

public sealed class SqliteHistoryService(SqliteWorkClockStore store) : IHistoryService
{
    public Task<IReadOnlyList<ActivityReport>> GetActivitiesAsync(Guid sessionId) => store.ReadActivitiesAsync(sessionId);
    public Task<SessionPage> GetSessionsAsync(SessionQuery query) => store.QuerySessionsAsync(query);
    public async Task<DaySummary> GetDayAsync(DateOnly localDate) =>
        (await GetDaysAsync(new(localDate, localDate)).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<DaySummary>> GetDaysAsync(DateRange range)
    {
        int dayCount = range.To.DayNumber - range.From.DayNumber + 1;
        if (dayCount is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(range));

        var snapshot = await store.ReadSummaryReportAsync(range).ConfigureAwait(false);
        var data = snapshot.Data;
        var slicesByDay = data.Slices.ToLookup(s => s.LocalDate);
        var endedByDay = data.Sessions.Where(s => s.Outcome != SessionOutcome.Active && s.EndedLocalDate.HasValue)
            .ToLookup(s => s.EndedLocalDate!.Value);
        var activitiesBySession = data.Activities.ToLookup(a => a.SessionId);
        var asOf = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, dayCount).Select(offset =>
        {
            var day = range.From.AddDays(offset);
            var slices = slicesByDay[day];
            var ended = endedByDay[day];
            var activityCounts = ended.SelectMany(s => activitiesBySession[s.SessionId])
                .GroupBy(a => a.ActivityId).ToDictionary(g => g.Key, g => g.Select(a => a.SessionId).Distinct().Count());
            return new DaySummary(day,
                slices.Where(s => s.Kind == SliceKind.Work).Sum(s => s.DurationMs),
                slices.Where(s => s.Kind == SliceKind.Rest).Sum(s => s.DurationMs),
                ended.Count(s => s.Outcome == SessionOutcome.Completed), ended.Count(s => s.Outcome == SessionOutcome.Skipped),
                ended.Count(s => s.Outcome == SessionOutcome.EndedEarly), ended.Count(s => s.Outcome == SessionOutcome.Interrupted),
                SummarizeRoundDurations(ended.Select(s => s.WorkMs)), SummarizeRoundDurations(ended.Select(s => s.RestMs)),
                activityCounts, asOf) { Reminder = snapshot.Reminders[day] };
        }).ToArray();
    }

    internal static ReminderDayState SummarizeReminder(IEnumerable<TimeSlice> slices, IEnumerable<SessionRecord> ended,
        ILookup<Guid, ActivityReport> activitiesBySession, ReminderRates rates, ReminderLevels history)
    {
        var events = new List<(DateTimeOffset Time, int Order, long WorkMs, string? Activity)>();
        bool hasRecords = false;
        foreach (var slice in slices)
        {
            if (slice.DurationMs <= 0) continue;
            hasRecords = true;
            if (slice.Kind == SliceKind.Work) events.Add((slice.EndedAtUtc, 0, slice.DurationMs, null));
        }
        foreach (var session in ended)
        {
            if (session.CheckInStatus != CheckInStatus.Reported || !session.EndedAtUtc.HasValue) continue;
            foreach (var activity in activitiesBySession[session.SessionId].Select(a => a.ActivityId).Distinct())
            {
                if (activity is not ("look_away" or "relax_shoulders_hands" or "walk")) continue;
                hasRecords = true;
                events.Add((session.EndedAtUtc.Value, 1, 0, activity));
            }
        }
        var levels = ReminderLevels.Full;
        foreach (var item in events.OrderBy(e => e.Time).ThenBy(e => e.Order))
        {
            if (item.Activity is not null) levels = levels.Restore(item.Activity);
            else levels = levels.Consume(item.WorkMs, rates, history);
        }
        return new(levels, hasRecords);
    }

    private static RoundDurationTotals SummarizeRoundDurations(IEnumerable<long> durations)
    {
        long totalMs = 0;
        int count = 0;
        foreach (var duration in durations)
        {
            if (duration <= 0) continue;
            totalMs += duration;
            count++;
        }
        return new(totalMs, count);
    }
}

public sealed class CsvHistoryExporter(SqliteWorkClockStore store) : IHistoryExporter
{
    public async Task<ExportReport> ExportAsync(ExportRequest request, CancellationToken cancellation)
    {
        string? temporary = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (request.Format != ExportFormat.CsvPair) throw new ArgumentException("Unsupported format.");
            var target = Path.GetFullPath(request.TargetDirectory);
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Export requires a new directory.");
            var parent = Path.GetDirectoryName(target) ?? throw new IOException("No parent directory.");
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException();
            var data = await store.ReadReportAsync(request.Range);
            cancellation.ThrowIfCancellationRequested();
            temporary = Path.Combine(parent, ".healthywork-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            using (var writer = new StreamWriter(Path.Combine(temporary, "sessions.csv"), false, new UTF8Encoding(true)))
            {
                await writer.WriteLineAsync("session_id,started_at_utc,ended_at_utc,start_time_zone_id,ended_local_date,planned_work_ms,planned_rest_ms,work_ms,rest_ms,requested_delay_ms,actual_delay_ms,outcome,end_reason,checkin_status,activity_ids" + (request.IncludeNotes ? ",resume_note" : ""));
                foreach (var s in data.Sessions)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var fields = new List<object?> { s.SessionId, s.StartedAtUtc, s.EndedAtUtc, s.StartTimeZoneId, s.EndedLocalDate, s.PlannedWorkMs, s.PlannedRestMs, s.WorkMs, s.RestMs, s.RequestedDelayMs, s.ActualDelayMs, s.Outcome, s.EndReason, s.CheckInStatus, string.Join(';', data.Activities.Where(a => a.SessionId == s.SessionId).Select(a => a.ActivityId)) };
                    if (request.IncludeNotes) fields.Add(s.ResumeNote);
                    await writer.WriteLineAsync(string.Join(',', fields.Select(Cell)));
                }
            }
            using (var writer = new StreamWriter(Path.Combine(temporary, "time-slices.csv"), false, new UTF8Encoding(true)))
            {
                await writer.WriteLineAsync("slice_id,session_id,sequence_no,kind,work_subtype,local_date,time_zone_id,started_at_utc,ended_at_utc,duration_ms");
                foreach (var s in data.Slices)
                {
                    cancellation.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(string.Join(',', new object?[] { s.SliceId, s.SessionId, s.SequenceNo, s.Kind, s.WorkSubtype, s.LocalDate, s.TimeZoneId, s.StartedAtUtc, s.EndedAtUtc, s.DurationMs }.Select(Cell)));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(temporary, target); temporary = null;
            return new ExportReport(ExportStatus.Succeeded, target, new[] { Path.Combine(target, "sessions.csv"), Path.Combine(target, "time-slices.csv") }, data.Sessions.Count, data.Slices.Count, null);
        }
        catch (OperationCanceledException) { return new ExportReport(ExportStatus.Cancelled, null, Array.Empty<string>(), 0, 0, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { return new ExportReport(ExportStatus.Failed, null, Array.Empty<string>(), 0, 0, ErrorCode.ExportFailed); }
        finally { if (temporary is not null && Directory.Exists(temporary)) { try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
    private static string Cell(object? value)
    {
        string text = value switch { null => "", DateTimeOffset d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        if (value is string && text.Length > 0 && "=+-@\t\r\n".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
