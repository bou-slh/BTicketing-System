using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Interceptors;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Infrastructure;

/// <summary>
/// Application database context. Deliberately a plain <see cref="DbContext"/> rather than
/// IdentityDbContext, because the application hosts two independent Identity principals
/// (StaffUser and CustomerUser) and IdentityDbContext hardwires a single user type.
/// The mappings below replicate the conventions IdentityDbContext would apply.
/// Domain (S3) entities are configured in <see cref="DomainModelConfiguration"/>.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // ----- Identity -----
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<StaffRole> StaffRoles => Set<StaffRole>();
    public DbSet<CustomerUser> CustomerUsers => Set<CustomerUser>();

    // ----- Domain: tickets & threads -----
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketStatus> TicketStatuses => Set<TicketStatus>();
    public DbSet<TicketPriority> TicketPriorities => Set<TicketPriority>();
    public DbSet<EffortProposal> EffortProposals => Set<EffortProposal>();
    public DbSet<Thread> Threads => Set<Thread>();
    public DbSet<ThreadEntry> ThreadEntries => Set<ThreadEntry>();
    public DbSet<ThreadEvent> ThreadEvents => Set<ThreadEvent>();
    public DbSet<ThreadEventType> ThreadEventTypes => Set<ThreadEventType>();
    public DbSet<ThreadCollaborator> ThreadCollaborators => Set<ThreadCollaborator>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<EditLock> EditLocks => Set<EditLock>();

    // ----- Domain: people & staffing -----
    public DbSet<User> Users => Set<User>();
    public DbSet<UserEmail> UserEmails => Set<UserEmail>();
    public DbSet<UserNote> UserNotes => Set<UserNote>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Department> Departments => Set<Department>();

    // ----- Domain: routing & config -----
    public DbSet<HelpTopic> HelpTopics => Set<HelpTopic>();
    public DbSet<SlaPlan> SlaPlans => Set<SlaPlan>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<Filter> Filters => Set<Filter>();
    public DbSet<SavedQueue> SavedQueues => Set<SavedQueue>();
    public DbSet<QueueColumn> QueueColumns => Set<QueueColumn>();
    public DbSet<QueueSortOption> QueueSortOptions => Set<QueueSortOption>();
    public DbSet<Sequence> Sequences => Set<Sequence>();
    public DbSet<FormDefinition> FormDefinitions => Set<FormDefinition>();
    public DbSet<FormEntry> FormEntries => Set<FormEntry>();
    public DbSet<ListDefinition> ListDefinitions => Set<ListDefinition>();

    // ----- Domain: kb, email, system -----
    public DbSet<KbCategory> KbCategories => Set<KbCategory>();
    public DbSet<FaqArticle> FaqArticles => Set<FaqArticle>();
    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();
    public DbSet<EmailAccount> EmailAccounts => Set<EmailAccount>();
    public DbSet<EmailTemplateSet> EmailTemplateSets => Set<EmailTemplateSet>();
    public DbSet<BanlistEntry> BanlistEntries => Set<BanlistEntry>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<SitePage> SitePages => Set<SitePage>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SystemLogEntry> SystemLogEntries => Set<SystemLogEntry>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Per-context-instance interceptors: AuditInterceptor keeps per-save state, so it
        // must not be shared across contexts. (Interceptor instances are not part of the
        // internal service-provider cache key, so this does not fragment the cache.)
        optionsBuilder.AddInterceptors(new TimestampInterceptor(), new AuditInterceptor());
        base.OnConfiguring(optionsBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Npgsql only writes UTC DateTimeOffsets to timestamptz; normalize on save so
        // callers may pass any offset (e.g. Europe/Istanbul canon dates in the seed).
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();

        // Domain enums persist as readable strings (parity with osTicket's enum columns).
        foreach (var enumType in typeof(Ticket).Assembly.GetTypes()
                     .Where(t => t.IsEnum && t.Namespace?.StartsWith("RapidsolDestek.Domain") == true))
        {
            configurationBuilder.Properties(enumType)
                .HaveConversion(typeof(EnumToStringConverter<>).MakeGenericType(enumType));
        }

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ----- Staff identity -----

        builder.Entity<StaffUser>(b =>
        {
            b.ToTable("staff_user");
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.NormalizedUserName).IsUnique();
            b.HasIndex(u => u.NormalizedEmail);
            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(u => u.UserName).HasMaxLength(256);
            b.Property(u => u.NormalizedUserName).HasMaxLength(256);
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);
            b.Property(u => u.FullName).IsRequired().HasMaxLength(256);

            b.HasMany<StaffUserClaim>().WithOne().HasForeignKey(uc => uc.UserId).IsRequired();
            b.HasMany<StaffUserLogin>().WithOne().HasForeignKey(ul => ul.UserId).IsRequired();
            b.HasMany<StaffUserToken>().WithOne().HasForeignKey(ut => ut.UserId).IsRequired();
            b.HasMany<StaffUserRole>().WithOne().HasForeignKey(ur => ur.UserId).IsRequired();
        });

        builder.Entity<StaffRole>(b =>
        {
            b.ToTable("staff_role");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.NormalizedName).IsUnique();
            b.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(r => r.Name).HasMaxLength(256);
            b.Property(r => r.NormalizedName).HasMaxLength(256);

            b.HasMany<StaffUserRole>().WithOne().HasForeignKey(ur => ur.RoleId).IsRequired();
            b.HasMany<StaffRoleClaim>().WithOne().HasForeignKey(rc => rc.RoleId).IsRequired();
        });

        builder.Entity<StaffUserClaim>(b =>
        {
            b.ToTable("staff_user_claim");
            b.HasKey(uc => uc.Id);
        });

        builder.Entity<StaffUserRole>(b =>
        {
            b.ToTable("staff_user_role");
            b.HasKey(ur => new { ur.UserId, ur.RoleId });
        });

        builder.Entity<StaffUserLogin>(b =>
        {
            b.ToTable("staff_user_login");
            b.HasKey(l => new { l.LoginProvider, l.ProviderKey });
        });

        builder.Entity<StaffUserToken>(b =>
        {
            b.ToTable("staff_user_token");
            b.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
        });

        builder.Entity<StaffRoleClaim>(b =>
        {
            b.ToTable("staff_role_claim");
            b.HasKey(rc => rc.Id);
        });

        // ----- Customer identity -----

        builder.Entity<CustomerUser>(b =>
        {
            b.ToTable("customer_user");
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.NormalizedUserName).IsUnique();
            b.HasIndex(u => u.NormalizedEmail);
            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(u => u.UserName).HasMaxLength(256);
            b.Property(u => u.NormalizedUserName).HasMaxLength(256);
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);
            b.Property(u => u.FullName).IsRequired().HasMaxLength(256);
            b.Property(u => u.OrganizationName).HasMaxLength(256);
            b.Property(u => u.TimeZone).HasMaxLength(64);
            b.Property(u => u.Language).HasMaxLength(8);

            b.HasMany<CustomerUserClaim>().WithOne().HasForeignKey(uc => uc.UserId).IsRequired();
            b.HasMany<CustomerUserLogin>().WithOne().HasForeignKey(ul => ul.UserId).IsRequired();
            b.HasMany<CustomerUserToken>().WithOne().HasForeignKey(ut => ut.UserId).IsRequired();
        });

        builder.Entity<CustomerUserClaim>(b =>
        {
            b.ToTable("customer_user_claim");
            b.HasKey(uc => uc.Id);
        });

        builder.Entity<CustomerUserLogin>(b =>
        {
            b.ToTable("customer_user_login");
            b.HasKey(l => new { l.LoginProvider, l.ProviderKey });
        });

        builder.Entity<CustomerUserToken>(b =>
        {
            b.ToTable("customer_user_token");
            b.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
        });

        // ----- Domain (S3) -----

        builder.ApplyDomainModel();
    }

    private sealed class UtcDateTimeOffsetConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>
    {
        public UtcDateTimeOffsetConverter() : base(v => v.ToUniversalTime(), v => v)
        {
        }
    }
}
