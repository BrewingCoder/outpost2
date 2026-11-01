using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Files;

public interface IUploadTicketService
{
    /// <summary>
    /// Mints a ticket. The plaintext token is returned once and never stored.
    /// </summary>
    Task<(UploadTicket Ticket, string Token)> MintAsync(
        Guid userId, string name, long maxBytes, CancellationToken ct = default);

    /// <summary>
    /// Spends a ticket, returning it, or null when the token is unknown, already spent or
    /// stale. Marked used <b>before</b> the body is read, so a failed upload cannot be
    /// replayed against it.
    /// </summary>
    Task<UploadTicket?> RedeemAsync(string token, string? from, CancellationToken ct = default);

    /// <summary>Records which staged file a spent ticket produced.</summary>
    Task RecordAsync(string tokenHash, Guid stagedFileId, CancellationToken ct = default);

    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// One-use permission to stage a file (OP2-252).
/// <para>
/// This hands out a bearer credential for an endpoint with no sign-in behind it, so the
/// rules are deliberately tight and all of them are enforced here rather than at the
/// endpoint: one use, fifteen minutes, one user, a size agreed in advance, and a cap on how
/// many can be outstanding at once.
/// </para>
/// </summary>
public sealed class UploadTicketService(OutPostDbContext db, ILogger<UploadTicketService> log)
    : IUploadTicketService
{
    public async Task<(UploadTicket Ticket, string Token)> MintAsync(
        Guid userId, string name, long maxBytes, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Expired ones do not count against the cap, and sweeping here means the table
        // cannot grow on a user who mints and abandons.
        var dead = await db.UploadTickets
            .Where(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);

        var outstanding = await db.UploadTickets
            .CountAsync(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt > now, ct);

        if (outstanding >= UploadTicket.MaxOutstanding)
            throw new InvalidOperationException(
                $"You already have {outstanding} uploads waiting to be used. "
                + "Finish or abandon one of those first — they expire in fifteen minutes.");

        // 256 bits. The token is the only thing standing between a guess and a write, so it
        // is not a short readable handle like the ones used for messages.
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));

        var ticket = new UploadTicket
        {
            TokenHash = Hash(token),
            UserId = userId,
            Name = name.Trim(),
            MaxBytes = maxBytes,
            ExpiresAt = now.Add(UploadTicket.Lifetime),
        };

        db.UploadTickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        // The name and the size, never the token.
        log.LogInformation(
            "Minted an upload ticket for {User} as \"{Name}\", up to {MaxBytes} bytes; swept {Dead} stale.",
            userId, ticket.Name, maxBytes, dead);

        return (ticket, token);
    }

    public async Task<UploadTicket?> RedeemAsync(
        string token, string? from, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Hash(token.Trim());
        var now = DateTimeOffset.UtcNow;

        // A conditional update rather than read-then-write: two requests arriving together
        // with the same token must not both pass, and "one use" has to mean that even when
        // the two are racing.
        var claimed = await db.UploadTickets
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.UsedAt, now)
                .SetProperty(t => t.UsedFrom, from), ct);

        if (claimed == 0)
        {
            // Said at warning, because a token arriving that is unknown rather than merely
            // late is the shape of somebody guessing.
            log.LogWarning("An upload ticket was refused: unknown, already used or expired.");
            return null;
        }

        return await db.UploadTickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
    }

    public async Task RecordAsync(string tokenHash, Guid stagedFileId, CancellationToken ct = default) =>
        await db.UploadTickets
            .Where(t => t.TokenHash == tokenHash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.StagedFileId, stagedFileId), ct);

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);

        // Spent ones are kept a day: "that upload went to this address" is worth being able
        // to answer after the fact if a ticket turns out to have leaked.
        return await db.UploadTickets
            .Where(t => t.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string HashPublic(string value) => Hash(value);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
