using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure.Identity;

namespace RapidsolDestek.Infrastructure;

/// <summary>
/// Application database context. Deliberately a plain <see cref="DbContext"/> rather than
/// IdentityDbContext, because the application hosts two independent Identity principals
/// (StaffUser and CustomerUser) and IdentityDbContext hardwires a single user type.
/// The mappings below replicate the conventions IdentityDbContext would apply.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<StaffRole> StaffRoles => Set<StaffRole>();
    public DbSet<CustomerUser> CustomerUsers => Set<CustomerUser>();

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
    }
}
