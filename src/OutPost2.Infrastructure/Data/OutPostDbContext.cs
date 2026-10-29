using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Auth;
using OutPost2.Core.Code;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Jobs;
using OutPost2.Core.Users;

namespace OutPost2.Infrastructure.Data;

public sealed class OutPostDbContext(DbContextOptions<OutPostDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<PendingConnection> PendingConnections => Set<PendingConnection>();
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthAuthCode> OAuthAuthCodes => Set<OAuthAuthCode>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<StagedFile> StagedFiles => Set<StagedFile>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<PreviewLink> PreviewLinks => Set<PreviewLink>();
    public DbSet<RepoAlias> RepoAliases => Set<RepoAlias>();

    /// <summary>
    /// ASP.NET Core's data protection key ring (OP2-56). Here because the alternative is
    /// the container filesystem, which is new on every deploy: the ring would be
    /// regenerated, and every auth cookie and antiforgery token minted before the deploy
    /// would be rejected after it. Shared storage also means replicas agree on the ring
    /// rather than each minting its own.
    /// <para>
    /// The rows hold key material, so they are encrypted at rest under the same Vault KEK
    /// as the mailbox credentials — see <c>VaultXmlEncryptor</c>. A database copy on its
    /// own decrypts nothing.
    /// </para>
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);

            // Authorisation keys on the numeric id, so it must be unique and indexed.
            e.HasIndex(u => u.GitHubUserId).IsUnique();

            e.Property(u => u.GitHubLogin).HasMaxLength(100).IsRequired();
            e.Property(u => u.AvatarUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<RepoAlias>(e =>
        {
            e.ToTable("repo_aliases");
            e.HasKey(a => a.Id);

            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);

            // Deleting a connection takes its aliases with it. An alias naming a
            // repository nothing can reach any more is not a record worth keeping.
            e.HasOne<Connection>().WithMany().HasForeignKey(a => a.ConnectionId).OnDelete(DeleteBehavior.Cascade);

            e.Property(a => a.Alias).HasMaxLength(120).IsRequired();
            e.Property(a => a.FullName).HasMaxLength(255).IsRequired();

            // One meaning per spoken name per connection. Two rows for "the core" would
            // make resolution depend on insertion order, which is no resolution at all.
            e.HasIndex(a => new { a.ConnectionId, a.Alias }).IsUnique();
        });

        modelBuilder.Entity<Connection>(e =>
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

            // No length cap, like SealedCredential and for the same kind of reason: the
            // admin-consent URL carries every requested scope, URL-encoded, and already
            // runs past 800 characters. A cap would be a number chosen today that
            // silently truncates the link the first time a scope is added — and a
            // truncated consent URL fails in the administrator's browser, where nobody
            // here would see it.
            e.Property(c => c.AdminConsentUrl);

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

        modelBuilder.Entity<PendingConnection>(e =>
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

        modelBuilder.Entity<OAuthClient>(e =>
        {
            e.ToTable("oauth_clients");
            e.HasKey(c => c.ClientId);
            e.Property(c => c.ClientId).HasMaxLength(64);
            e.Property(c => c.ClientName).HasMaxLength(200);
            e.Property(c => c.RedirectUris).IsRequired();
        });

        modelBuilder.Entity<OAuthAuthCode>(e =>
        {
            e.ToTable("oauth_auth_codes");
            e.HasKey(c => c.Code);
            e.Property(c => c.Code).HasMaxLength(64);
            e.Property(c => c.ClientId).HasMaxLength(64);
            e.Property(c => c.RedirectUri).HasMaxLength(500);
            e.Property(c => c.CodeChallenge).HasMaxLength(128);
            e.HasIndex(c => c.ExpiresAt);
        });

        modelBuilder.Entity<PreviewLink>(e =>
        {
            e.ToTable("preview_links");
            e.HasKey(l => l.TokenHash);
            e.Property(l => l.TokenHash).HasMaxLength(64);
            e.Property(l => l.Label).HasMaxLength(120);
            e.Property(l => l.FirstUsedFrom).HasMaxLength(200);
            e.HasIndex(l => l.UserId);
        });

        modelBuilder.Entity<OAuthToken>(e =>
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

        modelBuilder.Entity<StagedFile>(e =>
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

        modelBuilder.Entity<DataProtectionKey>(e =>
        {
            e.ToTable("data_protection_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.FriendlyName).HasMaxLength(200);

            // No length cap: the value is a sealed XML element, and its size depends on
            // the Vault envelope rather than on anything we choose.
            e.Property(k => k.Xml).IsRequired();
        });

        modelBuilder.Entity<Job>(e =>
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
