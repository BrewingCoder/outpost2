using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Issues;

/// <summary>
/// One issue tracker's implementation of <see cref="IYouTrackService"/> (OP2-82), the way
/// code got <c>ICodeProvider</c> (OP2-84). The interface keeps its YouTrack name for the
/// same reason <c>IGitHubService</c> kept GitHub's: the issue tools already speak it, and
/// what it describes — projects, a search, an issue, a comment, a change — is what any
/// tracker has. What a tracker's QUERY looks like is not shared, and is said per
/// connection by <see cref="IssueQueryLanguage"/>.
/// </summary>
public interface IIssueProvider : IYouTrackService
{
    ProviderKind Provider { get; }
}

/// <summary>
/// Sends each call to the implementation for the connection's provider, and does nothing
/// else. Anything tracker-specific — the query language, how a status changes, what a
/// comment body is made of — belongs in the provider, where it is tested against that
/// tracker's wire format.
/// </summary>
public sealed class IssueServiceRouter(IEnumerable<IIssueProvider> providers) : IYouTrackService
{
    private readonly Dictionary<ProviderKind, IIssueProvider> _by = Index(providers);

    private static Dictionary<ProviderKind, IIssueProvider> Index(IEnumerable<IIssueProvider> providers)
    {
        var by = new Dictionary<ProviderKind, IIssueProvider>();
        foreach (var p in providers)
            if (!by.TryAdd(p.Provider, p))
                throw new InvalidOperationException($"Two issue providers are registered for {p.Provider}.");
        return by;
    }

    public bool Supports(ProviderKind provider) => _by.ContainsKey(provider);

    /// <summary>
    /// A connection of a kind with no issue implementation is a plain sentence, not a call
    /// to YouTrack with somebody's Jira credential.
    /// </summary>
    private IYouTrackService For(Connection c) =>
        _by.TryGetValue(c.Provider, out var p)
            ? p
            : throw new InvalidOperationException(
                $"{c.Label} is a {CapabilityCatalog.ProviderHeading(c.Provider)} connection, and Outpost can't use the issue tools on it.");

    public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Connection c, CancellationToken ct = default) =>
        For(c).ListProjectsAsync(c, ct);

    public Task<IReadOnlyList<IssueSummary>> SearchAsync(Connection c, string query, int top = 20, CancellationToken ct = default) =>
        For(c).SearchAsync(c, query, top, ct);

    public Task<IssueDetail?> GetIssueAsync(Connection c, string issueKey, CancellationToken ct = default) =>
        For(c).GetIssueAsync(c, issueKey, ct);

    public Task<string> CreateIssueAsync(
        Connection c, string projectShortName, string summary, string? description,
        string? issueType = null, CancellationToken ct = default) =>
        For(c).CreateIssueAsync(c, projectShortName, summary, description, issueType, ct);

    public Task CommentAsync(Connection c, string issueKey, string text, CancellationToken ct = default) =>
        For(c).CommentAsync(c, issueKey, text, ct);

    public Task ApplyCommandAsync(Connection c, string issueKey, string command, CancellationToken ct = default) =>
        For(c).ApplyCommandAsync(c, issueKey, command, ct);

    public Task<ProjectSummary> CreateProjectAsync(Connection c, string name, string shortName, CancellationToken ct = default) =>
        For(c).CreateProjectAsync(c, name, shortName, ct);
}

/// <summary>
/// Which query language a connection's <c>search_issues</c> reads, said in the tool's
/// output every time (OP2-82). A model that has just used YouTrack will otherwise send
/// <c>project: OP2 #Unresolved</c> to Jira, which answers with a parse error that names
/// neither the cause nor the fix.
/// <para>
/// Static rather than on the provider interface: it is our own constant about a kind of
/// connection, not something a tracker tells us, so it never needs to be treated as
/// provider text.
/// </para>
/// </summary>
public static class IssueQueryLanguage
{
    public static string Name(ProviderKind provider) => provider switch
    {
        ProviderKind.Jira => "JQL",
        _ => "YouTrack query syntax",
    };

    public static string Example(ProviderKind provider) => provider switch
    {
        ProviderKind.Jira => "project = OPS AND statusCategory != Done AND assignee = currentUser()",
        _ => "project: OP2 #Unresolved for: me",
    };
}
