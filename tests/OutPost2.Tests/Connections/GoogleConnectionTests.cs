using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Signing in to Google for Gmail, and staying signed in (OP2-279).
/// <para>
/// Against Google's documented token-endpoint responses rather than a live server — the
/// shapes below are the ones in Google's OAuth 2.0 web-server and OpenID Connect guides.
/// A live run is done separately (OP2-284); what these pin down is everything that can be
/// decided without one: what is asked for, what is believed, what is kept, and which
/// failures mean "sign in again" versus "Google is having a bad minute".
/// </para>
/// </summary>
public sealed class GoogleConnectionTests
{
    private const string ClientId = "1234567890-abc.apps.googleusercontent.com";
    private const string Sub = "110169484474386276334";
    private const string Email = "ann@example.com";
    private static readonly Guid User = Guid.CreateVersion7();

    private const string AllScopes =
        "https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.readonly "
        + "openid https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email";

    // ── a fake token endpoint ────────────────────────────────────────────────

    private sealed class TokenEndpoint(Func<Dictionary<string, string>, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var form = (await r.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(kv => kv.Split('='))
                .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => Uri.UnescapeDataString(kv[1].Replace('+', ' ')));
            lock (Posts) Posts.Add(form);

            // A beat, so that concurrent callers genuinely overlap.
            await Task.Delay(10, ct);
            return respond(form);
        }
    }

    private sealed class OneClient(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static string B64(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An ID token shaped like Google's (header.payload.signature).</summary>
    private static string IdToken(string sub = Sub, string email = Email, string aud = ClientId,
        string iss = "https://accounts.google.com", string? hd = null)
    {
        var claims = new Dictionary<string, object> { ["iss"] = iss, ["aud"] = aud, ["sub"] = sub, ["email"] = email,
            ["email_verified"] = true, ["iat"] = 1762160000, ["exp"] = 1762163600 };
        if (hd is not null) claims["hd"] = hd;
        return $"{B64("""{"alg":"RS256","kid":"x","typ":"JWT"}""")}.{B64(JsonSerializer.Serialize(claims))}.c2lnbmF0dXJl";
    }

    /// <summary>Google's documented authorization-code response.</summary>
    private static HttpResponseMessage CodeReply(string scope = AllScopes, string? refresh = "1//0gRefreshToken",
        string? idToken = null) =>
        Json(new Dictionary<string, object?>
        {
            ["access_token"] = "ya29.a0AccessToken",
            ["expires_in"] = 3599,
            ["refresh_token"] = refresh,
            ["scope"] = scope,
            ["token_type"] = "Bearer",
            ["id_token"] = idToken ?? IdToken(),
        }.Where(kv => kv.Value is not null).ToDictionary());

    /// <summary>Google's documented refresh response: no refresh token, no ID token.</summary>
    private static HttpResponseMessage RefreshReply(string access = "ya29.a0Refreshed", string? newRefresh = null) =>
        Json(new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["expires_in"] = 3599,
            ["scope"] = AllScopes,
            ["token_type"] = "Bearer",
            ["refresh_token"] = newRefresh,
        }.Where(kv => kv.Value is not null).ToDictionary());

    private static HttpResponseMessage OAuthError(string error, string description, HttpStatusCode code = HttpStatusCode.BadRequest) =>
        Json(new { error, error_description = description }, code);

    // ── the world ────────────────────────────────────────────────────────────

    private sealed class World
    {
        public required GoogleConnectionService Google { get; init; }
        public required TokenEndpoint Wire { get; init; }
        public required Stub<IConnectionStore> Store { get; init; }
        public required Connection Connection { get; init; }
        public string? Credential { get; set; }
        public List<Capability> Granted { get; } = [];
    }

    private static World Build(Func<Dictionary<string, string>, HttpResponseMessage> respond,
        string? credential = null, bool configured = true, IMemoryCache? cache = null)
    {
        var conn = new Connection
        {
            UserId = User, Label = "personal", Provider = ProviderKind.Google, Instance = GoogleConnectionService.Instance,
            HomeAccountId = Sub, AuthKind = AuthKind.OAuth2, SealedCredential = "sealed", State = ConnectionState.Active,
        };

        World? w = null;
        var (store, stub) = Stub<IConnectionStore>.Create(new()
        {
            ["UpsertAsync"] = a =>
            {
                w!.Credential = (string)a[6]!;
                w.Granted.AddRange((IEnumerable<Capability>)a[7]!);
                return Task.FromResult(conn);
            },
            ["SetAccountIdentityAsync"] = _ => Task.CompletedTask,
            ["GetCredentialAsync"] = _ => Task.FromResult(w!.Credential),
            ["SetCredentialAsync"] = a => { w!.Credential = (string)a[1]!; return Task.CompletedTask; },
            ["SetStateAsync"] = _ => Task.CompletedTask,
            ["TouchAsync"] = _ => Task.CompletedTask,
        });

        var wire = new TokenEndpoint(respond);
        var options = Options.Create(new GoogleOptions
        {
            ClientId = configured ? ClientId : "", ClientSecret = "GOCSPX-secret",
            RedirectUri = "https://op2.example/signin-google",
        });

        var google = new GoogleConnectionService(options, store, new OneClient(wire),
            cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<GoogleConnectionService>.Instance);

        w = new World { Google = google, Wire = wire, Store = stub, Connection = conn, Credential = credential };
        return w;
    }

    private static PendingConnection Pending(string? expectedSub = null) => new()
    {
        State = "state-abc", UserId = User, Provider = ProviderKind.Google, Label = "personal",
        CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", ExpectedHomeAccountId = expectedSub,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    private static string Stored(string refresh = "1//0gRefreshToken") =>
        JsonSerializer.Serialize(new GoogleConnectionService.StoredCredential(refresh, AllScopes, Sub, Email));

    private static Dictionary<string, string> Query(Uri u) =>
        u.Query.TrimStart('?').Split('&').Select(kv => kv.Split('='))
            .ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));

    // ── what is asked for ────────────────────────────────────────────────────

    [Fact]
    public void The_authorize_url_asks_for_offline_access_and_fresh_consent_every_time()
    {
        var q = Query(Build(_ => throw new InvalidOperationException()).Google.BuildAuthorizeUrl(Pending()));

        Assert.Equal(ClientId, q["client_id"]);
        Assert.Equal("https://op2.example/signin-google", q["redirect_uri"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("state-abc", q["state"]);

        // Without offline there is no refresh token at all; without consent a returning
        // account gets none either, and the connection dies within the hour.
        Assert.Equal("offline", q["access_type"]);
        Assert.Equal("consent", q["prompt"]);

        Assert.Equal(GoogleConnectionService.Scopes, q["scope"].Split(' '));
        Assert.Contains(GoogleConnectionService.ScopeReadonly, q["scope"]);
        Assert.Contains(GoogleConnectionService.ScopeModify, q["scope"]);
        Assert.Contains(GoogleConnectionService.ScopeSend, q["scope"]);
        Assert.DoesNotContain("login_hint", q.Keys);
    }

    [Fact]
    public void The_challenge_is_rfc_7636_s256()
    {
        // RFC 7636 Appendix B's own example.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            GoogleConnectionService.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

        var q = Query(Build(_ => throw new InvalidOperationException()).Google.BuildAuthorizeUrl(Pending()));
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", q["code_challenge"]);
    }

    [Fact]
    public void A_reconnect_preselects_the_account_it_belongs_to()
    {
        var q = Query(Build(_ => throw new InvalidOperationException()).Google.BuildAuthorizeUrl(Pending(Sub), Email));
        Assert.Equal(Email, q["login_hint"]);
    }

    [Fact]
    public void An_unconfigured_server_offers_no_google_sign_in()
    {
        var w = Build(_ => throw new InvalidOperationException(), configured: false);
        Assert.False(w.Google.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => w.Google.BuildAuthorizeUrl(Pending()));
    }

    // ── completing the sign-in ───────────────────────────────────────────────

    [Fact]
    public async Task A_sign_in_becomes_a_gmail_connection_with_every_granted_capability()
    {
        var w = Build(_ => CodeReply());

        var created = await w.Google.CompleteAsync(Pending(), "4/0AcodeFromGoogle");

        var post = Assert.Single(w.Wire.Posts);
        Assert.Equal("authorization_code", post["grant_type"]);
        Assert.Equal("4/0AcodeFromGoogle", post["code"]);
        Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", post["code_verifier"]);
        Assert.Equal("https://op2.example/signin-google", post["redirect_uri"]);
        Assert.Equal(ClientId, post["client_id"]);
        Assert.Equal("GOCSPX-secret", post["client_secret"]);

        var upsert = Assert.Single(w.Store.Calls, c => c.Name == "UpsertAsync");
        Assert.Equal(ProviderKind.Google, upsert.Args[1]);
        Assert.Equal(GoogleConnectionService.Instance, upsert.Args[2]);
        Assert.Equal(Sub, upsert.Args[3]);           // the stable account id, not the address
        Assert.Equal("personal", upsert.Args[4]);
        Assert.Equal(AuthKind.OAuth2, upsert.Args[5]);

        Assert.All(w.Granted, c => Assert.True(c.Granted, c.Name));
        Assert.Equal([Capabilities.MailRead, Capabilities.MailWrite, Capabilities.MailSend], w.Granted.Select(c => c.Name));

        var identity = Assert.Single(w.Store.Calls, c => c.Name == "SetAccountIdentityAsync");
        Assert.Equal(Email, identity.Args[1]);
        Assert.Equal(true, identity.Args[2]);          // no hosted domain: a consumer account
        Assert.Same(w.Connection, created);
    }

    [Fact]
    public async Task Only_the_refresh_token_is_kept_and_the_access_token_is_not()
    {
        var w = Build(_ => CodeReply());
        await w.Google.CompleteAsync(Pending(), "code");

        var kept = JsonSerializer.Deserialize<GoogleConnectionService.StoredCredential>(w.Credential!)!;
        Assert.Equal("1//0gRefreshToken", kept.RefreshToken);
        Assert.Equal(Sub, kept.Subject);
        Assert.DoesNotContain("ya29", w.Credential);
    }

    [Fact]
    public async Task A_workspace_account_is_not_recorded_as_personal()
    {
        var w = Build(_ => CodeReply(idToken: IdToken(hd: "example.com")));
        await w.Google.CompleteAsync(Pending(), "code");

        Assert.Equal(false, Assert.Single(w.Store.Calls, c => c.Name == "SetAccountIdentityAsync").Args[2]);
    }

    [Fact]
    public async Task Unticking_send_on_googles_screen_means_outpost_does_not_send()
    {
        // gmail.modify would technically allow sending; the user said no, and that holds.
        var w = Build(_ => CodeReply(scope: "openid https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email"));
        await w.Google.CompleteAsync(Pending(), "code");

        Assert.True(w.Granted.Single(c => c.Name == Capabilities.MailRead).Granted);
        Assert.True(w.Granted.Single(c => c.Name == Capabilities.MailWrite).Granted);
        Assert.False(w.Granted.Single(c => c.Name == Capabilities.MailSend).Granted);
    }

    [Fact]
    public async Task Read_only_consent_gives_a_read_only_connection()
    {
        var w = Build(_ => CodeReply(scope: "openid https://www.googleapis.com/auth/gmail.readonly"));
        await w.Google.CompleteAsync(Pending(), "code");

        Assert.Equal([true, false, false], w.Granted.Select(c => c.Granted));
    }

    [Fact]
    public async Task Unticking_every_gmail_permission_adds_nothing_and_says_why()
    {
        var w = Build(_ => CodeReply(scope: "openid https://www.googleapis.com/auth/userinfo.email"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Google.CompleteAsync(Pending(), "code"));
        Assert.Contains("leave the Gmail permissions ticked", ex.Message);
        Assert.DoesNotContain(w.Store.Calls, c => c.Name == "UpsertAsync");
    }

    [Fact]
    public async Task No_refresh_token_is_refused_rather_than_a_connection_that_dies_in_an_hour()
    {
        var w = Build(_ => CodeReply(refresh: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Google.CompleteAsync(Pending(), "code"));
        Assert.Contains("long-lived sign-in", ex.Message);
        Assert.DoesNotContain(w.Store.Calls, c => c.Name == "UpsertAsync");
    }

    [Fact]
    public async Task A_reconnect_that_signs_in_as_someone_else_is_refused_before_anything_is_written()
    {
        var w = Build(_ => CodeReply(idToken: IdToken(sub: "999", email: "bob@example.com")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Google.CompleteAsync(Pending(Sub), "code"));
        Assert.Contains("bob@example.com", ex.Message);
        Assert.DoesNotContain(w.Store.Calls, c => c.Name == "UpsertAsync");
    }

    [Theory]
    [InlineData("someone-elses-client.apps.googleusercontent.com", "https://accounts.google.com")]
    [InlineData(ClientId, "https://evil.example")]
    public async Task An_id_token_for_another_client_or_issuer_is_not_believed(string aud, string iss)
    {
        var w = Build(_ => CodeReply(idToken: IdToken(aud: aud, iss: iss)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Google.CompleteAsync(Pending(), "code"));
        Assert.DoesNotContain(w.Store.Calls, c => c.Name == "UpsertAsync");
    }

    [Fact]
    public void The_bare_issuer_google_also_uses_is_accepted()
    {
        // Google's docs: iss is "https://accounts.google.com" or "accounts.google.com".
        Assert.NotNull(GoogleConnectionService.ReadIdentity(IdToken(iss: "accounts.google.com"), ClientId));
        Assert.Null(GoogleConnectionService.ReadIdentity("not.a.jwt", ClientId));
        Assert.Null(GoogleConnectionService.ReadIdentity(null, ClientId));
    }

    [Theory]
    [InlineData("invalid_grant", "already been used or had expired")]
    [InlineData("redirect_uri_mismatch", "misconfigured")]
    [InlineData("invalid_client", "misconfigured")]
    public async Task An_exchange_google_refuses_says_what_it_means(string error, string expected)
    {
        var w = Build(_ => OAuthError(error, "x"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Google.CompleteAsync(Pending(), "code"));
        Assert.Contains(expected, ex.Message);
    }

    // ── staying signed in ────────────────────────────────────────────────────

    [Fact]
    public async Task The_access_token_from_the_sign_in_is_used_rather_than_refreshed_straight_away()
    {
        var w = Build(_ => CodeReply());
        await w.Google.CompleteAsync(Pending(), "code");

        Assert.Equal("ya29.a0AccessToken", await w.Google.GetAccessTokenAsync(w.Connection));
        Assert.Single(w.Wire.Posts);   // the code exchange only
    }

    [Fact]
    public async Task A_cold_token_is_refreshed_once_and_then_reused()
    {
        var w = Build(_ => RefreshReply(), credential: Stored());

        Assert.Equal("ya29.a0Refreshed", await w.Google.GetAccessTokenAsync(w.Connection));
        Assert.Equal("ya29.a0Refreshed", await w.Google.GetAccessTokenAsync(w.Connection));

        var post = Assert.Single(w.Wire.Posts);
        Assert.Equal("refresh_token", post["grant_type"]);
        Assert.Equal("1//0gRefreshToken", post["refresh_token"]);
        Assert.Equal(ClientId, post["client_id"]);
        Assert.Equal(1, w.Store.Count("TouchAsync"));
    }

    [Fact]
    public async Task Callers_racing_for_a_cold_token_share_one_refresh()
    {
        var w = Build(_ => RefreshReply(), credential: Stored());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => w.Google.GetAccessTokenAsync(w.Connection)));

        Assert.All(tokens, t => Assert.Equal("ya29.a0Refreshed", t));
        Assert.Single(w.Wire.Posts);
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_kept_so_the_next_refresh_still_works()
    {
        var w = Build(_ => RefreshReply(newRefresh: "1//0gRotated"), credential: Stored());

        await w.Google.GetAccessTokenAsync(w.Connection);

        Assert.Equal(1, w.Store.Count("SetCredentialAsync"));
        Assert.Equal("1//0gRotated",
            JsonSerializer.Deserialize<GoogleConnectionService.StoredCredential>(w.Credential!)!.RefreshToken);
    }

    [Fact]
    public async Task A_revoked_or_expired_sign_in_marks_the_connection_for_reconnecting()
    {
        // What Google answers for a revoked token — and for one from an app in testing
        // mode that has passed its seven days.
        var w = Build(_ => OAuthError("invalid_grant", "Token has been expired or revoked."), credential: Stored());

        Assert.Null(await w.Google.GetAccessTokenAsync(w.Connection));

        var state = Assert.Single(w.Store.Calls, c => c.Name == "SetStateAsync");
        Assert.Equal(ConnectionState.Expired, state.Args[2]);
    }

    [Fact]
    public async Task Outposts_own_client_being_wrong_is_not_blamed_on_the_users_connection()
    {
        var w = Build(_ => OAuthError("invalid_client", "The OAuth client was not found.", HttpStatusCode.Unauthorized),
            credential: Stored());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Google.GetAccessTokenAsync(w.Connection));
        Assert.Contains("isn't something reconnecting will fix", ex.Message);
        Assert.DoesNotContain(w.Store.Calls, c => c.Name == "SetStateAsync");
    }

    [Fact]
    public async Task Google_being_down_is_an_error_and_not_sign_in_again()
    {
        var w = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), credential: Stored());

        await Assert.ThrowsAsync<HttpRequestException>(() => w.Google.GetAccessTokenAsync(w.Connection));
        Assert.DoesNotContain(w.Store.Calls, c => c.Name == "SetStateAsync");
    }

    [Fact]
    public async Task A_connection_with_no_readable_credential_needs_reconnecting()
    {
        var w = Build(_ => throw new InvalidOperationException("should not be called"), credential: "not json");

        Assert.Null(await w.Google.GetAccessTokenAsync(w.Connection));
        Assert.Equal(ConnectionState.Expired, Assert.Single(w.Store.Calls, c => c.Name == "SetStateAsync").Args[2]);
        Assert.Empty(w.Wire.Posts);
    }

    // ── what the callback says ───────────────────────────────────────────────

    [Theory]
    [InlineData("access_denied", "You cancelled at Google")]
    [InlineData("admin_policy_enforced", "Workspace administrator")]
    [InlineData("something_new", "something_new")]
    public void Googles_callback_errors_are_said_in_the_users_terms(string error, string expected)
    {
        Assert.Contains(expected, OutPost2.Web.Connections.ConnectionEndpoints.DescribeGoogle(error));
    }

    // ── and the connection is a Gmail one ────────────────────────────────────

    [Fact]
    public void A_google_connection_says_whose_account_it_is()
    {
        var c = new Connection
        {
            UserId = User, Label = "personal", Provider = ProviderKind.Google, Instance = "google",
            AuthKind = AuthKind.OAuth2, SealedCredential = "s", AccountIdentifier = Email,
        };
        Assert.Equal($"{Email} · Google account", OutPost2.Web.Pages.ConnectionsModel.AccountLine(c));
    }
}
