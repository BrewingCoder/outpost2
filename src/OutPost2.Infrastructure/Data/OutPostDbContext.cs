using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Auth;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Jobs;
using OutPost2.Core.Users;

namespace OutPost2.Infrastructure.Data;

public sealed class OutPostDbContext(DbContextOptions<OutPostDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<PendingConnection> PendingConnections => Set<PendingConnection>();
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthAuthCode> OAuthAuthCodes => Set<OAuthAuthCode>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<StagedFile> StagedFiles => Set<StagedFile>();
    public DbSet<Job> Jobs => Set<Job>();

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
            e.Property(c => c.AccountIdentifier).HasMaxLength(320);

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

        b.Entity<OAuthClient>(e =>
        {
            e.ToTable("oauth_clients");
            e.HasKey(c => c.ClientId);
            e.Property(c => c.ClientId).HasMaxLength(64);
            e.Property(c => c.ClientName).HasMaxLength(200);
            e.Property(c => c.RedirectUris).IsRequired();
        });

        b.Entity<OAuthAuthCode>(e =>
        {
            e.ToTable("oauth_auth_codes");
            e.HasKey(c => c.Code);
            e.Property(c => c.Code).HasMaxLength(64);
            e.Property(c => c.ClientId).HasMaxLength(64);
            e.Property(c => c.RedirectUri).HasMaxLength(500);
            e.Property(c => c.CodeChallenge).HasMaxLength(128);
            e.HasIndex(c => c.ExpiresAt);
        });

        b.Entity<OAuthToken>(e =>
        {
            e.ToTable("oauth_tokens");
            e.HasKey(t => t.TokenHash);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.RefreshTokenHash).HasMaxLength(64);
            e.Property(t => t.ClientId).HasMaxLength(64);
            e.Property(t => t.Label).HasMaxLength(120);

            // Refresh lookups hit this on every renewal.
            e.HasIndex(t => t.RefreshTokenHash);
            e.HasIndex(t => new { t.UserId, t.RevokedAt });
        });

        b.Entity<StagedFile>(e =>
        {
            e.ToTable("staged_files");
            e.HasKey(f => f.Id);
            e.Property(f => f.Name).HasMaxLength(200).IsRequired();
            e.Property(f => f.FileName).HasMaxLength(300).IsRequired();
            e.Property(f => f.ContentType).HasMaxLength(200).IsRequired();
            e.Property(f => f.StorageKey).HasMaxLength(200).IsRequired();
            e.Property(f => f.Sha256).HasMaxLength(64);

            e.HasIndex(f => new { f.UserId, f.ExpiresAt });
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Kind).HasConversion<string>().HasMaxLength(40);
            e.Property(j => j.State).HasConversion<string>().HasMaxLength(40);
            e.Property(j => j.Description).HasMaxLength(500).IsRequired();
            e.Property(j => j.ConnectionLabel).HasMaxLength(60);
            e.Property(j => j.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(j => j.Detail).HasMaxLength(500);
            e.Property(j => j.Error).HasMaxLength(1000);

            // Idempotency is enforced by the DATABASE, not by a check-then-insert that can
            // race: two concurrent retries of the same tool call must not both send.
            e.HasIndex(j => new { j.UserId, j.IdempotencyKey }).IsUnique();
            e.HasIndex(j => new { j.UserId, j.State });
        });
    }
}
