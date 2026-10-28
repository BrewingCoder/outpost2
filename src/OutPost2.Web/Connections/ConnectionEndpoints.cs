using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
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
            [FromForm] string label,
            [FromForm] string? tenantHint) =>
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

            // BuildAuthorizeUrlAsync replaces the verifier with MSAL's own, so the record
            // must be saved again afterwards or redemption will present the wrong one.
            var url = await microsoft.BuildAuthorizeUrlAsync(pending);
            await pendings.SaveAsync(pending);

            return Results.Redirect(url.ToString());
        });

        // ── re-authorise an existing Microsoft connection ───────────────────
        // Needed because /microsoft/start cannot serve this: it rejects a label that
        // already exists — correctly, since finding out after consent would waste the
        // user's time — but that check runs before anyone knows which account will be
        // picked, so it also blocks renewing the connection that owns the name. Without
        // this the only route was remove-and-re-add, which throws away the label, the
        // default and every capability toggle, to fix a credential.
        group.MapPost("/{id:guid}/reconnect", async (
            HttpContext ctx,
            IPendingConnectionStore pendings,
            IMicrosoftConnectionService microsoft,
            IConnectionStore connections,
            Guid id) =>
        {
            var userId = ctx.User.UserId();

            var connection = await connections.GetAsync(userId, id);
            if (connection is null) return Results.NotFound();

            if (connection.Provider != ProviderKind.Microsoft)
                return Results.Redirect("/connections?error=" +
                    Uri.EscapeDataString($"{connection.Label} isn't a Microsoft connection — replace its token instead."));

            // The tenant id, not a domain: it targets the exact directory this connection
            // already belongs to, so the user cannot land in the wrong one by habit.
            var pending = await pendings.CreateAsync(
                userId, ProviderKind.Microsoft, connection.Label, connection.Instance,
                connection.HomeAccountId);

            var url = await microsoft.BuildAuthorizeUrlAsync(pending);
            await pendings.SaveAsync(pending);

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

                // Reconnecting an existing mailbox is not "added", and saying so sends the
                // user hunting for a second entry that does not exist.
                return Results.Redirect(pending.IsReconnect
                    ? $"/connections?reconnected={Uri.EscapeDataString(connection.Label)}"
                    : $"/connections?added={Uri.EscapeDataString(connection.Label)}");
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
            [FromForm] string label, [FromForm] string baseUrl, [FromForm] string token) =>
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

        // ── add a GitHub connection (PAT, no redirect) ──────────────────────
        group.MapPost("/github/add", async (
            HttpContext ctx,
            IGitHubConnectionService github,
            IConnectionStore connections,
            [FromForm] string label, [FromForm] string? baseUrl, [FromForm] string token) =>
        {
            var userId = ctx.User.UserId();
            label = label.Trim();

            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(token))
                return Results.Redirect("/connections?error=Name+and+token+are+both+required.");

            if (await connections.FindByLabelAsync(userId, label) is not null)
                return Results.Redirect($"/connections?error={Uri.EscapeDataString($"You already have a connection called '{label}'.")}");

            try
            {
                // Blank means github.com. Only Enterprise users need to say anything.
                var c = await github.ConnectAsync(userId, baseUrl ?? "", token, label);
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

        // Leaves the provider with no default, so unlabelled requests must name one.
        group.MapPost("/{id:guid}/undefault", async (HttpContext ctx, IConnectionStore s, Guid id) =>
        {
            var userId = ctx.User.UserId();
            var c = await s.GetAsync(userId, id);
            if (c is not null) await s.ClearDefaultAsync(userId, c.Provider);
            return Results.Redirect("/connections");
        });

        group.MapPost("/{id:guid}/rename", async (HttpContext ctx, IConnectionStore s, Guid id, [FromForm] string label) =>
        {
            try { await s.RenameAsync(ctx.User.UserId(), id, label.Trim()); return Results.Redirect("/connections"); }
            catch (InvalidOperationException ex) { return Results.Redirect($"/connections?error={Uri.EscapeDataString(ex.Message)}"); }
        });

        group.MapPost("/{id:guid}/capability", async (
            HttpContext ctx, IConnectionStore s, Guid id,
            [FromForm] string capability, [FromForm] bool enabled) =>
        {
            try { await s.SetCapabilityEnabledAsync(ctx.User.UserId(), id, capability, enabled); return Results.Redirect("/connections"); }
            catch (InvalidOperationException ex) { return Results.Redirect($"/connections?error={Uri.EscapeDataString(ex.Message)}"); }
        });

        // Swapping a lapsed or rotated token without losing the connection, its name or
        // its capability choices. Re-running "add" would work at the storage layer, since
        // upsert matches on identity, but the add endpoint rejects a name already in use —
        // so from the user's side the only route was delete and start again.
        group.MapPost("/{id:guid}/replace-token", async (
            HttpContext ctx, IConnectionStore s,
            IYouTrackConnectionService yt, IGitHubConnectionService gh,
            Guid id, [FromForm] string token) =>
        {
            var userId = ctx.User.UserId();
            var c = await s.GetAsync(userId, id);
            if (c is null) return Results.Redirect("/connections?error=No+such+connection.");

            if (string.IsNullOrWhiteSpace(token))
                return Results.Redirect("/connections?error=Paste+a+token+first.");

            try
            {
                _ = c.Provider switch
                {
                    ProviderKind.YouTrack => await yt.ConnectAsync(userId, c.Instance, token, c.Label),
                    ProviderKind.GitHub => await gh.ConnectAsync(userId, c.Instance, token, c.Label),
                    _ => throw new InvalidOperationException(
                        "That connection signs in through its provider. Reconnect it instead."),
                };

                return Results.Redirect($"/connections?replaced={Uri.EscapeDataString(c.Label)}");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Redirect($"/connections?error={Uri.EscapeDataString(ex.Message)}");
            }
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
