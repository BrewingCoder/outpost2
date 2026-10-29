namespace OutPost2.Core.Users;

/// <summary>
/// A product user, identified by GitHub. Access is ASSIGNED, not open signup —
/// the endpoint is public, the allowlist says no to almost everyone (OP2-3).
/// <para>
/// This is the PRODUCT identity layer only. It confers no provider grant whatsoever;
/// what data a request can reach is decided entirely by that user's Connections.
/// Collapsing the two is what makes "allowed in, but work needs admin approval"
/// inexpressible — and that is the common day-one state.
/// </para>
/// </summary>
public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// GitHub's NUMERIC user id. Stable. The allowlist keys on this, never on the login,
    /// because a login can be changed by its owner at any time.
    /// </summary>
    public required long GitHubUserId { get; init; }

    /// <summary>Display only. Refreshed on sign-in; never used for authorisation.</summary>
    public required string GitHubLogin { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Where to reach this user when something happens while they are not looking
    /// (OP2-165). Null means no channel, which is the ordinary state and not an error.
    /// <para>
    /// A chat id rather than a username: a username can be changed by its owner, and a
    /// notification quietly going to whoever took the name next is worse than one not
    /// arriving. Stored unverified on purpose — Telegram offers no way to check a chat
    /// id short of sending to it, so the settings page sends a test message and the user
    /// looks at their phone, which is a better check than any field validation.
    /// </para>
    /// </summary>
    public string? TelegramChatId { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSignInAt { get; set; }
}
