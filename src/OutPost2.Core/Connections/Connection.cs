namespace OutPost2.Core.Connections;

/// <summary>
/// An authorised link to ONE instance of ONE provider, owned by ONE product user.
/// <para>
/// Deliberately not "a mailbox" and not "a Microsoft tenant" — Microsoft (OAuth) and
/// YouTrack (PAT) are two providers under this one model, and further providers must
/// plug in without re-solving tenancy, consent or token custody. See OP2-2.
/// </para>
/// </summary>
public sealed class Connection
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Owning product user (GitHub-authenticated). Every call is scoped by this.</summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// User-chosen name: "work", "home", "lab YouTrack". Unique PER USER, not globally —
    /// my "work" is not your "work". This is what natural language resolves against.
    /// </summary>
    public required string Label { get; set; }

    public required ProviderKind Provider { get; init; }

    /// <summary>Entra tenant id, or a base URL for self-hosted providers.</summary>
    public required string Instance { get; init; }

    public required AuthKind AuthKind { get; init; }

    /// <summary>
    /// Opaque handle into the encrypted credential store. Never the credential itself —
    /// credentials are envelope-encrypted with a Vault Transit DEK (OP2-5, OP2-15).
    /// </summary>
    public required string CredentialRef { get; set; }

    /// <summary>
    /// For Microsoft connections: MSAL's <c>home_account_id</c> (<c>&lt;oid&gt;.&lt;tid&gt;</c>),
    /// so the right cached account can be resolved. Null for PAT providers.
    /// </summary>
    public string? HomeAccountId { get; set; }

    public ConnectionState State { get; set; } = ConnectionState.Unconnected;

    /// <summary>
    /// Granted vs enabled are DIFFERENT (OP2-4): granted costs an auth round trip and may
    /// need admin approval; enabled is an instant local toggle. Capability is an attribute
    /// of the connection, never of global config — work may refuse what home grants.
    /// </summary>
    public List<Capability> Capabilities { get; init; } = [];

    /// <summary>
    /// Defaults are PER PROVIDER. "check email" and "what are my open tickets" resolve
    /// independently, so a single is-default flag is wrong.
    /// </summary>
    public bool IsDefaultForProvider { get; set; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Last successful health probe. PAT providers give no expiry warning, so health is polled.</summary>
    public DateTimeOffset? LastHealthyAt { get; set; }
}

public enum ProviderKind
{
    Microsoft,
    YouTrack,
}

public enum AuthKind
{
    /// <summary>Auth-code + PKCE against a confidential client. Refresh tokens auto-renew.</summary>
    OAuth2,

    /// <summary>A pasted personal access token. Static, no refresh, no advance expiry warning.</summary>
    PersonalAccessToken,
}

/// <summary>
/// Lifecycle of a connection. Each state needs its own user-facing next action —
/// and a speakable form, because the user may hit it mid-conversation while driving.
/// </summary>
public enum ConnectionState
{
    Unconnected,

    /// <summary>User consented; usable.</summary>
    Consented,

    /// <summary>
    /// Tenant requires admin approval. Durable — a user cannot make their admin act, so this
    /// may persist for days and must resume without the user redoing anything (OP2-4).
    /// </summary>
    NeedsAdminApproval,

    Active,

    /// <summary>Refresh failed or the PAT expired. Needs a browser, so it cannot be fixed by voice.</summary>
    Expired,

    /// <summary>Admin deleted the service principal, or the PAT was revoked upstream.</summary>
    Revoked,
}
