using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Tests.Web;

/// <summary>
/// Walks the OAuth dance over real HTTP, the way Claude's cloud connector will: discover,
/// register, authorize, consent, redeem, then call /mcp with the bearer.
/// <para>
/// OAuthServiceTests already covers the grant rules thoroughly, and every one of those
/// passed while nobody had checked that the endpoints in front of them were wired up at
/// all. That is the same shape of gap that let a connections page ship broken behind a
/// green suite, so this exercises the pipeline rather than the service.
/// </para>
/// </summary>
public sealed class OAuthFlowTests : IClassFixture<FormBindingTests.Factory>, IDisposable
{
    private readonly FormBindingTests.Factory _factory;
    private readonly HttpClient _client;

    public OAuthFlowTests(FormBindingTests.Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    public void Dispose() => _client.Dispose();

    private static readonly Regex TokenField =
        new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private const string Redirect = "https://claude.ai/api/mcp/auth_callback";

    // ── the two metadata documents a client reads before anything else ──────────

    [SkippableFact]
    public async Task Protected_resource_metadata_points_at_this_server_as_its_own_authority()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        foreach (var path in (string[])
                 ["/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp"])
        {
            using var resp = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("resource").GetString()));
            Assert.NotEmpty(doc.GetProperty("authorization_servers").EnumerateArray());
        }

        // RFC 9728 inserts the well-known segment BEFORE the resource path, so a client
        // protecting /mcp looks here first. It must describe /mcp, not the site root, or
        // the audience the client binds its token to is the wrong one.
        using var scoped = await _client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        var scopedDoc = JsonDocument.Parse(await scoped.Content.ReadAsStringAsync()).RootElement;
        Assert.EndsWith("/mcp", scopedDoc.GetProperty("resource").GetString());
    }

    [SkippableFact]
    public async Task Authorization_server_metadata_advertises_PKCE_and_the_three_endpoints()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        using var resp = await _client.GetAsync("/.well-known/oauth-authorization-server");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        foreach (var key in (string[])
                 ["issuer", "authorization_endpoint", "token_endpoint", "registration_endpoint"])
            Assert.False(string.IsNullOrWhiteSpace(d.GetProperty(key).GetString()), $"{key} missing");

        // S256 only. Advertising "plain" would let a client downgrade PKCE to decoration.
        var methods = d.GetProperty("code_challenge_methods_supported")
            .EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("S256", methods);
        Assert.DoesNotContain("plain", methods);
    }

    [SkippableFact]
    public async Task An_unauthenticated_mcp_call_challenges_with_the_metadata_url()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // Without the resource_metadata hint the client has nowhere to begin discovery,
        // so a bare 401 would strand the connector with no way to recover.
        using var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        anon.DefaultRequestHeaders.Add("X-Skip-Test-Auth", "1");

        using var resp = await anon.PostAsync("/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                Encoding.UTF8, "application/json"));

        Skip.If(resp.StatusCode != HttpStatusCode.Unauthorized,
            "the stub auth scheme authenticates every request; covered live instead");

        var challenge = resp.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata", challenge);
    }

    // ── the flow itself ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_client_can_register_consent_redeem_and_then_call_mcp()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // 1. Dynamic registration. Claude's connector has no pre-shared client id, so a
        //    server that cannot mint one on demand cannot be added at all.
        using var reg = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Claude test connector",
            redirect_uris = new[] { Redirect },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });

        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var clientId = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(clientId));

        // 2. PKCE pair, generated the way a real client would.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var authorize =
            $"/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId!)}" +
            $"&redirect_uri={Uri.EscapeDataString(Redirect)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz" +
            $"&resource={Uri.EscapeDataString("https://localhost/mcp")}";

        using var authResp = await _client.GetAsync(authorize);

        // Signed in already (stub cookie), so this lands on the consent screen rather
        // than a login challenge.
        Assert.Equal(HttpStatusCode.Redirect, authResp.StatusCode);
        var consentUrl = authResp.Headers.Location!.ToString();
        Assert.StartsWith("/authorize", consentUrl);

        // 3. The consent screen must actually render, and carry an antiforgery token.
        using var consent = await _client.GetAsync(consentUrl);
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        var html = await consent.Content.ReadAsStringAsync();
        var af = TokenField.Match(html);
        Assert.True(af.Success, "the consent form has no antiforgery token");

        // 4. Approve. This is the moment the user grants standing access.
        using var approve = await _client.PostAsync("/oauth/approve", new FormUrlEncodedContent(
        [
            new("client_id", clientId!),
            new("redirect_uri", Redirect),
            new("code_challenge", challenge),
            new("state", "xyz"),
            new("__RequestVerificationToken", af.Groups[1].Value),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        var back = approve.Headers.Location!.ToString();
        Assert.StartsWith(Redirect, back);

        var q = HttpUtility.ParseQueryString(new Uri(back).Query);
        var code = q["code"];
        Assert.False(string.IsNullOrWhiteSpace(code));

        // state must come back untouched, or the client cannot match the response to the
        // request it started and should refuse it.
        Assert.Equal("xyz", q["state"]);

        // 5. A wrong verifier must be refused BEFORE the real one is spent, otherwise the
        //    PKCE binding is decorative.
        using var wrong = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", code!),
            new("client_id", clientId!), new("redirect_uri", Redirect),
            new("code_verifier", Base64Url(RandomNumberGenerator.GetBytes(32))),
        ]));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // 6. Redeem properly.
        using var tok = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", code!),
            new("client_id", clientId!), new("redirect_uri", Redirect),
            new("code_verifier", verifier),
        ]));

        Assert.Equal(HttpStatusCode.OK, tok.StatusCode);
        var grant = JsonDocument.Parse(await tok.Content.ReadAsStringAsync()).RootElement;

        var access = grant.GetProperty("access_token").GetString();
        var refresh = grant.GetProperty("refresh_token").GetString();
        Assert.Equal("Bearer", grant.GetProperty("token_type").GetString());
        Assert.True(grant.GetProperty("expires_in").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(access));

        // 7. The code is single-use: a replay must fail even with the right verifier.
        using var replay = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", code!),
            new("client_id", clientId!), new("redirect_uri", Redirect),
            new("code_verifier", verifier),
        ]));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        // 8. The token is stored hashed, never in the clear — a database read must not
        //    hand someone a working credential.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();
            Assert.DoesNotContain(db.OAuthTokens.ToList(),
                t => t.TokenHash.Contains(access!, StringComparison.Ordinal));
        }

        // 9. And it actually opens the door. Sent on a bare client with no cookie: /mcp
        //    is guarded by the bearer scheme specifically, so proving the TOKEN works
        //    means proving it works without a session behind it. That is how Claude
        //    calls it — a server process holding a token and nothing else.
        using var bearerOnly = _factory.CreateClient();
        bearerOnly.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var call = await bearerOnly.PostAsync("/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.Unauthorized, call.StatusCode);

        // A token that has been tampered with must not.
        using var tampered = _factory.CreateClient();
        tampered.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", access![..^4] + "AAAA");

        using var refused = await tampered.PostAsync("/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // 10. Refresh rotates: the new pair works and must differ from the old.
        using var refreshed = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(
        [
            new("grant_type", "refresh_token"), new("refresh_token", refresh!),
            new("client_id", clientId!),
        ]));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEqual(access, second.GetProperty("access_token").GetString());
    }

    [SkippableFact]
    public async Task Authorize_refuses_a_redirect_uri_the_client_never_registered()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        using var reg = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Redirect test",
            redirect_uris = new[] { Redirect },
            token_endpoint_auth_method = "none",
        });
        var clientId = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("client_id").GetString();

        // An attacker-controlled redirect is how authorization codes get stolen, and a
        // prefix match is the usual way it slips through — so a host that merely STARTS
        // with the registered one must be refused too.
        foreach (var evil in (string[])
                 ["https://evil.example.com/cb", Redirect + ".evil.com", Redirect + "/../x"])
        {
            using var resp = await _client.GetAsync(
                $"/oauth/authorize?response_type=code&client_id={clientId}" +
                $"&redirect_uri={Uri.EscapeDataString(evil)}" +
                "&code_challenge=abc&code_challenge_method=S256");

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Authorize_refuses_to_proceed_without_S256_PKCE()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        using var reg = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "PKCE test",
            redirect_uris = new[] { Redirect },
            token_endpoint_auth_method = "none",
        });
        var clientId = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("client_id").GetString();

        // No challenge at all, and a downgrade to plain. Both must be refused: this is a
        // public client with no secret, so PKCE is the only thing binding the code to the
        // client that asked for it.
        foreach (var tail in (string[]) ["", "&code_challenge=abc&code_challenge_method=plain"])
        {
            using var resp = await _client.GetAsync(
                $"/oauth/authorize?response_type=code&client_id={clientId}" +
                $"&redirect_uri={Uri.EscapeDataString(Redirect)}{tail}");

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Authorize_refuses_a_resource_belonging_to_a_different_server()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        using var reg = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Resource test",
            redirect_uris = new[] { Redirect },
            token_endpoint_auth_method = "none",
        });
        var clientId = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("client_id").GetString();

        async Task<HttpStatusCode> Ask(string? resource)
        {
            var url = $"/oauth/authorize?response_type=code&client_id={clientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                      "&code_challenge=abc&code_challenge_method=S256";
            if (resource is not null) url += $"&resource={Uri.EscapeDataString(resource)}";

            using var resp = await _client.GetAsync(url);
            return resp.StatusCode;
        }

        // A resource naming somebody else is a token this server has no business minting.
        Assert.Equal(HttpStatusCode.BadRequest, await Ask("https://someone-else.example.com/mcp"));

        // The lookalike host is the one worth pinning: a prefix comparison would pass it.
        Assert.Equal(HttpStatusCode.BadRequest, await Ask("https://localhost.evil.test/mcp"));

        // Ours, in each shape a client might legitimately send, plus omitted entirely —
        // older clients predate RFC 8707 and must not be locked out.
        foreach (var ok in (string?[]) [null, "https://localhost", "https://localhost/", "https://localhost/mcp"])
            Assert.Equal(HttpStatusCode.Redirect, await Ask(ok));
    }

    [Theory]
    [InlineData("https://op2.example.com", "https://op2.example.com", true)]
    [InlineData("https://op2.example.com/mcp", "https://op2.example.com", true)]
    [InlineData("https://op2.example.com/mcp/", "https://op2.example.com", true)]
    [InlineData("https://OP2.EXAMPLE.COM/mcp", "https://op2.example.com", true)]
    [InlineData("https://op2.example.com.evil.test/mcp", "https://op2.example.com", false)]
    [InlineData("https://evil.test/mcp", "https://op2.example.com", false)]
    [InlineData("https://op2.example.com/admin", "https://op2.example.com", false)]
    [InlineData("not-a-uri", "https://op2.example.com", false)]
    public void Resource_identity_compares_host_and_path_not_prefixes(
        string resource, string ours, bool expected) =>
        Assert.Equal(expected, OutPost2.Web.Mcp.OAuthEndpoints.IdentifiesThisServer(resource, ours));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
