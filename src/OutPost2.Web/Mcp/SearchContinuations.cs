using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OutPost2.Web.Mcp;

/// <summary>
/// Where a search got to, so the next page can be asked for (OP2-208).
/// </summary>
public sealed record Continuation
{
    public required string Query { get; init; }

    /// <summary>The folder the search was scoped to, as the caller said it.</summary>
    public string? Folder { get; init; }

    public required int Count { get; init; }

    /// <summary>
    /// Connection label to the Graph link that carries on from where it stopped. Only the
    /// mailboxes that had more are in here, so a second page does not re-ask the ones that
    /// were already exhausted.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Next { get; init; }
}

/// <summary>
/// Holds the unfinished half of a search between tool calls (OP2-208).
/// <para>
/// Graph settles the design rather than taste: asked live, it refuses <c>$skip</c> and
/// <c>$filter</c> alongside <c>$search</c> — "The query parameter '$skip' is not supported
/// with '$search'" — so an offset and a before-this-date cut-off, the two things the ticket
/// suggested, cannot be built at all. An opaque <c>@odata.nextLink</c> is the only handle
/// the service gives out.
/// </para>
/// <para>
/// That link is kept <b>here</b> and the caller is handed a four-character token instead.
/// The link is a 263-character URL that we would fetch with the user's access token
/// attached, and the model that would be holding it spends its day reading mail bodies
/// written by strangers. A token that only ever resolves to a link Graph itself gave us
/// cannot be talked into pointing somewhere else.
/// </para>
/// </summary>
public interface ISearchContinuations
{
    /// <summary>Stores where a search stopped and returns the token that resumes it.</summary>
    string Remember(Guid userId, Continuation continuation);

    /// <summary>Null when the token is unknown or has aged out; see <see cref="Forgot"/>.</summary>
    Continuation? Resolve(Guid userId, string token);

    /// <summary>
    /// True when this token is one we did hold and have since dropped. The same distinction
    /// as <see cref="IItemIndex.Forgot"/>, for the same reason (OP2-201): "no such page"
    /// sends a caller looking for a mistake it did not make.
    /// </summary>
    bool Forgot(Guid userId, string token);
}

public sealed class SearchContinuations : ISearchContinuations
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Only the recent ones are any use — a search is resumed within a turn or two, or not
    /// at all. Evicted oldest-first.
    /// </summary>
    private const int Capacity = 20;

    // Same alphabet as a message handle: no i, l, o, 0 or 1, because a token is read back
    // by a model working from a transcript.
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private readonly ConcurrentDictionary<Guid, Entry> _byUser = new();

    private sealed class Entry
    {
        public readonly Dictionary<string, Continuation> ByToken = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Order = [];
        public readonly HashSet<string> Forgotten = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset At = DateTimeOffset.UtcNow;
    }

    public string Remember(Guid userId, Continuation continuation)
    {
        var e = _byUser.GetOrAdd(userId, _ => new Entry());

        lock (e)
        {
            if (DateTimeOffset.UtcNow - e.At > Lifetime) Clear(e);

            string token;
            do { token = Mint(); } while (e.ByToken.ContainsKey(token));

            e.ByToken[token] = continuation;
            e.Order.Add(token);
            e.At = DateTimeOffset.UtcNow;

            while (e.Order.Count > Capacity)
            {
                var oldest = e.Order[0];
                e.Order.RemoveAt(0);
                e.ByToken.Remove(oldest);
                e.Forgotten.Add(oldest);
            }

            return token;
        }
    }

    public Continuation? Resolve(Guid userId, string token)
    {
        var said = Clean(token);
        if (said.Length == 0) return null;
        if (!_byUser.TryGetValue(userId, out var e)) return null;

        lock (e)
        {
            if (DateTimeOffset.UtcNow - e.At > Lifetime)
            {
                Clear(e);
                return null;
            }

            if (!e.ByToken.TryGetValue(said, out var found)) return null;

            // A page is good for one use. Serving it twice would hand back the same
            // messages under a token that reads like progress, which is the shape of the
            // failure OP2-208 reported in the first place.
            e.ByToken.Remove(said);
            e.Order.Remove(said);
            e.Forgotten.Add(said);
            e.At = DateTimeOffset.UtcNow;

            return found;
        }
    }

    public bool Forgot(Guid userId, string token)
    {
        var said = Clean(token);
        if (said.Length == 0) return false;
        if (!_byUser.TryGetValue(userId, out var e)) return false;

        lock (e) return e.Forgotten.Contains(said);
    }

    /// <summary>Keeps which tokens were held, so a stale one can be told apart from a wrong one.</summary>
    private static void Clear(Entry e)
    {
        foreach (var gone in e.ByToken.Keys) e.Forgotten.Add(gone);
        e.ByToken.Clear();
        e.Order.Clear();
        e.At = DateTimeOffset.UtcNow;
    }

    private static string Clean(string? token) =>
        new((token ?? "").Trim().Where(char.IsLetterOrDigit).ToArray());

    private static string Mint() =>
        new(Enumerable.Range(0, 4)
            .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)])
            .ToArray());
}
