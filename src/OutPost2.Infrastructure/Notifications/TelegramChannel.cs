using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OutPost2.Core.Notifications;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Notifications;

/// <summary>
/// Telegram, chosen as the first channel for a product whose surface is a phone in a car
/// (OP2-165).
/// <para>
/// The alternative worth stating is email, and it is the one thing that cannot be relied
/// on here: the event this exists for is most often "your mailbox is finally reachable",
/// so the mailbox is exactly what is unavailable to tell them with. Telegram needs no new
/// address verified against anything, reaches a phone in seconds and costs a bot token.
/// </para>
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "OutPost2:Telegram";

    /// <summary>
    /// A bot token from BotFather, deployment-wide. One bot speaks to every user of this
    /// deployment; the per-user part is the chat id.
    /// </summary>
    public string BotToken { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}

public sealed class TelegramChannel(
    OutPostDbContext db,
    IHttpClientFactory httpFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramChannel> log) : INotificationChannel
{
    private readonly TelegramOptions _o = options.Value;

    public string Name => "Telegram";

    public async Task<bool> SendAsync(Guid userId, Notification notification, CancellationToken ct = default)
    {
        if (!_o.IsConfigured) return false;

        var chatId = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.TelegramChatId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(chatId)) return false;

        return await SendToChatAsync(chatId, notification, ct);
    }

    /// <summary>
    /// Sends to a chat id directly, for the settings page's "send a test" button — which
    /// is the whole of the verification story. Telegram will not tell us whether a chat
    /// id is real and reachable by any means short of sending to it, so rather than
    /// pretending to validate, the user presses a button and looks at their phone.
    /// </summary>
    public async Task<bool> SendToChatAsync(string chatId, Notification notification, CancellationToken ct = default)
    {
        if (!_o.IsConfigured) return false;

        using var http = httpFactory.CreateClient("telegram");
        http.Timeout = TimeSpan.FromSeconds(15);

        var text = string.IsNullOrWhiteSpace(notification.Body)
            ? notification.Title
            : $"{notification.Title}\n\n{notification.Body}";

        if (!string.IsNullOrWhiteSpace(notification.Url)) text += $"\n\n{notification.Url}";

        // Plain text, no parse_mode. A connection label is the user's own words and a
        // stray underscore or asterisk in it would either break the message or silently
        // eat part of it — and the one message that must arrive intact is the one saying
        // which connection was approved.
        using var resp = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{_o.BotToken}/sendMessage",
            new { chat_id = chatId, text, disable_web_page_preview = true },
            ct);

        if (resp.IsSuccessStatusCode) return true;

        // The body carries Telegram's own description ("chat not found", "bot was blocked
        // by the user"), which is the only useful thing here — and the token is in the
        // URL, never in the body, so this is safe to log.
        log.LogWarning("Telegram refused the message: HTTP {Status} {Body}",
            (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));

        return false;
    }
}
