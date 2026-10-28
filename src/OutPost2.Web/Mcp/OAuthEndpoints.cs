using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Extensions;
using OutPost2.Infrastructure.Auth;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// We are an OAuth 2.1 authorization server to MCP clients, while simultaneously being an
/// OAuth client to GitHub (identity) and Microsoft (data). Three legs; keeping them
/// distinct is what makes per-connection token scoping possible at all.
/// </summary>
public static class OAuthEndpoints
{
    public static void MapMcpOAuth(this WebApplication app, string publicBaseUrl)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');

        // ── discovery ───────────────────────────────────────────────────────
        // RFC 9728. Claude probes this to learn WHERE to authenticate; a 404 here makes
        // the connector report the server as broken rather than as needing sign-in.
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
        {
            resource = baseUrl,
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = new[] { "header" },
        })).AllowAnonymous();

        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new
        {
            resource = $"{baseUrl}/mcp",
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = new[] { "header" },
        })).AllowAnonymous();

        // RFC 8414.
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
        {
            issuer = baseUrl,
            authorization_endpoint = $"{baseUrl}/oauth/authorize",
            token_endpoint = $"{baseUrl}/oauth/token",
            registration_endpoint = $"{baseUrl}/oauth/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            // S256 only. "plain" offers no protection and exists solely for clients that
            // cannot hash, which is not a category we need to serve.
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
        })).AllowAnonymous();

        // ── dynamic client registration (RFC 7591) ──────────────────────────
        app.MapPost("/oauth/register", async (RegistrationRequest req, IOAuthService oauth) =>
        {
            var redirects = (req.RedirectUris ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (redirects.Count == 0)
                return Results.BadRequest(new { error = "invalid_redirect_uri" });

            var client = await oauth.RegisterClientAsync(req.ClientName ?? "MCP client", redirects);

            return Results.Json(new
            {
                client_id = client.ClientId,
                client_name = client.ClientName,
                redirect_uris = client.Redirects(),
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
            }, statusCode: 201);
        }).AllowAnonymous();

        // ── authorize ───────────────────────────────────────────────────────
        // Requires a signed-in GitHub session. The user approves here, choosing which
        // connections this client may reach.
        app.MapGet("/oauth/authorize", async (
            HttpContext ctx, IOAuthService oauth,
            string client_id, string redirect_uri, string? state,
            string? code_challenge, string? code_challenge_method, string? resource) =>
        {
            var client = await oauth.FindClientAsync(client_id);
            if (client is null) return Results.BadRequest("Unknown client.");

            // Exact match, never a prefix: a loose comparison here is an open redirect
            // that leaks authorization codes to an attacker-controlled host.
            if (!client.Redirects().Contains(redirect_uri, StringComparer.Ordinal))
                return Results.BadRequest("redirect_uri does not match this client's registration.");

            if (string.IsNullOrEmpty(code_challenge) || code_challenge_method != "S256")
                return Results.BadRequest("PKCE with S256 is required.");

            // RFC 8707. The client names the resource it intends the token for; if that
            // is some OTHER server, refuse rather than mint a credential it could then
            // present there. Nothing stops it asking that server directly — the point is
            // that this one does not help, which is what makes a confused deputy possible.
            if (!string.IsNullOrEmpty(resource) && !IdentifiesThisServer(resource, publicBaseUrl))
                return Results.BadRequest("resource does not identify this server.");

            if (ctx.User.Identity?.IsAuthenticated != true)
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = ctx.Request.GetEncodedPathAndQuery() },
                    [CookieAuthenticationDefaults.AuthenticationScheme]);

            // Show the consent screen rather than auto-approving. The user is about to
            // hand a third party standing access to their mail; that deserves a decision.
            var qs = $"?client_id={Uri.EscapeDataString(client_id)}" +
                     $"&redirect_uri={Uri.EscapeDataString(redirect_uri)}" +
                     $"&code_challenge={Uri.EscapeDataString(code_challenge)}" +
                     $"&state={Uri.EscapeDataString(state ?? "")}";

            return Results.Redirect("/authorize" + qs);
        }).AllowAnonymous();

        // The consent form posts here.
        app.MapPost("/oauth/approve", async (
            HttpContext ctx, IOAuthService oauth,
            [FromForm] string client_id, [FromForm] string redirect_uri,
            [FromForm] string code_challenge, [FromForm] string? state,
            [FromForm] string? connections) =>
        {
            var client = await oauth.FindClientAsync(client_id);
            if (client is null || !client.Redirects().Contains(redirect_uri, StringComparer.Ordinal))
                return Results.BadRequest("Invalid client or redirect.");

            var scope = (connections ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var code = await oauth.IssueAuthCodeAsync(
                client_id, ctx.User.UserId(), redirect_uri, code_challenge, scope);

            var sep = redirect_uri.Contains('?') ? "&" : "?";
            var back = $"{redirect_uri}{sep}code={Uri.EscapeDataString(code)}";
            if (!string.IsNullOrEmpty(state)) back += $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(back);
        }).RequireAuthorization();

        // ── token ───────────────────────────────────────────────────────────
        app.MapPost("/oauth/token", async (HttpContext ctx, IOAuthService oauth) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var grantType = form["grant_type"].ToString();
            var clientId = form["client_id"].ToString();

            TokenGrant? grant = grantType switch
            {
                "authorization_code" => await oauth.RedeemCodeAsync(
                    form["code"].ToString(), clientId,
                    form["redirect_uri"].ToString(), form["code_verifier"].ToString()),

                "refresh_token" => await oauth.RefreshAsync(form["refresh_token"].ToString(), clientId),

                _ => null,
            };

            if (grant is null)
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

            return Results.Json(new
            {
                access_token = grant.AccessToken,
                refresh_token = grant.RefreshToken,
                token_type = "Bearer",
                expires_in = grant.ExpiresInSeconds,
            });
        }).AllowAnonymous();

        // ── revoke, from the web app ────────────────────────────────────────
        app.MapPost("/oauth/revoke-token", async (HttpContext ctx, IOAuthService oauth, string hash) =>
        {
            await oauth.RevokeAsync(ctx.User.UserId(), hash);
            return Results.Redirect("/connections");
        }).RequireAuthorization();
    }

    public sealed record RegistrationRequest(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris);

    /// <summary>
    /// Whether an RFC 8707 <c>resource</c> value names this deployment. Accepts the site
    /// root and the MCP endpoint, with or without a trailing slash, because clients
    /// legitimately differ on which they send — but compares host and path rather than
    /// prefix-matching, so "https://outpost.example.com.evil.test/mcp" is not a match.
    /// </summary>
    public static bool IdentifiesThisServer(string resource, string publicBaseUrl)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var asked)) return false;
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var ours)) return false;

        if (!string.Equals(asked.Host, ours.Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (asked.Port != ours.Port) return false;

        var path = asked.AbsolutePath.TrimEnd('/');
        return path.Length == 0 || path.Equals("/mcp", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Authenticates <c>/mcp</c> from a Bearer token.
/// <para>
/// A failure must be a 401 carrying <c>WWW-Authenticate</c> with the resource-metadata
/// pointer — not the cookie pipeline's 302 to an HTML sign-in page. An MCP client cannot
/// follow a redirect to a login form; it reads the challenge header to discover where to
/// authenticate, and a 302 makes the server look broken rather than unauthenticated.
/// </para>
/// </summary>
public sealed class McpBearerMiddleware(RequestDelegate next, string publicBaseUrl)
{
    public async Task InvokeAsync(HttpContext ctx, IOAuthService oauth)
    {
        var header = ctx.Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await ChallengeAsync(ctx, "missing_token");
            return;
        }

        var token = await oauth.ValidateAsync(header["Bearer ".Length..].Trim());
        if (token is null)
        {
            await ChallengeAsync(ctx, "invalid_token");
            return;
        }

        var identity = new ClaimsIdentity("mcp");
        identity.AddClaim(new Claim("op2:uid", token.UserId.ToString()));
        identity.AddClaim(new Claim("op2:client", token.ClientId));
        if (token.ConnectionScope.Length > 0)
            identity.AddClaim(new Claim("op2:connections", token.ConnectionScope.Replace('\n', ',')));

        ctx.User = new ClaimsPrincipal(identity);
        await next(ctx);
    }

    private async Task ChallengeAsync(HttpContext ctx, string error)
    {
        // Path-aware, per RFC 9728 §3.1. The generic document claims a resource of the
        // bare origin, which is not the resource being protected here — a client that
        // checks the document it was sent to against the endpoint that sent it is right
        // to object. Pointing at the /mcp document makes the two agree.
        var meta = $"{publicBaseUrl.TrimEnd('/')}/.well-known/oauth-protected-resource/mcp";

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"{error}\", resource_metadata=\"{meta}\"";

        await ctx.Response.WriteAsJsonAsync(new { error, resource_metadata = meta });
    }
}
