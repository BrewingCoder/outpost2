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
    public required bool IsRead { get; init; }
    public required bool HasAttachments { get; init; }

    /// <summary>Clean text, converted from HTML and with quoted history already removed.</summary>
    public required string Body { get; init; }

    /// <summary>True when Graph gave us uniqueBody, so the reply chain is genuinely absent.</summary>
    public required bool QuotedHistoryStripped { get; init; }
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
