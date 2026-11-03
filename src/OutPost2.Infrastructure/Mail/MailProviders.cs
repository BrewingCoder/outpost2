using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Mail;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// One mail provider's implementation of <see cref="IMailService"/> (OP2-278).
/// <para>
/// Every mail tool, the job runner and the attachment download endpoint already talk to
/// <see cref="IMailService"/> with a <see cref="Connection"/> in hand, so the connection is
/// all that is needed to choose the provider. The tools stay provider-blind; each provider
/// is registered as one of these, and <see cref="MailServiceRouter"/> is what they get.
/// </para>
/// </summary>
public interface IMailProvider : IMailService
{
    ProviderKind Provider { get; }
}

/// <summary>The same, for sending with attachments, which runs in the job queue.</summary>
public interface IAttachmentSenderProvider : ILargeAttachmentSender
{
    ProviderKind Provider { get; }
}

/// <summary>
/// The connection is a kind this build has no mail implementation for. A
/// <see cref="MailException"/>, so every tool that already reports mail failures in one
/// plain sentence reports this one the same way rather than throwing or — worse — calling
/// Microsoft Graph with somebody's Google token.
/// </summary>
public sealed class MailProviderNotSupportedException(Connection connection)
    : MailException(Describe(connection))
{
    public static string Describe(Connection c) =>
        $"{c.Label} is a {CapabilityCatalog.ProviderHeading(c.Provider)} connection, and "
        + "Outpost can't do mail on it yet.";
}

/// <summary>
/// A send whose request reached the mailbox and got no clear answer — a dropped connection
/// mid-upload, a timeout, a 5xx (OP2-262, OP2-281). The message may well be in the
/// recipients' inboxes, so this must never be reported as a plain failure: retrying it is
/// how a second copy goes out.
/// </summary>
public sealed class MailMaybeSentException(string label)
    : MailException($"{label}: {SendOutcome.MaybeSentMessage}");

/// <summary>
/// Sends each call to the implementation for the connection's provider (OP2-278).
/// <para>
/// Deliberately nothing but dispatch. Anything provider-specific — what a folder is, how a
/// search is phrased, what importance means — belongs in the provider, where it can be
/// tested against that provider's wire format; a router that started interpreting would be
/// a second place for the same decision to drift.
/// </para>
/// </summary>
public sealed class MailServiceRouter(IEnumerable<IMailProvider> providers) : IMailService
{
    private readonly Dictionary<ProviderKind, IMailProvider> _by = Index(providers);

    private static Dictionary<ProviderKind, IMailProvider> Index(IEnumerable<IMailProvider> providers)
    {
        var by = new Dictionary<ProviderKind, IMailProvider>();
        foreach (var p in providers)
            if (!by.TryAdd(p.Provider, p))
                throw new InvalidOperationException($"Two mail providers are registered for {p.Provider}.");
        return by;
    }

    public bool Supports(ProviderKind provider) => _by.ContainsKey(provider);

    private IMailService For(Connection c) =>
        _by.TryGetValue(c.Provider, out var p) ? p : throw new MailProviderNotSupportedException(c);

    public Task<IReadOnlyList<MailSummary>> ListAsync(
        Connection connection, string folder = "inbox", int top = 25, bool unreadOnly = false,
        CancellationToken ct = default) =>
        For(connection).ListAsync(connection, folder, top, unreadOnly, ct);

    public Task<MailMessage?> ReadAsync(Connection connection, string messageId, CancellationToken ct = default) =>
        For(connection).ReadAsync(connection, messageId, ct);

    public Task<MailSearchResult> SearchAsync(
        Connection connection, string query, int top = 25, string? folderId = null,
        string? continueFrom = null, CancellationToken ct = default) =>
        For(connection).SearchAsync(connection, query, top, folderId, continueFrom, ct);

