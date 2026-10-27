namespace OutPost2.Core.Files;

/// <summary>
/// A file the user uploaded through the web app, waiting to be attached to something.
/// <para>
/// The staging area exists because of how OutPost2 is actually used (OP2-9). Passing an
/// opaque capability token through a conversation means: leave Claude, open the web app,
/// upload, copy a token, come back, paste it. Impossible hands-free and tedious otherwise.
/// So the token stays an internal mechanism and the user-facing handle is a <b>name</b> —
/// "attach the signed contract" works by voice because the file was named when it was
/// uploaded, at a moment when the user had a screen.
/// </para>
/// </summary>
public sealed class StagedFile
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }

    /// <summary>What the user calls it. Defaulted from the filename, editable, matched loosely by voice.</summary>
    public required string Name { get; set; }

    /// <summary>Original filename, used verbatim as the attachment name.</summary>
    public required string FileName { get; init; }

    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>Key in object storage. Never guessable from the id, so a leaked key is not a listing.</summary>
    public required string StorageKey { get; init; }

    /// <summary>SHA-256 of the content, so an upload can be shown to have survived intact.</summary>
    public string? Sha256 { get; init; }

    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Staged files expire. A file locker that never forgets is a liability, not a feature.</summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsLive(DateTimeOffset now) => now < ExpiresAt;

    /// <summary>Human-sized, because "4194304 bytes" is unspeakable.</summary>
    public string HumanSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
        _ => $"{SizeBytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
