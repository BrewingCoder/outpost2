namespace OutPost2.Core.Notifications;

/// <summary>
/// Something that happened while the user was not looking (OP2-165).
/// <para>
/// Deliberately small, and deliberately not a template id. Every surface of this product
/// answers in prose because the primary one is spoken; a notification is the same thing
/// said to a phone, so the sentence is composed where the event happens, by the code that
/// knows what happened, and the channel's only job is to carry it.
/// </para>
/// </summary>
/// <param name="Title">One short line. Read on a lock screen, so it has to stand alone.</param>
/// <param name="Body">
/// What happened and what it means, in a sentence or two. May be empty where the title
/// already says everything.
/// </param>
/// <param name="Url">
/// Where to go to act on it, absolute. Optional: plenty of these need nothing doing, and
/// a link on one of those trains the reader to ignore the ones that do.
/// </param>
/// <param name="Kind">
/// What sort of event this is, for the user's own filtering later and for the log now.
/// Not shown to the user.
/// </param>
public sealed record Notification(string Title, string Body = "", string? Url = null,
                                  NotificationKind Kind = NotificationKind.Other);

public enum NotificationKind
{
    Other,

    /// <summary>A tenant administrator approved a connection that was waiting (OP2-100).</summary>
    AdminApproval,

    /// <summary>A queued job finished, one way or the other (OP2-11).</summary>
    JobFinished,
}

/// <summary>
/// One way of reaching a user. Implementations are registered side by side and all of
/// them are offered every notification; each decides whether it has anything to send to.
/// </summary>
public interface INotificationChannel
{
    /// <summary>For logs and for the settings page. Short, lower case: "Telegram".</summary>
    string Name { get; }

    /// <summary>
    /// Sends, or does nothing if this user has not set this channel up.
    /// </summary>
    /// <returns>True if something was actually sent.</returns>
    Task<bool> SendAsync(Guid userId, Notification notification, CancellationToken ct = default);
}

/// <summary>
/// The one place that says "tell this user that X happened" (OP2-165).
/// <para>
/// Callers do not choose a channel and do not handle failure. A notification is a
/// courtesy on top of work that has already succeeded, so a channel that is down, slow or
/// unconfigured must never turn a completed operation into a failed one — the whole point
/// is that the user finds out, and the fallback when they do not is the same settings page
/// and the same spoken answer they had before this existed.
/// </para>
/// </summary>
public interface INotifier
{
    Task NotifyAsync(Guid userId, Notification notification, CancellationToken ct = default);
}
