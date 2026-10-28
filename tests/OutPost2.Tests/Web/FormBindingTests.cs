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
