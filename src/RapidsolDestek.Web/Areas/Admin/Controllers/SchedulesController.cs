using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin schedules list + editor (mockups/admin/schedules.html + schedule-edit.html,
/// ROADMAP §6.3). List: the B1 engine over Schedule rows, dlg-more bulk enable/
/// disable/delete with an in-use guard (SLA plans, departments and the
/// system/default_schedule_id pointer block deletion). Editor: whole-page save of
/// name/kind/timezone/description plus the two B4 row builders — entry rows
/// (weekday checkboxes posted as a marker sequence, staff-edit precedent) and
/// holiday rows — reconciled by row id. Clone deep-copies the schedule with every
/// entry+holiday. The B10 diagnostic posts the CURRENT editor state (unsaved rows
/// included) and answers through <see cref="ScheduleEvaluator"/>.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SchedulesController(AppDbContext db, ISettingsService settings) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "created", "updated"];

    /// <summary>The mockup's timezone option list (IANA ids; labels verbatim —
    /// SettingsSystemController.Timezones twin). A schedule holding another valid
    /// id renders as an appended option, label = the id.</summary>
    private static readonly (string Value, string Label)[] TimezoneOptions =
    [
        ("Europe/Istanbul", "Europe/Istanbul (UTC+03)"),
        ("UTC", "UTC"),
        ("Europe/Berlin", "Europe/Berlin (UTC+01)"),
    ];

    // ---- schedules.html (B1 list) -------------------------------------------------------

    [HttpGet("/admin/schedules")]
    [NavKey("schedules")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = db.Schedules.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.Name, pattern));
        }

        var projected = query.Select(s => new
        {
            s.Id, s.Name, s.Kind, s.IsActive, Created = s.CreatedAt,
            Updated = s.UpdatedAt ?? s.CreatedAt,
        });

        var sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        var desc = dir == "asc" ? false : dir == "desc" || sortKey == "updated";
        projected = (sortKey, desc) switch
        {
            ("name", true) => projected.OrderByDescending(s => s.Name).ThenBy(s => s.Id),
            ("name", false) => projected.OrderBy(s => s.Name).ThenBy(s => s.Id),
            ("created", true) => projected.OrderByDescending(s => s.Created).ThenBy(s => s.Id),
            ("created", false) => projected.OrderBy(s => s.Created).ThenBy(s => s.Id),
            (_, false) => projected.OrderBy(s => s.Updated).ThenBy(s => s.Id),
            // Seed rows share one CreatedAt — the id tiebreak keeps the mockup's row
            // order Hafta içi → Cumartesi under the default updated-desc indicator.
            _ => projected.OrderByDescending(s => s.Updated).ThenBy(s => s.Id),
        };

        var total = await projected.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await projected.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(s => new ScheduleRowVm(s.Id, s.Name, s.Kind, s.IsActive, s.Created, s.Updated))
            .ToListAsync(ct);

        return View(new SchedulesIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>
    /// dlg-more bulk actions. Delete guard: schedules referenced by SLA plans,
    /// departments or the system default-schedule pointer are skipped and reported
    /// in the partial toast (slas precedent; help topics carry no schedule column).
    /// </summary>
    [HttpPost("/admin/schedules/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("sch.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var schedules = await db.Schedules.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - schedules.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var schedule in schedules)
            {
                switch (act)
                {
                    case "enable":
                        schedule.IsActive = true;
                        ok++;
                        break;
                    case "disable":
                        schedule.IsActive = false;
                        ok++;
                        break;
                    case "delete":
                        if (await IsScheduleInUseAsync(schedule.Id, ct))
                        {
                            skipped++;
                        }
                        else
                        {
                            db.Schedules.Remove(schedule); // entries cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["SchedulesToastOk"] = ok;
        TempData["SchedulesToastSkipped"] = skipped;
        TempData["SchedulesToast"] = skipped > 0 ? "sch.bulkPartial" : "sch.bulkDone";
        if (skipped > 0)
            TempData["SchedulesToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- schedule-edit.html ---------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Takvim" create form
    /// (the mockup's new-button links straight to schedule-edit.html).</summary>
    [HttpGet("/admin/schedule-edit")]
    [NavKey("schedules")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        Schedule? schedule = null;
        if (id is not null)
        {
            schedule = await db.Schedules
                .Include(s => s.Entries.OrderBy(e => e.Sort).ThenBy(e => e.Id))
                .SingleOrDefaultAsync(s => s.Id == id, ct);
            if (schedule is null)
                return NotFound();
        }

        return View(BuildEditVm(schedule));
    }

    /// <summary>
    /// Whole-page save: info fields + both B4 builders in one post. Entry weekday
    /// checkboxes arrive as the entryDayMarks marker sequence ("row" opens a row,
    /// day bits follow — unchecked checkboxes post nothing, staff-edit precedent);
    /// every other row control is an always-present input so the parallel arrays
    /// align by row order. Validation writes NOTHING on failure (B3).
    /// </summary>
    [HttpPost("/admin/schedule-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? name, string? kind, string? timezone, string? description,
        int[] entryIds, string[] entryNames, string[] entryStarts, string[] entryEnds,
        string[] entryRepeats, string[] entryDates, string[] entryDayMarks,
        int[] holIds, string[] holNames, string[] holDates, string[] holModes,
        string[] holStarts, string[] holEnds,
        CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return EditToastBack(id, "sche.errName");
        if (kind is not ("business" or "holiday"))
            kind = "business";
        timezone = NormalizeTimezone(timezone, out var tzValid);
        if (!tzValid)
            return EditToastBack(id, "sche.errTz");

        var (entries, entryError) = ParseEntryRows(
            entryIds, entryNames, entryStarts, entryEnds, entryRepeats, entryDates, entryDayMarks, lenient: false);
        if (entryError is not null)
            return EditToastBack(id, entryError);
        var (holidays, holidayError) = ParseHolidayRows(
            holIds, holNames, holDates, holModes, holStarts, holEnds, lenient: false);
        if (holidayError is not null)
            return EditToastBack(id, holidayError);

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            Schedule schedule;
            if (id is null)
            {
                schedule = new Schedule { Name = name };
                db.Schedules.Add(schedule);
            }
            else
            {
                var found = await db.Schedules.Include(s => s.Entries)
                    .SingleOrDefaultAsync(s => s.Id == id, ct);
                if (found is null)
                    return NotFound();
                schedule = found;
            }

            schedule.Name = name;
            schedule.Kind = kind == "holiday" ? ScheduleKind.Holidays : ScheduleKind.BusinessHours;
            schedule.Timezone = timezone;
            schedule.Description = (description ?? "").Trim();

            // ---- Row reconciliation (B4): update by id, remove missing, add new. ----
            var posted = entries.Concat(holidays).ToList();
            var keepIds = posted.Where(p => p.Id > 0).Select(p => p.Id).ToHashSet();
            schedule.Entries.RemoveAll(e => !keepIds.Contains(e.Id));
            foreach (var (rowId, proto) in posted)
            {
                var row = rowId > 0 ? schedule.Entries.FirstOrDefault(e => e.Id == rowId) : null;
                if (row is null)
                {
                    schedule.Entries.Add(proto);
                }
                else
                {
                    row.Name = proto.Name;
                    row.IsHoliday = proto.IsHoliday;
                    row.Repeats = proto.Repeats;
                    row.StartsOn = proto.StartsOn;
                    row.StartsAt = proto.StartsAt;
                    row.EndsOn = proto.EndsOn;
                    row.EndsAt = proto.EndsAt;
                    row.Day = proto.Day;
                    row.Sort = proto.Sort;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["SchedulesToast"] = id is null ? "sch.toastCreated" : "sch.toastSaved";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Header "Kopyala": deep-copies the schedule with every entry+holiday
    /// row and lands on the copy's editor. The localized default name arrives from
    /// the view (sche.cloneNameFmt) — a numeric suffix keeps it unique.</summary>
    [HttpPost("/admin/schedule-edit/clone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clone(int id, string? cloneName, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var source = await db.Schedules
            .Include(s => s.Entries.OrderBy(e => e.Sort).ThenBy(e => e.Id))
            .SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null)
            return NotFound();

        var baseName = (cloneName ?? "").Trim();
        if (baseName.Length is 0 or > 120)
            baseName = source.Name.Length > 110 ? source.Name[..110] + " 2" : source.Name + " 2";
        var candidate = baseName;
        for (var i = 2; await db.Schedules.AnyAsync(s => s.Name == candidate, ct); i++)
            candidate = $"{baseName} {i}";

        var copy = new Schedule
        {
            Name = candidate,
            Kind = source.Kind,
            Timezone = source.Timezone,
            Description = source.Description,
            IsActive = source.IsActive,
            Entries = [.. source.Entries.Select(e => new ScheduleEntry
            {
                Name = e.Name,
                IsHoliday = e.IsHoliday,
                Repeats = e.Repeats,
                StartsOn = e.StartsOn,
                StartsAt = e.StartsAt,
                EndsOn = e.EndsOn,
                EndsAt = e.EndsAt,
                StopsOn = e.StopsOn,
                Day = e.Day,
                Week = e.Week,
                Month = e.Month,
                Sort = e.Sort,
            })],
        };

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.Schedules.Add(copy);
            await db.SaveChangesAsync(ct);
        }

        TempData["ScheduleEditToast"] = "sche.toastCloned";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    /// <summary>Editor delete (dlg-delete confirm): a schedule referenced by SLA
    /// plans, departments or the default-schedule setting is refused.</summary>
    [HttpPost("/admin/schedule-edit/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
            return NotFound();
        if (await IsScheduleInUseAsync(id, ct))
            return EditToastBack(id, "sche.errInUse");

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.Schedules.Remove(schedule); // entries cascade
            await db.SaveChangesAsync(ct);
        }

        TempData["SchedulesToast"] = "sch.toastDeleted";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// B10 diagnostic: evaluates the POSTED editor state (unsaved edits included —
    /// works on the create form too) for the chosen date and returns the pieces the
    /// dialog composes into its localized banner. Lenient parse: rows that would be
    /// refused by Save are skipped instead of failing the question.
    /// </summary>
    [HttpPost("/admin/schedule-edit/diagnose")]
    [ValidateAntiForgeryToken]
    public IActionResult Diagnose(
        string? kind, string? diagDate,
        int[] entryIds, string[] entryNames, string[] entryStarts, string[] entryEnds,
        string[] entryRepeats, string[] entryDates, string[] entryDayMarks,
        int[] holIds, string[] holNames, string[] holDates, string[] holModes,
        string[] holStarts, string[] holEnds)
    {
        if (!DateOnly.TryParse(diagDate, CultureInfo.InvariantCulture, out var date))
            return BadRequest();

        var (entries, _) = ParseEntryRows(
            entryIds, entryNames, entryStarts, entryEnds, entryRepeats, entryDates, entryDayMarks, lenient: true);
        var (holidays, _) = ParseHolidayRows(
            holIds, holNames, holDates, holModes, holStarts, holEnds, lenient: true);

        var probe = new Schedule
        {
            Name = "diagnostic",
            Kind = kind == "holiday" ? ScheduleKind.Holidays : ScheduleKind.BusinessHours,
            Entries = [.. entries.Select(e => e.Row), .. holidays.Select(h => h.Row)],
        };

        var holiday = ScheduleEvaluator.HolidayOn(probe, date);
        if (probe.Kind == ScheduleKind.Holidays)
        {
            return Json(holiday is null
                ? new DiagnoseResultVm("noholiday", null, null)
                : new DiagnoseResultVm("holiday", null, holiday.Name));
        }

        var ranges = ScheduleEvaluator.OpenRangesOn(probe, date);
        if (ranges.Count > 0)
        {
            return Json(new DiagnoseResultVm("open",
                string.Join(", ", ranges.Select(r =>
                    r.Start.ToString("HH:mm", CultureInfo.InvariantCulture) + "–"
                    + r.End.ToString("HH:mm", CultureInfo.InvariantCulture))), null));
        }
        return Json(holiday is null
            ? new DiagnoseResultVm("closed", null, null)
            : new DiagnoseResultVm("holiday", null, holiday.Name));
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Referenced schedules are undeletable: SLA plans, departments and the
    /// system default-schedule pointer resolve against them.</summary>
    private async Task<bool> IsScheduleInUseAsync(int scheduleId, CancellationToken ct) =>
        await db.SlaPlans.AnyAsync(s => s.ScheduleId == scheduleId, ct)
        || await db.Departments.AnyAsync(d => d.ScheduleId == scheduleId, ct)
        || (int.TryParse(await settings.GetAsync("system", "default_schedule_id", ct), out var def)
            && def == scheduleId);

    /// <summary>"" = system timezone (null); anything else must be a resolvable id.</summary>
    private static string? NormalizeTimezone(string? timezone, out bool valid)
    {
        valid = true;
        timezone = (timezone ?? "").Trim();
        if (timezone.Length == 0)
            return null;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return timezone;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            valid = false;
            return null;
        }
    }

    /// <summary>Entry rows ("Girdiler" tab). Weekly needs at least one checked day;
    /// monthly/one-time need their anchor date (the editor's invented date input).</summary>
    private static (List<(int Id, ScheduleEntry Row)> Rows, string? Error) ParseEntryRows(
        int[] ids, string[] names, string[] starts, string[] ends,
        string[] repeats, string[] dates, string[] dayMarks, bool lenient)
    {
        var rows = new List<(int, ScheduleEntry)>();
        var count = names.Length;
        if (ids.Length != count || starts.Length != count || ends.Length != count
            || repeats.Length != count || dates.Length != count)
            return (rows, lenient ? null : "sche.errEntry");

        // Weekday masks: "row" opens the next row's mask, day bits follow.
        var masks = new int[count];
        var rowIndex = -1;
        foreach (var mark in dayMarks)
        {
            if (mark == "row")
                rowIndex++;
            else if (rowIndex >= 0 && rowIndex < count
                && int.TryParse(mark, out var bit) && bit is >= 1 and <= 64)
                masks[rowIndex] |= bit;
        }

        for (var i = 0; i < count; i++)
        {
            var name = (names[i] ?? "").Trim();
            var repeat = repeats[i];
            var hasStart = TimeOnly.TryParse(starts[i], CultureInfo.InvariantCulture, out var startsAt);
            var hasEnd = TimeOnly.TryParse(ends[i], CultureInfo.InvariantCulture, out var endsAt);
            var hasDate = DateOnly.TryParse(dates[i], CultureInfo.InvariantCulture, out var anchor);

            string? error =
                name.Length is 0 or > 120 ? "sche.errEntryName"
                : !hasStart || !hasEnd || endsAt <= startsAt ? "sche.errEntryTime"
                : repeat == "weekly" && masks[i] == 0 ? "sche.errEntryDays"
                : repeat is "monthly" or "once" && !hasDate ? "sche.errEntryDate"
                : null;
            if (error is not null)
            {
                if (lenient)
                    continue;
                return (rows, error);
            }

            rows.Add((ids[i], new ScheduleEntry
            {
                Name = name,
                IsHoliday = false,
                Repeats = repeat switch
                {
                    "daily" => ScheduleRepeat.Daily,
                    "monthly" => ScheduleRepeat.Monthly,
                    "once" => ScheduleRepeat.Never,
                    _ => ScheduleRepeat.Weekly,
                },
                StartsAt = startsAt,
                EndsAt = endsAt,
                StartsOn = repeat is "monthly" or "once" && hasDate ? anchor : null,
                // The mask persists for every repeat so checkbox state round-trips.
                Day = masks[i] == 0 ? null : masks[i],
                Sort = rows.Count + 1,
            }));
        }
        return (rows, null);
    }

    /// <summary>Holiday rows ("Tatiller" tab): date + name + all-day or a time range
    /// (the range's time inputs are the editor's invented gated pair).</summary>
    private static (List<(int Id, ScheduleEntry Row)> Rows, string? Error) ParseHolidayRows(
        int[] ids, string[] names, string[] dates, string[] modes,
        string[] starts, string[] ends, bool lenient)
    {
        var rows = new List<(int, ScheduleEntry)>();
        var count = names.Length;
        if (ids.Length != count || dates.Length != count || modes.Length != count
            || starts.Length != count || ends.Length != count)
            return (rows, lenient ? null : "sche.errHoliday");

        for (var i = 0; i < count; i++)
        {
            var name = (names[i] ?? "").Trim();
            var range = modes[i] == "range";
            var hasDate = DateOnly.TryParse(dates[i], CultureInfo.InvariantCulture, out var date);
            var hasStart = TimeOnly.TryParse(starts[i], CultureInfo.InvariantCulture, out var startsAt);
            var hasEnd = TimeOnly.TryParse(ends[i], CultureInfo.InvariantCulture, out var endsAt);

            string? error =
                name.Length is 0 or > 120 ? "sche.errHolName"
                : !hasDate ? "sche.errHolDate"
                : range && (!hasStart || !hasEnd || endsAt <= startsAt) ? "sche.errHolTime"
                : null;
            if (error is not null)
            {
                if (lenient)
                    continue;
                return (rows, error);
            }

            rows.Add((ids[i], new ScheduleEntry
            {
                Name = name,
                IsHoliday = true,
                Repeats = ScheduleRepeat.Never,
                StartsOn = date,
                StartsAt = range ? startsAt : null,
                EndsAt = range ? endsAt : null,
                Sort = rows.Count + 1,
            }));
        }
        return (rows, null);
    }

    private ScheduleEditVm BuildEditVm(Schedule? schedule)
    {
        var entries = new List<ScheduleEntryRowVm>();
        var holidays = new List<ScheduleHolidayRowVm>();
        foreach (var e in (schedule?.Entries ?? []).OrderBy(e => e.Sort).ThenBy(e => e.Id))
        {
            if (ScheduleEvaluator.IsHolidayRow(e))
            {
                holidays.Add(new ScheduleHolidayRowVm(
                    e.Id, e.Name,
                    e.StartsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    AllDay: e.StartsAt is null || e.EndsAt is null,
                    e.StartsAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                    e.EndsAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? ""));
            }
            else
            {
                entries.Add(new ScheduleEntryRowVm(
                    e.Id, e.Name,
                    e.StartsAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                    e.EndsAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                    // Yearly is osTicket surface the select cannot express — renders
                    // as the nearest editable rule (once, flagged).
                    e.Repeats switch
                    {
                        ScheduleRepeat.Daily => "daily",
                        ScheduleRepeat.Monthly => "monthly",
                        ScheduleRepeat.Weekly => "weekly",
                        _ => "once",
                    },
                    e.StartsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    e.Day ?? 0));
            }
        }

        // The mockup's fixed option list + the schedule's own id when it is neither
        // empty nor listed (kept honest instead of silently remapping).
        var tzOptions = TimezoneOptions
            .Select(t => new TzOptionVm(t.Value, t.Label))
            .ToList();
        if (schedule?.Timezone is { Length: > 0 } tz && tzOptions.All(t => t.Value != tz))
            tzOptions.Add(new TzOptionVm(tz, tz));

        return new ScheduleEditVm(schedule, entries, holidays, tzOptions);
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["SchedulesToast"] = key;
        if (error)
            TempData["SchedulesToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation failure PRG back to the editor (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["ScheduleEditToast"] = key;
        TempData["ScheduleEditToastError"] = true;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record SchedulesIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<ScheduleRowVm> Rows);

public sealed record ScheduleRowVm(
    int Id,
    string Name,
    ScheduleKind Kind,
    bool IsActive,
    DateTimeOffset Created,
    DateTimeOffset Updated);

public sealed record ScheduleEditVm(
    Schedule? Schedule,
    IReadOnlyList<ScheduleEntryRowVm> Entries,
    IReadOnlyList<ScheduleHolidayRowVm> Holidays,
    IReadOnlyList<TzOptionVm> Timezones);

public sealed record ScheduleEntryRowVm(
    int Id, string Name, string Starts, string Ends, string Repeat, string Date, int DayMask);

public sealed record ScheduleHolidayRowVm(
    int Id, string Name, string Date, bool AllDay, string Starts, string Ends);

public sealed record TzOptionVm(string Value, string Label);

/// <summary>Diagnose JSON payload; the dialog composes the localized banner from it.</summary>
public sealed record DiagnoseResultVm(string Status, string? Ranges, string? Holiday);

/// <summary>Schedule select option carrying activity so pickers can hide inactive
/// schedules while keeping a row's current (inactive) selection visible.</summary>
public sealed record ScheduleOptionVm(int Id, string Label, bool IsActive);
