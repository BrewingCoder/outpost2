using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Auth;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Auth;

public interface IOAuthService
{
    Task<OAuthClient> RegisterClientAsync(string clientName, IEnumerable<string> redirectUris, CancellationToken ct = default);
    Task<OAuthClient?> FindClientAsync(string clientId, CancellationToken ct = default);

    Task<string> IssueAuthCodeAsync(
        string clientId, Guid userId, string redirectUri, string codeChallenge,
        IEnumerable<string> connectionScope, CancellationToken ct = default);

    Task<TokenGrant?> RedeemCodeAsync(
        string code, string clientId, string redirectUri, string codeVerifier, CancellationToken ct = default);

    Task<TokenGrant?> RefreshAsync(string refreshToken, string clientId, CancellationToken ct = default);

    /// <summary>Validates a bearer token. Returns null if unknown, expired or revoked.</summary>
    Task<OAuthToken?> ValidateAsync(string accessToken, CancellationToken ct = default);

    Task<IReadOnlyList<OAuthToken>> ListTokensAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAsync(Guid userId, string tokenHash, CancellationToken ct = default);
}

public sealed record TokenGrant(string AccessToken, string RefreshToken, int ExpiresInSeconds);

public sealed class OAuthService(OutPostDbContext db) : IOAuthService
{
    /// <summary>
    /// Short. The refresh token carries longevity, so a leaked access token is useful for
    /// an hour rather than indefinitely.
    /// </summary>
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public async Task<OAuthClient> RegisterClientAsync(
        string clientName, IEnumerable<string> redirectUris, CancellationToken ct = default)
    {
        var client = new OAuthClient
        {
            ClientId = "op2_" + Base64Url(RandomNumberGenerator.GetBytes(24)),
            ClientName = string.IsNullOrWhiteSpace(clientName) ? "Unnamed MCP client" : clientName.Trim(),
            RedirectUris = string.Join('\n', redirectUris),
        };

        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(ct);
        return client;
    }

    public Task<OAuthClient?> FindClientAsync(string clientId, CancellationToken ct = default) =>
        db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

    public async Task<string> IssueAuthCodeAsync(
        string clientId, Guid userId, string redirectUri, string codeChallenge,
        IEnumerable<string> connectionScope, CancellationToken ct = default)
    {
        var code = Base64Url(RandomNumberGenerator.GetBytes(32));

        db.OAuthAuthCodes.Add(new OAuthAuthCode
        {
            Code = code,
            ClientId = clientId,
            UserId = userId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            ConnectionScope = string.Join('\n', connectionScope),
            ExpiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime),
        });

        await db.SaveChangesAsync(ct);
        return code;
    }

    public async Task<TokenGrant?> RedeemCodeAsync(
        string code, string clientId, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        var record = await db.OAuthAuthCodes.FirstOrDefaultAsync(c => c.Code == code, ct);
        if (record is null) return null;

        // Every one of these must hold. Each guards a distinct attack: replay, a stolen
        // code used by another client, an open-redirect swap, and interception without
        // the verifier.
        if (!record.IsUsable(DateTimeOffset.UtcNow)) return null;
        if (record.ClientId != clientId) return null;
        if (record.RedirectUri != redirectUri) return null;
        if (!VerifyPkce(codeVerifier, record.CodeChallenge)) return null;

        record.ConsumedAt = DateTimeOffset.UtcNow;

        var grant = IssueToken(record.UserId, clientId, record.ConnectionScope);
        await db.SaveChangesAsync(ct);
        return grant;
    }

    public async Task<TokenGrant?> RefreshAsync(string refreshToken, string clientId, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        var existing = await db.OAuthTokens.FirstOrDefaultAsync(
            t => t.RefreshTokenHash == hash && t.ClientId == clientId, ct);

        if (existing is null || existing.RevokedAt is not null) return null;

        // Rotate: the old pair dies with the refresh. A refresh token that keeps working
        // after use cannot be distinguished from a stolen one being replayed.
        existing.RevokedAt = DateTimeOffset.UtcNow;

        var grant = IssueToken(existing.UserId, clientId, existing.ConnectionScope, existing.Label);
        await db.SaveChangesAsync(ct);
        return grant;
    }

    // Not async, and no CancellationToken: it adds a row to the change tracker and
    // nothing here goes near the network. The caller's SaveChangesAsync is what can be
    // cancelled, and taking a token that is never observed says otherwise.
    private TokenGrant IssueToken(
        Guid userId, string clientId, string connectionScope, string? label = null)
    {
        var access = Base64Url(RandomNumberGenerator.GetBytes(32));
        var refresh = Base64Url(RandomNumberGenerator.GetBytes(32));

        db.OAuthTokens.Add(new OAuthToken
        {
            // Hashed, never stored in the clear: a database leak must not yield working
            // credentials to every user's mail.
            TokenHash = Hash(access),
            RefreshTokenHash = Hash(refresh),
            UserId = userId,
            ClientId = clientId,
            ConnectionScope = connectionScope,
            ExpiresAt = DateTimeOffset.UtcNow.Add(AccessLifetime),
            Label = label,
        });

        return new TokenGrant(access, refresh, (int)AccessLifetime.TotalSeconds);
    }

    public async Task<OAuthToken?> ValidateAsync(string accessToken, CancellationToken ct = default)
    {
        var hash = Hash(accessToken);

        // AsNoTracking is load-bearing, not an optimisation. RevokeAsync writes via
        // ExecuteUpdateAsync, which bypasses the change tracker — so a tracked read would
        // hand back a cached entity with RevokedAt still null and a revoked token would
        // keep working. Revocation must be observed on the very next call.
        var token = await db.OAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null || !token.IsUsable(DateTimeOffset.UtcNow)) return null;

        // Set-based for the same reason, and because this runs on every MCP request.
        await db.OAuthTokens.Where(t => t.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, DateTimeOffset.UtcNow), ct);

        return token;
    }

    public async Task<IReadOnlyList<OAuthToken>> ListTokensAsync(Guid userId, CancellationToken ct = default) =>
        await db.OAuthTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.IssuedAt)
            .ToListAsync(ct);

    public async Task RevokeAsync(Guid userId, string tokenHash, CancellationToken ct = default)
    {
        // Scoped by userId: revoking is a privileged act and must not be possible against
        // another user's token even with its hash.
        await db.OAuthTokens
            .Where(t => t.UserId == userId && t.TokenHash == tokenHash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string HashPublic(string value) => Hash(value);

    private static bool VerifyPkce(string verifier, string challenge)
    {
        if (string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(challenge)) return false;

        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Fixed-time comparison: a timing oracle on the challenge would let an attacker
        // recover it byte by byte.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
