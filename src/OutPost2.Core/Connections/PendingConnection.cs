namespace OutPost2.Core.Connections;

/// <summary>
/// Context carried across an OAuth round trip, keyed by the <c>state</c> parameter.
/// <para>
/// Entra string-matches redirect URIs, so a varying <c>?connection=work</c> would be
/// rejected and registering one URI per connection would mean editing the app registration
/// for every new tenant. <c>state</c> is the mechanism designed for this, and it is needed
/// for CSRF protection regardless — so one callback URI serves N tenants forever.
/// </para>
/// </summary>
public sealed class PendingConnection
{
    /// <summary>The opaque <c>state</c> value. Unguessable, single-use, short-lived.</summary>
    public required string State { get; init; }

    /// <summary>
    /// Who started this. The callback must verify the returned state belongs to the user
    /// who is signed in now — otherwise a leaked state could attach someone else's mailbox
    /// to an attacker's account.
    /// </summary>
    public required Guid UserId { get; init; }

    public required ProviderKind Provider { get; init; }

    /// <summary>What the user wants to call it: "work", "home".</summary>
    public required string Label { get; init; }

    /// <summary>
    /// PKCE verifier. Protects the authorization code even if it leaks in transit.
    /// <para>
    /// Settable because MSAL generates the pair itself when building the authorize URL,
    /// and the verifier it produces is the one Entra will expect back at redemption —
    /// ours would be rejected.
    /// </para>
    /// </summary>
    public required string CodeVerifier { get; set; }

    /// <summary>Optional tenant hint, so a user can target a specific directory.</summary>
    public string? TenantHint { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Set the moment it is redeemed. A state replayed after use must be refused.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;
}
