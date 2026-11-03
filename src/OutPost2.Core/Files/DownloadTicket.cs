namespace OutPost2.Core.Files;

/// <summary>
/// Permission to fetch one mail attachment's original bytes, exactly once (OP2-265).
/// <para>
/// The inbound mirror of <see cref="UploadTicket"/>. <c>read_attachment</c> returns extracted
/// text, which is no substitute when the file itself is the record: it drops a PDF's
/// images and layout, a spreadsheet's formulas and sheets, and has nothing at all to say
/// about an image or an archive. Nothing wrote the original anywhere.
/// </para>
/// <para>
/// A ticket rather than a path for the same reason uploads use one: this server shares no
/// filesystem with whatever is calling it, so it cannot write into the caller's directory.
/// It hands out a one-use link and the caller fetches the bytes itself. The bytes are read
/// from Graph at the moment the link is used, so nothing is copied into staging and nothing
/// counts against the staging quota.
/// </para>
/// <para>
/// It is a bearer credential for the content of somebody's mailbox on an endpoint with no
/// sign-in, so it is as narrow as the upload ticket: one use, fifteen minutes, one user,
/// one attachment, and a cap on how many can be outstanding.
/// </para>
/// </summary>
public sealed class DownloadTicket
{
    /// <summary>SHA-256 of the token. The plaintext exists once, in the response that mints it.</summary>
    public required string TokenHash { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>The mailbox it reads from. Re-checked on use: a connection can be removed or lose its read permission inside fifteen minutes.</summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>Graph's message id. Long and opaque; never shown to anybody.</summary>
    public required string MessageId { get; init; }

    /// <summary>Graph's attachment id. Long and opaque; never shown to anybody.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>
    /// The name the file is served under — already made safe by <see cref="SafeFileName"/>,
    /// because the original is whatever the sender chose to call it.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>What Graph reported, for the log. The size Graph gives is approximate, so it is never checked against the bytes.</summary>
    public required long SizeBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When it was spent. Claimed before Graph is asked, so a failed download cannot be replayed.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Recorded on use, so an unexpected origin is visible afterwards.</summary>
    public string? UsedFrom { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many unspent tickets one user may hold. Higher than uploads because one call can
    /// ask for every attachment on a message, and a message with a dozen inline images is
    /// ordinary; still a hard bound on how much unauthenticated read surface is open.
    /// </summary>
    public const int MaxOutstanding = 50;
}
