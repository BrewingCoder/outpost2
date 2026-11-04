using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

/// <summary>
/// The Google OAuth client Outpost signs in with (OP2-279). Configured like
/// <c>EntraOptions</c>; when it is not, the Gmail option is simply not offered.
/// </summary>
public sealed class GoogleOptions
{
    public const string SectionName = "OutPost2:Google";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Must match an "Authorised redirect URI" on the OAuth client exactly — scheme, host, path.</summary>
    public string RedirectUri { get; set; } = "";

    // Google's documented endpoints. Configurable only so a test can point them at a fake;
    // nothing in a deployment should ever set them.
    public string AuthorizeEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}

/// <summary>An access token for a Google connection, for the Gmail provider (OP2-280 onward).</summary>
public interface IGoogleTokenAccessor
{
    /// <summary>
    /// A current access token, refreshing it when needed. Null when the connection needs
    /// the user to sign in again — the refresh token was revoked, expired, or (for an app
    /// still in Google's testing mode) aged out after seven days. The connection has been
    /// marked Expired by then, so the resolver and the connections page already say so.
    /// </summary>
    Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default);
}

public interface IGoogleConnectionService : IGoogleTokenAccessor
{
    bool IsConfigured { get; }

    /// <summary>
    /// Where to send the browser. <paramref name="loginHint"/> pre-selects an account on
    /// Google's chooser — the connection's own address, on a reconnect.
    /// </summary>
    Uri BuildAuthorizeUrl(PendingConnection pending, string? loginHint = null);

    /// <summary>Redeems the authorization code and creates or updates the connection.</summary>
    Task<Connection> CompleteAsync(PendingConnection pending, string code, CancellationToken ct = default);
}

