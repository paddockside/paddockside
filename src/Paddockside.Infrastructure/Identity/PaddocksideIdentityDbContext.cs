using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Paddockside.Infrastructure.Identity;

/// <summary>
/// People, credentials and memberships, in the <c>identity</c> schema. Kept apart from
/// <see cref="Persistence.PaddocksideDbContext"/> because a person exists before any tenant is chosen, so these
/// tables cannot sit behind the tenant filter. Memberships are only ever read for the signed-in person.
/// </summary>
public sealed class PaddocksideIdentityDbContext(DbContextOptions<PaddocksideIdentityDbContext> options)
    : IdentityUserContext<Person, Guid>(options)
{
    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<SignInToken> SignInTokens => Set<SignInToken>();

    public DbSet<StaffInvitation> StaffInvitations => Set<StaffInvitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");

        builder.Entity<Person>().ToTable("People");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("PersonClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("PersonLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("PersonTokens");

        builder.Entity<Membership>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Role).HasConversion<string>().HasMaxLength(32);
            b.Property(m => m.Status).HasConversion<string>().HasMaxLength(32);
            b.Ignore(m => m.Class);
            b.HasIndex(m => new { m.PersonId, m.TenantId, m.Role }).IsUnique();
            b.HasIndex(m => m.TenantId);
            b.HasOne<Person>().WithMany(p => p.Memberships).HasForeignKey(m => m.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SignInToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.TokenHash).HasMaxLength(64).IsUnicode(false);
            b.Property(t => t.CodeHash).HasMaxLength(64).IsUnicode(false);
            b.Property(t => t.BindingHash).HasMaxLength(64).IsUnicode(false);
            b.Property(t => t.Email).HasMaxLength(320);
            b.Property(t => t.Mobile).HasMaxLength(20);
            b.Property(t => t.ReturnPath).HasMaxLength(400);
            b.HasIndex(t => t.TokenHash).IsUnique().HasFilter("[TokenHash] IS NOT NULL");
            b.HasIndex(t => t.BindingHash);
            b.HasIndex(t => t.ExpiresAt);
        });

        builder.Entity<StaffInvitation>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.TenantName).HasMaxLength(200);
            b.Property(i => i.Email).HasMaxLength(320);
            b.Property(i => i.Role).HasConversion<string>().HasMaxLength(32);
            b.Property(i => i.InvitedByName).HasMaxLength(320);
            b.Property(i => i.TokenHash).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(i => i.TokenHash).IsUnique();
            b.HasIndex(i => new { i.TenantId, i.Email });
        });
    }
}
