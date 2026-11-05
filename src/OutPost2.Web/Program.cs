using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using OutPost2.Infrastructure.Users;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web;
using OutPost2.Web.Auth;
using OutPost2.Web.Code;
using OutPost2.Web.Connections;
using OutPost2.Web.Files;
using OutPost2.Web.Jobs;
using OutPost2.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

// MCP: the tool surface IS the product (D1). Streamable HTTP, protected by the same
// cookie/auth pipeline as the rest of the app for now.
builder.Services.AddSingleton<OutPost2.Web.Mcp.IItemIndex, OutPost2.Web.Mcp.ItemIndex>();
builder.Services.AddSingleton<OutPost2.Web.Mcp.ISearchContinuations,
                              OutPost2.Web.Mcp.SearchContinuations>();
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

var gh = builder.Configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>()
         ?? new GitHubOptions();
builder.Services.AddSingleton(gh);

builder.Services.AddDbContext<OutPostDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddScoped<OutPost2.Infrastructure.Auth.IOAuthService, OutPost2.Infrastructure.Auth.OAuthService>();

builder.Services.Configure<OutPost2.Infrastructure.Security.VaultOptions>(
    builder.Configuration.GetSection(OutPost2.Infrastructure.Security.VaultOptions.SectionName));
builder.Services.Configure<EntraOptions>(builder.Configuration.GetSection(EntraOptions.SectionName));
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));
builder.Services.Configure<OutPost2.Infrastructure.Files.StorageOptions>(
    builder.Configuration.GetSection(OutPost2.Infrastructure.Files.StorageOptions.SectionName));
builder.Services.Configure<OutPost2.Infrastructure.Feedback.FeedbackOptions>(
    builder.Configuration.GetSection(OutPost2.Infrastructure.Feedback.FeedbackOptions.SectionName));
builder.Services.Configure<OutPost2.Infrastructure.Security.TrustedRootsOptions>(
    builder.Configuration.GetSection(OutPost2.Infrastructure.Security.TrustedRootsOptions.SectionName));
builder.Services.Configure<OutPost2.Infrastructure.Notifications.TelegramOptions>(
    builder.Configuration.GetSection(OutPost2.Infrastructure.Notifications.TelegramOptions.SectionName));

builder.Services.AddSingleton<OutPost2.Infrastructure.Security.IVaultTransitClient,
                              OutPost2.Infrastructure.Security.VaultTransitClient>();

// Singleton rather than scoped: it holds no state, its only dependency is the singleton
// Vault client, and the data protection key ring resolves it from the ROOT provider —
// where a scoped registration cannot be resolved at all.
builder.Services.AddSingleton<OutPost2.Infrastructure.Security.ICredentialProtector,
                              OutPost2.Infrastructure.Security.CredentialProtector>();

// OP2-56. Without this the key ring is generated fresh in the container filesystem on
// every start, so a deploy rejects every auth cookie and every antiforgery token minted
// before it — observed directly, as a preview session that stopped working across a
// rebuild, and it would break an OAuth consent that happened to be in flight.
//
// The application name is fixed and must stay fixed: it is part of the purpose chain
// every payload is derived with, so changing it has the same effect as losing the keys.
builder.Services.AddDataProtection()
    .SetApplicationName("outpost2")
    .PersistKeysToDbContext<OutPostDbContext>()
    .ProtectKeysWithVault();

// Serialised, because the DbContext behind it is scoped per request and several tools fan
// out across mailboxes — which made six concurrent users of one context and turned a
// database error into a confident "Nothing found" (OP2-239).
builder.Services.AddScoped<ConnectionStore>();
builder.Services.AddScoped<IConnectionStore>(sp =>
    new SerialisedConnectionStore(sp.GetRequiredService<ConnectionStore>()));
builder.Services.AddScoped<IPendingConnectionStore, PendingConnectionStore>();
builder.Services.AddScoped<IMicrosoftConnectionService, MicrosoftConnectionService>();

