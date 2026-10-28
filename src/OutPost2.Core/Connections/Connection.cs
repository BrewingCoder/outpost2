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
    /// The envelope-encrypted credential itself, stored inline as JSON (OP2-5, OP2-15):
    /// a Vault-wrapped data key plus AES-GCM ciphertext, nonce and tag.
    /// <para>
    /// Unbounded by design. A YouTrack PAT seals to a few hundred bytes, but an MSAL token
    /// cache runs to kilobytes — an earlier varchar(200) cap silently worked in unit tests
    /// and failed against real data.
    /// </para>
    /// </summary>
    public required string SealedCredential { get; set; }

    /// <summary>
    /// For Microsoft connections: MSAL's <c>home_account_id</c> (<c>&lt;oid&gt;.&lt;tid&gt;</c>),
    /// so the right cached account can be resolved. Null for PAT providers.
    /// </summary>
    public string? HomeAccountId { get; set; }

    /// <summary>
    /// The signed-in account as a human reads it — a UPN or login, not a GUID.
    /// <para>
    /// Showing only the tenant id hid a real mistake: a PERSONAL Microsoft account can be
    /// registered under a work email address, so the account picker offers two entries
    /// that look identical. Without this, a connection to the wrong one is invisible.
    /// </para>
    /// </summary>
    /// <summary>
    /// When the stored credential stops working, where the provider says so. GitHub
    /// returns it on every response; YouTrack permanent tokens genuinely never expire;
    /// Microsoft refresh is opaque and renews itself. Null therefore means "not
    /// applicable or not known", and the UI must say which rather than imply a date.
    /// </summary>
    public DateTimeOffset? CredentialExpiresAt { get; set; }

    public string? AccountIdentifier { get; set; }

    /// <summary>
    /// True when this is a consumer Microsoft account rather than an organisational one.
    /// Worth surfacing: the mailbox behind it is a different mailbox entirely, even when
    /// the address is the same.
    /// </summary>
    public bool IsPersonalAccount { get; set; }

    /// <summary>Microsoft's well-known tenant id for consumer accounts.</summary>
    public const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

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
    GitHub,
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
