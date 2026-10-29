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

    // Separate from both of its neighbours on purpose. Filing, flagging and deleting change
    // the mailbox but are invisible outside it, so gating them behind mail.send would make
    // "tidy my inbox" require standing permission to write as the user — and gating them
    // behind mail.read would mean switching off deletion also switches off reading.
    public const string MailWrite = "mail.write";

    // Calendar is separate from mail even though one Graph scope (Calendars.ReadWrite)
    // grants both halves of it. The capability is what the USER switches off, and
    // "read my mail but stay out of my diary" is a sentence people mean.
    public const string CalendarRead = "calendar.read";

    // Write covers creating, amending, cancelling and replying to invitations. All four
    // send mail to other people from the user's address, which is why none of them sits
    // under calendar.read and why there is no finer split: a cancellation that notifies
    // eight attendees is not meaningfully safer than an invitation that does.
    public const string CalendarWrite = "calendar.write";

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