// Gmail sign-in (OP2-279). Scoped for the same reason as Microsoft's: it reads and writes
// the connection through the request's store. The token accessor is the same object under
// the narrower interface the Gmail provider will take.
builder.Services.AddScoped<IGoogleConnectionService, GoogleConnectionService>();
builder.Services.AddScoped<IGoogleTokenAccessor>(sp => sp.GetRequiredService<IGoogleConnectionService>());
builder.Services.AddHttpClient("google").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("graph").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

// Upload-session chunks, which carry their own authorisation in the URL and must NOT
// be given our bearer token. Ten minutes because one chunk is 8 MiB over whatever
// connection the server happens to have (OP2-101); the sender sets this itself, and
// registering the name keeps it out of the default client's configuration.
builder.Services.AddHttpClient("graph-upload").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IMicrosoftConnectionServiceAccessor>(
    sp => sp.GetRequiredService<IMicrosoftConnectionService>());
// Mail is routed by the connection's provider (OP2-278): each provider registers itself as
// an IMailProvider, and the tools get the router. Microsoft Graph and Gmail (OP2-280).
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IMailProvider, OutPost2.Infrastructure.Mail.MailService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IMailProvider, OutPost2.Infrastructure.Mail.GmailService>();
builder.Services.AddHttpClient("gmail").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IMailService, OutPost2.Infrastructure.Mail.MailServiceRouter>();

// Scoped like the other Graph services -- it resolves an access token per request, and a
// singleton holding a scoped accessor is a captive dependency the container refuses. The
// per-mailbox timezone it caches therefore lives in IMemoryCache, not in a field.
builder.Services.AddScoped<OutPost2.Infrastructure.Calendar.ICalendarService,
                           OutPost2.Infrastructure.Calendar.CalendarService>();

// The zone exact mail times print in, asked of the provider that owns the account (OP2-285):
// Graph mailbox settings for Microsoft, the Calendar time-zone setting for Google. Scoped
// with the services the sources wrap; each source caches in IMemoryCache.
builder.Services.AddScoped<OutPost2.Infrastructure.Calendar.IMailboxZoneSource,
                           OutPost2.Infrastructure.Calendar.GraphMailboxZoneSource>();
builder.Services.AddScoped<OutPost2.Infrastructure.Calendar.IMailboxZoneSource,
                           OutPost2.Infrastructure.Calendar.GoogleMailboxZoneSource>();
builder.Services.AddScoped<OutPost2.Infrastructure.Calendar.IMailboxZones,
                           OutPost2.Infrastructure.Calendar.MailboxZones>();
// Chat is routed by the connection's provider (OP2-86), as code and mail are: each chat
// service registers as an IChatProvider, and the chat tools get the router.
builder.Services.AddScoped<OutPost2.Infrastructure.Teams.IChatProvider,
                           OutPost2.Infrastructure.Teams.TeamsService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Teams.IChatProvider,
                           OutPost2.Infrastructure.Teams.SlackService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Teams.ITeamsService,
                           OutPost2.Infrastructure.Teams.ChatServiceRouter>();
builder.Services.AddScoped<ISlackConnectionService, SlackConnectionService>();
// Slack's public API (OP2-86): no private CA, for the same reason Graph has none.
builder.Services.AddHttpClient("slack").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<OutPost2.Infrastructure.People.IPeopleService,
                           OutPost2.Infrastructure.People.PeopleService>();
builder.Services.AddSingleton<OutPost2.Infrastructure.Mail.IAttachmentExtractor,
                              OutPost2.Infrastructure.Mail.AttachmentExtractor>();
builder.Services.AddSingleton<OutPost2.Infrastructure.Files.IObjectStore, OutPost2.Infrastructure.Files.S3ObjectStore>();
builder.Services.AddScoped<OutPost2.Infrastructure.Files.IStagingService, OutPost2.Infrastructure.Files.StagingService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Files.IUploadTicketService,
    OutPost2.Infrastructure.Files.UploadTicketService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Files.IDownloadTicketService,
    OutPost2.Infrastructure.Files.DownloadTicketService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IAttachmentSenderProvider,
                           OutPost2.Infrastructure.Mail.LargeAttachmentSender>();
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IAttachmentSenderProvider,
                           OutPost2.Infrastructure.Mail.GmailAttachmentSender>();
