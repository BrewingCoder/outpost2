namespace OutPost2.Core.Auth;

/// <summary>
/// A time-boxed, read-only way to let something that cannot sign in with GitHub — a design
/// tool, a reviewer, a screenshot bot — see the app's pages.
/// <para>
/// A URL-borne credential is normally a bad idea: it lands in browser history, in
/// <c>Referer</c> headers, in proxy and CDN logs, and in whatever pasted it. Three things
/// make this one defensible, and all three are load-bearing.
/// </para>
/// <list type="number">
/// <item>It is spent on arrival. The redeem endpoint sets a session cookie and immediately
/// redirects to a clean URL, so the token is in the address bar for one hop.</item>
/// <item>It is read-only, enforced at the pipeline rather than by hiding buttons.</item>
/// <item>It is short-lived and revocable, so a leak has a deadline even unnoticed.</item>
/// </list>
/// <para>
/// What it is NOT is anonymous: a preview session acts as the user who minted it, and the
/// pages it can read show that user's connection labels and account addresses. It is a
/// loaded gun pointed somewhere fairly safe, not an unloaded one.
/// </para>
/// </summary>
public sealed class PreviewLink
{
    /// <summary>SHA-256 of the token. The plaintext exists once, in the response that mints it.</summary>
    public required string TokenHash { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>What it is for — "Claude design" — so the list is readable before revoking.</summary>
    public required string Label { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// First redemption. Kept distinct from <see cref="LastUsedAt"/> because "this link has
    /// been opened by someone" is the signal that matters if it was shared by mistake.
    /// </summary>
    public DateTimeOffset? FirstUsedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
    public int UseCount { get; set; }

    /// <summary>Recorded on first use, so an unexpected origin is visible afterwards.</summary>
    public string? FirstUsedFrom { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// The longest a preview may live. A design pass is measured in hours, and anything
    /// that genuinely needs standing access should be a GitHub account on the allowlist.
    /// </summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);
}
