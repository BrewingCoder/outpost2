namespace OutPost2.Core.Connections;

/// <summary>How much damage a capability can do if it is on when you did not expect it.</summary>
public enum CapabilityRisk
{
    /// <summary>Reads. Being wrong costs privacy, not reputation.</summary>
    None,

    /// <summary>Changes something other people can see.</summary>
    Writes,

    /// <summary>Produces something under the user's own name, which cannot be taken back.</summary>
    ActsAsYou,

    /// <summary>Administrative. Creates, deletes or reconfigures.</summary>
    High,
}

/// <summary>
/// The user-facing truth about each capability, in one place.
/// <para>
/// Risk is DERIVED from the capability name rather than stored on the row. It is a property
/// of what the capability is, not of one connection's copy of it, and storing it per row
/// would let two connections disagree about whether sending mail acts as you.
/// </para>
/// </summary>
public static class CapabilityCatalog
{
    private sealed record Entry(string Verb, CapabilityRisk Risk);

    private static readonly Dictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [Capabilities.MailRead]    = new("Read your email", CapabilityRisk.None),
        [Capabilities.MailWrite]   = new("File, flag and delete your email", CapabilityRisk.Writes),
        [Capabilities.MailSend]    = new("Send email as you", CapabilityRisk.ActsAsYou),
        [Capabilities.CalendarRead]  = new("Read your calendar", CapabilityRisk.None),

        // ActsAsYou, not Writes. Creating, moving or cancelling a meeting sends mail to
        // every attendee under the user's own name, and a cancellation cannot be recalled
        // any more than a sent message can.
        [Capabilities.CalendarWrite] = new("Create, change and cancel meetings as you", CapabilityRisk.ActsAsYou),

        [Capabilities.PeopleRead]    = new("Look people up and read your contacts", CapabilityRisk.None),
        [Capabilities.ContactsWrite] = new("Add and change your contacts", CapabilityRisk.Writes),

        [Capabilities.IssuesRead]  = new("Read issues", CapabilityRisk.None),
        [Capabilities.IssuesWrite] = new("Create and update issues", CapabilityRisk.Writes),
        [Capabilities.IssuesAdmin] = new("Administer projects", CapabilityRisk.High),
        [Capabilities.CodeRead]    = new("Read code, pull requests and issues", CapabilityRisk.None),
        [Capabilities.CodeWrite]   = new("Comment, review and push", CapabilityRisk.ActsAsYou),
    };

    /// <summary>A plain verb phrase. Falls back to the scope code rather than inventing one.</summary>
    public static string Verb(string capability) =>
        Map.TryGetValue(capability, out var e) ? e.Verb : capability;

    public static CapabilityRisk Risk(string capability) =>
        Map.TryGetValue(capability, out var e) ? e.Risk : CapabilityRisk.None;

    /// <summary>The badge text, or null where there is nothing to warn about.</summary>
    public static string? RiskLabel(string capability) => Risk(capability) switch
    {
        CapabilityRisk.Writes => "writes",
        CapabilityRisk.ActsAsYou => "acts as you",
        CapabilityRisk.High => "high risk",
        _ => null,
    };

    /// <summary>
    /// What the user would SAY to reach a connection of this provider. The label is a
    /// spoken command, so the UI has to be able to show the whole sentence — and it lives
    /// here so the wording can be changed in one edit rather than seven templates.
    /// </summary>
    public static string SpokenExample(ProviderKind provider, string label) => provider switch
    {
        ProviderKind.Microsoft => $"check {label} email",
        ProviderKind.YouTrack => $"what's open in {label}",
        ProviderKind.GitHub => $"what pull requests am I blocking on {label}",
        _ => label,
    };

    /// <summary>The group heading each provider appears under.</summary>
    public static string ProviderHeading(ProviderKind provider) => provider switch
    {
        ProviderKind.Microsoft => "Microsoft mail",
        ProviderKind.YouTrack => "YouTrack issue trackers",
        ProviderKind.GitHub => "GitHub pull requests and issues",
        _ => provider.ToString(),
    };

    /// <summary>
    /// Why this provider has no default, in the user's terms. Shown instead of a blank,
    /// because the absence of a default is a decision and has to read like one.
    /// </summary>
    public static string NoDefaultReason(ProviderKind provider) => provider switch
    {
        ProviderKind.YouTrack =>
            "Chosen on purpose. One tracker per client: answering about the wrong client's "
            + "tickets is worse than being asked.",
        ProviderKind.GitHub =>
            "Chosen on purpose. With one account Claude still confirms it, so a second "
            + "account added later never gets used by accident.",
        _ =>
            "Claude will ask which connection to use until you nominate one.",
    };
}
