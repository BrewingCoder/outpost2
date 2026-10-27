using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Connections;
using OutPost2.Core.Users;

namespace OutPost2.Infrastructure.Data;

public sealed class OutPostDbContext(DbContextOptions<OutPostDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Connection> Connections => Set<Connection>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);

            // Authorisation keys on the numeric id, so it must be unique and indexed.
            e.HasIndex(u => u.GitHubUserId).IsUnique();

            e.Property(u => u.GitHubLogin).HasMaxLength(100).IsRequired();
            e.Property(u => u.AvatarUrl).HasMaxLength(500);
        });

        b.Entity<Connection>(e =>
        {
            e.ToTable("connections");
            e.HasKey(c => c.Id);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(c => c.Label).HasMaxLength(60).IsRequired();
            e.Property(c => c.Instance).HasMaxLength(500).IsRequired();
            e.Property(c => c.CredentialRef).HasMaxLength(200).IsRequired();
            e.Property(c => c.HomeAccountId).HasMaxLength(200);

            e.Property(c => c.Provider).HasConversion<string>().HasMaxLength(40);
            e.Property(c => c.AuthKind).HasConversion<string>().HasMaxLength(40);
            e.Property(c => c.State).HasConversion<string>().HasMaxLength(40);

            // Labels are unique PER USER, not globally — my "work" is not your "work".
            // This is the index that makes "check work email" resolvable.
            e.HasIndex(c => new { c.UserId, c.Label }).IsUnique();

            // Guards against the same account being added twice.
            e.HasIndex(c => new { c.UserId, c.Provider, c.Instance, c.HomeAccountId }).IsUnique();

            // Defaults are PER PROVIDER: "check email" and "my open tickets" resolve
            // independently, so a single default per user would be wrong. The filtered
            // unique index enforces at most one default per (user, provider) in the
            // database rather than trusting application code to keep it true.
            e.HasIndex(c => new { c.UserId, c.Provider })
                .IsUnique()
                .HasFilter("\"IsDefaultForProvider\" = true");

            // Capabilities ride with the connection as JSONB. They are only ever read and
            // written whole, alongside their owner, and they are provider-specific — so a
            // relational table would buy joins we never want and a schema migration every
            // time a provider gains a capability.
            e.OwnsMany(c => c.Capabilities, cap =>
            {
                cap.ToJson();
                cap.Property(x => x.Name).HasMaxLength(60);
            });
        });
    }
}
