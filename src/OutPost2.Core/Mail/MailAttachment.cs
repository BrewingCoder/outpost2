namespace OutPost2.Core.Mail;

public sealed record MailAttachment
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>
    /// A reference attachment: a OneDrive/SharePoint link with no bytes behind it.
    /// Downloading it the normal way returns nothing useful, so it must be reported as a
    /// link rather than failing mysteriously.
    /// </summary>
    public bool IsLink { get; init; }

    /// <summary>An embedded message or calendar item rather than a file.</summary>
    public bool IsEmbeddedItem { get; init; }

    public string HumanSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
    };
}
