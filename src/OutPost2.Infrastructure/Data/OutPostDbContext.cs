using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Connections;
using OutPost2.Core.Users;

namespace OutPost2.Infrastructure.Data;

public sealed class OutPostDbContext(DbContextOptions<OutPostDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<PendingConnection> PendingConnections => Set<PendingConnection>();

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
            // No length cap: this holds the sealed blob, and an MSAL token cache is kilobytes.
            e.Property(c => c.SealedCredential).IsRequired();
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

        b.Entity<PendingConnection>(e =>
        {
            e.ToTable("pending_connections");
            e.HasKey(p => p.State);

            e.Property(p => p.State).HasMaxLength(64);
            e.Property(p => p.CodeVerifier).HasMaxLength(128);
            e.Property(p => p.Label).HasMaxLength(60);
            e.Property(p => p.TenantHint).HasMaxLength(100);
            e.Property(p => p.Provider).HasConversion<string>().HasMaxLength(40);

            // Expired rows are swept; index the column the sweep scans.
            e.HasIndex(p => p.ExpiresAt);
        });
    }
}
