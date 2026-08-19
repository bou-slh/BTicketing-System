using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/schedules.html + schedule-edit.html: the B1 list over Schedule rows with
/// the bulk in-use delete guard, the editor's B4 entry/holiday row builders
/// round-tripping through the whole-page save, Clone's deep copy, and the B10
/// diagnostic — the HTTP answer over the posted editor state plus the
/// ScheduleEvaluator timezone/holiday semantics the engine reads.
/// </summary>
[Collection("Postgres")]
public class SchedulesAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // 2026-08-12 = Wednesday, 2026-08-16 = Sunday.
    private const string Wednesday = "2026-08-12";
    private const string Sunday = "2026-08-16";

    // ---- schedules.html (B1) ------------------------------------------------------------

    [Fact]
    public async Task SchedulesList_RendersSeedCanon_AndSearchFilters()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/schedules"));

        Assert.Contains("Hafta içi 09:00–18:00", html);
        Assert.Contains("7/24", html);
        Assert.Contains("Resmi Tatiller 2026", html);
        Assert.Contains("Cumartesi Yarım Gün", html);
        Assert.Contains("/admin/schedule-edit?id=", html);
        // Type cells: TR default culture (sch.typeBusiness / sch.typeHoliday).
        Assert.Contains("Çalışma Saatleri", html);
        Assert.Contains(">Tatil<", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/schedules?q=" + Uri.EscapeDataString("Resmi")));
        Assert.Contains("Resmi Tatiller 2026", filtered);
        Assert.DoesNotContain("Cumartesi Yarım Gün", filtered);
    }

    [Fact]
    public async Task ScheduleEdit_PrefillsEntriesAndHolidays_FromSeedCanon()
    {
        int weekId, holidaysId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            weekId = await s.Db.Schedules.Where(x => x.Name == "Hafta içi 09:00–18:00")
                .Select(x => x.Id).SingleAsync();
            holidaysId = await s.Db.Schedules.Where(x => x.Name == "Resmi Tatiller 2026")
                .Select(x => x.Id).SingleAsync();
        }

        var client = await AdminClientAsync();
        var week = WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/schedule-edit?id={weekId}"));
        // Entry row: 09:00–18:00 weekly, Mon..Fri checked (Day=62), Sat/Sun not.
        Assert.Contains("value=\"09:00\"", week);
        Assert.Contains("value=\"18:00\"", week);
        Assert.Contains("value=\"weekly\" selected", week);
        foreach (var bit in new[] { 2, 4, 8, 16, 32 })
            Assert.Contains($"name=\"entryDayMarks\" value=\"{bit}\" checked", week);
        Assert.DoesNotContain("name=\"entryDayMarks\" value=\"64\" checked", week);
        Assert.DoesNotContain("name=\"entryDayMarks\" value=\"1\" checked", week);
        // Timezone: the schedule's Europe/Istanbul option is selected.
        Assert.Contains("value=\"Europe/Istanbul\" selected", week);

        var holidays = WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/schedule-edit?id={holidaysId}"));
        // Holiday rows land in the Tatiller tab builder (IsHoliday split).
        Assert.Contains("name=\"holDates\" value=\"2026-10-29\"", holidays);
        Assert.Contains("29 Ekim Cumhuriyet Bayramı", holidays);
        Assert.Contains("1 Ocak Yılbaşı", holidays);
        Assert.DoesNotContain("name=\"entryNames\" value=\"29 Ekim", holidays);
    }

    // ---- schedule-edit.html save (B4 round-trip) ------------------------------------------

    [Fact]
    public async Task ScheduleSave_RoundTripsEntryAndHolidayRows_AndReconciles()
    {
        var name = $"S7 Takvim {Guid.NewGuid():N}"[..24];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/schedule-edit");

        var landed = await PostFormAsync(client, "/admin/schedule-edit", token,
            ("name", name), ("kind", "business"), ("timezone", "Europe/Istanbul"), ("description", "S7 test"),
            ("entryIds", "0"), ("entryNames", "Mesai"), ("entryStarts", "09:00"), ("entryEnds", "17:30"),
            ("entryRepeats", "weekly"), ("entryDates", ""),
            ("entryDayMarks", "row"), ("entryDayMarks", "2"), ("entryDayMarks", "64"),
            ("holIds", "0"), ("holNames", "Test Bayram"), ("holDates", "2026-09-01"),
            ("holModes", "allday"), ("holStarts", ""), ("holEnds", ""),
            ("holIds", "0"), ("holNames", "Yarım Gün"), ("holDates", "2026-09-02"),
            ("holModes", "range"), ("holStarts", "12:00"), ("holEnds", "18:00"));
        Assert.Equal("/admin/schedules", PathOf(landed));

        int scheduleId, entryId, holidayId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var schedule = await s.Db.Schedules.Include(x => x.Entries)
                .SingleAsync(x => x.Name == name);
            scheduleId = schedule.Id;
            Assert.Equal(ScheduleKind.BusinessHours, schedule.Kind);
            Assert.Equal("Europe/Istanbul", schedule.Timezone);
            Assert.Equal(3, schedule.Entries.Count);

            var entry = schedule.Entries.Single(e => !e.IsHoliday);
            entryId = entry.Id;
            Assert.Equal("Mesai", entry.Name);
            Assert.Equal(ScheduleRepeat.Weekly, entry.Repeats);
            Assert.Equal(new TimeOnly(9, 0), entry.StartsAt);
            Assert.Equal(new TimeOnly(17, 30), entry.EndsAt);
            Assert.Equal(66, entry.Day); // Mon + Sat

            var allDay = schedule.Entries.Single(e => e.IsHoliday && e.Name == "Test Bayram");
            holidayId = allDay.Id;
            Assert.Equal(new DateOnly(2026, 9, 1), allDay.StartsOn);
            Assert.Null(allDay.StartsAt);

            var range = schedule.Entries.Single(e => e.IsHoliday && e.Name == "Yarım Gün");
            Assert.Equal(new TimeOnly(12, 0), range.StartsAt);
            Assert.Equal(new TimeOnly(18, 0), range.EndsAt);
        }

        // Update: edit the kept entry, keep one holiday, drop the other (B4 reconcile).
        var (token2, _) = await GetWithTokenAsync(client, $"/admin/schedule-edit?id={scheduleId}");
        var saved = await PostFormAsync(client, "/admin/schedule-edit", token2,
            ("id", scheduleId.ToString()),
            ("name", name), ("kind", "business"), ("timezone", ""), ("description", ""),
            ("entryIds", entryId.ToString()), ("entryNames", "Mesai"), ("entryStarts", "08:00"),
            ("entryEnds", "18:00"), ("entryRepeats", "daily"), ("entryDates", ""),
            ("entryDayMarks", "row"),
            ("holIds", holidayId.ToString()), ("holNames", "Test Bayram"), ("holDates", "2026-09-01"),
            ("holModes", "allday"), ("holStarts", ""), ("holEnds", ""));
        Assert.Equal("/admin/schedules", PathOf(saved));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var schedule = await s.Db.Schedules.Include(x => x.Entries)
                .SingleAsync(x => x.Id == scheduleId);
            Assert.Null(schedule.Timezone);
            Assert.Equal(2, schedule.Entries.Count);
            var entry = schedule.Entries.Single(e => !e.IsHoliday);
            Assert.Equal(entryId, entry.Id); // updated in place, not recreated
            Assert.Equal(ScheduleRepeat.Daily, entry.Repeats);
            Assert.Equal(new TimeOnly(8, 0), entry.StartsAt);
            Assert.DoesNotContain(schedule.Entries, e => e.Name == "Yarım Gün");
        }

        // B3: an invalid weekly row (no day checked) is refused and writes nothing.
        var (token3, _) = await GetWithTokenAsync(client, $"/admin/schedule-edit?id={scheduleId}");
        var refused = await PostFormAsync(client, "/admin/schedule-edit", token3,
            ("id", scheduleId.ToString()),
            ("name", name), ("kind", "business"), ("timezone", ""), ("description", ""),
            ("entryIds", entryId.ToString()), ("entryNames", "Mesai"), ("entryStarts", "08:00"),
            ("entryEnds", "18:00"), ("entryRepeats", "weekly"), ("entryDates", ""),
            ("entryDayMarks", "row"));
        Assert.Equal("/admin/schedule-edit", PathOf(refused));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var entry = await s.Db.Set<ScheduleEntry>().SingleAsync(e => e.Id == entryId);
            Assert.Equal(ScheduleRepeat.Daily, entry.Repeats); // untouched
        }
    }

    // ---- Clone ------------------------------------------------------------------------------

    [Fact]
    public async Task ScheduleClone_DeepCopiesEntriesAndHolidays()
    {
        int sourceId, sourceEntryCount;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var source = await s.Db.Schedules.Include(x => x.Entries)
                .SingleAsync(x => x.Name == "Resmi Tatiller 2026");
            sourceId = source.Id;
            sourceEntryCount = source.Entries.Count;
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, $"/admin/schedule-edit?id={sourceId}");
        var landed = await PostFormAsync(client, "/admin/schedule-edit/clone", token,
            ("id", sourceId.ToString()), ("cloneName", "Resmi Tatiller 2026 (Kopya)"));

        Assert.Equal("/admin/schedule-edit", PathOf(landed));
        var copyId = int.Parse(Regex.Match(
            landed.RequestMessage!.RequestUri!.Query, @"id=(\d+)").Groups[1].Value);
        Assert.NotEqual(sourceId, copyId);

        using var scope = new ServiceScopeBundle(fixture);
        var copy = await scope.Db.Schedules.Include(x => x.Entries).SingleAsync(x => x.Id == copyId);
        var original = await scope.Db.Schedules.Include(x => x.Entries).SingleAsync(x => x.Id == sourceId);
        Assert.StartsWith("Resmi Tatiller 2026 (Kopya)", copy.Name);
        Assert.Equal(original.Kind, copy.Kind);
        Assert.Equal(original.Timezone, copy.Timezone);
        Assert.Equal(sourceEntryCount, copy.Entries.Count);
        // Deep copy: same values, different rows.
        foreach (var entry in copy.Entries)
        {
            Assert.DoesNotContain(original.Entries, e => e.Id == entry.Id);
            Assert.Contains(original.Entries, e =>
                e.Name == entry.Name && e.StartsOn == entry.StartsOn && e.IsHoliday == entry.IsHoliday);
        }
    }

    // ---- Diagnostic (B10) ---------------------------------------------------------------

    [Fact]
    public async Task Diagnose_AnswersOverThePostedEditorState()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/schedule-edit");

        (string Key, string Value)[] BusinessRows(string date) =>
        [
            ("kind", "business"), ("diagDate", date),
            ("entryIds", "0"), ("entryNames", "Mesai"), ("entryStarts", "09:00"), ("entryEnds", "18:00"),
            ("entryRepeats", "weekly"), ("entryDates", ""),
            ("entryDayMarks", "row"), ("entryDayMarks", "2"), ("entryDayMarks", "4"),
            ("entryDayMarks", "8"), ("entryDayMarks", "16"), ("entryDayMarks", "32"),
            ("holIds", "0"), ("holNames", "Ara Tatil"), ("holDates", "2026-08-13"),
            ("holModes", "allday"), ("holStarts", ""), ("holEnds", ""),
        ];

        // Open weekday hit.
        var open = await DiagnoseAsync(client, token, BusinessRows(Wednesday));
        Assert.Equal("open", open.GetProperty("status").GetString());
        Assert.Equal("09:00–18:00", open.GetProperty("ranges").GetString());

        // Closed weekend.
        var closed = await DiagnoseAsync(client, token, BusinessRows(Sunday));
        Assert.Equal("closed", closed.GetProperty("status").GetString());

        // Holiday override: Thursday 2026-08-13 would be open but the row closes it.
        var holiday = await DiagnoseAsync(client, token, BusinessRows("2026-08-13"));
        Assert.Equal("holiday", holiday.GetProperty("status").GetString());
        Assert.Equal("Ara Tatil", holiday.GetProperty("holiday").GetString());

        // Holiday calendars answer the holiday question instead of working hours.
        var holidayKind = await DiagnoseAsync(client, token,
        [
            ("kind", "holiday"), ("diagDate", "2026-10-29"),
            ("holIds", "0"), ("holNames", "Cumhuriyet Bayramı"), ("holDates", "2026-10-29"),
            ("holModes", "allday"), ("holStarts", ""), ("holEnds", ""),
        ]);
        Assert.Equal("holiday", holidayKind.GetProperty("status").GetString());
        var noHoliday = await DiagnoseAsync(client, token,
        [
            ("kind", "holiday"), ("diagDate", Wednesday),
            ("holIds", "0"), ("holNames", "Cumhuriyet Bayramı"), ("holDates", "2026-10-29"),
            ("holModes", "allday"), ("holStarts", ""), ("holEnds", ""),
        ]);
        Assert.Equal("noholiday", noHoliday.GetProperty("status").GetString());
    }

    [Fact]
    public void Evaluator_TimezoneEdge_OpenInIstanbulClosedInUtc()
    {
        Schedule Build(string tz) => new()
        {
            Name = "tz probe",
            Timezone = tz,
            Entries =
            [
                new ScheduleEntry
                {
                    Name = "Hafta içi", Repeats = ScheduleRepeat.Weekly,
                    StartsAt = new TimeOnly(9, 0), EndsAt = new TimeOnly(18, 0), Day = 62,
                },
            ],
        };

        // Wednesday 07:00 UTC = 10:00 in Istanbul: open there, closed under UTC.
        var instant = new DateTimeOffset(2026, 8, 12, 7, 0, 0, TimeSpan.Zero);
        var istanbul = Build("Europe/Istanbul");
        var utc = Build("UTC");
        Assert.True(ScheduleEvaluator.IsOpenAt(
            istanbul, instant, ScheduleEvaluator.ResolveTimeZone(istanbul.Timezone)));
        Assert.False(ScheduleEvaluator.IsOpenAt(
            utc, instant, ScheduleEvaluator.ResolveTimeZone(utc.Timezone)));

        // A timed holiday subtracts its range instead of closing the whole day.
        istanbul.Entries.Add(new ScheduleEntry
        {
            Name = "Yarım tatil", IsHoliday = true, StartsOn = new DateOnly(2026, 8, 12),
            StartsAt = new TimeOnly(12, 0), EndsAt = new TimeOnly(18, 0),
        });
        var ranges = ScheduleEvaluator.OpenRangesOn(istanbul, new DateOnly(2026, 8, 12));
        var range = Assert.Single(ranges);
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(12, 0)), range);

        // Monthly + one-time anchors (the editor's invented date input).
        var monthly = new Schedule
        {
            Name = "monthly probe",
            Entries =
            [
                new ScheduleEntry
                {
                    Name = "Ayın 15'i", Repeats = ScheduleRepeat.Monthly,
                    StartsOn = new DateOnly(2026, 1, 15),
                    StartsAt = new TimeOnly(9, 0), EndsAt = new TimeOnly(12, 0),
                },
                new ScheduleEntry
                {
                    Name = "Envanter", Repeats = ScheduleRepeat.Never,
                    StartsOn = new DateOnly(2026, 8, 20),
                    StartsAt = new TimeOnly(13, 0), EndsAt = new TimeOnly(15, 0),
                },
            ],
        };
        Assert.NotEmpty(ScheduleEvaluator.OpenRangesOn(monthly, new DateOnly(2026, 9, 15)));
        Assert.Empty(ScheduleEvaluator.OpenRangesOn(monthly, new DateOnly(2026, 9, 16)));
        Assert.NotEmpty(ScheduleEvaluator.OpenRangesOn(monthly, new DateOnly(2026, 8, 20)));
        Assert.Empty(ScheduleEvaluator.OpenRangesOn(monthly, new DateOnly(2026, 8, 21)));
        // Anchored rules do not fire before their anchor.
        Assert.Empty(ScheduleEvaluator.OpenRangesOn(monthly, new DateOnly(2025, 12, 15)));
    }

    // ---- Delete guard + bulk --------------------------------------------------------------

    [Fact]
    public async Task ScheduleDelete_RefusesInUse_DeletesUnreferenced()
    {
        int weekId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            weekId = await s.Db.Schedules.Where(x => x.Name == "Hafta içi 09:00–18:00")
                .Select(x => x.Id).SingleAsync();
        }

        var client = await AdminClientAsync();

        // Referenced by SLA plans + the Bordro department: editor delete refuses.
        var (token, _) = await GetWithTokenAsync(client, $"/admin/schedule-edit?id={weekId}");
        var refused = await PostFormAsync(client, "/admin/schedule-edit/delete", token,
            ("id", weekId.ToString()));
        Assert.Equal("/admin/schedule-edit", PathOf(refused));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.True(await s.Db.Schedules.AnyAsync(x => x.Id == weekId));

        // Bulk delete skips it too (partial toast path).
        var (listToken, _) = await GetWithTokenAsync(client, "/admin/schedules");
        await PostFormAsync(client, "/admin/schedules/bulk", listToken,
            ("act", "delete"), ("ids", weekId.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.True(await s.Db.Schedules.AnyAsync(x => x.Id == weekId));

        // An unreferenced schedule deletes (entries cascade).
        int freshId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var fresh = new Schedule
            {
                Name = $"S7 Silinecek {Guid.NewGuid():N}"[..24],
                Entries = [new ScheduleEntry { Name = "Mesai", Repeats = ScheduleRepeat.Daily, StartsAt = new TimeOnly(9, 0), EndsAt = new TimeOnly(17, 0) }],
            };
            s.Db.Schedules.Add(fresh);
            await s.Db.SaveChangesAsync();
            freshId = fresh.Id;
        }
        var (token2, _) = await GetWithTokenAsync(client, $"/admin/schedule-edit?id={freshId}");
        var deleted = await PostFormAsync(client, "/admin/schedule-edit/delete", token2,
            ("id", freshId.ToString()));
        Assert.Equal("/admin/schedules", PathOf(deleted));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.Schedules.AnyAsync(x => x.Id == freshId));
            Assert.False(await s.Db.Set<ScheduleEntry>().AnyAsync(e => e.ScheduleId == freshId));
        }
    }

    [Fact]
    public async Task ScheduleBulk_EnableDisable_FlipsPickers()
    {
        var name = $"S7 Aktiflik {Guid.NewGuid():N}"[..24];
        int id;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var schedule = new Schedule { Name = name };
            s.Db.Schedules.Add(schedule);
            await s.Db.SaveChangesAsync();
            id = schedule.Id;
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/schedules");

        // Business-hours + active: offered in the SLA dialog's schedule select.
        Assert.Contains(name, WebUtility.HtmlDecode(await client.GetStringAsync("/admin/slas")));

        await PostFormAsync(client, "/admin/schedules/bulk", token, ("act", "disable"), ("ids", id.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False(await s.Db.Schedules.Where(x => x.Id == id).Select(x => x.IsActive).SingleAsync());
        Assert.DoesNotContain(name, WebUtility.HtmlDecode(await client.GetStringAsync("/admin/slas")));

        var (token2, _) = await GetWithTokenAsync(client, "/admin/schedules");
        await PostFormAsync(client, "/admin/schedules/bulk", token2, ("act", "enable"), ("ids", id.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.True(await s.Db.Schedules.Where(x => x.Id == id).Select(x => x.IsActive).SingleAsync());
        Assert.Contains(name, WebUtility.HtmlDecode(await client.GetStringAsync("/admin/slas")));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task<JsonElement> DiagnoseAsync(
        HttpClient client, string token, (string Key, string Value)[] fields)
    {
        var response = await PostFormAsync(client, "/admin/schedule-edit/diagnose", token, fields);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7SCHEDULEKEY23ABC";
        var username = $"s7sc{Guid.NewGuid():N}"[..14];
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = new StaffUser
            {
                UserName = username,
                Email = $"{username}@rapidsol.com.tr",
                EmailConfirmed = true,
                FullName = $"S7 {username}",
            };
            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, "Agent");
            await users.AddToRoleAsync(user, "Admin");
            await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", user.FullName!));
            await users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", totpKey);
            await users.SetTwoFactorEnabledAsync(user, true);

            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var deptId = await db.Departments.Select(d => d.Id).OrderBy(x => x).FirstAsync();
            var roleId = await db.Roles.Select(r => r.Id).OrderBy(x => x).FirstAsync();
            db.Staff.Add(new Staff
            {
                IdentityUserId = user.Id,
                Username = username,
                FirstName = "S7",
                LastName = username,
                Email = user.Email,
                DepartmentId = deptId,
                RoleId = roleId,
                IsAdmin = true,
                IsVisible = false,
            });
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", username), ("Password", Password));
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, ("Code", ComputeTotp(totpKey)));
        Assert.Equal("/admin/dashboard", PathOf(landed));
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (ids / row arrays / day-mark sequences).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    // ---- totp helper (AdminAuthTests twin) --------------------------------------------

    private static string ComputeTotp(string base32Key)
    {
        var keyBytes = FromBase32(base32Key.Replace(" ", "").ToUpperInvariant());
        var timestep = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var timestepBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timestepBytes);
        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(timestepBytes);
        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input)
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
