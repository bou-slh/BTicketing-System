using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;

namespace RapidsolDestek.Tests;

/// <summary>
/// S3 gate: metadata-level checks on the domain model (no database needed —
/// the EF model is built in memory against the Npgsql provider).
/// </summary>
public class DomainModelTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var ctx = new AppDbContext(options);
        return ctx.Model;
    }

    [Fact]
    public void Model_ContainsTheFullS3EntitySweep()
    {
        // The roadmap's S3 entity list (mockups/ROADMAP.md §5) must all be mapped.
        Type[] required =
        [
            typeof(Ticket), typeof(TicketStatus), typeof(TicketPriority), typeof(EffortProposal),
            typeof(Domain.Entities.Thread), typeof(ThreadEntry), typeof(ThreadEvent), typeof(ThreadCollaborator),
            typeof(TaskItem), typeof(User), typeof(UserEmail), typeof(Organization),
            typeof(Staff), typeof(Team), typeof(Role), typeof(Department), typeof(StaffDepartmentAccess),
            typeof(HelpTopic), typeof(SlaPlan), typeof(Schedule), typeof(ScheduleEntry),
            typeof(Filter), typeof(FilterRule), typeof(FilterAction),
            typeof(SavedQueue), typeof(QueueColumn), typeof(FormDefinition), typeof(FormField),
            typeof(FormEntry), typeof(FormEntryValue), typeof(ListDefinition), typeof(ListItem),
            typeof(KbCategory), typeof(FaqArticle), typeof(CannedResponse),
            typeof(EmailAccount), typeof(EmailChannel), typeof(EmailTemplateSet), typeof(EmailTemplate),
            typeof(Attachment), typeof(StoredFile), typeof(AuditEvent), typeof(Setting),
            typeof(ApiKey), typeof(BanlistEntry), typeof(SitePage), typeof(Sequence),
        ];

        var mapped = Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();
        var missing = required.Where(t => !mapped.Contains(t)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void LookupDeletes_NeverCascadeIntoTickets()
    {
        // A status/department/priority/SLA delete must not be able to eat tickets.
        var ticket = Model.FindEntityType(typeof(Ticket))!;
        foreach (var fk in ticket.GetForeignKeys())
        {
            Assert.NotEqual(DeleteBehavior.Cascade, fk.DeleteBehavior);
        }
    }

    [Fact]
    public void AggregateChildren_CascadeWithTheirParent()
    {
        (Type Child, Type Parent)[] cascades =
        [
            (typeof(ThreadEntry), typeof(Domain.Entities.Thread)),
            (typeof(EffortProposal), typeof(Ticket)),
            (typeof(UserEmail), typeof(User)),
            (typeof(FilterRule), typeof(Filter)),
            (typeof(FormField), typeof(FormDefinition)),
            (typeof(EmailChannel), typeof(EmailAccount)),
            (typeof(EmailTemplate), typeof(EmailTemplateSet)),
            (typeof(ScheduleEntry), typeof(Schedule)),
            (typeof(ListItem), typeof(ListDefinition)),
        ];

        foreach (var (child, parent) in cascades)
        {
            var fk = Model.FindEntityType(child)!.GetForeignKeys()
                .Single(k => k.PrincipalEntityType.ClrType == parent);
            Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
        }
    }

    [Fact]
    public void DomainEnums_PersistAsReadableStrings()
    {
        (Type Entity, string Property)[] enumProps =
        [
            (typeof(Ticket), nameof(Ticket.Source)),
            (typeof(TicketStatus), nameof(TicketStatus.State)),
            (typeof(ThreadEntry), nameof(ThreadEntry.Type)),
            (typeof(EffortProposal), nameof(EffortProposal.State)),
            (typeof(Filter), nameof(Filter.Target)),
            (typeof(EmailChannel), nameof(EmailChannel.Protocol)),
        ];

        foreach (var (entity, property) in enumProps)
        {
            var prop = Model.FindEntityType(entity)!.FindProperty(property)!;
            Assert.Equal(typeof(string), prop.GetProviderClrType()
                ?? prop.GetValueConverter()?.ProviderClrType);
        }
    }

    [Fact]
    public void EffortProposals_AreUniquePerTicketRevision()
    {
        var entity = Model.FindEntityType(typeof(EffortProposal))!;
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(EffortProposal.TicketId), nameof(EffortProposal.RevisionNo)]));
    }

    [Fact]
    public void CanonNaturalKeys_AreUnique()
    {
        (Type Entity, string[] Props)[] uniques =
        [
            (typeof(Ticket), [nameof(Ticket.Number)]),
            (typeof(TaskItem), [nameof(TaskItem.Number)]),
            (typeof(TicketStatus), [nameof(TicketStatus.Key)]),
            (typeof(TicketPriority), [nameof(TicketPriority.Key)]),
            (typeof(Staff), [nameof(Staff.Username)]),
            (typeof(UserEmail), [nameof(UserEmail.Address)]),
            (typeof(Setting), [nameof(Setting.Namespace), nameof(Setting.Key)]),
            (typeof(EmailTemplate), [nameof(EmailTemplate.SetId), nameof(EmailTemplate.CodeName)]),
        ];

        foreach (var (entity, props) in uniques)
        {
            Assert.Contains(Model.FindEntityType(entity)!.GetIndexes(), i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(props));
        }
    }
}
