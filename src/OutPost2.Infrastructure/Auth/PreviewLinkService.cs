using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Auth;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Auth;

public interface IPreviewLinkService
{
    /// <summary>Mints a link. The plaintext token is returned once and never stored.</summary>
    Task<(PreviewLink Link, string Token)> MintAsync(
        Guid userId, string label, TimeSpan? lifetime = null, CancellationToken ct = default);

    /// <summary>Returns the owning user id, or null if the token is unknown, spent or stale.</summary>
    Task<Guid?> RedeemAsync(string token, string? from, CancellationToken ct = default);

    Task<IReadOnlyList<PreviewLink>> ListAsync(Guid userId, CancellationToken ct = default);

    Task RevokeAsync(Guid userId, string tokenHash, CancellationToken ct = default);

    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

public sealed class PreviewLinkService(OutPostDbContext db, ILogger<PreviewLinkService> log)
    : IPreviewLinkService
{
    public async Task<(PreviewLink, string)> MintAsync(
        Guid userId, string label, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        var requested = lifetime ?? PreviewLink.DefaultLifetime;

        // Clamped rather than rejected: a caller asking for a year gets a week and can see
        // the expiry it actually got, which is more useful than a validation error.
        if (requested > PreviewLink.MaxLifetime) requested = PreviewLink.MaxLifetime;
        if (requested < TimeSpan.FromMinutes(5)) requested = TimeSpan.FromMinutes(5);

        // 256 bits. This is a bearer credential in a URL; it has to survive being guessed
        // at by anyone who knows the hostname.
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));

        var link = new PreviewLink
        {
            TokenHash = Hash(token),
            UserId = userId,
            Label = string.IsNullOrWhiteSpace(label) ? "Preview" : label.Trim(),
            ExpiresAt = DateTimeOffset.UtcNow.Add(requested),
        };

        db.PreviewLinks.Add(link);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Minted preview link '{Label}' for {UserId}, expires {Expires:u}.",
            link.Label, userId, link.ExpiresAt);

        return (link, token);
    }

    public async Task<Guid?> RedeemAsync(string token, string? from, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Hash(token);
        var link = await db.PreviewLinks.FirstOrDefaultAsync(l => l.TokenHash == hash, ct);

        if (link is null)
        {
            // Logged without the token. An unknown preview token is either a typo or
            // somebody probing, and both are worth being able to see in the log.
            log.LogWarning("Preview link redemption failed: no such token.");
            return null;
        }

        if (!link.IsUsable(DateTimeOffset.UtcNow))
        {
            log.LogWarning("Preview link '{Label}' refused: {Reason}.", link.Label,
                link.RevokedAt is not null ? "revoked" : "expired");
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (link.FirstUsedAt is null)
        {
            link.FirstUsedAt = now;
            link.FirstUsedFrom = Truncate(from, 200);
        }

        link.LastUsedAt = now;
        link.UseCount++;
        await db.SaveChangesAsync(ct);

        return link.UserId;
    }

    public async Task<IReadOnlyList<PreviewLink>> ListAsync(Guid userId, CancellationToken ct = default) =>
        await db.PreviewLinks
            .Where(l => l.UserId == userId && l.RevokedAt == null && l.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

    public async Task RevokeAsync(Guid userId, string tokenHash, CancellationToken ct = default)
    {
        // Scoped by user in the WHERE clause, not checked afterwards: one user must not be
        // able to revoke another's link by guessing a hash.
        await db.PreviewLinks
            .Where(l => l.UserId == userId && l.TokenHash == tokenHash && l.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, DateTimeOffset.UtcNow), ct);
    }

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        db.PreviewLinks
            .Where(l => l.ExpiresAt < DateTimeOffset.UtcNow.AddDays(-30))
            .ExecuteDeleteAsync(ct);

    internal static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? s, int n) =>
        s is null ? null : s.Length <= n ? s : s[..n];
}
