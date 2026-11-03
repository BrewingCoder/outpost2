namespace OutPost2.Core.Mail;

/// <summary>One message in a list. Carries its connection label so a fan-out result
/// can always say WHICH mailbox an item came from — unlabelled merged results are worse
/// than no merge at all.</summary>
public sealed record MailSummary
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required bool IsRead { get; init; }
    public required bool HasAttachments { get; init; }
    public required string Preview { get; init; }

    /// <summary>
    /// Which folder it is in, when the provider said. Set on search results so a listing
    /// can show that a hit is already filed — a clean-up pass could not otherwise tell
    /// what was still in the Inbox from what it had just moved out (OP2-196).
    /// </summary>
    public string? FolderId { get; init; }
}

/// <summary>
/// A page of search results, and whether there are more behind it (OP2-189).
/// <para>
/// The flag is the point. Returning the first fifty of two hundred and saying nothing
/// reads as "that is all of them", and a bulk delete built on that belief leaves most of
/// the mail in place while reporting success.
/// </para>
/// </summary>
public sealed record MailSearchResult
{
    public required IReadOnlyList<MailSummary> Items { get; init; }
    public bool More { get; init; }

    /// <summary>
    /// The provider's own opaque link to what comes next, when it gave one and we stopped
    /// before using it. Never shown to a caller — it is a URL we would fetch with the
    /// user's token — but handed back later to carry on from (OP2-208).
    /// </summary>
    public string? Continuation { get; init; }
}

public sealed record MailMessage
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required IReadOnlyList<string> To { get; init; }
    public required IReadOnlyList<string> Cc { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// When the sender's client says it was sent — the <c>Date</c> header. Kept apart from
    /// <see cref="ReceivedAt"/> because for a deadline those are different facts (OP2-264).
    /// </summary>
    public DateTimeOffset? SentAt { get; init; }

    public required bool IsRead { get; init; }
    public required bool HasAttachments { get; init; }

    /// <summary>Clean text, converted from HTML and with quoted history already removed.</summary>
    public required string Body { get; init; }

    /// <summary>True when Graph gave us uniqueBody, so the reply chain is genuinely absent.</summary>
    public required bool QuotedHistoryStripped { get; init; }
}

/// <summary>A mail folder, as a destination for filing.</summary>
public sealed record MailFolder
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The Graph well-known name (<c>archive</c>, <c>deleteditems</c>) where there is one.</summary>
    public string? WellKnownName { get; init; }

    /// <summary>Qualified with the parent for display, so two "Archive" folders are distinguishable.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// How much is in it. Carried so that deleting a folder can say what would go with it —
    /// a folder name misheard on a phone must not take four hundred messages quietly
    /// (OP2-188).
    /// </summary>
    public int MessageCount { get; init; }

    public int ChildCount { get; init; }

    /// <summary>
    /// True for the folders Outlook itself owns. They are not ours to rename or delete:
    /// Graph refuses some outright and allows others, and a renamed Inbox is a mailbox the
    /// user has to repair by hand.
    /// </summary>
    public bool IsWellKnown => !string.IsNullOrEmpty(WellKnownName);
}

/// <summary>An Outlook colour category, as defined on the mailbox rather than on a message.</summary>
public sealed record MailCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Graph's preset code (<c>preset0</c>…<c>preset24</c>), or <c>none</c>.</summary>
    public required string Preset { get; init; }

    /// <summary>The colour a human would say, derived from the preset.</summary>
    public string Colour => CategoryColours.Describe(Preset);
}