builder.Services.AddHttpClient("gmail-upload").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.ILargeAttachmentSender,
                           OutPost2.Infrastructure.Mail.LargeAttachmentSenderRouter>();
builder.Services.AddScoped<OutPost2.Infrastructure.Jobs.IJobService, OutPost2.Infrastructure.Jobs.JobService>();

// Reaching a user who is not looking (OP2-165). Channels are registered as a set and
// the notifier offers each of them every message; adding a second one later is a line
// here and nothing at any call site. Registering none is a supported configuration --
// the notifier says so in the log and the product behaves exactly as it did before.
builder.Services.AddScoped<OutPost2.Infrastructure.Notifications.TelegramChannel>();
builder.Services.AddScoped<OutPost2.Core.Notifications.INotificationChannel>(
    sp => sp.GetRequiredService<OutPost2.Infrastructure.Notifications.TelegramChannel>());
builder.Services.AddScoped<OutPost2.Core.Notifications.INotifier,
                           OutPost2.Infrastructure.Notifications.Notifier>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<HousekeepingService>();
// Both of these reach SELF-HOSTED servers, which is where a private CA turns up: a
// GitHub Enterprise instance or an on-premises YouTrack behind an internal authority
// fails TLS against the OS trust store alone (OP2-45). Configure
// OutPost2__TrustedRoots__CaCertPaths__0 and it is trusted; configure nothing and the
// platform's own validation is left exactly as it was.
//
// Graph is deliberately NOT given this treatment. It is Microsoft's public endpoint, and
// a deployment that needed a private root to reach it would be describing an
// interception proxy rather than a self-hosted server.
builder.Services.AddHttpClient("telegram").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("youtrack")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(TrustedRootHandler);
builder.Services.AddHttpClient("github")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(TrustedRootHandler);
// Gitea and Forgejo are self-hosted by definition (OP2-84), so the private CA applies.
builder.Services.AddHttpClient("gitea")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(TrustedRootHandler);

static HttpMessageHandler TrustedRootHandler(IServiceProvider sp)
{
    var paths = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutPost2.Infrastructure.Security.TrustedRootsOptions>>()
        .Value.CaCertPaths;

    return OutPost2.Infrastructure.Security.PrivateCaTrust.Handler(
        OutPost2.Infrastructure.Security.PrivateCaTrust.Load(paths,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("OutPost2.TrustedRoots")));
}
builder.Services.AddScoped<OutPost2.Infrastructure.Auth.IPreviewLinkService,
                           OutPost2.Infrastructure.Auth.PreviewLinkService>();
builder.Services.AddScoped<IGitHubConnectionService, GitHubConnectionService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<OutPost2.Infrastructure.Code.IRepoResolver,
    OutPost2.Infrastructure.Code.RepoResolver>();
// Code is routed by the connection's provider (OP2-84), as mail is: each code host
// registers as an ICodeProvider, and the tools and the repo resolver get the router.
builder.Services.AddScoped<IGiteaConnectionService, GiteaConnectionService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Code.ICodeProvider,
                           OutPost2.Infrastructure.Code.GitHubService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Code.ICodeProvider,
                           OutPost2.Infrastructure.Code.GiteaService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Code.IGitHubService,
                           OutPost2.Infrastructure.Code.CodeServiceRouter>();
builder.Services.AddScoped<IYouTrackConnectionService, YouTrackConnectionService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Issues.IYouTrackService,
                           OutPost2.Infrastructure.Issues.YouTrackService>();

// Friction reports go to OutPost2's OWN tracker under the server's credential, not to a
// user connection — the coworkers this exists for have no connection to it (OP2-74).
builder.Services.AddScoped<OutPost2.Infrastructure.Feedback.IFrictionService,
                           OutPost2.Infrastructure.Feedback.FrictionService>();

