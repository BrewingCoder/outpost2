using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OutPost2.Web.Mcp;

/// <summary>What a handle points at. Mail and calendar share one handle space.</summary>
public enum ItemKind
{
    Mail,
    Event,
    Contact,
    Chat,
}

/// <summary>
/// One item the conversation can refer to, by a short handle that does not move.
/// </summary>
public sealed class ItemRef
{
    public required string Ref { get; init; }

    /// <summary>
    /// Graph's id. Mutable because a move reissues it — the handle is the stable thing
    /// here, not the provider's identifier.
    /// </summary>
    public required string ItemId { get; set; }

    public required string ConnectionLabel { get; init; }

    /// <summary>Kept so an action can read back WHAT it acted on. "Deleted it" is not a confirmation.</summary>
    public required string Subject { get; init; }

    /// <summary>Sender for mail, organiser for an event.</summary>
    public required string From { get; init; }

    /// <summary>
    /// Mail and calendar deliberately share one index, so "the second one" always means
    /// the second of whatever was listed LAST rather than depending on which tool the
    /// model happens to reach for next. The cost is that a handle can arrive at the wrong
    /// family of tool, so each one carries what it is and the tools check.
    /// </summary>
    public ItemKind Kind { get; init; } = ItemKind.Mail;
}

/// <summary>
/// Addresses messages for the conversation (OP2-73).
/// <para>
/// Graph message ids are 150-odd characters of base64 — unspeakable, and per D1 there is
/// no screen to copy one from. So listings number their results and the next turn can say
/// "the second one".
/// </para>
/// <para>
/// An ordinal alone is not safe to act on, though: it means "whatever is second in the
/// most recent listing", and a message arriving between the listing and the action shifts
/// every row down one. Reading the wrong message is a surprise; deleting or junking the
/// wrong one is not recoverable from the car. So every listed message also gets a short
/// handle derived from its id — stable for as long as the window lasts, unchanged by new
/// mail, and the same handle every time the same message appears. Reads accept either;
/// anything that changes the mailbox takes the handle.
/// </para>
/// <para>
/// Deliberately in-memory and short-lived: conversational scratch, not state worth
/// persisting. Losing it costs one "list them again", which is the right failure.
/// </para>
/// </summary>
public interface IItemIndex
{
    /// <summary>Records a listing in order and returns it with handles attached.</summary>
    IReadOnlyList<ItemRef> Remember(
        Guid userId, IReadOnlyList<(string ItemId, string ConnectionLabel, string Subject, string From)> ordered,
        ItemKind kind = ItemKind.Mail);

    /// <summary>
    /// Resolves a handle, or an ordinal against the last listing. Null when neither matches
    /// — including when the window has expired, which must read as "list them again"
    /// rather than resolving against something an hour stale.
    /// </summary>
    ItemRef? Resolve(Guid userId, string token);

    /// <summary>
    /// True when this handle is one we <b>did</b> hold and have since dropped — aged out of
    /// the window, or evicted because the session ran long (OP2-201).
    /// <para>
    /// Exists only so the refusal can tell the truth. "I don't have that one — list your
    /// messages first" reads as <i>no such message</i>, which sends a caller hunting for a
    /// mail that is sitting in the mailbox exactly where it left it.
    /// </para>
    /// </summary>
    bool Forgot(Guid userId, string token);

    /// <summary>Called after a move, which reissues the provider's id under the same handle.</summary>
    void Retarget(Guid userId, string handle, string newItemId);
}

/// <summary>
/// What to say when a handle does not resolve (OP2-201).
/// <para>
/// Shared by mail, calendar and contacts because the two cases are the same everywhere and
/// the difference matters more than the noun: a handle that <b>never</b> existed means
/// "list them and try again", while one that has been dropped means "the thing you are
/// pointing at is fine, my shorthand for it is not". Telling the second as the first sent a
/// caller looking for a message that had not moved.
/// </para>
/// </summary>
public static class Handles
{
    public static string Unknown(IItemIndex index, Guid userId, string? said, string things) =>
        index.Forgot(userId, said ?? "")
            ? $"\"{(said ?? "").Trim()}\" was a handle I had and have since dropped — this "
              + "session has run long enough that I let go of it. Nothing has happened to the "
              + $"item itself. List or search your {things} again and it comes back under a "
              + "fresh handle."
            : $"I don't have that one — list your {things} first, then tell me which.";
}

