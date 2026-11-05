using OutPost2.Core.Connections;
using OutPost2.Core.Teams;

namespace OutPost2.Infrastructure.Teams;

/// <summary>
/// One chat service's implementation of <see cref="ITeamsService"/> (OP2-86), the way code
/// got <c>ICodeProvider</c> and mail got <c>IMailProvider</c>. The interface keeps its
/// Teams name because the chat tools already speak it; what it describes — list the
/// conversations, read one, find one by a person's name, post to it — is what Slack has
/// too.
/// </summary>
public interface IChatProvider : ITeamsService
{
    ProviderKind Provider { get; }
}

/// <summary>
/// Sends each call to the implementation for the connection's provider, and does nothing
/// else. Anything provider-specific — what a conversation is, how a mention reads, what
/// a thread is — belongs in the provider, where it is tested against that provider's wire
/// format.
/// </summary>
public sealed class ChatServiceRouter(IEnumerable<IChatProvider> providers) : ITeamsService
{
    private readonly Dictionary<ProviderKind, IChatProvider> _by = Index(providers);

    private static Dictionary<ProviderKind, IChatProvider> Index(IEnumerable<IChatProvider> providers)
    {
        var by = new Dictionary<ProviderKind, IChatProvider>();
        foreach (var p in providers)
            if (!by.TryAdd(p.Provider, p))
                throw new InvalidOperationException($"Two chat providers are registered for {p.Provider}.");
        return by;
    }

    public bool Supports(ProviderKind provider) => _by.ContainsKey(provider);

    /// <summary>
    /// A connection of a kind with no chat implementation is a plain sentence, not a call
    /// to Graph with somebody's Slack token.
    /// </summary>
    private ITeamsService For(Connection c) =>
        _by.TryGetValue(c.Provider, out var p)
            ? p
            : throw new TeamsException(
                $"{c.Label} is a {CapabilityCatalog.ProviderHeading(c.Provider)} connection, and Outpost can't read chats on it.");

    public Task<IReadOnlyList<Chat>> ListChatsAsync(
        Connection connection, int top = 15, bool unreadOnly = false, CancellationToken ct = default) =>
        For(connection).ListChatsAsync(connection, top, unreadOnly, ct);

    public Task<ChatTranscript> ReadChatAsync(
        Connection connection, string chatId, int count = 20, CancellationToken ct = default) =>
        For(connection).ReadChatAsync(connection, chatId, count, ct);

    public Task<IReadOnlyList<Chat>> FindChatsWithAsync(
        Connection connection, string who, CancellationToken ct = default) =>
        For(connection).FindChatsWithAsync(connection, who, ct);

    public Task<Chat> SendAsync(
        Connection connection, string chatId, string text, CancellationToken ct = default) =>
        For(connection).SendAsync(connection, chatId, text, ct);
}
