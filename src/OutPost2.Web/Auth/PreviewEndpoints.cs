using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OutPost2.Core.Auth;
using OutPost2.Infrastructure.Auth;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Auth;

public static class PreviewEndpoints
{
    /// <summary>Marks a cookie session as having come from a preview link, not a sign-in.</summary>
    public const string PreviewClaim = "op2:preview";

    public static bool IsPreview(this ClaimsPrincipal user) =>
        user.HasClaim(c => c.Type == PreviewClaim && c.Value == "1");

    public static void MapPreviewLinks(this WebApplication app)
    {
        // ── redeem ──────────────────────────────────────────────────────────
        // Anonymous by necessity: the whole point is that the caller cannot sign in.
        app.MapGet("/preview", async (
            HttpContext ctx, IPreviewLinkService links, [FromQuery] string? t, [FromQuery] string? to) =>
        {
            var userId = await links.RedeemAsync(t ?? "", Describe(ctx));

            // One message for unknown, expired and revoked alike. Telling them apart tells
            // a prober which of their guesses was once real.
            if (userId is null) return Results.Redirect("/denied?reason=preview");

            var identity = new ClaimsIdentity(
            [
                new Claim("op2:uid", userId.Value.ToString()),
                new Claim(ClaimTypes.Name, "preview"),
                new Claim(PreviewClaim, "1"),
            ], CookieAuthenticationDefaults.AuthenticationScheme);

            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    // Capped independently of the link. A link good for a week should not
                    // mint a week-long cookie on its last day.
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12),
                });

            // Straight back out to a clean URL. The token has now been in the address bar
            // for exactly one request, which is the difference between this and pasting a
            // credential into a query string and leaving it there.
            return Results.Redirect(SafeTarget(to));
        }).AllowAnonymous();

        // ── manage, from the signed-in web app ──────────────────────────────
        app.MapPost("/preview/mint", async (
            HttpContext ctx, IPreviewLinkService links,
            [FromForm] string label, [FromForm] int? hours) =>
        {
            var lifetime = hours is > 0 ? TimeSpan.FromHours(hours.Value) : (TimeSpan?)null;
            var (link, token) = await links.MintAsync(ctx.User.UserId(), label, lifetime);

            // Handed back in the query string so the page can show it once. It is never
            // stored, so navigating away loses it — which is the intended behaviour.
            return Results.Redirect(
                $"/preview-links?token={Uri.EscapeDataString(token)}" +
                $"&expires={Uri.EscapeDataString(link.ExpiresAt.ToString("u"))}");
        }).RequireAuthorization();

        app.MapPost("/preview/revoke", async (
            HttpContext ctx, IPreviewLinkService links, [FromForm] string hash) =>
        {
            await links.RevokeAsync(ctx.User.UserId(), hash);
            return Results.Redirect("/preview-links");
        }).RequireAuthorization();
    }

    /// <summary>
    /// Only ever a local path, and never back into /preview. An open redirect here would
    /// turn the link into a way to bounce someone off a trusted hostname.
    /// </summary>
    private static string SafeTarget(string? to)
    {
        if (string.IsNullOrWhiteSpace(to)) return "/connections";
        if (!to.StartsWith('/') || to.StartsWith("//")) return "/connections";
        if (to.StartsWith("/preview", StringComparison.OrdinalIgnoreCase)) return "/connections";
        return to;
    }

    private static string Describe(HttpContext ctx)
    {
        // Cloudflare rewrites the socket address, so the forwarded header is the only
        // thing that says where this actually came from.
        var ip = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                 ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                 ?? ctx.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown";

        var ua = ctx.Request.Headers.UserAgent.FirstOrDefault() ?? "";
        return $"{ip} {ua}".Trim();
    }
}

/// <summary>
/// Confines a preview session to reading. Enforced here rather than by hiding buttons,
/// because the thing holding this session is an automated client that will follow forms
/// and links it finds, and "the UI does not offer it" is not a boundary.
/// </summary>
public sealed class PreviewReadOnlyMiddleware(RequestDelegate next, ILogger<PreviewReadOnlyMiddleware> log)
{
    /// <summary>
    /// Paths a preview session may not touch at all, by any method.
    /// <para>
    /// The OAuth endpoints are the important entry. Without this, a preview session could
    /// walk the authorize flow and mint itself a full MCP bearer token — turning a
    /// read-only, expiring design link into permanent API access to the user's mail. The
    /// consent screen is a GET followed by a POST, and blocking writes alone would stop
    /// it, but defence here does not depend on that ordering holding forever.
    /// </para>
    /// </summary>
    private static readonly string[] Forbidden =
    [
        "/oauth", "/mcp", "/preview/mint", "/preview/revoke", "/signin-github",
    ];

    /// <summary>
    /// Paths that serve bytes rather than UI. Blocked by suffix because the Files PAGE and
    /// the file API share the /files prefix — blanket-blocking it made the page itself 403,
    /// which is worse than the thing the rule was guarding against.
    /// </summary>
    private static bool ServesBytes(string path) =>
        path.EndsWith("/download", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsPreview())
        {
            var path = ctx.Request.Path.Value ?? "";

            if (Forbidden.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || ServesBytes(path))
            {
                log.LogWarning("Preview session blocked from {Method} {Path}.", ctx.Request.Method, path);
                await RefuseAsync(ctx, "That part of Outpost is not available in a preview session.");
                return;
            }

            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                log.LogWarning("Preview session blocked from {Method} {Path}.", ctx.Request.Method, path);
                await RefuseAsync(ctx, "A preview session can look at everything and change nothing.");
                return;
            }
        }

        await next(ctx);
    }

    /// <summary>
    /// A readable page rather than a bare string. Something is driving this session
    /// automatically, and a wall of plain text with a 403 looks like a broken app rather
    /// than a deliberate boundary.
    /// </summary>
    private static async Task RefuseAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "text/html; charset=utf-8";

        await ctx.Response.WriteAsync($"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Read-only &middot; Outpost</title>
            <link rel="stylesheet" href="/css/fonts.css"><link rel="stylesheet" href="/css/outpost.css">
            </head><body style="margin:0"><div class="op-app"><main class="op-page">
            <h1 class="op-h1">Read-only</h1>
            <p class="op-lede">{System.Net.WebUtility.HtmlEncode(message)}</p>
            <p><a class="op-btn op-btn--secondary" href="/connections">Back to connections</a></p>
            </main></div></body></html>
            """);
    }
}
