namespace OutPost2.Core.Connections;

/// <summary>
/// One capability on one connection, carrying BOTH halves of the granted/enabled
/// distinction (OP2-4). Collapsing these into a single boolean is the classic mistake:
/// an "off" switch that silently means "we never asked" is a different thing from one
/// that means "granted, but we choose not to use it".
/// </summary>
public sealed class Capability
{
    public required string Name { get; init; }

    /// <summary>
    /// The provider has consented to the underlying scope. Changing this costs an auth
    /// round trip and may need admin approval again.
    /// </summary>
    public bool Granted { get; set; }

    /// <summary>
    /// We will actually use it. Instant local toggle. Granted + !Enabled is legitimate —
    /// but the UI must not imply we revoked the grant, because we did not.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// True where we cannot technically constrain ourselves to this capability — notably
    /// PAT providers, where whatever the user pasted is what we hold. The toggle is then
    /// our own restraint, not an enforced boundary, and the UI must say so honestly.
    /// </summary>
    public bool IsSelfImposedOnly { get; init; }

    public bool Usable => Granted && Enabled;
}

/// <summary>Well-known capability names. Admin is deliberately separate from read/write (OP2-12).</summary>
public static class Capabilities
{
    public const string MailRead = "mail.read";
    public const string MailSend = "mail.send";

    public const string IssuesRead = "issues.read";
    public const string IssuesWrite = "issues.write";
    public const string IssuesAdmin = "issues.admin";

    // Deliberately NOT issues.* even though a GitHub issue is an issue. Capability names
    // are how the resolver decides what can serve a request, so sharing a name with
    // YouTrack would let a GitHub connection be selected for a YouTrack tool and then be
    // called with the wrong API entirely.
    public const string CodeRead = "code.read";
    public const string CodeWrite = "code.write";
}
