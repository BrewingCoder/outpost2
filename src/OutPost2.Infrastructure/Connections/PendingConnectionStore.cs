using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Connections;

public interface IPendingConnectionStore
{
    /// <param name="expectedHomeAccountId">
    /// Set only when re-authorising an existing connection; the account the flow must come
    /// back as. See <see cref="PendingConnection.ExpectedHomeAccountId"/>.
    /// </param>
    Task<PendingConnection> CreateAsync(
        Guid userId, ProviderKind provider, string label, string? tenantHint,
        string? expectedHomeAccountId = null, CancellationToken ct = default);

    /// <summary>
    /// Redeems a state value. Returns null unless it exists, is unexpired, is unused, and
    /// belongs to <paramref name="userId"/>. Marks it consumed in the same breath.
    /// </summary>
    Task<PendingConnection?> ConsumeAsync(string state, Guid userId, CancellationToken ct = default);

    /// <summary>Persists changes to a pending record, notably the PKCE verifier MSAL assigns.</summary>
    Task SaveAsync(PendingConnection pending, CancellationToken ct = default);

    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

public sealed class PendingConnectionStore(OutPostDbContext db) : IPendingConnectionStore
{
    /// <summary>
    /// Deliberately short. The window only has to cover a human completing a consent
    /// screen; anything longer is just a larger target.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public async Task<PendingConnection> CreateAsync(
        Guid userId, ProviderKind provider, string label, string? tenantHint,
        string? expectedHomeAccountId = null, CancellationToken ct = default)
    {
        var pending = new PendingConnection
        {
            // 256 bits from a CSPRNG. The state is the only thing binding a callback to the
            // request that started it, so a guessable value would let an attacker complete
            // someone else's connection flow.
            State = Base64Url(RandomNumberGenerator.GetBytes(32)),
            CodeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32)),
            UserId = userId,
            Provider = provider,
            Label = label,
            TenantHint = tenantHint,
            ExpectedHomeAccountId = expectedHomeAccountId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime),
        };

        db.PendingConnections.Add(pending);
        await db.SaveChangesAsync(ct);
        return pending;
    }

    public async Task<PendingConnection?> ConsumeAsync(string state, Guid userId, CancellationToken ct = default)
    {
        var pending = await db.PendingConnections.FirstOrDefaultAsync(p => p.State == state, ct);
        if (pending is null) return null;

        // Ownership check, not just existence. Without it a leaked or injected state could
        // attach the victim's mailbox to whoever is holding the browser session — the
        // classic OAuth account-injection shape.
        if (pending.UserId != userId) return null;

        if (!pending.IsUsable(DateTimeOffset.UtcNow)) return null;

        pending.ConsumedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return pending;
    }

    public Task SaveAsync(PendingConnection pending, CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        db.PendingConnections
            .Where(p => p.ExpiresAt < DateTimeOffset.UtcNow.AddHours(-1))
            .ExecuteDeleteAsync(ct);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
