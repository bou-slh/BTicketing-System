using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Agent.Controllers;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 agent/tickets list engine: advanced-search rules and derived-status filters
/// compose with the queue engine and translate to SQL; save-as-queue persists a
/// personal SavedQueue the engine can load back.
/// </summary>
[Collection("Postgres")]
public class AgentTicketListTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AdvancedRules_ComposeWithQueueEngine()
    {
        using var s = new ServiceScopeBundle(fixture);
        var engine = s.Get<IQueueEngine>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var visible = await engine.BuildAsync(QueueCriteria.Empty, uakin);

        // org contains → only Ulaşım A.Ş. tickets (hero R716555 among them).
        var org = await TicketListEngine
            .ApplyRules(visible, [new AdvRule("org", "contains", "Ulaşım")])
            .Include(t => t.User!).ThenInclude(u => u.Organization)
            .ToListAsync();
        Assert.NotEmpty(org);
        Assert.All(org, t => Assert.Equal("Ulaşım A.Ş.", t.User!.Organization!.Name));
        Assert.Contains(org, t => t.Number == "R716555");

        // dept is (exact, case-insensitive) → only Bordro.
        var dept = await TicketListEngine
            .ApplyRules(visible, [new AdvRule("dept", "is", "bordro")])
            .Include(t => t.Department)
            .ToListAsync();
        Assert.NotEmpty(dept);
        Assert.All(dept, t => Assert.Equal("Bordro", t.Department!.Name));

        // effort is pending (TR wording) → the pending-proposal tickets.
        var effort = await TicketListEngine
            .ApplyRules(visible, [new AdvRule("effort", "is", "Onayda")])
            .Select(t => t.Number)
            .ToListAsync();
        Assert.Contains("R716555", effort);
        Assert.DoesNotContain("R716544", effort); // latest revision rejected

        // status "is Açık" matches the seeded status name.
        var open = await TicketListEngine
            .ApplyRules(visible, [new AdvRule("status", "is", "Açık")])
            .Include(t => t.Status)
            .ToListAsync();
        Assert.NotEmpty(open);
        Assert.All(open, t => Assert.Equal("open", t.Status!.Key));
    }

    [Fact]
    public async Task DerivedStatusFilter_AndEffortSort_Translate()
    {
        using var s = new ServiceScopeBundle(fixture);
        var engine = s.Get<IQueueEngine>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var visible = await engine.BuildAsync(QueueCriteria.Empty, uakin);

        // effortWait / overdue are derived flags, not status rows.
        var derived = await TicketListEngine
            .ApplyStatusFilter(visible, ["effortWait", "overdue"])
            .Select(t => t.Number)
            .ToListAsync();
        Assert.Contains("R716555", derived); // pending effort
        Assert.Contains("R716539", derived); // IsOverdue flag

        // effort sort uses a correlated subquery — assert it executes.
        var sorted = await TicketListEngine.ApplySort(visible, "effort", desc: true)
            .Select(t => t.Number)
            .ToListAsync();
        Assert.NotEmpty(sorted);
    }

    [Fact]
    public async Task SaveSearch_PersistsPersonalQueue_EngineLoadsIt()
    {
        var title = $"Test Kuyruk {Guid.NewGuid():N}";
        int queueId;
        int staffId;

        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Username == "uakin");
            staffId = staff.Id;
            var controller = new TicketsController(
                s.Db, s.Get<IQueueEngine>(), s.Get<ITicketService>(), s.Get<ISettingsService>(), s.Get<IMemoryCache>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, staff.IdentityUserId!.Value.ToString())], "test")),
                    },
                },
            };

            var result = await controller.SaveSearch(
                title, ["dept"], ["is"], ["Bordro"], ["no", "subject"], "updated");
            var json = Assert.IsType<JsonResult>(result);
            var url = (string)json.Value!.GetType().GetProperty("url")!.GetValue(json.Value)!;
            queueId = int.Parse(url.Split('=')[1]);
        }

        // Assert via a fresh scope (EF identity map returns stale entities otherwise).
        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.SavedQueues
            .Include(x => x.Columns).ThenInclude(c => c.Column)
            .Include(x => x.Sorts)
            .SingleAsync(x => x.Id == queueId);
        Assert.Equal(title, saved.Title);
        Assert.Equal(staffId, saved.StaffId); // personal → "Kayıtlı Aramalarım" node
        Assert.Equal($"/{queueId}/", saved.Path);
        Assert.False(saved.InheritColumns);
        // no + subject + always-on status column.
        Assert.Equal(3, saved.Columns.Count);
        Assert.Single(saved.Sorts, x => x.IsDefault);

        // The flat criteria round-trips through the engine ("dept is Bordro" → dept id).
        var criteria = await fresh.Get<IQueueEngine>().LoadAsync(queueId);
        var bordroId = await fresh.Db.Departments.Where(d => d.Name == "Bordro").Select(d => d.Id).SingleAsync();
        Assert.Equal(bordroId, criteria.DepartmentId);
    }

    [Fact]
    public void Csv_EscapesDelimitersAndQuotes()
    {
        Assert.Equal("plain", TicketListEngine.Csv("plain"));
        Assert.Equal("\"a,b\"", TicketListEngine.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", TicketListEngine.Csv("say \"hi\""));
        Assert.Equal("\"line\nbreak\"", TicketListEngine.Csv("line\nbreak"));
        Assert.Equal("", TicketListEngine.Csv(null));
    }
}
