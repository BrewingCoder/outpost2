using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Tests.Web;

/// <summary>
/// Posts the forms exactly as a browser does, through the real HTTP pipeline.
/// <para>
/// Written because the first real use of the connections page failed with
/// <c>Required parameter "string label" was not provided from query string</c>. Minimal
/// APIs bind bare simple types from the QUERY STRING, but an HTML form sends them in the
/// body, so the handler never ran. Every unit test passed, because none of them crossed
/// the pipeline. These do.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class FormBindingTests : IClassFixture<FormBindingTests.Factory>, IDisposable
{
    private const string Admin =
        "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "op2_web_" + Guid.NewGuid().ToString("n")[..12];
        public bool Available { get; private set; }
        public string Why { get; private set; } = "";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            try
            {
                using var admin = new OutPostDbContext(
                    new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
                admin.Database.ExecuteSqlRaw($"CREATE DATABASE {DbName}");
                Available = true;
            }
            catch (Exception ex) { Why = $"Postgres unavailable: {ex.Message}"; }

            // Development, so the app applies migrations on boot exactly as it does in
            // the dev stack. A separate "Testing" environment skipped them and every page
            // 500'd on a missing table.
            builder.UseEnvironment("Development");

            builder.UseSetting("ConnectionStrings:Postgres",
                Admin.Replace("Database=postgres", $"Database={DbName}"));
            builder.UseSetting("OutPost2:Vault:Address", "http://localhost:8200");
            builder.UseSetting("OutPost2:Vault:AuthMethod", "Token");
            builder.UseSetting("OutPost2:Vault:Token", "op2-dev-root");
            builder.UseSetting("OutPost2:Storage:ServiceUrl", "http://localhost:3900");
            builder.UseSetting("OutPost2:Storage:AccessKey", "GK" + new string('0', 22) + "d2");
            builder.UseSetting("OutPost2:Storage:SecretKey", new string('0', 62) + "d2");
            // Pinned, so the OAuth tests are not asserting against whatever the
            // production default happens to be. The resource check compares host and
            // port, and an unset value here silently meant outpost.brewingcoder.com.
            builder.UseSetting("OutPost2:PublicBaseUrl", "https://localhost");

            builder.UseSetting("OutPost2:GitHub:ClientId", "test-client");
            builder.UseSetting("OutPost2:GitHub:ClientSecret", "test-secret");
            builder.UseSetting("OutPost2:Entra:ClientId", "00000000-0000-0000-0000-000000000001");
            builder.UseSetting("OutPost2:Entra:ClientSecret", "test-secret");

            // Supplied here because EntraOptions no longer carries a default. A default
            // would have to name somebody's deployment, and a fork inheriting it would
            // send its users there to consent.
            builder.UseSetting("OutPost2:Entra:RedirectUri", "https://localhost/signin-microsoft");

            builder.ConfigureServices(services =>
            {
                // Stand in for the GitHub cookie, so these tests exercise BINDING and not
                // sign-in, which has its own coverage.
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, StubAuth>("Test", _ => { });

                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                });

                // The background workers would poll a database this test is about to drop.
                foreach (var d in services.Where(s => s.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(d);
            });

            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!Available) return;
            try
            {
                using var admin = new OutPostDbContext(
                    new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
                admin.Database.ExecuteSqlRaw($"DROP DATABASE IF EXISTS {DbName} WITH (FORCE)");
            }
            catch { /* best effort */ }
        }
    }

    private sealed class StubAuth(
        IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        internal static readonly Guid UserId = Guid.CreateVersion7();

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // A way to be genuinely signed out. Without it this scheme authenticates every
            // request, so nothing can test what a visitor who is not signed in actually
            // sees — and a page that throws instead of redirecting passes every test.
            if (Request.Headers.ContainsKey("X-Skip-Test-Auth"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim("op2:uid", UserId.ToString()), new Claim(ClaimTypes.Name, "tester")], "Test");

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public FormBindingTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,   // the redirect itself is what we assert on
        });
    }

    public void Dispose() => _client.Dispose();

    private static readonly Regex TokenField =
        new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Builds a form body carrying a real antiforgery token, exactly as a browser does —
    /// scraped from the page hosting the form, because that is the only place it
    /// legitimately comes from.
    /// </summary>
    private async Task<FormUrlEncodedContent> FormAsync(
        string pageHostingTheForm, params (string Key, string Value)[] fields)
    {
        var html = await _client.GetStringAsync(pageHostingTheForm);
        var m = TokenField.Match(html);

        var all = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList();
        if (m.Success) all.Add(new KeyValuePair<string, string>("__RequestVerificationToken", m.Groups[1].Value));

        return new FormUrlEncodedContent(all);
    }

    private static FormUrlEncodedContent Bare(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    // ── the regression this file exists for ──────────────────────────────────

    [SkippableFact]
    public async Task Microsoft_start_binds_the_label_from_the_form_body()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        var resp = await _client.PostAsync("/connections/microsoft/start",
            await FormAsync("/connections", ("label", "work"), ("tenantHint", "")));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var location = resp.Headers.Location!.ToString();
        Assert.Contains("login.microsoftonline.com", location);
        Assert.Contains("signin-microsoft", location);
        Assert.Contains("state=", location);
        Assert.Contains("prompt=select_account", location);

        // PKCE challenge must be present AND answerable later: MSAL generates the pair,
        // so its verifier is stored on the pending record for redemption. Sending a
        // challenge we cannot answer fails only after the user has already consented.
        Assert.Contains("code_challenge=", location);
        Assert.Contains("code_challenge_method=S256", location);
    }

    [SkippableFact]
    public async Task YouTrack_add_binds_all_three_fields_from_the_form_body()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        var resp = await _client.PostAsync("/connections/youtrack/add",
            await FormAsync("/connections",
                ("label", "lab"), ("baseUrl", "yt.invalid-host-op2.test"), ("token", "perm-nope")));

        // Binding worked if we reached the handler: an unreachable host redirects back
        // with a readable error rather than 400-ing on a missing parameter.
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/connections?error=", resp.Headers.Location!.ToString());
    }

    [SkippableFact]
    public async Task OAuth_approve_binds_its_hidden_fields_from_the_form_body()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // The connector consent screen posts these as hidden inputs, so it would have
        // failed in exactly the same way.
        var resp = await _client.PostAsync("/oauth/approve",
            await FormAsync("/connections",
                ("client_id", "nonexistent"), ("redirect_uri", "https://example.test/cb"),
                ("code_challenge", "abc"), ("state", "s")));

        // Reaching the handler is the point; an unknown client is then properly refused.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Invalid client", await resp.Content.ReadAsStringAsync());
    }

    // ── the protections around those forms ───────────────────────────────────

    [SkippableFact]
    public async Task A_form_post_without_an_antiforgery_token_is_refused()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // These forms change connections and carry credentials. SameSite=Lax already
        // blocks cross-site POSTs, but an app custodying other people's mailboxes should
        // not depend on a single mechanism.
        var resp = await _client.PostAsync("/connections/microsoft/start", Bare(("label", "work")));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [SkippableFact]
    public async Task An_empty_label_is_rejected_cleanly_rather_than_with_a_500()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        var resp = await _client.PostAsync("/connections/microsoft/start",
            await FormAsync("/connections", ("label", "   ")));

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── the pages themselves ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_connections_page_renders_with_tokens_in_its_forms()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        var resp = await _client.GetAsync("/connections");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Asserted on the form ACTIONS rather than on headings. Copy is the most likely
        // thing to be reworded, and a test that breaks on wording teaches people to edit
        // the test without reading it. An endpoint disappearing is the real regression.
        Assert.Contains("/connections/microsoft/start", html);
        Assert.Contains("/connections/youtrack/add", html);
        Assert.Contains("/connections/github/add", html);

        // Without this the POST fails antiforgery validation, which is how the page
        // would silently stop working.
        Assert.Contains("__RequestVerificationToken", html);
    }

    [SkippableTheory]
    [InlineData("/")]
    [InlineData("/connections")]
    [InlineData("/jobs")]
    [InlineData("/files")]
    [InlineData("/preview-links")]
    [InlineData("/repo-names")]
    [InlineData("/connected-apps")]
    [InlineData("/signin")]
    [InlineData("/denied")]
    public async Task Every_page_renders_without_leaking_its_own_source(string path)
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        var resp = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var html = await resp.Content.ReadAsStringAsync();

        // Razor only treats @@if as a transition at the start of a markup node, so
        // "Approvals@@if (n > 0)" renders the literal source into the nav of every page.
        // It did, and nothing failed, because no test ever looked at the output.
        foreach (var leak in (string[]) ["@if", "@foreach", "@Model.", "@Html.", "@await"])
            Assert.DoesNotContain(leak, html);

        // The shell, so a page that silently lost its layout is caught too.
        Assert.Contains("op-app", html);
    }

    [SkippableTheory]
    [InlineData("/connections")]
    [InlineData("/jobs")]
    [InlineData("/files")]
    [InlineData("/preview-links")]
    [InlineData("/repo-names")]
    [InlineData("/connected-apps")]
    public async Task A_signed_out_visitor_is_sent_to_sign_in_rather_than_an_error(string path)
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // A page that reads the signed-in user's id without [Authorize] throws instead of
        // redirecting, and a 500 on the sign-in path looks like the site is down. Caught
        // in production on /repo-names, which every other page happened to get right.
        using var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        anon.DefaultRequestHeaders.Add("X-Skip-Test-Auth", "1");

        using var resp = await anon.GetAsync(path);

        // Not a redirect specifically: the stub scheme challenges with 401 where the real
        // cookie scheme sends 302 to the sign-in page. Both are a refusal the visitor can
        // act on. The defect is the third outcome — a 500 from reading a claim that is not
        // there, which looks to the visitor like the site is broken rather than private.
        Assert.True((int)resp.StatusCode < 500,
            $"{path} answered {(int)resp.StatusCode} to a signed-out visitor");
    }

    [SkippableFact]
    public async Task The_files_and_queue_pages_render()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        foreach (var path in (string[])["/files", "/jobs"])
        {
            var resp = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    // ── re-authorising an existing connection ───────────────────────────────

    /// <summary>
    /// Seeds a connection owned by the stub user, writing straight to the table. The
    /// credential is never read on this path — reconnect only needs the label, tenant and
    /// account id — so going through the store and Vault would buy nothing but a
    /// dependency this test does not need.
    /// </summary>
    private async Task<Guid> SeedConnectionAsync(
        ProviderKind provider, string label, string instance, string? homeAccountId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();

        if (!await db.Users.AnyAsync(u => u.Id == StubAuth.UserId))
        {
            db.Users.Add(new OutPost2.Core.Users.User
            {
                Id = StubAuth.UserId, GitHubUserId = 4242, GitHubLogin = "tester",
            });
        }

        var c = new Connection
        {
            UserId = StubAuth.UserId,
            Label = label,
            Provider = provider,
            Instance = instance,
            HomeAccountId = homeAccountId,
            AuthKind = provider == ProviderKind.Microsoft
                ? AuthKind.OAuth2 : AuthKind.PersonalAccessToken,
            SealedCredential = "{}",
            State = ConnectionState.Active,
        };

        db.Connections.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    [SkippableFact]
    public async Task Reconnect_sends_an_existing_mailbox_back_to_its_own_tenant()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        const string Tenant = "00000000-1111-2222-3333-444444444444";
        var id = await SeedConnectionAsync(
            ProviderKind.Microsoft, "work-reconnect", Tenant, "oid-1." + Tenant);

        var resp = await _client.PostAsync($"/connections/{id}/reconnect",
            await FormAsync("/connections"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var location = resp.Headers.Location!.ToString();
        Assert.Contains("login.microsoftonline.com", location);

        // The connection's OWN directory, not /common. Reconnecting must not be a chance
        // to land in a different tenant by habit and quietly create a second mailbox.
        Assert.Contains(Tenant, location);
        Assert.Contains("prompt=select_account", location);
        Assert.Contains("code_challenge_method=S256", location);

        // The whole point: the label is reused. /microsoft/start refuses a label that
        // already exists, so if this ever started routing through that guard it would
        // break and the only route back would be remove-and-re-add.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();
        var pending = await db.PendingConnections
            .OrderByDescending(x => x.CreatedAt).FirstAsync();

        Assert.Equal("work-reconnect", pending.Label);
        Assert.Equal("oid-1." + Tenant, pending.ExpectedHomeAccountId);
        Assert.True(pending.IsReconnect);
    }

    [SkippableFact]
    public async Task Reconnect_refuses_a_connection_that_is_not_OAuth()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        var id = await SeedConnectionAsync(
            ProviderKind.YouTrack, "tracker-reconnect", "https://yt.example.com", null);

        var resp = await _client.PostAsync($"/connections/{id}/reconnect",
            await FormAsync("/connections"));

        // Redirected back with an explanation rather than sent to Microsoft to consent
        // for a tracker, which would fail in a way nobody could interpret.
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.Contains("error=", location);
        Assert.DoesNotContain("login.microsoftonline.com", location);
    }

    [SkippableFact]
    public async Task Reconnect_will_not_touch_a_connection_owned_by_someone_else()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // A connection belonging to a different user. Looked up by id alone it would be
        // found; scoped by owner it must not be, or a guessed id reaches another person's
        // mailbox — the single most damaging bug this endpoint could have.
        Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();
            var other = new OutPost2.Core.Users.User { GitHubUserId = 9999, GitHubLogin = "someone-else" };
            db.Users.Add(other);

            var c = new Connection
            {
                UserId = other.Id,
                Label = "their-mailbox",
                Provider = ProviderKind.Microsoft,
                Instance = "aaaaaaaa-0000-0000-0000-000000000000",
                HomeAccountId = "oid-9.aaaaaaaa-0000-0000-0000-000000000000",
                AuthKind = AuthKind.OAuth2,
                SealedCredential = "{}",
                State = ConnectionState.Active,
            };
            db.Connections.Add(c);
            await db.SaveChangesAsync();
            id = c.Id;
        }

        var resp = await _client.PostAsync($"/connections/{id}/reconnect",
            await FormAsync("/connections"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Mcp_still_answers_401_with_a_challenge_header()
    {
        Skip.IfNot(_factory.Available, _factory.Why);

        // The stub auth scheme must not accidentally let the MCP endpoint through; it is
        // bearer-only on purpose.
        var resp = await _client.PostAsync("/mcp",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("resource_metadata", resp.Headers.WwwAuthenticate.ToString());
    }
}
