using Microsoft.Extensions.Logging;
using OutPost2.Core.Notifications;

namespace OutPost2.Infrastructure.Notifications;

/// <summary>
/// Fans a notification out to every configured channel, and never throws (OP2-165).
/// </summary>
public sealed class Notifier(IEnumerable<INotificationChannel> channels, ILogger<Notifier> log) : INotifier
{
    public async Task NotifyAsync(Guid userId, Notification notification, CancellationToken ct = default)
    {
        var sent = 0;

        foreach (var channel in channels)
        {
            try
            {
                if (await channel.SendAsync(userId, notification, ct)) sent++;
            }
            catch (Exception ex)
            {
                // Swallowed on purpose, and this is the whole contract of this class. The
                // caller has already done the real work — approved a connection, finished
                // a send — and a courtesy message failing must not undo it or surface as
                // an error on a page the user is reading for good news.
                log.LogWarning(ex, "{Channel} could not notify {UserId} about {Kind}.",
                    channel.Name, userId, notification.Kind);
            }
        }

        if (sent == 0)
        {
            // Not a warning. Having no channel is the ordinary state and the product works
            // without one: the settings page says what happened and the next tool call
            // says it out loud. Logged so that "I never got told" can be answered.
            log.LogInformation("No notification channel for {UserId}; {Kind} went unsent.",
                userId, notification.Kind);
        }
    }
}
