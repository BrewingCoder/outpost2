using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OutPost2.Core.Code;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Code;

public enum RepoResolutionKind { Resolved, Ambiguous, NotFound }

/// <summary>
/// The outcome of turning something said out loud into an <c>owner/name</c>.
/// </summary>
public sealed record RepoResolution(
    RepoResolutionKind Kind,
    string? FullName,
    IReadOnlyList<string> Candidates,
    string? Message)
{
    public static RepoResolution Found(string fullName) => new(RepoResolutionKind.Resolved, fullName, [], null);
}

public interface IRepoResolver
{
    Task<RepoResolution> ResolveAsync(Connection c, string spoken, CancellationToken ct = default);
    Task<IReadOnlyList<RepoRef>> KnownAsync(Connection c, CancellationToken ct = default);

    /// <summary>
    /// The names configured for this connection. Exposed because a tool that cannot read
    /// them reports, correctly from what it can see, that aliases do not exist — so the
    /// one place they are configured is invisible to the conversation that uses them.
    /// </summary>
    Task<IReadOnlyList<RepoAlias>> AliasesAsync(Connection c, CancellationToken ct = default);
    void Forget(Guid connectionId);
}

/// <summary>
/// Resolves a repository name the way the connection resolver handles mailbox labels:
/// settle it when it is unambiguous, ask when it is not, and never guess.
/// <para>
/// Three sources, in order. An explicit <c>owner/name</c> is taken literally. An alias the
/// user configured wins next, because it exists precisely to override what a search would
/// otherwise decide. Failing both, the repositories the token can actually see are matched
/// by name — which is what makes a bare "RosettaCore" work with no configuration at all.
/// </para>
/// </summary>
public sealed class RepoResolver(
    OutPostDbContext db,
    IGitHubService github,
    IMemoryCache cache) : IRepoResolver
{
    // Long enough that a conversation does not re-list on every question, short enough
    // that a repository created this morning is reachable this afternoon.
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static string Key(Guid connectionId) => $"repos:{connectionId}";

    public void Forget(Guid connectionId) => cache.Remove(Key(connectionId));

    public async Task<IReadOnlyList<RepoRef>> KnownAsync(Connection c, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(c.Id), out IReadOnlyList<RepoRef>? hit) && hit is not null) return hit;

        var repos = await github.ListRepositoriesAsync(c, ct);
        cache.Set(Key(c.Id), repos, CacheFor);
        return repos;
    }

    public async Task<IReadOnlyList<RepoAlias>> AliasesAsync(Connection c, CancellationToken ct = default) =>
        await db.RepoAliases
            .Where(a => a.ConnectionId == c.Id)
            .OrderBy(a => a.Alias)
            .ToListAsync(ct);

    public async Task<RepoResolution> ResolveAsync(Connection c, string spoken, CancellationToken ct = default)
    {
        var said = (spoken ?? "").Trim().Trim('/');
        if (said.Length == 0)
            return new(RepoResolutionKind.NotFound, null, [], "Name a repository.");

        // Already fully qualified. Taken at its word — the caller may legitimately be
        // naming a public repository this token has never listed.
        if (said.Contains('/')) return RepoResolution.Found(said);

        var key = RepoAlias.Normalise(said);

        // Aliases first. The whole point of configuring one is to beat the search.
        var aliases = await AliasesAsync(c, ct);

        var alias = aliases.FirstOrDefault(a => RepoAlias.Normalise(a.Alias) == key);
        if (alias is not null) return RepoResolution.Found(alias.FullName);

        var known = await KnownAsync(c, ct);

        // Exact name match, ignoring case and separators, because speech-to-text does not
        // reliably reproduce a hyphen and "rosetta core" must find "RosettaCore".
        var exact = known.Where(r => RepoAlias.Normalise(r.Name) == key).ToList();

        if (exact.Count == 1) return RepoResolution.Found(exact[0].FullName);

        if (exact.Count > 1)
        {
            // The same name in two organisations. Settled by asking, or permanently by
            // adding an alias — never by preferring whichever GitHub listed first.
            var names = exact.Select(r => r.FullName).ToList();
            return new(RepoResolutionKind.Ambiguous, null, names,
                $"There are {names.Count} repositories called {said}: {string.Join(", ", names)}. Which one?");
        }

        // Nothing matched exactly. Offer near misses rather than a flat no — a wrong
        // transcription is far more likely than a repository that does not exist.
        var near = known
            .Where(r => RepoAlias.Normalise(r.Name).Contains(key) || key.Contains(RepoAlias.Normalise(r.Name)))
            .Select(r => r.FullName)
            .Take(5)
            .ToList();

        var message = near.Count > 0
            ? $"No repository called {said} on {c.Label}. Closest: {string.Join(", ", near)}."
            : $"No repository called {said} among the {known.Count} this connection can see on {c.Label}.";

        // If names are configured, say so and list them. Otherwise the reply states a
        // fact about the listing and leaves the listener to wonder whether the thing they
        // set up is being consulted at all.
        if (aliases.Count > 0)
        {
            message += " Configured names: " +
                string.Join(", ", aliases.Select(a => $"\"{a.Alias}\" = {a.FullName}")) + ".";
        }

        return new(RepoResolutionKind.NotFound, null, near, message);
    }
}
