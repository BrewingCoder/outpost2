namespace OutPost2.Core.People;

/// <summary>
/// Somebody the user could address a message or an invitation to, from whichever of the
/// three sources knew about them.
/// </summary>
public sealed record Person
{
    /// <summary>Graph's id in whichever collection this came from. Null for a directory
    /// hit we only matched by address.</summary>
    public string? Id { get; init; }

    public required string ConnectionLabel { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Best address. Empty is possible and must be handled: a directory entry for a room
    /// or a shared mailbox can have no mail attribute at all, and offering it as a
    /// recipient would produce a send that bounces.
    /// </summary>
    public required string Address { get; init; }

    /// <summary>Everything else known, so "the other Justin" can be distinguished.</summary>
    public IReadOnlyList<string> OtherAddresses { get; init; } = [];

    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? Company { get; init; }
    public string? Phone { get; init; }

    public required PersonSource Source { get; init; }

    /// <summary>
    /// True when this is a distribution list or group rather than a human. Graph's People
    /// API returns both from the same endpoint, and mailing a department by accident when
    /// a person was meant is the failure worth preventing.
    /// </summary>
    public bool IsGroup { get; init; }

    /// <summary>
    /// Graph's own relevance score where there is one — how much the user deals with this
    /// person. Used only to order, never shown: a number out of context means nothing
    /// read aloud.
    /// </summary>
    public double Relevance { get; init; }
}

/// <summary>
/// Which collection a person came out of. Shown to the user, because "in your contacts"
/// and "in the company directory" answer different questions about who somebody is.
/// </summary>
public enum PersonSource
{
    /// <summary>Graph's relevance ranking — people the user actually corresponds with.</summary>
    Relevant,

    /// <summary>The organisation's directory.</summary>
    Directory,

    /// <summary>The user's own address book.</summary>
    Contact,
}

/// <summary>A personal contact, which unlike a directory entry the user owns and can edit.</summary>
public sealed record Contact
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public string? MobilePhone { get; init; }
    public IReadOnlyList<string> BusinessPhones { get; init; } = [];
    public string? Company { get; init; }
    public string? JobTitle { get; init; }
    public string? Notes { get; init; }

    public string? PrimaryAddress => Addresses.Count > 0 ? Addresses[0] : null;
}

/// <summary>
/// A new contact, or a change to one. Every field on a change is nullable and only what
/// is set gets sent, for the same reason as <c>EventChanges</c>: a PATCH echoing back
/// unchanged values clobbers whatever was edited elsewhere in between.
/// </summary>
public sealed record ContactDetails
{
    public string? Name { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public IReadOnlyList<string>? Addresses { get; init; }
    public string? MobilePhone { get; init; }
    public string? Company { get; init; }
    public string? JobTitle { get; init; }
    public string? Notes { get; init; }

    public bool IsEmpty =>
        Name is null && GivenName is null && Surname is null && Addresses is null
        && MobilePhone is null && Company is null && JobTitle is null && Notes is null;
}