/// <summary>
/// Graph names category colours <c>preset0</c>…<c>preset24</c> and the API never says what
/// they look like, so "preset7" is unspeakable and unsettable by anyone who has not
/// memorised the table. Transcribed from the <c>categoryColor</c> table on the
/// outlookCategory resource page.
/// <para>
/// Microsoft qualifies that table as the mapping for the Outlook DESKTOP client, so a
/// colour named here is what Outlook on Windows draws, not a guarantee about every client.
/// </para>
/// <para>
/// A category's name and its colour are independent, and the name is read-only once
/// created — a real mailbox here holds "Purple Category" coloured <c>preset9</c>, which is
/// Cranberry. Reporting the colour as the name would be the easy mistake and the wrong one.
/// </para>
/// </summary>
public static class CategoryColours
{
    private static readonly string[] Names =
    [
        "red", "orange", "brown", "yellow", "green", "teal", "olive", "blue", "purple",
        "cranberry", "steel", "dark steel", "grey", "dark grey", "black", "dark red",
        "dark orange", "dark brown", "dark yellow", "dark green", "dark teal", "dark olive",
        "dark blue", "dark purple", "dark cranberry",
    ];

    public static string Describe(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "no colour";

        return preset.StartsWith("preset", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(preset.AsSpan(6), out var i) && i >= 0 && i < Names.Length
            ? Names[i]
            : preset;
    }

    /// <summary>The preset code for a spoken colour, or null when it is not one of Outlook's.</summary>
    public static string? Preset(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour)) return null;
        var said = colour.Trim();