/// <summary>
/// Signing in to Google for Gmail, and keeping the sign-in alive (OP2-279).
/// <para>
/// Written against Google's documented OAuth 2.0 web-server flow and OpenID Connect, not
/// against a live server (each deployment brings its own OAuth client; README, Google setup). The parts that
/// matter and are easy to get wrong:
/// </para>
/// <list type="bullet">
/// <item><c>access_type=offline</c> is what makes Google issue a refresh token at all, and
/// <c>prompt=consent</c> is what makes it issue one on EVERY sign-in. Without the second, a
/// reconnect from an account that has consented before comes back with no refresh token,
/// and the connection dies an hour later.</item>
/// <item>Consent is granular: the user can untick individual permissions on Google's
/// screen, so what was granted is read from the token response's <c>scope</c>, never
/// assumed from what was asked.</item>
/// <item>A refresh that fails with <c>invalid_grant</c> is final — revoked, expired, or
/// the seven-day limit on apps in testing mode — and needs a browser. Anything else
/// (Google down, a timeout) is not, and must not be reported as "sign in again".</item>
/// </list>
/// </summary>
public sealed class GoogleConnectionService(
    IOptions<GoogleOptions> options,
    IConnectionStore connections,
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<GoogleConnectionService> log) : IGoogleConnectionService
{
    private readonly GoogleOptions _o = options.Value;

    public const string ScopeOpenId = "openid";
    public const string ScopeEmail = "email";
    public const string ScopeReadonly = "https://www.googleapis.com/auth/gmail.readonly";
    public const string ScopeModify = "https://www.googleapis.com/auth/gmail.modify";
    public const string ScopeSend = "https://www.googleapis.com/auth/gmail.send";

    /// <summary>The full-access scope. Never asked for, but honoured if a token carries it.</summary>
    public const string ScopeFull = "https://mail.google.com/";

    /// <summary>
    /// What Outpost asks for. Read-only is asked for separately from modify, although modify
    /// covers reading, so that a user can untick "manage" on Google's screen and still have a
    /// read-only connection rather than none at all.
    /// </summary>
    public static readonly string[] Scopes = [ScopeOpenId, ScopeEmail, ScopeReadonly, ScopeModify, ScopeSend];

    /// <summary>The value stored as the connection's instance. Google has one, so it is a constant.</summary>
    public const string Instance = "google";

    public bool IsConfigured => _o.IsConfigured;

    // ── signing in ──────────────────────────────────────────────────────────

    public Uri BuildAuthorizeUrl(PendingConnection pending, string? loginHint = null)
    {
        if (!_o.IsConfigured)
            throw new InvalidOperationException("Gmail isn't set up on this Outpost server.");

        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", _o.ClientId),
            new("redirect_uri", _o.RedirectUri),
            new("response_type", "code"),
            new("scope", string.Join(' ', Scopes)),
            new("access_type", "offline"),
            new("prompt", "consent"),
            new("state", pending.State),
            new("code_challenge", Challenge(pending.CodeVerifier)),
            new("code_challenge_method", "S256"),
        };

        if (!string.IsNullOrWhiteSpace(loginHint)) query.Add(new("login_hint", loginHint));

        return new Uri(_o.AuthorizeEndpoint + "?" + string.Join('&',
            query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
    }

    /// <summary>RFC 7636 S256: base64url(SHA-256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task<Connection> CompleteAsync(PendingConnection pending, string code, CancellationToken ct = default)
    {
        var token = await PostTokenAsync(
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", _o.RedirectUri),
            new("code_verifier", pending.CodeVerifier),
        ], ct);

        if (token.Error is { } error)
            throw new InvalidOperationException(DescribeExchangeError(error, token.ErrorDescription));

        var reply = token.Reply!;

        if (string.IsNullOrWhiteSpace(reply.RefreshToken))
            throw new InvalidOperationException(
                "Google didn't hand over a long-lived sign-in, so the connection would stop working within "
                + "the hour. Try again; if it keeps happening, remove Outpost under Third-party access in "
                + "your Google account and connect afresh.");

        var who = IdentityFrom(reply.IdToken)
            ?? throw new InvalidOperationException("Google didn't say which account signed in. Try again.");

        // Before anything is written, as on the Microsoft side: a reconnect that signed in
        // as somebody else must not quietly re-point the connection at a different inbox.
        if (pending.ExpectedHomeAccountId is { } expected && !string.Equals(expected, who.Subject, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"That signed in as {who.Email}, which is a different account from the one '{pending.Label}' "
                + "is connected to. Reconnect it and pick the original account, or add it as a new connection "
                + "under its own name.");

        var granted = ScopeSet(reply.Scope);
        var capabilities = CapabilitiesFor(granted);

        // Every Gmail box unticked leaves a connection that can do nothing at all. Refusing
        // is kinder than adding something that fails on first use.
        if (!capabilities.Any(c => c.Name == Capabilities.MailRead && c.Granted))
            throw new InvalidOperationException(
                "Google didn't give Outpost permission to read your Gmail, so there is nothing this connection "
                + "could do. Try again and leave the Gmail permissions ticked.");

        var credential = JsonSerializer.Serialize(new StoredCredential(
            reply.RefreshToken!, reply.Scope ?? "", who.Subject, who.Email));

        log.LogInformation("Completed Google connection for {Email}; granted {Scopes}.",
            who.Email, string.Join(' ', granted));

        var created = await connections.UpsertAsync(
            pending.UserId, ProviderKind.Google, Instance, who.Subject, pending.Label,
            AuthKind.OAuth2, credential, capabilities, ct);

        // A consumer account carries no hosted-domain claim; a Workspace account does.
        await connections.SetAccountIdentityAsync(created, who.Email, isPersonal: who.HostedDomain is null, ct);

        // The access token from the exchange is good for about an hour; keep it rather than
        // refreshing straight away on the first tool call.
        Remember(created.Id, reply.AccessToken, reply.ExpiresIn);

        return created;
    }

    /// <summary>
    /// The capabilities a set of granted scopes supports, all of them declared.
    /// <para>
    /// Send is granted only by <c>gmail.send</c> (or full access). <c>gmail.modify</c> would
    /// technically let Outpost send too, but a user who unticked "send email on your behalf"
    /// on Google's screen said what they meant, and this honours it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Capability> CapabilitiesFor(IReadOnlySet<string> granted)
    {
        var full = granted.Contains(ScopeFull);
        var modify = full || granted.Contains(ScopeModify);
        var read = modify || granted.Contains(ScopeReadonly);
        var send = full || granted.Contains(ScopeSend);

        return
        [
            new Capability { Name = Capabilities.MailRead, Granted = read, Enabled = true },
            new Capability { Name = Capabilities.MailWrite, Granted = modify, Enabled = true },
            new Capability { Name = Capabilities.MailSend, Granted = send, Enabled = true },
        ];
    }

    private static HashSet<string> ScopeSet(string? scope) =>
        (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    // ── staying signed in ───────────────────────────────────────────────────

    /// <summary>
    /// One refresh per connection at a time. Listing every mailbox fans out in parallel and
    /// several tools can run at once; without this each would spend the refresh token on
    /// its own exchange.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Refreshing = new();

    /// <summary>Refreshed this long before Google's stated expiry, so a token never lapses mid-call.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private static string CacheKey(Guid connectionId) => $"op2:gtok:{connectionId}";

    public async Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default)
    {
        if (cache.TryGetValue<string>(CacheKey(connection.Id), out var cached) && cached is not null)
            return cached;

        var gate = Refreshing.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Somebody else may have refreshed while this call waited.
            if (cache.TryGetValue(CacheKey(connection.Id), out cached) && cached is not null)
                return cached;

            var stored = await ReadCredentialAsync(connection, ct);
            if (stored is null)
            {
                log.LogWarning("Google connection {Label} holds no usable credential; it needs reconnecting.", connection.Label);
                await connections.SetStateAsync(connection.UserId, connection.Id, ConnectionState.Expired, ct);
                return null;
            }

            var token = await PostTokenAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", stored.RefreshToken),
            ], ct);

            if (token.Error is { } error)
            {
                if (error == "invalid_grant")
                {
                    // Revoked, expired, password changed with Gmail scopes, or the seven-day
                    // limit for apps still in testing mode. Final: only a browser fixes it.
                    log.LogInformation("Google connection {Label} needs reconnecting: {Description}.",
                        connection.Label, token.ErrorDescription);
                    await connections.SetStateAsync(connection.UserId, connection.Id, ConnectionState.Expired, ct);
                    return null;
                }

                // invalid_client, unauthorized_client and the like are Outpost's own OAuth
                // client being wrong. Marking the user's connection Expired would send them
                // to reconnect into the same failure.
                log.LogError("Google refused Outpost's own client while refreshing {Label}: {Error} {Description}.",
                    connection.Label, error, token.ErrorDescription);
                throw new InvalidOperationException(
                    "Outpost's sign-in with Google is misconfigured on this server, so Gmail can't be reached. "
                    + "This isn't something reconnecting will fix.");
            }

            var reply = token.Reply!;

            // Google does not normally rotate refresh tokens, but documents that it can issue
            // a new one; when it does, the old one stops working, so it has to be kept.
            if (!string.IsNullOrWhiteSpace(reply.RefreshToken) && reply.RefreshToken != stored.RefreshToken)
            {
                await connections.SetCredentialAsync(connection, JsonSerializer.Serialize(
                    stored with { RefreshToken = reply.RefreshToken!, Scope = reply.Scope ?? stored.Scope }), ct);
            }

            await connections.TouchAsync(connection.Id, ct);
            return Remember(connection.Id, reply.AccessToken, reply.ExpiresIn);
        }
        finally
        {
            gate.Release();
        }
    }

    private string Remember(Guid connectionId, string accessToken, int expiresIn)
    {
        var lifetime = TimeSpan.FromSeconds(Math.Max(expiresIn, 0)) - Margin;

        // A token that is already inside the margin is used once and not kept.
        if (lifetime > TimeSpan.Zero)
            cache.Set(CacheKey(connectionId), accessToken, lifetime);

        return accessToken;
    }

    private async Task<StoredCredential?> ReadCredentialAsync(Connection connection, CancellationToken ct)
    {
        var plain = await connections.GetCredentialAsync(connection, ct);
        if (string.IsNullOrWhiteSpace(plain)) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(plain);
            return string.IsNullOrWhiteSpace(stored?.RefreshToken) ? null : stored;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── the token endpoint ──────────────────────────────────────────────────

    private sealed record TokenResult(TokenReply? Reply, string? Error, string? ErrorDescription);

    /// <summary>
    /// One call to Google's token endpoint. The client is authenticated with its secret in
    /// the form body, as Google's web-server flow documents. OAuth errors come back as a
    /// result rather than an exception, because the caller decides what each one means;
    /// anything that is not an OAuth error — Google unreachable, a 5xx — throws, and is
    /// never mistaken for "sign in again".
    /// </summary>
    private async Task<TokenResult> PostTokenAsync(
        IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        if (!_o.IsConfigured)
            throw new InvalidOperationException("Gmail isn't set up on this Outpost server.");

        var body = new FormUrlEncodedContent(form.Concat(
        [
            new("client_id", _o.ClientId),
            new("client_secret", _o.ClientSecret),
        ]));

        using var http = httpFactory.CreateClient("google");
        using var resp = await http.PostAsync(_o.TokenEndpoint, body, ct);

        if (resp.IsSuccessStatusCode)
        {
            var reply = await resp.Content.ReadFromJsonAsync<TokenReply>(ct);
            if (reply is null || string.IsNullOrWhiteSpace(reply.AccessToken))
                throw new InvalidOperationException("Google's sign-in service returned an empty answer.");
            return new TokenResult(reply, null, null);
        }

        // RFC 6749 §5.2: 400 (or 401 for client authentication) with {"error": "..."}.
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            try
            {
                var err = await resp.Content.ReadFromJsonAsync<TokenError>(ct);
                if (!string.IsNullOrWhiteSpace(err?.Error))
                    return new TokenResult(null, err.Error, err.ErrorDescription);
            }
            catch (JsonException) { /* fall through to the generic failure */ }
        }

        throw new HttpRequestException(
            $"Google's sign-in service answered HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
    }

    private static string DescribeExchangeError(string error, string? description) => error switch
    {
        // The code is single-use and short-lived; a slow or repeated callback lands here.
        "invalid_grant" => "That Google sign-in had already been used or had expired. Please try again.",
        "invalid_client" or "unauthorized_client" =>
            "Outpost's sign-in with Google is misconfigured on this server, so the connection couldn't be finished.",
        "redirect_uri_mismatch" =>
            "Outpost's sign-in with Google is misconfigured on this server (its return address isn't registered with Google).",
        _ => $"Google refused the sign-in: {description ?? error}.",
    };

    // ── who signed in ───────────────────────────────────────────────────────

    public sealed record Identity(string Subject, string Email, string? HostedDomain);

    /// <summary>
    /// The account behind an ID token from the token endpoint.
    /// <para>
    /// Not signature-checked, deliberately, per Google's OpenID Connect documentation: a
    /// token received directly from Google's token endpoint, over HTTPS, in exchange for
    /// the client secret, is known to come from Google. Issuer and audience are still
    /// checked — cheaply, and enough to catch a token meant for somebody else's client.
    /// </para>
    /// </summary>
    public Identity? IdentityFrom(string? idToken) => ReadIdentity(idToken, _o.ClientId);

    public static Identity? ReadIdentity(string? idToken, string clientId)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<IdClaims>(FromBase64Url(parts[1]));
            if (payload is null) return null;

            if (payload.Iss is not ("accounts.google.com" or "https://accounts.google.com")) return null;
            if (!AudienceIs(payload.Aud, clientId)) return null;
            if (string.IsNullOrWhiteSpace(payload.Sub) || string.IsNullOrWhiteSpace(payload.Email)) return null;

            return new Identity(payload.Sub, payload.Email, string.IsNullOrWhiteSpace(payload.Hd) ? null : payload.Hd);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>OIDC allows <c>aud</c> to be a string or an array of strings.</summary>
    private static bool AudienceIs(JsonElement aud, string clientId) => aud.ValueKind switch
    {
        JsonValueKind.String => aud.GetString() == clientId,
        JsonValueKind.Array => aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == clientId),
        _ => false,
    };

    // ── wire shapes ─────────────────────────────────────────────────────────

    /// <summary>
    /// What is kept, sealed, as the connection's credential. The refresh token is the only
    /// secret; the access token lives in memory and is never written down.
    /// </summary>
    public sealed record StoredCredential(
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("sub")] string Subject,
        [property: JsonPropertyName("email")] string Email);

    private sealed record TokenReply(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("id_token")] string? IdToken);

    private sealed record TokenError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record IdClaims(
        [property: JsonPropertyName("iss")] string? Iss,
        [property: JsonPropertyName("aud")] JsonElement Aud,
        [property: JsonPropertyName("sub")] string? Sub,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("hd")] string? Hd);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
