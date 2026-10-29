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

    /// <summary>Called after a move, which reissues the provider's id under the same handle.</summary>
    void Retarget(Guid userId, string handle, string newItemId);
}

public sealed class ItemIndex : IItemIndex
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Enough handles for a long session; evicted oldest-first past this.</summary>
    private const int Capacity = 300;

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
        public List<ItemRef> LastListing = [];
        public DateTimeOffset At = DateTimeOffset.UtcNow;
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
                    Subject = subject,
                    From = from,
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
            }

            e.LastListing = listing;
            e.At = DateTimeOffset.UtcNow;
            return listing;
        }
    }

    public ItemRef? Resolve(Guid userId, string token)
    {
        var said = (token ?? "").Trim();
        if (said.Length == 0) return null;

        // Spoken handles pick up decoration on the way through: "[a7kq]", "#a7kq", "'a7kq'".
        // Only that decoration is stripped, and deliberately not "everything that is not a
        // letter or digit" — that turns "-1" into "1" and acts on the first message when
        // the caller asked for something that does not exist.
        said = new string(said.Where(c => !Decoration.Contains(c)).ToArray());
        if (said.Length == 0) return null;

        if (!_byUser.TryGetValue(userId, out var e)) return null;

        lock (e)
        {
            if (DateTimeOffset.UtcNow - e.At > Lifetime)
            {
                _byUser.TryRemove(userId, out _);
                return null;
            }

            if (e.ByHandle.TryGetValue(said, out var byHandle)) return byHandle;

            // Only after the handle lookup fails. A handle is all digits sometimes — the
            // alphabet includes 2-9 — and it must not be read as a position.
            if (int.TryParse(said, out var n))
            {
                var i = n - 1;
                return i >= 0 && i < e.LastListing.Count ? e.LastListing[i] : null;
            }

            return null;
        }
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
        if (DateTimeOffset.UtcNow - e.At > Lifetime)
        {
            e = new Entry();
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