        if (said.StartsWith("preset", StringComparison.OrdinalIgnoreCase)) return said.ToLowerInvariant();
        if (said.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";

        var i = Array.FindIndex(Names, n => n.Equals(said, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : $"preset{i}";
    }

    public static IReadOnlyList<string> All => Names;
}

/// <summary>Which of Graph's three reply shapes to build. They differ only in recipients.</summary>
public enum ReplyKind
{
    Sender,
    All,
    Forward,
}

/// <summary>One person on a message, kept structured rather than formatted.</summary>
public sealed record MailRecipient(string? Name, string Address)
{
    /// <summary>
    /// A name alone is ambiguous and an address alone is unfriendly. This is also the form
    /// a caller is expected to name a recipient back in, so it has to round-trip.
    /// </summary>
    public string Display =>
        string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

/// <summary>
/// Changes to the recipients Graph put on a reply draft (OP2-187).
/// <para>
/// Additive and subtractive rather than a replacement list, deliberately. A caller handed
/// a bare <c>to</c> override would silently drop everyone it forgot to name, and nobody
/// notices a participant missing from a thread until the decision has been taken without
/// them.
/// </para>
/// </summary>
public sealed record RecipientEdit
{
    public IReadOnlyList<string> AddTo { get; init; } = [];
    public IReadOnlyList<string> AddCc { get; init; } = [];

    /// <summary>
    /// Addresses or display names to take off. Matched exactly: a partial match on a send
    /// is how the wrong person gets removed.
    /// </summary>
    public IReadOnlyList<string> Drop { get; init; } = [];

    public bool Any => AddTo.Count > 0 || AddCc.Count > 0 || Drop.Count > 0;
}

/// <summary>The recipient lists a reply will actually go out with, and what it cost.</summary>
public sealed record RecipientPlan
{
    public required IReadOnlyList<MailRecipient> To { get; init; }
    public required IReadOnlyList<MailRecipient> Cc { get; init; }

    /// <summary>Who was taken off, for reading back.</summary>
    public IReadOnlyList<MailRecipient> Dropped { get; init; } = [];

    /// <summary>
    /// Drop terms that matched nobody. Non-empty means the send must be refused: the usual
    /// reason to drop someone is that their address is wrong, and a drop that quietly
    /// matched nothing sends to the wrong address anyway — the one outcome the caller was
    /// trying to avoid.
    /// </summary>
    public IReadOnlyList<string> Unmatched { get; init; } = [];
}

/// <summary>What a reply or forward actually went out as, so the caller can read it back.</summary>
public sealed record ReplySent
{
    public required string Subject { get; init; }
    public required IReadOnlyList<MailRecipient> To { get; init; }
    public IReadOnlyList<MailRecipient> Cc { get; init; } = [];
    public IReadOnlyList<MailRecipient> Dropped { get; init; } = [];
}

public sealed record OutgoingMail
{
    public required IReadOnlyList<string> To { get; init; }
    public IReadOnlyList<string> Cc { get; init; } = [];
    public required string Subject { get; init; }

    /// <summary>Markdown when <see cref="IsHtml"/>; converted and sanitised before sending.</summary>
    public required string Body { get; init; }

    /// <summary>Defaults to HTML: most corporate mail is, and plain text looks like a 1998 utility.</summary>
    public bool IsHtml { get; init; } = true;
}

/// <summary>
/// Who to clear out, as a filter Graph will accept (OP2-224).
/// <para>
/// Separate from <see cref="MailSearchResult"/> on purpose: a search is read with
/// <c>$search</c>, which refuses <c>$filter</c> and so cannot express "from this address",
/// and this is read with <c>$filter</c>, which cannot do free text. They are two different
/// conversations with the service and conflating them is what makes the second one
/// impossible.
/// </para>
/// </summary>
public sealed record ClearTarget
{
    /// <summary>An address, or a fragment to match against addresses.</summary>
    public required string From { get; init; }

    /// <summary>
    /// The folders to clear, by id. Null means the whole mailbox.
    /// <para>
    /// A list rather than one folder because the default is deliberately narrow (OP2-227):
    /// clearing the whole mailbox by default also emptied folders the user had just filed
    /// that sender's mail into, and filing something is an act of keeping it. The default
    /// is now the places mail lands rather than the places it has been put.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? FolderIds { get; init; }

    /// <summary>Only messages received strictly before this. Null means all of them.</summary>
    public DateTimeOffset? Before { get; init; }
}

/// <summary>What a clear-out WOULD do. Always read back before anything is deleted.</summary>
public sealed record ClearPreview
{
    /// <summary>Matches inside the scope — the number that would actually be deleted.</summary>
    public required int Matched { get; init; }

    /// <summary>
    /// Matches anywhere in the mailbox, including folders outside the scope. When this is
    /// larger than <see cref="Matched"/> the user has filed some of this sender's mail, and
    /// has to be told that it is being left alone rather than discovering either way later
    /// (OP2-227).
    /// </summary>
    public int Everywhere { get; init; }

    /// <summary>
    /// Folders holding matches that the scope excludes, by folder id. Named by the caller,
    /// which is the layer that knows folder names.
    /// </summary>
    public IReadOnlyList<string> SkippedFolderIds { get; init; } = [];

    /// <summary>
    /// The distinct senders behind the matches, commonest first. This is the gate that
    /// catches a misheard name: "everything from Dave" matching three different Daves is
    /// something the user has to hear before it happens.
    /// </summary>
    public IReadOnlyList<SenderTally> Senders { get; init; } = [];

    /// <summary>
    /// True when the senders were tallied from a sample rather than every match, so the
    /// list may be missing one. The counts themselves are always exact.
    /// </summary>
    public bool Sampled { get; init; }
}

public sealed record SenderTally(string Display, string Address, int Count);

/// <summary>What a clear-out actually did.</summary>
public sealed record ClearOutcome
{
    public required int Deleted { get; init; }

    /// <summary>
    /// Refused individually by the service. Reported rather than swallowed: "deleted 400"
    /// when 12 are still there is the failure mode this whole tool exists to replace.
    /// </summary>
    public int Failed { get; init; }

    /// <summary>Matches left over because the per-call ceiling was reached.</summary>
    public int Remaining { get; init; }

    /// <summary>
    /// True when Microsoft started refusing requests and the job stopped early (OP2-238).
    /// <para>
    /// Distinct from <see cref="Remaining"/>, which means "there is more to do". This means
    /// "there is more to do <em>and</em> going straight back will make it worse". The caller
    /// has to be able to tell the user to wait rather than cheerfully trying again.
    /// </para>
    /// </summary>
    public bool Throttled { get; init; }

    /// <summary>Roughly how long to wait before asking again, when throttled.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
