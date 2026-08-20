using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RapidsolDestek.Domain.Entities;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Infrastructure;

/// <summary>
/// Fluent configuration for the S3 domain model. Keys/uniques mirror the osTicket
/// reference schema; osTicket itself ships no FK constraints at all, so every real
/// constraint here is an upgrade — cascade only inside aggregates, Restrict everywhere
/// else (enforced by the sweep at the bottom).
/// </summary>
public static class DomainModelConfiguration
{
    public static void ApplyDomainModel(this ModelBuilder b)
    {
        // ----- Tickets -----

        b.Entity<TicketStatus>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.State);
        });

        b.Entity<TicketPriority>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.StatusId);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.StaffId);
            e.HasIndex(x => x.TeamId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ClosedAt);
            e.HasIndex(x => x.DueDate);

            e.HasOne(x => x.Thread).WithOne().HasForeignKey<Ticket>(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.SetNull);

            // Full-text search (osTicket "search" replaced with Postgres tsvector).
            // Shadow property keeps the Domain entity framework-free; "simple" config
            // because the corpus is mixed TR/EN — stemming asymmetry hurts more than
            // no stemming. Generated column + GIN index land in migration S4_Search.
            e.Property<NpgsqlTypes.NpgsqlTsVector>("SearchVector")
                .IsGeneratedTsVectorColumn("simple", nameof(Ticket.Number), nameof(Ticket.Subject));
            e.HasIndex("SearchVector").HasMethod("GIN");
        });

        b.Entity<EffortProposal>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.RevisionNo }).IsUnique();
            e.Property(x => x.Hours).HasPrecision(6, 2);
            e.HasOne(x => x.Ticket).WithMany(t => t.EffortProposals)
                .HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- Threads -----

        b.Entity<ThreadEntry>(e =>
        {
            e.HasIndex(x => x.ThreadId);
            e.HasIndex(x => x.Type);
            e.HasOne(x => x.Thread).WithMany(t => t.Entries)
                .HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);

            // Body-level full-text (bodies are sanitized at ingress, so tag noise is low).
            e.Property<NpgsqlTypes.NpgsqlTsVector>("SearchVector")
                .IsGeneratedTsVectorColumn("simple", nameof(ThreadEntry.Title), nameof(ThreadEntry.Body));
            e.HasIndex("SearchVector").HasMethod("GIN");
        });

        b.Entity<ThreadEventType>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<ThreadEvent>(e =>
        {
            e.HasIndex(x => new { x.ThreadId, x.EventTypeId, x.OccurredAt });
            e.HasIndex(x => new { x.OccurredAt, x.EventTypeId });
            e.HasOne(x => x.Thread).WithMany(t => t.Events)
                .HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ThreadCollaborator>(e =>
        {
            e.HasIndex(x => new { x.ThreadId, x.UserId }).IsUnique();
            e.HasOne(x => x.Thread).WithMany(t => t.Collaborators)
                .HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Draft>(e => e.HasIndex(x => new { x.StaffId, x.Namespace }));

        // ----- Tasks -----

        b.Entity<TaskItem>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.StaffId);
            e.HasOne(x => x.Thread).WithOne().HasForeignKey<TaskItem>(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ----- People -----

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.IdentityUserId).IsUnique();
            e.HasOne(x => x.DefaultEmail).WithMany().HasForeignKey(x => x.DefaultEmailId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Organization).WithMany(o => o.Members)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<UserEmail>(e =>
        {
            e.HasIndex(x => x.Address).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Emails)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserNote>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Organization>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<OrgNote>(e =>
        {
            e.HasIndex(x => x.OrganizationId);
            e.HasOne(x => x.Organization).WithMany()
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- Staffing -----

        b.Entity<Staff>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.IdentityUserId).IsUnique();
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.OnVacation);
            // DB default so pre-S7 rows keep receiving primary-department alerts.
            e.Property(x => x.PrimaryDepartmentAlerts).HasDefaultValue(true);
            // S7 staff-edit columns: defaults keep pre-migration rows honest
            // (local auth, primary-role-on-assignment checked per the mockup DOM).
            e.Property(x => x.UsePrimaryRoleOnAssigned).HasDefaultValue(true);
            e.Property(x => x.AuthBackend).HasMaxLength(32).HasDefaultValue("local");
        });

        b.Entity<StaffDepartmentAccess>(e =>
        {
            e.HasKey(x => new { x.StaffId, x.DepartmentId });
            e.HasOne(x => x.Staff).WithMany(s => s.DepartmentAccess)
                .HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Department).WithMany()
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany()
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Role>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<Team>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<TeamMember>(e =>
        {
            e.HasKey(x => new { x.TeamId, x.StaffId });
            e.HasOne(x => x.Team).WithMany(t => t.Members)
                .HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Staff).WithMany()
                .HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Department>(e =>
        {
            e.HasIndex(x => new { x.Name, x.ParentId }).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            // DB defaults so pre-S7 rows keep working values when the columns land.
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.AlertGroup).HasDefaultValue(DepartmentAlertGroup.All);
        });

        // ----- Routing -----

        b.Entity<HelpTopic>(e =>
        {
            e.HasIndex(x => new { x.Name, x.ParentId }).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<HelpTopicForm>(e =>
        {
            e.HasIndex(x => new { x.HelpTopicId, x.FormDefinitionId }).IsUnique();
            e.HasOne(x => x.HelpTopic).WithMany(t => t.Forms)
                .HasForeignKey(x => x.HelpTopicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FormDefinition).WithMany()
                .HasForeignKey(x => x.FormDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SlaPlan>(e => e.HasIndex(x => x.Name).IsUnique());

        // DB default TRUE so pre-S7 live rows stay active when the column lands.
        b.Entity<Schedule>(e => e.Property(x => x.IsActive).HasDefaultValue(true));

        b.Entity<ScheduleEntry>(e =>
        {
            e.HasIndex(x => x.ScheduleId);
            e.HasOne(x => x.Schedule).WithMany(s => s.Entries)
                .HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FilterRule>(e =>
        {
            e.HasIndex(x => new { x.FilterId, x.What, x.How, x.Value }).IsUnique();
            e.HasOne(x => x.Filter).WithMany(f => f.Rules)
                .HasForeignKey(x => x.FilterId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FilterAction>(e =>
        {
            e.HasIndex(x => x.FilterId);
            e.HasOne(x => x.Filter).WithMany(f => f.Actions)
                .HasForeignKey(x => x.FilterId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedQueue>(e =>
        {
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.StaffId);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SavedQueueColumn>(e =>
        {
            e.HasKey(x => new { x.QueueId, x.ColumnId });
            e.HasOne(x => x.Queue).WithMany(q => q.Columns)
                .HasForeignKey(x => x.QueueId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Column).WithMany()
                .HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedQueueSort>(e =>
        {
            e.HasKey(x => new { x.QueueId, x.SortOptionId });
            e.HasOne(x => x.Queue).WithMany(q => q.Sorts)
                .HasForeignKey(x => x.QueueId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SortOption).WithMany()
                .HasForeignKey(x => x.SortOptionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedQueueExportField>(e =>
        {
            e.HasIndex(x => x.QueueId);
            e.HasOne(x => x.Queue).WithMany(q => q.ExportFields)
                .HasForeignKey(x => x.QueueId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Sequence>(e => e.HasIndex(x => x.Name).IsUnique());

        // ----- Forms & lists -----

        b.Entity<FormDefinition>(e => e.Property(x => x.IsActive).HasDefaultValue(true));
        b.Entity<ListDefinition>(e => e.Property(x => x.IsActive).HasDefaultValue(true));

        b.Entity<FormField>(e =>
        {
            e.HasIndex(x => new { x.FormDefinitionId, x.Name }).IsUnique();
            e.HasOne(x => x.FormDefinition).WithMany(f => f.Fields)
                .HasForeignKey(x => x.FormDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FormEntry>(e =>
        {
            e.HasIndex(x => new { x.ObjectType, x.ObjectId });
        });

        b.Entity<FormEntryValue>(e =>
        {
            e.HasKey(x => new { x.FormEntryId, x.FormFieldId });
            e.HasOne(x => x.FormEntry).WithMany(en => en.Values)
                .HasForeignKey(x => x.FormEntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FormField).WithMany()
                .HasForeignKey(x => x.FormFieldId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ListItem>(e =>
        {
            e.HasIndex(x => x.ListDefinitionId);
            e.HasOne(x => x.ListDefinition).WithMany(l => l.Items)
                .HasForeignKey(x => x.ListDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- KB -----

        b.Entity<KbCategory>(e =>
        {
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FaqArticle>(e =>
        {
            e.HasIndex(x => x.Question).IsUnique();
            e.HasIndex(x => x.IsPublished);
            e.HasOne(x => x.Category).WithMany(c => c.Articles)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FaqArticleTopic>(e =>
        {
            e.HasKey(x => new { x.FaqArticleId, x.HelpTopicId });
            e.HasOne(x => x.FaqArticle).WithMany(a => a.HelpTopics)
                .HasForeignKey(x => x.FaqArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.HelpTopic).WithMany()
                .HasForeignKey(x => x.HelpTopicId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CannedResponse>(e =>
        {
            e.HasIndex(x => x.Title).IsUnique();
            e.HasIndex(x => x.IsEnabled);
        });

        // ----- Email -----

        b.Entity<EmailAccount>(e =>
        {
            e.HasIndex(x => x.Address).IsUnique();
            // Department.EmailAccountId / AutoResponseEmailAccountId are deliberate soft
            // refs; claim this navigation explicitly so convention doesn't pair it
            // one-to-one with them (which invented a shadow DepartmentId1).
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<EmailChannel>(e =>
        {
            e.HasIndex(x => new { x.EmailAccountId, x.Kind }).IsUnique();
            e.HasOne(x => x.EmailAccount).WithMany(a => a.Channels)
                .HasForeignKey(x => x.EmailAccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailTemplate>(e =>
        {
            e.HasIndex(x => new { x.SetId, x.CodeName }).IsUnique();
            e.HasOne(x => x.Set).WithMany(s => s.Templates)
                .HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BanlistEntry>(e => e.HasIndex(x => x.Address).IsUnique());

        b.Entity<EmailOutbound>(e =>
        {
            e.HasIndex(x => x.Status);      // pending sweep / admin outbox filters
            e.HasIndex(x => x.TicketId);    // per-ticket mail log
            // FromEmailAccountId / TicketId / ThreadEntryId are deliberate soft refs
            // (EmailAccount.DepartmentId precedent): outbox rows outlive account and
            // ticket deletions as a send log.
            e.HasIndex(x => x.MessageId); // inbound reply-threading + bounce correlation
        });

        b.Entity<EmailInbound>(e =>
        {
            // Idempotency guard: one row per (channel, server UID).
            e.HasIndex(x => new { x.EmailChannelId, x.Uid }).IsUnique();
            e.HasIndex(x => x.MessageId); // cross-channel duplicate backstop
            e.HasIndex(x => x.TicketId);  // per-ticket inbound mail log
            // EmailChannelId / EmailAccountId / TicketId / ThreadEntryId are soft refs
            // (EmailOutbound precedent): the fetch log outlives channel/ticket deletes.
        });

        // ----- System -----

        b.Entity<Setting>(e => e.HasIndex(x => new { x.Namespace, x.Key }).IsUnique());

        b.Entity<ApiKey>(e => e.HasIndex(x => x.Key).IsUnique());

        b.Entity<SitePage>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<SystemLogEntry>(e =>
        {
            e.HasIndex(x => x.Type);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => new { x.ObjectType, x.ObjectId });
            e.HasIndex(x => x.OccurredAt);
        });

        b.Entity<StoredFile>(e =>
        {
            e.HasIndex(x => x.StorageKey).IsUnique();
            e.HasIndex(x => x.Signature);
        });

        b.Entity<Attachment>(e =>
        {
            e.HasIndex(x => new { x.ObjectType, x.ObjectId, x.FileId }).IsUnique();
            e.HasOne(x => x.File).WithMany()
                .HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
        });

        RestrictUnconfiguredCascades(b);
    }

    /// <summary>
    /// Convention makes every required FK cascade on delete; outside the aggregate
    /// boundaries configured above that would let a lookup delete (status, department…)
    /// eat tickets. Anything still cascading whose delete behavior wasn't explicitly
    /// configured flips to Restrict. Identity tables keep their conventions.
    /// </summary>
    private static void RestrictUnconfiguredCascades(ModelBuilder b)
    {
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            if (entityType.ClrType.Namespace?.StartsWith("RapidsolDestek.Domain") != true)
                continue;

            foreach (var fk in entityType.GetForeignKeys())
            {
                if (fk.DeleteBehavior != DeleteBehavior.Cascade)
                    continue;

                var source = ((IConventionForeignKey)fk).GetDeleteBehaviorConfigurationSource();
                if (source != ConfigurationSource.Explicit)
                    fk.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
    }
}
