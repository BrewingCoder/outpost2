using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Web.Connections;

public static class ConnectionEndpoints
{
    /// <summary>Our own user id, planted as a claim at sign-in so nothing re-derives identity.</summary>
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("op2:uid"), out var id)
            ? id
            : throw new InvalidOperationException("Signed-in principal carries no op2:uid claim.");

    public static void MapConnectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/connections").RequireAuthorization();

        // ── start a Microsoft connection ────────────────────────────────────
        group.MapPost("/microsoft/start", async (
            HttpContext ctx,
            IPendingConnectionStore pendings,
            IMicrosoftConnectionService microsoft,
            IConnectionStore connections,
            string label,
            string? tenantHint) =>
        {
            var userId = ctx.User.UserId();
            label = label.Trim();

            if (string.IsNullOrWhiteSpace(label))
                return Results.BadRequest("A name is required.");

            // Reject a clashing label before sending the user to Microsoft. Finding out
            // after consent would mean they authorised for nothing.
            if (await connections.FindByLabelAsync(userId, label) is not null)
                return Results.Redirect($"/connections?error={Uri.EscapeDataString($"You already have a connection called '{label}'.")}");

            var pending = await pendings.CreateAsync(userId, ProviderKind.Microsoft, label, tenantHint);
            var url = await microsoft.BuildAuthorizeUrlAsync(pending);
            return Results.Redirect(url.ToString());
        });

        // ── OAuth callback ──────────────────────────────────────────────────
        // One URI serves every tenant and every connection; `state` carries the context.
        app.MapGet("/signin-microsoft", async (
            HttpContext ctx,
            IPendingConnectionStore pendings,
            IMicrosoftConnectionService microsoft,
            ILoggerFactory logs,
            string? code,
            string? state,
            string? error,
            string? error_description) =>
        {
            var log = logs.CreateLogger("OutPost2.Connections");

            if (!string.IsNullOrEmpty(error))
            {
                // AADSTS65004 = user declined. Anything else is usually consent policy,
                // and the description is the only useful thing we will ever get.
                log.LogWarning("Microsoft returned {Error}: {Description}", error, error_description);
                return Results.Redirect($"/connections?error={Uri.EscapeDataString(Describe(error, error_description))}");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Redirect("/connections?error=Microsoft+returned+an+incomplete+response.");

            if (ctx.User.Identity?.IsAuthenticated != true)
                return Results.Redirect("/signin");

            // Consume checks existence, expiry, single-use AND ownership in one step.
            var pending = await pendings.ConsumeAsync(state, ctx.User.UserId());
            if (pending is null)
            {
                log.LogWarning("Rejected a Microsoft callback with an unknown, expired, reused or foreign state.");
                return Results.Redirect("/connections?error=That+sign-in+link+has+expired+or+was+already+used.+Please+try+again.");
            }

            try
            {
                var connection = await microsoft.CompleteAsync(pending, code);
                return Results.Redirect($"/connections?added={Uri.EscapeDataString(connection.Label)}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to complete Microsoft connection '{Label}'.", pending.Label);
                return Results.Redirect($"/connections?error={Uri.EscapeDataString("Could not finish connecting: " + ex.Message)}");
            }
        }).AllowAnonymous();

        // ── add a YouTrack connection (PAT, no redirect) ────────────────────
        group.MapPost("/youtrack/add", async (
            HttpContext ctx,
            IYouTrackConnectionService youtrack,
            IConnectionStore connections,
            string label, string baseUrl, string token) =>
        {
            var userId = ctx.User.UserId();
            label = label.Trim();

            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
                return Results.Redirect("/connections?error=Name,+server+and+token+are+all+required.");

            if (await connections.FindByLabelAsync(userId, label) is not null)
                return Results.Redirect($"/connections?error={Uri.EscapeDataString($"You already have a connection called '{label}'.")}");

            try
            {
                var c = await youtrack.ConnectAsync(userId, baseUrl, token, label);
                return Results.Redirect($"/connections?added={Uri.EscapeDataString(c.Label)}");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Redirect($"/connections?error={Uri.EscapeDataString(ex.Message)}");
            }
        });

        // ── re-check a connection's health ──────────────────────────────────
        group.MapPost("/{id:guid}/check", async (
            HttpContext ctx, IConnectionStore s, IYouTrackConnectionService yt, Guid id) =>
        {
            var c = await s.GetAsync(ctx.User.UserId(), id);
            if (c is null) return Results.Redirect("/connections");

            if (c.Provider == ProviderKind.YouTrack)
            {
                var ok = await yt.CheckHealthAsync(c);
                return Results.Redirect(ok
                    ? "/connections"
                    : $"/connections?error={Uri.EscapeDataString($"{c.Label} is no longer authenticating - the token may have been revoked or expired.")}");
            }

            return Results.Redirect("/connections");
        });

        // ── management ──────────────────────────────────────────────────────
        group.MapPost("/{id:guid}/default", async (HttpContext ctx, IConnectionStore s, Guid id) =>
        {
            await s.SetDefaultAsync(ctx.User.UserId(), id);
            return Results.Redirect("/connections");
        });

        group.MapPost("/{id:guid}/rename", async (HttpContext ctx, IConnectionStore s, Guid id, string label) =>
        {
            try { await s.RenameAsync(ctx.User.UserId(), id, label.Trim()); return Results.Redirect("/connections"); }
            catch (InvalidOperationException ex) { return Results.Redirect($"/connections?error={Uri.EscapeDataString(ex.Message)}"); }
        });

        group.MapPost("/{id:guid}/capability", async (
            HttpContext ctx, IConnectionStore s, Guid id, string capability, bool enabled) =>
        {
            try { await s.SetCapabilityEnabledAsync(ctx.User.UserId(), id, capability, enabled); return Results.Redirect("/connections"); }
            catch (InvalidOperationException ex) { return Results.Redirect($"/connections?error={Uri.EscapeDataString(ex.Message)}"); }
        });

        group.MapPost("/{id:guid}/delete", async (HttpContext ctx, IConnectionStore s, Guid id) =>
        {
            await s.DeleteAsync(ctx.User.UserId(), id);
            return Results.Redirect("/connections");
        });
    }

    /// <summary>
    /// Turn an Entra error code into something a person can act on. The raw codes are
    /// meaningless to a user and the descriptions are long enough to bury the point.
    /// </summary>
    private static string Describe(string error, string? description)
    {
        if (description?.Contains("AADSTS65004") == true) return "You declined the permission request.";
        if (description?.Contains("AADSTS90094") == true || error == "consent_required")
            return "Your tenant administrator needs to approve OutPost2 before you can connect this account.";
        if (description?.Contains("AADSTS50020") == true)
            return "That account does not exist in the tenant you selected.";

        var first = description?.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(first) ? $"Microsoft refused the request ({error})." : first;
    }
}
