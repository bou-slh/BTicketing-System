using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Domain.Services;

/// <summary>
/// Evaluates a <see cref="Schedule"/>'s entries + holidays for a calendar date or an
/// absolute instant (admin schedule-edit diagnostic, B10; the S8 SLA sweep's clock).
/// Entry rows OPEN the schedule on the dates their repeat rule hits; holiday rows
/// CLOSE it again (all-day rows close the whole date, timed rows subtract their
/// range). Wall-clock semantics: a date question needs no timezone at all — the
/// timezone only matters when an absolute instant is converted to the schedule's
/// local wall clock (<see cref="IsOpenAt"/>).
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>Merged open ranges of <paramref name="schedule"/> on a local calendar
    /// date, holidays already subtracted. Empty = closed.</summary>
    public static IReadOnlyList<(TimeOnly Start, TimeOnly End)> OpenRangesOn(Schedule schedule, DateOnly date)
    {
        var ranges = new List<(TimeOnly Start, TimeOnly End)>();
        foreach (var entry in schedule.Entries.Where(e => !IsHolidayRow(e) && AppliesOn(e, date)))
        {
            // Entry rows always carry times in the editor; a time-less entry row
            // (never creatable from the UI) counts as the whole day.
            var start = entry.StartsAt ?? new TimeOnly(0, 0);
            var end = entry.EndsAt ?? TimeOnly.MaxValue;
            if (end > start)
                ranges.Add((start, end));
        }

        if (ranges.Count == 0)
            return ranges;

        // Merge overlapping / touching ranges.
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(TimeOnly Start, TimeOnly End)> { ranges[0] };
        foreach (var r in ranges.Skip(1))
        {
            var last = merged[^1];
            if (r.Start <= last.End)
                merged[^1] = (last.Start, r.End > last.End ? r.End : last.End);
            else
                merged.Add(r);
        }

        // Subtract holidays: all-day rows clear the date, timed rows cut their range.
        foreach (var holiday in schedule.Entries.Where(e => IsHolidayRow(e) && AppliesOn(e, date)))
        {
            if (holiday.StartsAt is null || holiday.EndsAt is null)
                return [];
            var (hs, he) = (holiday.StartsAt.Value, holiday.EndsAt.Value);
            var cut = new List<(TimeOnly Start, TimeOnly End)>();
            foreach (var r in merged)
            {
                if (he <= r.Start || hs >= r.End)
                {
                    cut.Add(r);
                    continue;
                }
                if (hs > r.Start)
                    cut.Add((r.Start, hs));
                if (he < r.End)
                    cut.Add((he, r.End));
            }
            merged = cut;
        }

        return merged;
    }

    /// <summary>First holiday row matching the date (its name answers "which holiday?").</summary>
    public static ScheduleEntry? HolidayOn(Schedule schedule, DateOnly date) =>
        schedule.Entries.Where(e => IsHolidayRow(e) && AppliesOn(e, date))
            .OrderBy(e => e.Sort).ThenBy(e => e.Id)
            .FirstOrDefault();

    /// <summary>Whether the schedule is open at an absolute instant: the instant is
    /// converted to the schedule's wall clock first, so the same entries answer
    /// differently under different timezones.</summary>
    public static bool IsOpenAt(Schedule schedule, DateTimeOffset instant, TimeZoneInfo timezone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, timezone);
        var date = DateOnly.FromDateTime(local.Date);
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return OpenRangesOn(schedule, date).Any(r => time >= r.Start && time < r.End);
    }

    /// <summary>Safety horizon for <see cref="AddWorkingHours"/>: a schedule that
    /// stays closed for a whole year cannot consume the grace period — the caller
    /// falls back to wall-clock (osTicket addGracePeriod does the same when
    /// addWorkingHours fails).</summary>
    private const int WorkingHoursHorizonDays = 366;

    /// <summary>
    /// Adds <paramref name="hours"/> working hours to <paramref name="start"/>,
    /// counting only instants where the schedule is open (the S8 SLA sweep's clock —
    /// osTicket BusinessHours::addWorkingHours semantics, modeled not copied): the
    /// instant is converted to the schedule's wall clock, then open ranges (entries
    /// minus holidays) are consumed day by day until the hours are spent. Returns
    /// null when the schedule never opens long enough within a one-year horizon —
    /// callers fall back to wall-clock addition.
    /// </summary>
    public static DateTimeOffset? AddWorkingHours(
        Schedule schedule, DateTimeOffset start, decimal hours, TimeZoneInfo timezone)
    {
        if (hours <= 0)
            return start;

        var local = TimeZoneInfo.ConvertTime(start, timezone);
        var firstDate = DateOnly.FromDateTime(local.Date);
        var startTime = TimeOnly.FromTimeSpan(local.TimeOfDay);
        var remaining = TimeSpan.FromHours((double)hours);

        for (var offset = 0; offset <= WorkingHoursHorizonDays; offset++)
        {
            var date = firstDate.AddDays(offset);
            foreach (var (open, close) in OpenRangesOn(schedule, date))
            {
                var from = open;
                if (offset == 0)
                {
                    // First day: the clock starts at `start`, not at the range opening.
                    if (startTime >= close)
                        continue;
                    if (startTime > open)
                        from = startTime;
                }

                var available = close - from;
                if (available >= remaining)
                {
                    var dt = date.ToDateTime(from).Add(remaining);
                    return new DateTimeOffset(dt, timezone.GetUtcOffset(dt));
                }
                remaining -= available;
            }
        }

        return null; // never opened long enough — wall-clock fallback is the caller's
    }

    /// <summary>Resolves an IANA id to a <see cref="TimeZoneInfo"/>; null/unknown ids
    /// fall back to <paramref name="fallbackId"/>, then to the machine's zone.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id, string? fallbackId = null)
    {
        foreach (var candidate in new[] { id, fallbackId })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>Day-of-week bit (Sun=1, Mon=2 … Sat=64 — the seeded osTicket-style mask).</summary>
    public static int DayBit(DayOfWeek day) => 1 << (int)day;

    /// <summary>Holiday classification with a legacy fallback: rows written before the
    /// IsHoliday column (pre-S7 live data) are date-only one-time rows without times.</summary>
    public static bool IsHolidayRow(ScheduleEntry entry) =>
        entry.IsHoliday
        || (entry.Repeats == ScheduleRepeat.Never && entry.StartsOn is not null && entry.StartsAt is null);

    /// <summary>Whether an entry's repeat rule hits the given local date.</summary>
    public static bool AppliesOn(ScheduleEntry entry, DateOnly date)
    {
        if (entry.StartsOn is { } startsOn && date < startsOn)
            return false;
        if (entry.StopsOn is { } stopsOn && date.ToDateTime(TimeOnly.MinValue) > stopsOn.Date)
            return false;

        return entry.Repeats switch
        {
            ScheduleRepeat.Daily => true,
            ScheduleRepeat.Weekly => entry.Day is { } mask && (mask & DayBit(date.DayOfWeek)) != 0,
            // Monthly: same day-of-month as the anchor date (the editor's invented
            // date input); a legacy Day 1–31 without an anchor is honored too.
            ScheduleRepeat.Monthly when entry.StartsOn is { } anchor => date.Day == anchor.Day,
            ScheduleRepeat.Monthly => entry.Day is >= 1 and <= 31 && date.Day == entry.Day,
            // Yearly (osTicket parity; not creatable from the editor): anchor's month+day.
            ScheduleRepeat.Yearly when entry.StartsOn is { } anchor =>
                date.Month == anchor.Month && date.Day == anchor.Day,
            ScheduleRepeat.Yearly => entry.Month == date.Month
                && entry.Day is >= 1 and <= 31 && date.Day == entry.Day,
            // One-time ("Tek seferlik" / holiday rows): the anchor date, or the
            // StartsOn..EndsOn span when an end date exists.
            _ => entry.StartsOn is { } once
                && (entry.EndsOn is { } endsOn ? date <= endsOn && date >= once : date == once),
        };
    }
}
