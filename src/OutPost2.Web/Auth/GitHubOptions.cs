namespace OutPost2.Web.Auth;

/// <summary>
/// GitHub is the PRODUCT identity layer only (OP2-3): it decides who may use OutPost2 at
/// all. It confers no Microsoft or YouTrack grant whatsoever — what data a request can
/// reach is decided entirely by that user's Connections.
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "OutPost2:GitHub";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Comma-separated GitHub <b>numeric</b> user ids permitted to sign in.
    /// <para>
    /// Numeric, never logins: a GitHub login can be changed by its owner at any time, and a
    /// freed-up login can then be claimed by someone else. Keying authorisation on a mutable,
    /// re-claimable string is how an allowlist silently lets in a stranger.
    /// </para>
    /// </summary>
    public string AllowedUserIds { get; set; } = "";

    public IReadOnlySet<long> ParseAllowedUserIds() =>
        AllowedUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
