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
            //
            // Except on a connection parked for admin approval (OP2-100), which has never
            // been signed in to: its "tenant" is whatever the user hinted, or the
            // `organizations` placeholder, and handing that placeholder to MSAL as a
            // tenant id would produce an authority it cannot build. Null means "ask the
            // normal way", which is exactly right for a connection that has no directory
            // of its own yet.
            var hint = connection.Instance == Connection.UnknownTenant ? null : connection.Instance;

            var pending = await pendings.CreateAsync(
                userId, ProviderKind.Microsoft, connection.Label, hint,
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
            IConnectionStore connections,
            ILoggerFactory logs,
            string? code,
            string? state,
            string? error,
            string? error_description,
            string? admin_consent) =>
        {
            var log = logs.CreateLogger("OutPost2.Connections");

            // The administrator coming back from the admin-consent endpoint, which shares
            // this URI (see EntraOptions.AdminConsentRedirectUri). Entra sets
            // admin_consent=True on BOTH its success and its error redirect and never on
            // a sign-in callback, so it is the discriminator — and it has to be tested
            // before the code/state check below, because an admin consent response
            // carries no code at all and would otherwise read as "incomplete".
            if (!string.IsNullOrEmpty(admin_consent))
                return await HandleAdminConsentAsync(connections, log, state, error, error_description, ctx);

            if (!string.IsNullOrEmpty(error))
            {
                // AADSTS65004 = user declined. Anything else is usually consent policy,
                // and the description is the only useful thing we will ever get.
                log.LogWarning("Microsoft returned {Error}: {Description}", error, error_description);

                if (NeedsAdminApproval(error, error_description) && !string.IsNullOrEmpty(state))
                    return await ParkAsync(ctx, pendings, microsoft, connections, log, state);

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
    /// Whether Entra refused because the tenant's administrator has to consent (OP2-100),
    /// as opposed to any of the other reasons a sign-in comes back empty-handed.
    /// <para>
    /// Both forms are checked. <c>consent_required</c> is the clean one, but Entra also
    /// answers <c>access_denied</c> with AADSTS90094 ("the grant requires admin
    /// permission") in the description, and AADSTS65001 where an administrator has
    /// previously declined — and the description is the only place either appears.
    /// </para>
    /// </summary>
    private static bool NeedsAdminApproval(string? error, string? description) =>
        error == "consent_required"
        || description?.Contains("AADSTS90094", StringComparison.Ordinal) == true
        || description?.Contains("AADSTS65001", StringComparison.Ordinal) == true;

    /// <summary>
    /// Turn a refusal that needs an administrator into something durable the user can
    /// act on: a connection parked in <see cref="ConnectionState.NeedsAdminApproval"/>,
    /// carrying the link to forward (OP2-100).
    /// </summary>
    private static async Task<IResult> ParkAsync(
        HttpContext ctx,
        IPendingConnectionStore pendings,
        IMicrosoftConnectionService microsoft,
        IConnectionStore connections,
        ILogger log,
        string state)
    {
        if (ctx.User.Identity?.IsAuthenticated != true) return Results.Redirect("/signin");

        // Same consume as the success path: this state is spent either way, and a refused
        // attempt that left it live would leave a usable sign-in link lying around.
        var pending = await pendings.ConsumeAsync(state, ctx.User.UserId());
        if (pending is null)
            return Results.Redirect("/connections?error=That+sign-in+link+has+expired+or+was+already+used.+Please+try+again.");

        // Whatever the user told us, or Entra's own multi-tenant placeholder. A hint is
        // usually present precisely because a user who knows their tenant is blocked is
        // the user who typed it in — and where it is absent, `organizations` still works:
        // the administrator signs in and consent lands in whichever directory they
        // administer. `common` would NOT work, and Microsoft documents why: a personal
        // account cannot grant admin consent outside a tenant context.
        var tenant = string.IsNullOrWhiteSpace(pending.TenantHint)
            ? Connection.UnknownTenant
            : pending.TenantHint.Trim();

        await connections.ParkForAdminApprovalAsync(
            pending.UserId, ProviderKind.Microsoft, tenant, pending.Label,

            // Nothing was granted, so nothing is Granted — but every capability we would
            // have asked for is declared, or the resolver would answer a request against
            // this connection with "no connection by that name".
            microsoft.DeclaredCapabilities,

            // The connection's own id is the `state` Entra echoes back. Durable, which a
            // PendingConnection deliberately is not: the administrator may follow this
            // link days later, in a different browser, with no OutPost2 session at all.
            id => microsoft.BuildAdminConsentUrl(tenant, id.ToString()).ToString());

        log.LogInformation(
            "Parked Microsoft connection '{Label}' for tenant {Tenant} awaiting admin approval.",
            pending.Label, tenant);

        return Results.Redirect($"/connections?needsadmin={Uri.EscapeDataString(pending.Label)}");
    }

    /// <summary>
    /// The administrator's return trip from Entra's admin-consent endpoint (OP2-100).
    /// <para>
    /// Anonymous by necessity: the person on this request is a tenant administrator, who
    /// usually has no OutPost2 account and is certainly not signed in to one. Everything
    /// it is allowed to do is therefore bounded by what a stranger holding a connection
    /// id may do — which is to record that an approval happened, and nothing else.
    /// </para>
    /// <para>
    /// In particular the <c>tenant</c> parameter Entra returns is read and discarded.
    /// Microsoft's own documentation warns against trusting it ("the tenant ID value can
    /// be updated and sent by bad actors to impersonate a response"), and we do not need
    /// it: the tenant this connection belongs to is whatever the user actually signs in
    /// to afterwards, recorded then from the token rather than from a query string.
    /// </para>
    /// </summary>
    private static async Task<IResult> HandleAdminConsentAsync(
        IConnectionStore connections,
        ILogger log,
        string? state,
        string? error,
        string? description,
        HttpContext ctx)
    {
        if (!string.IsNullOrEmpty(error))
        {
            log.LogWarning("Admin consent was refused: {Error} {Description}", error, description);
            return AdminPage(ctx,
                "Nothing was approved",
                "Microsoft reported that the request was declined or could not be completed. " +
                "Whoever sent you this link can generate a fresh one from their Outpost settings.");
        }

        if (!Guid.TryParse(state, out var connectionId))
        {
            log.LogWarning("Admin consent callback carried an unusable state.");
            return AdminPage(ctx, "That link is not valid",
                "Ask the person who sent it to generate a new one.");
        }

        var connection = await connections.MarkAdminConsentGrantedAsync(connectionId);

        if (connection is null)
        {
            // Already completed, deleted, or an id that never existed. All three are the
            // same sentence to this reader, and distinguishing them would tell a stranger
            // whether a given id is real.
            log.LogInformation("Admin consent callback for {ConnectionId} matched nothing waiting.", connectionId);
            return AdminPage(ctx, "Nothing left to approve",
                "This request has already been dealt with, or was withdrawn. No action is needed.");
        }

        log.LogInformation("Admin consent granted for connection {ConnectionId}.", connectionId);

        return AdminPage(ctx, "Approved — thank you",
            "Outpost can now be used in your organisation. The person who sent you this link " +
            "needs to sign in once more to finish connecting their mailbox; they will see that " +
            "on their settings page.");
    }

    /// <summary>
    /// The page the administrator sees. Deliberately a self-contained response rather than
    /// a Razor page in the signed-in site: this reader is not a user, has no session, and
    /// should be shown one sentence and no navigation into an app that is not theirs.
    /// <para>
    /// Nothing from the query string reaches it. The two arguments are both literals
    /// chosen above, so there is no untrusted text to encode and no way to turn a crafted
    /// callback into a page that says whatever the caller wanted it to say.
    /// </para>
    /// </summary>
    private static IResult AdminPage(HttpContext ctx, string heading, string body)
    {
        ctx.Response.Headers.CacheControl = "no-store";

        // $$ and {{…}}: a raw string literal has no {{ escape, so the CSS braces below
        // would otherwise each need doubling and the stylesheet would stop being readable.
        return Results.Content($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Outpost</title>
            <style>
              :root { color-scheme: light dark; }
              body { font: 16px/1.6 system-ui, sans-serif; margin: 0;
                     display: grid; place-items: center; min-height: 100vh; padding: 24px; }
              main { max-width: 34rem; }
              h1 { font-size: 1.4rem; margin: 0 0 .6rem; }
              p { margin: 0; opacity: .85; }
            </style></head>
            <body><main><h1>{{heading}}</h1><p>{{body}}</p></main></body></html>
            """, "text/html; charset=utf-8");
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
