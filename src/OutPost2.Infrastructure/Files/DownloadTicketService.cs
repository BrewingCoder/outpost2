using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Files;

/// <summary>What one ticket is for, before it exists.</summary>
public sealed record DownloadRequest(string AttachmentId, string FileName, long SizeBytes);

public interface IDownloadTicketService
{
    /// <summary>
    /// Mints one ticket per attachment, all or none. The plaintext tokens are returned once,
    /// in the same order, and never stored.
    /// </summary>
    Task<IReadOnlyList<(DownloadTicket Ticket, string Token)>> MintAsync(
        Guid userId, Guid connectionId, string messageId, IReadOnlyList<DownloadRequest> wanted,
        CancellationToken ct = default);

    /// <summary>
    /// Spends a ticket, returning it, or null when the token is unknown, already spent or
    /// stale. Claimed before anything is fetched, so a failed download cannot be replayed.
    /// </summary>
    Task<DownloadTicket?> RedeemAsync(string token, string? from, CancellationToken ct = default);

    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// One-use links to an attachment's original bytes (OP2-265). The same rules as
/// <see cref="UploadTicketService"/>, enforced here rather than at the endpoint.
/// </summary>
public sealed class DownloadTicketService(OutPostDbContext db, ILogger<DownloadTicketService> log)
    : IDownloadTicketService
{
    public async Task<IReadOnlyList<(DownloadTicket Ticket, string Token)>> MintAsync(
        Guid userId, Guid connectionId, string messageId, IReadOnlyList<DownloadRequest> wanted,
        CancellationToken ct = default)
    {
        if (wanted.Count == 0) return [];

        var now = DateTimeOffset.UtcNow;

        var dead = await db.DownloadTickets
            .Where(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);

        var outstanding = await db.DownloadTickets
            .CountAsync(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt > now, ct);

        if (wanted.Count > DownloadTicket.MaxOutstanding)
            throw new TooManyTicketsException(
                $"That message has {wanted.Count} attachments to save and I can only hand out "
                + $"{DownloadTicket.MaxOutstanding} links at a time. Ask for them a few at a "
                + "time by number instead of all at once.");

        if (outstanding + wanted.Count > DownloadTicket.MaxOutstanding)
            throw new TooManyTicketsException(
                $"You already have {outstanding} download link(s) waiting to be used, and this "
                + $"would make {outstanding + wanted.Count}; the limit is {DownloadTicket.MaxOutstanding}. "
                + "Use or abandon those first — they expire in "
                + $"{(int)DownloadTicket.Lifetime.TotalMinutes} minutes.");

        var minted = new List<(DownloadTicket, string)>(wanted.Count);
        foreach (var w in wanted)
        {
            // 256 bits: the token is the only thing between a guess and someone's mail.
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var ticket = new DownloadTicket
            {
                TokenHash = Hash(token),
                UserId = userId,
                ConnectionId = connectionId,
                MessageId = messageId,
                AttachmentId = w.AttachmentId,
                FileName = w.FileName,
                SizeBytes = w.SizeBytes,
                ExpiresAt = now.Add(DownloadTicket.Lifetime),
            };
            db.DownloadTickets.Add(ticket);
            minted.Add((ticket, token));
        }

        await db.SaveChangesAsync(ct);

        // Names and sizes, never tokens.
        log.LogInformation(
            "Minted {Count} download ticket(s) for {User}: {Names}; swept {Dead} stale.",
            minted.Count, userId, string.Join(", ", wanted.Select(w => w.FileName)), dead);

        return minted;
    }

    public async Task<DownloadTicket?> RedeemAsync(
        string token, string? from, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Hash(token.Trim());
        var now = DateTimeOffset.UtcNow;

        // A conditional update, so two requests racing the same token cannot both pass.
        var claimed = await db.DownloadTickets
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.UsedAt, now)
                .SetProperty(t => t.UsedFrom, from), ct);

        if (claimed == 0)
        {
            log.LogWarning("A download ticket was refused: unknown, already used or expired.");
            return null;
        }

        return await db.DownloadTickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        // Spent ones are kept a day, so "who fetched that attachment, from where" can be
        // answered if a link turns out to have leaked.
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        return await db.DownloadTickets.Where(t => t.ExpiresAt <= cutoff).ExecuteDeleteAsync(ct);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