public sealed class ItemIndex : IItemIndex
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Enough handles for a long session; evicted oldest-first past this.
    /// <para>
    /// Was 300, which a bulk clean-up reached in four searches and then quietly dropped the
    /// handles it started with — reported as OP2-201. An ItemRef is a Graph id (~150 chars)
    /// plus a subject and a sender, so roughly half a kilobyte; 5,000 of them is a couple of
    /// megabytes per user, which is the right trade for a session that files a few thousand
    /// messages. The cap still exists, because this is per-user memory on a long-lived
    /// process — past it, <see cref="Forgot"/> is what keeps the failure honest.
    /// </para>
    /// </summary>
    private const int Capacity = 5000;

    /// <summary>
    /// How many dropped handles to remember as dropped. Only the four-character handle is
    /// kept, not the item, so this is cheap — and it is what the difference between "never
    /// heard of it" and "that has aged out" costs.
    /// </summary>
    private const int ForgottenCapacity = 20_000;

    // No i, l, o, 0 or 1. A handle gets read aloud and typed back by a model working from
    // a transcript, and those five are the pairs that get confused.
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>Punctuation a handle collects in transit, none of which is ever part of one.</summary>
    private static readonly SearchValues<char> Decoration = SearchValues.Create("[](){}#\"'` \t\r\n.,:;!?");

    private readonly ConcurrentDictionary<Guid, Entry> _byUser = new();

    private sealed class Entry
    {
        public readonly Dictionary<string, ItemRef> ByHandle = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ItemRef> ByItemId = new(StringComparer.Ordinal);
        public readonly List<ItemRef> Order = [];

        /// <summary>Handles this user once had. See <see cref="Forgot"/>.</summary>
        public readonly HashSet<string> Forgotten = new(StringComparer.OrdinalIgnoreCase);

        public List<ItemRef> LastListing = [];
        public DateTimeOffset At = DateTimeOffset.UtcNow;

        public void Forget(string handle)
        {
            // Dropped wholesale rather than trimmed: this is a hint for an error message,
            // and a bounded set that occasionally forgets that it forgot something is
            // better than one that walks a 20,000-entry collection to keep order.
            if (Forgotten.Count >= ForgottenCapacity) Forgotten.Clear();
            Forgotten.Add(handle);
        }
    }

    public IReadOnlyList<ItemRef> Remember(
        Guid userId,
        IReadOnlyList<(string ItemId, string ConnectionLabel, string Subject, string From)> ordered,
        ItemKind kind = ItemKind.Mail)
    {
        var e = Fresh(userId);

        lock (e)
        {
            var listing = new List<ItemRef>(ordered.Count);

            foreach (var (id, label, subject, from) in ordered)
            {
                // The same message listed twice keeps the handle it already had, which is
                // the entire point: "delete a7kq" means one message for the whole session.
                if (e.ByItemId.TryGetValue(id, out var known))
                {
                    listing.Add(known);
                    continue;
                }

                var created = new ItemRef
                {
                    Ref = Mint(id, e.ByHandle),
                    ItemId = id,
                    ConnectionLabel = label,
                    // Scrubbed once, here, rather than at the twenty-odd places that print a
                    // remembered subject or sender back (OP2-286): "Marked "{subject}" read"
                    // is exactly the shape OP2-265 was.
                    Subject = Untrusted.Scrub(subject),
                    From = Untrusted.Scrub(from),
                    Kind = kind,
                };

                e.ByHandle[created.Ref] = created;
                e.ByItemId[id] = created;
                e.Order.Add(created);
                listing.Add(created);
            }

            while (e.Order.Count > Capacity)
            {
                var oldest = e.Order[0];
                e.Order.RemoveAt(0);
                e.ByHandle.Remove(oldest.Ref);
                e.ByItemId.Remove(oldest.ItemId);
                e.Forget(oldest.Ref);
            }

            e.LastListing = listing;
            e.At = DateTimeOffset.UtcNow;
            return listing;
        }
    }

    /// <summary>
    /// Strips the decoration a handle picks up in transit: "[a7kq]", "#a7kq", "'a7kq'".
    /// Only that decoration, and deliberately not "everything that is not a letter or
    /// digit" — that turns "-1" into "1" and acts on the first message when the caller
    /// asked for something that does not exist.
    /// </summary>
    private static string Clean(string? token)
    {
        var said = (token ?? "").Trim();
        return said.Length == 0
            ? ""
            : new string(said.Where(c => !Decoration.Contains(c)).ToArray());
    }

    public ItemRef? Resolve(Guid userId, string token)
    {
        var said = Clean(token);
        if (said.Length == 0) return null;

        if (!_byUser.TryGetValue(userId, out var e)) return null;

        lock (e)
        {
            if (DateTimeOffset.UtcNow - e.At > Lifetime)
            {
                // The window goes, but WHICH handles it held is kept. Discarding the entry
                // outright made an aged-out handle indistinguishable from one that never
                // existed, and the refusal then told the caller the message was unknown
                // when it was sitting in the mailbox (OP2-201).
                var replacement = new Entry();
                foreach (var gone in e.ByHandle.Keys) replacement.Forget(gone);
                foreach (var gone in e.Forgotten) replacement.Forget(gone);
                _byUser[userId] = replacement;
                return null;
            }

            if (e.ByHandle.TryGetValue(said, out var byHandle))
            {
                // Sliding on USE, not just on listing. A clean-up that works through one
                // listing for over half an hour was otherwise cut off mid-session, which is
                // the same surprise as the eviction above and from the same ticket.
                e.At = DateTimeOffset.UtcNow;
                return byHandle;
            }

            // Only after the handle lookup fails. A handle is all digits sometimes — the
            // alphabet includes 2-9 — and it must not be read as a position.
            if (int.TryParse(said, out var n))
            {
                var i = n - 1;
                if (i < 0 || i >= e.LastListing.Count) return null;

                e.At = DateTimeOffset.UtcNow;
                return e.LastListing[i];
            }

            return null;
        }
    }

    public bool Forgot(Guid userId, string token)
    {
        var said = Clean(token);
        if (said.Length == 0) return false;
        if (!_byUser.TryGetValue(userId, out var e)) return false;

        lock (e) return e.Forgotten.Contains(said);
    }

    public void Retarget(Guid userId, string handle, string newItemId)
    {
        if (!_byUser.TryGetValue(userId, out var e)) return;

        lock (e)
        {
            if (!e.ByHandle.TryGetValue(handle, out var item)) return;

            e.ByItemId.Remove(item.ItemId);
            item.ItemId = newItemId;
            e.ByItemId[newItemId] = item;
        }
    }

    private Entry Fresh(Guid userId)
    {
        var e = _byUser.GetOrAdd(userId, _ => new Entry());

        // An expired window is discarded rather than appended to, so handles cannot leak
        // across a gap long enough that the user has forgotten the listing they came from.
        // What the window HELD is carried over, so a handle said after the gap is still
        // answered with "that has aged out" rather than "no such message".
        if (DateTimeOffset.UtcNow - e.At > Lifetime)
        {
            var replacement = new Entry();
            foreach (var gone in e.ByHandle.Keys) replacement.Forget(gone);
            foreach (var gone in e.Forgotten) replacement.Forget(gone);

            e = replacement;
            _byUser[userId] = e;
        }

        return e;
    }

    /// <summary>
    /// A short handle derived from the message id, so the same message always mints the
    /// same one. Four characters is ~920k possibilities against a few dozen live messages;
    /// the loop handles the collision anyway rather than trusting the arithmetic.
    /// </summary>
    public static string Mint(string messageId, IReadOnlyDictionary<string, ItemRef> taken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));

        for (var length = 4; length <= 8; length++)
        {
            for (var attempt = 0; attempt + length <= hash.Length; attempt++)
            {
                var sb = new StringBuilder(length);
                for (var i = 0; i < length; i++)
                    sb.Append(Alphabet[hash[attempt + i] % Alphabet.Length]);

                var candidate = sb.ToString();
                if (!taken.ContainsKey(candidate)) return candidate;
            }
        }

        // Unreachable in practice; a guaranteed-unique fallback beats an exception here.
        return Guid.NewGuid().ToString("n")[..8];
    }
}
