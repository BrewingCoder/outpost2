using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Code;

/// <summary>
/// One code host's implementation of <see cref="IGitHubService"/> (OP2-84), the way mail
/// got <c>IMailProvider</c>. The interface keeps its GitHub name because every code tool
/// and the repository resolver already speak it; what it describes — repositories,
/// commits, pull requests, issues, files — is what any of these servers has.
/// </summary>
public interface ICodeProvider : IGitHubService
{
    ProviderKind Provider { get; }
}

/// <summary>
/// Sends each call to the implementation for the connection's provider, and does nothing
/// else. Anything provider-specific — how "awaiting my review" is asked, what a draft is —
/// belongs in the provider, where it is tested against that provider's wire format.
/// </summary>
public sealed class CodeServiceRouter(IEnumerable<ICodeProvider> providers) : IGitHubService
{
    private readonly Dictionary<ProviderKind, ICodeProvider> _by = Index(providers);

    private static Dictionary<ProviderKind, ICodeProvider> Index(IEnumerable<ICodeProvider> providers)
    {
        var by = new Dictionary<ProviderKind, ICodeProvider>();
        foreach (var p in providers)
            if (!by.TryAdd(p.Provider, p))
                throw new InvalidOperationException($"Two code providers are registered for {p.Provider}.");
        return by;
    }

    public bool Supports(ProviderKind provider) => _by.ContainsKey(provider);

    /// <summary>
    /// A connection of a kind with no code implementation is a plain sentence, not a call
    /// to GitHub with somebody's Gitea token.
    /// </summary>
    private IGitHubService For(Connection c) =>
        _by.TryGetValue(c.Provider, out var p)
            ? p
            : throw new GitHubException(
                $"{c.Label} is a {CapabilityCatalog.ProviderHeading(c.Provider)} connection, and Outpost can't read code on it.");

    public Task<CodeTriage> TriageAsync(Connection c, CancellationToken ct = default) =>
        For(c).TriageAsync(c, ct);

    public Task<IReadOnlyList<RepoRef>> ListRepositoriesAsync(Connection c, CancellationToken ct = default) =>
        For(c).ListRepositoriesAsync(c, ct);

    public Task<IReadOnlyList<CommitRef>> ListCommitsAsync(
        Connection c, string repo, string? branch, int count, CancellationToken ct = default) =>
        For(c).ListCommitsAsync(c, repo, branch, count, ct);

    public Task<IReadOnlyList<CodeItem>> ListPullRequestsAsync(
        Connection c, PullRequestScope scope, string? repo, int count, CancellationToken ct = default) =>
        For(c).ListPullRequestsAsync(c, scope, repo, count, ct);

    public Task<IReadOnlyList<CodeItem>> ListIssuesAsync(
        Connection c, IssueScope scope, string? repo, int count, CancellationToken ct = default) =>
        For(c).ListIssuesAsync(c, scope, repo, count, ct);

    public Task<CodeItem?> GetAsync(Connection c, string repo, int number, CancellationToken ct = default) =>
        For(c).GetAsync(c, repo, number, ct);

    public Task<IReadOnlyList<RepoEntry>?> ListPathAsync(
        Connection c, string repo, string? path, string? gitRef, CancellationToken ct = default) =>
        For(c).ListPathAsync(c, repo, path, gitRef, ct);

    public string? ScopeNote(Connection c, PullRequestScope scope) =>
        _by.TryGetValue(c.Provider, out var p) ? p.ScopeNote(c, scope) : null;

    public Task<RepoFile?> ReadFileAsync(
        Connection c, string repo, string path, string? gitRef, int maxChars, CancellationToken ct = default) =>
        For(c).ReadFileAsync(c, repo, path, gitRef, maxChars, ct);
}