// The app sits behind the Cloudflare tunnel, which terminates TLS and forwards plain HTTP
// to the container. Without honouring X-Forwarded-Proto, ASP.NET Core believes the request
// is http:// and builds the OAuth redirect_uri as http://<public-host>/signin-github
// — which GitHub rejects as a mismatch against the registered https:// callback.
// cloudflared is an arbitrary address on the compose network, so the default known-proxy
// allowlist has to be cleared for the headers to be honoured at all.
// Resolved once at startup and injected wherever a user-facing link is produced, so no
// tool can quietly hard-code one deployment's address.
builder.Services.AddSingleton(new PublicUrls(builder.Configuration["OutPost2:PublicBaseUrl"]));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var auth = builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = GitHubAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "op2.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;          // Lax, not Strict: the OAuth return is a cross-site GET.
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.LoginPath = "/signin";
        o.LogoutPath = "/signout";
        o.AccessDeniedPath = "/denied";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });

if (gh.IsConfigured)
{
    var allowed = gh.ParseAllowedUserIds();

    auth.AddGitHub(o =>
    {
        o.ClientId = gh.ClientId;
        o.ClientSecret = gh.ClientSecret;
        o.CallbackPath = "/signin-github";

        // Identity only. No repo, org or user:email scope — we need to know who someone is,
        // not to read anything of theirs. Asking for more would be unjustifiable for an app
        // whose whole job is custody of other people's credentials.
        o.Scope.Clear();

        o.ClaimActions.MapJsonKey("urn:github:avatar", "avatar_url");

        // The allowlist gate. Enforced here, at ticket creation, so an unauthorised identity
        // never reaches the cookie-issuing path: access is ASSIGNED, and the endpoint being
        // public must not imply the product is.
        o.Events.OnCreatingTicket = async ctx =>
        {
            var idClaim = ctx.Identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var login = ctx.Identity?.FindFirst(ClaimTypes.Name)?.Value ?? "(unknown)";

            if (!long.TryParse(idClaim, out var githubId) || !allowed.Contains(githubId))
            {
                ctx.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OutPost2.Auth")
                    .LogWarning("Denied sign-in for GitHub user {Login} (id {Id}) — not on the allowlist.",
                                login, idClaim ?? "null");

                // Stashed for OnRemoteFailure, which is where the redirect happens and
                // which can no longer see the identity. Naming the refused account is the
                // difference between "not authorised" and "you used the wrong login".
                ctx.HttpContext.Items["op2:denied-login"] = login;

                ctx.Fail($"GitHub user {login} is not authorised to use Outpost.");
                return;
            }

            // Allowlisted: materialise the product user. Done after the gate, so an
            // unauthorised identity never creates a row.
            var store = ctx.HttpContext.RequestServices.GetRequiredService<IUserStore>();
            var user = await store.UpsertOnSignInAsync(
                githubId, login, ctx.Identity?.FindFirst("urn:github:avatar")?.Value);

            // Carry our own user id, so nothing downstream has to re-derive identity from
            // the GitHub claim. Every connection lookup is scoped by this.
            ctx.Identity?.AddClaim(new Claim("op2:uid", user.Id.ToString()));
        };

        o.Events.OnRemoteFailure = ctx =>
        {
            var denied = ctx.HttpContext.Items["op2:denied-login"] as string;
            ctx.Response.Redirect(denied is null
                ? "/denied"
                : "/denied?login=" + Uri.EscapeDataString(denied));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorization();

// An MCP client is a browser. Claude's connector dialog fetches /mcp and the discovery
// documents cross-origin, so without this the preflight never gets an answer and the
// server reports as unreachable — which looks nothing like the CORS problem it is.
//
// Any origin, and deliberately no credentials. These endpoints carry no ambient authority:
// /mcp is bearer-only and the well-known documents are public by specification, so a page
// that calls them cross-origin learns exactly what it could have learned by calling them
// itself. Allowing credentials here would be the opposite — it would let any page act as
// the signed-in user — and the two settings are mutually exclusive for that reason.
builder.Services.AddCors(o => o.AddPolicy("mcp", p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
    // Without exposing these the client gets the response but cannot read the two headers
    // that make the protocol work: the challenge that says where to authenticate, and the
    // session id that ties a streamable-HTTP conversation together.
    .WithExposedHeaders("WWW-Authenticate", "Mcp-Session-Id", "Mcp-Protocol-Version")));

var app = builder.Build();

app.UseForwardedHeaders();

// Migrating on boot is its own decision, asked for explicitly (OP2-259).
//
// It used to be keyed off Development, which quietly tied "apply the schema" to "hand out
// stack traces to anybody who can reach me" — and the second of those is not something a
// publicly reachable host should do. Two unrelated questions on one switch means neither
// can be answered without the other: this deployment needs the migration and must not have
// the developer exception page, and there was no way to say so.
//
// Still off by default, for the reason the old comment gave: concurrent replicas racing to
// migrate is a good way to corrupt a schema. The single-container dev stack sets it on.
if (app.Configuration.GetValue("OutPost2:MigrateOnBoot", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OutPostDbContext>().Database.MigrateAsync();
}

// Anywhere but a developer's own machine, an unhandled exception is one sentence. In
// Development ASP.NET Core serves the developer exception page instead, which returns
// internal source paths, the middleware pipeline, line numbers and the request's own
// headers echoed back — fine on a laptop, and an invitation on a host that is reachable
// from the internet and custodying other people's mailboxes (OP2-259).
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

// Scoped by path rather than applied globally: the Razor pages are cookie-authenticated
// and have no business answering cross-origin at all. This also has to sit ahead of the
// bearer middleware, because a CORS preflight carries no Authorization header by
// definition — challenge it and the browser never sends the real request.
app.UseWhen(IsCrossOriginEndpoint, b => b.UseCors("mcp"));

app.UseAuthentication();

// Immediately after authentication and BEFORE anything can act on the principal, so a
// preview session is confined no matter which endpoint it reaches.
app.UseMiddleware<PreviewReadOnlyMiddleware>();

// ASP0001 claims this is not between UseRouting and UseEndpoints. It is — the endpoint
// middleware WebApplication adds for the Map* calls below runs after it — and the branch
// that genuinely has no UseAuthorization is the /mcp one further down. That branch is
// checked rather than assumed (OP2-145): McpBearerMiddleware is an unconditional deny, it
// answers 401 before calling next() for a missing or invalid token, and nothing it lets
// through carries an [Authorize] for a policy to evaluate. Fail-closed by construction,
// which is stricter than a policy, not looser. Two tests hold that line — see
// Mcp_still_answers_401* in FormBindingTests.
#pragma warning disable ASP0001
app.UseAuthorization();
#pragma warning restore ASP0001

// Required by [FromForm] in minimal APIs, and wanted anyway: these forms change
// connections and post credentials. SameSite=Lax already blocks cross-site POSTs, but
// an app custodying other people's mailboxes should not rely on one mechanism.
app.UseAntiforgery();
app.MapRazorPages();
app.MapConnectionEndpoints();
app.MapFileEndpoints();
var publicBaseUrl = app.Services.GetRequiredService<PublicUrls>().Base;
app.MapMcpOAuth(publicBaseUrl);
app.MapPreviewLinks();
app.MapRepoNames();

// Bearer-only on /mcp. The cookie pipeline's 302-to-a-login-page is unusable by an MCP
// client, which needs a 401 plus WWW-Authenticate to discover where to authenticate.
app.MapWhen(c => c.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.UseMiddleware<McpBearerMiddleware>(publicBaseUrl);
        branch.UseRouting();
        branch.UseEndpoints(e => e.MapMcp("/mcp"));
    });

app.MapGet("/signin/github", (GitHubOptions o) =>
    o.IsConfigured
        ? Results.Challenge(
            new() { RedirectUri = "/" },
            [GitHubAuthenticationDefaults.AuthenticationScheme])
        : Results.Redirect("/denied?reason=unconfigured"));

app.MapPost("/signout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Liveness only — deliberately unauthenticated and says nothing about internals.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

// The endpoints an MCP client reaches with fetch(), and therefore the only ones that need
// to answer a preflight. /oauth/authorize is missing on purpose: the browser navigates to
// it, which is not a cross-origin request and must stay a normal cookie-bearing one.
static bool IsCrossOriginEndpoint(HttpContext c) =>
    c.Request.Path.StartsWithSegments("/mcp")
    || c.Request.Path.StartsWithSegments("/.well-known")
    || c.Request.Path.StartsWithSegments("/oauth/register")
    || c.Request.Path.StartsWithSegments("/oauth/token");
