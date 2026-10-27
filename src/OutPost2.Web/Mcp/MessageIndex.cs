using System.Collections.Concurrent;

namespace OutPost2.Web.Mcp;

/// <summary>
/// Remembers the last listing per user so the next turn can say "read the second one".
/// <para>
/// Graph message ids are long base64 strings — unspeakable, unreadable aloud, and
/// impossible to use hands-free. Per D1 there is no screen to copy one from either. So
/// ordinals are not a convenience here, they are the only workable addressing scheme.
/// </para>
/// <para>
/// Deliberately in-memory and short-lived: it is conversational scratch, not state worth
/// persisting. Losing it costs one "list them again", which is the right failure.
/// </para>
/// </summary>
public interface IMessageIndex
{
    void Remember(Guid userId, IReadOnlyList<(string MessageId, string ConnectionLabel)> ordered);
    (string MessageId, string ConnectionLabel)? Resolve(Guid userId, int oneBasedNumber);
}

public sealed class MessageIndex : IMessageIndex
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, Entry> _byUser = new();

    private sealed record Entry(IReadOnlyList<(string MessageId, string ConnectionLabel)> Items, DateTimeOffset At);

    public void Remember(Guid userId, IReadOnlyList<(string MessageId, string ConnectionLabel)> ordered) =>
        _byUser[userId] = new Entry(ordered, DateTimeOffset.UtcNow);

    public (string MessageId, string ConnectionLabel)? Resolve(Guid userId, int oneBasedNumber)
    {
        if (!_byUser.TryGetValue(userId, out var e)) return null;

        // Expire rather than resolve against a stale listing: acting on "number 2" from an
        // hour ago could read - or later delete - an entirely different message.
        if (DateTimeOffset.UtcNow - e.At > Lifetime)
        {
            _byUser.TryRemove(userId, out _);
            return null;
        }

        var i = oneBasedNumber - 1;
        return i >= 0 && i < e.Items.Count ? e.Items[i] : null;
    }
}