    public Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default) =>
        For(connection).SendAsync(connection, mail, ct);

    public Task<ReplySent> SendReplyAsync(
        Connection connection, string messageId, ReplyKind kind, string markdownBody,
        IReadOnlyList<string>? forwardTo = null, RecipientEdit? edit = null,
        MailImportance importance = MailImportance.Normal,
        CancellationToken ct = default) =>
        For(connection).SendReplyAsync(connection, messageId, kind, markdownBody, forwardTo, edit, importance, ct);

    public Task<ClearPreview> PreviewClearAsync(
        Connection connection, ClearTarget target, CancellationToken ct = default) =>
        For(connection).PreviewClearAsync(connection, target, ct);

    public Task<ClearOutcome> ClearAsync(
        Connection connection, ClearTarget target, int ceiling = 1000, CancellationToken ct = default) =>
        For(connection).ClearAsync(connection, target, ceiling, ct);

    public Task<ClearOutcome> RecoverAsync(
        Connection connection, string from, int ceiling = 1000, CancellationToken ct = default) =>
        For(connection).RecoverAsync(connection, from, ceiling, ct);

    public Task<int> RecoverableCountAsync(Connection connection, string from, CancellationToken ct = default) =>
        For(connection).RecoverableCountAsync(connection, from, ct);

    public Task MarkReadAsync(Connection connection, string messageId, bool read, CancellationToken ct = default) =>
        For(connection).MarkReadAsync(connection, messageId, read, ct);

    public Task<string> MoveAsync(
        Connection connection, string messageId, string destinationFolderId, CancellationToken ct = default) =>
        For(connection).MoveAsync(connection, messageId, destinationFolderId, ct);

    public Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Connection connection, CancellationToken ct = default) =>
        For(connection).ListFoldersAsync(connection, ct);

    public Task<MailFolder> CreateFolderAsync(
        Connection connection, string name, string? parentFolderId = null, CancellationToken ct = default) =>
        For(connection).CreateFolderAsync(connection, name, parentFolderId, ct);

    public Task<MailFolder> RenameFolderAsync(
        Connection connection, string folderId, string name, CancellationToken ct = default) =>
        For(connection).RenameFolderAsync(connection, folderId, name, ct);

    public Task DeleteFolderAsync(Connection connection, string folderId, CancellationToken ct = default) =>
        For(connection).DeleteFolderAsync(connection, folderId, ct);

    public Task<string?> MarkAsJunkAsync(Connection connection, string messageId, CancellationToken ct = default) =>
        For(connection).MarkAsJunkAsync(connection, messageId, ct);

    public Task FlagAsync(
        Connection connection, string messageId, bool flagged, DateTimeOffset? dueAt, CancellationToken ct = default) =>
        For(connection).FlagAsync(connection, messageId, flagged, dueAt, ct);

    public Task<IReadOnlyList<string>> GetCategoriesAsync(
        Connection connection, string messageId, CancellationToken ct = default) =>
        For(connection).GetCategoriesAsync(connection, messageId, ct);

    public Task SetCategoriesAsync(
        Connection connection, string messageId, IReadOnlyList<string> categories, CancellationToken ct = default) =>
        For(connection).SetCategoriesAsync(connection, messageId, categories, ct);

    public Task<IReadOnlyList<MailCategory>?> ListCategoriesAsync(Connection connection, CancellationToken ct = default) =>
        For(connection).ListCategoriesAsync(connection, ct);

    public Task<MailCategory?> CreateCategoryAsync(
        Connection connection, string name, string preset, CancellationToken ct = default) =>
        For(connection).CreateCategoryAsync(connection, name, preset, ct);

    public Task<IReadOnlyList<MailAttachment>> ListAttachmentsAsync(
        Connection connection, string messageId, CancellationToken ct = default) =>
        For(connection).ListAttachmentsAsync(connection, messageId, ct);

    public Task<Stream?> OpenAttachmentAsync(
        Connection connection, string messageId, string attachmentId, CancellationToken ct = default) =>
        For(connection).OpenAttachmentAsync(connection, messageId, attachmentId, ct);
}

/// <summary>
/// The same dispatch for sends with attachments (OP2-278). An unsupported provider is an
/// outcome, not an exception, because that is the sender's contract: the job runner
/// records outcomes, and this one is a plain failure — nothing was sent, and trying again
/// will not help.
/// </summary>
public sealed class LargeAttachmentSenderRouter(IEnumerable<IAttachmentSenderProvider> providers)
    : ILargeAttachmentSender
{
    private readonly Dictionary<ProviderKind, IAttachmentSenderProvider> _by =
        providers.ToDictionary(p => p.Provider);

    public Task<SendOutcome> SendAsync(
        Connection connection, OutgoingMail mail, IReadOnlyList<StagedFile> attachments,
        SendResumeState? resume = null,
        Func<SendResumeState, CancellationToken, Task>? persist = null,
        IProgress<(int Percent, string Detail)>? progress = null,
        CancellationToken ct = default) =>
        _by.TryGetValue(connection.Provider, out var p)
            ? p.SendAsync(connection, mail, attachments, resume, persist, progress, ct)
            : Task.FromResult(new SendOutcome(
                false, MailProviderNotSupportedException.Describe(connection) + " Nothing was sent.",
                Retryable: false, DraftId: null));
}
