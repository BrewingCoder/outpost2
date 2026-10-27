namespace OutPost2.Core.Auth;

/// <summary>
/// An MCP client that registered itself (RFC 7591). Claude registers dynamically the
/// first time a user adds the connector, so we cannot pre-provision these.
/// </summary>
public sealed class OAuthClient
{
    public required string ClientId { get; init; }
    public required string ClientName { get; set; }

    /// <summary>Newline-separated. Redemption must match one EXACTLY — a prefix match here is an open redirect.</summary>
    public required string RedirectUris { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Redirects() =>
        RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Short-lived authorization code, PKCE-bound.</summary>
public sealed class OAuthAuthCode
{
    public required string Code { get; init; }
    public required string ClientId { get; init; }
    public required Guid UserId { get; init; }
    public required string RedirectUri { get; init; }

    /// <summary>S256 challenge. Required — a public client without PKCE is interceptable.</summary>
    public required string CodeChallenge { get; init; }

    /// <summary>
    /// Connections this grant may reach, newline-separated, or empty for all of them.
    /// Lets a phone connector be limited to "home" while leaving the employer's mailbox
    /// out of reach — the honest answer to "do I want work mail on my personal assistant".
    /// </summary>
    public string ConnectionScope { get; set; } = "";

    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;
}

/// <summary>
/// The access token we issue to an MCP client — "type 3" in OP2-5, and the most powerful
/// object in the system: it transitively unlocks every connection the user owns, and it
/// lives on someone else's infrastructure.
/// <para>
/// Stored as a SHA-256 hash, never in the clear: a database leak must not hand an attacker
/// working credentials to every user's mail.
/// </para>
/// </summary>
public sealed class OAuthToken
{
    public required string TokenHash { get; init; }
    public required Guid UserId { get; init; }
    public required string ClientId { get; init; }

    /// <summary>Opaque refresh token hash, so a long-lived session never needs a long-lived access token.</summary>
    public string? RefreshTokenHash { get; set; }

    /// <summary>Empty means every connection. Otherwise newline-separated labels.</summary>
    public string ConnectionScope { get; set; } = "";

    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set by "disconnect Claude". Checked on every call, so revocation is immediate.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Shown in the UI so a user can tell two connectors apart before revoking one.</summary>
    public string? Label { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public IReadOnlyList<string> Connections() =>
        ConnectionScope.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when this token may act on the named connection.</summary>
    public bool Allows(string connectionLabel)
    {
        var scope = Connections();
        return scope.Count == 0 ||
               scope.Any(s => string.Equals(s, connectionLabel, StringComparison.OrdinalIgnoreCase));
    }
}
