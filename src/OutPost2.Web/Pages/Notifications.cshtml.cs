using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OutPost2.Core.Notifications;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Notifications;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

/// <summary>
/// Where a user says how to reach them (OP2-165).
/// </summary>
[Authorize]
public class NotificationsModel(
    OutPostDbContext db,
    TelegramChannel telegram,
    IOptions<TelegramOptions> options) : PageModel
{
    /// <summary>
    /// False when the deployment has no bot token. The page then explains that rather
    /// than offering a field that could never work — a setting that silently does
    /// nothing is worse than an absent one.
    /// </summary>
    public bool TelegramAvailable => options.Value.IsConfigured;

    public string? ChatId { get; private set; }

    public string? Saved { get; private set; }
    public string? Problem { get; private set; }

    public async Task OnGetAsync(string? saved, string? problem)
    {
        ChatId = await db.Users.Where(u => u.Id == User.UserId())
            .Select(u => u.TelegramChatId).FirstOrDefaultAsync();

        Saved = saved;
        Problem = problem;
    }

    public async Task<IActionResult> OnPostAsync(string? chatId)
    {
        var user = await db.Users.FirstAsync(u => u.Id == User.UserId());

        // Blank clears it, which is how somebody turns notifications off. Trimmed
        // because a pasted chat id arrives with whitespace more often than not.
        user.TelegramChatId = string.IsNullOrWhiteSpace(chatId) ? null : chatId.Trim();
        await db.SaveChangesAsync();

        return RedirectToPage(new { saved = user.TelegramChatId is null ? "off" : "on" });
    }

    /// <summary>
    /// The verification, such as it is. Telegram will not say whether a chat id is real
    /// and reachable without sending to it, so rather than validating the shape of the
    /// field and calling that a check, the user presses this and looks at their phone.
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        var chatId = await db.Users.Where(u => u.Id == User.UserId())
            .Select(u => u.TelegramChatId).FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(chatId))
            return RedirectToPage(new { problem = "Save a chat id first." });

        var ok = await telegram.SendToChatAsync(chatId, new Notification(
            "Outpost can reach you",
            "This is the test message. Nothing else is needed — the next thing you hear from "
            + "here will be something that actually happened."));

        return ok
            ? RedirectToPage(new { saved = "test" })
            : RedirectToPage(new { problem = "Telegram would not take it. Check the chat id, and that you have sent the bot a message first — a bot cannot open a conversation." });
    }
}
