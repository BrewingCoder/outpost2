namespace OutPost2.Core.Files;

/// <summary>
/// Permission to put exactly one file into a user's staging area, exactly once (OP2-252).
/// <para>
/// <c>send_mail_with_attachments</c> takes the names of staged files, and until now the only
/// way to stage one was a human uploading it through a browser. So an agent that had just
/// produced a report — and could read it, and knew its absolute path — had to stop and hand
/// the job back for the one part of it that is pure mechanics.
/// </para>
/// <para>
/// A ticket rather than a path, because this server does not share a filesystem with
/// whatever is calling it: it runs in a container with nothing but <c>/app</c> mounted, and
/// a caller may be on another machine entirely. And a ticket rather than a pre-signed S3
/// URL, because the object store sits on an internal network the caller cannot reach, while
/// this server is by definition reachable — the caller is already talking to it.
/// </para>
/// <para>
/// It is a bearer credential on an endpoint that needs no sign-in, so it is deliberately
/// narrow: one use, minutes rather than hours, a single user, and a size agreed in advance.
/// Modelled on <c>PreviewLink</c>, which solved the same problem for reading.
/// </para>
/// </summary>
public sealed class UploadTicket
{
    /// <summary>SHA-256 of the token. The plaintext exists once, in the response that mints it.</summary>
    public required string TokenHash { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>
    /// What the staged file will be called, agreed when the ticket is minted rather than
    /// taken from the upload. The caller was told this name, so it is the name it will use
    /// to attach the file — deriving one from a filename later would make the caller guess.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The largest body that may be redeemed against this ticket. Checked while reading, not
    /// from <c>Content-Length</c>, which the sender controls.
    /// </summary>
    public required long MaxBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// When it was spent. Set before the body is read, so a failed or abandoned upload
    /// cannot be retried against the same ticket — a new one costs a tool call and nothing
    /// else, and a ticket that can be replayed is a ticket somebody else can replay.
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Recorded on use, so an unexpected origin is visible afterwards.</summary>
    public string? UsedFrom { get; set; }

    /// <summary>The staged file it produced, once it has.</summary>
    public Guid? StagedFileId { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    /// <summary>
    /// How long a ticket lives. Long enough to read a file off a disk and push it, short
    /// enough that one left in a transcript is no use to anybody later.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many unspent tickets one user may hold. A bound on how much unauthenticated
    /// write surface can be opened at once, and far above any honest use.
    /// </summary>
    public const int MaxOutstanding = 10;
}
