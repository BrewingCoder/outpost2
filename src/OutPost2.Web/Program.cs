using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Users;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Auth;
using OutPost2.Web.Connections;
using OutPost2.Web.Files;
using OutPost2.Web.Jobs;
using OutPost2.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

// MCP: the tool surface IS the product (D1). Streamable HTTP, protected by the same
// cookie/auth pipeline as the rest of the app for now.
builder.Services.AddSingleton<OutPost2.Web.Mcp.IMessageIndex, OutPost2.Web.Mcp.MessageIndex>();
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
builder.Services.Configure<OutPost2.Infrastructure.Files.StorageOptions>(
    builder.Configuration.GetSection(OutPost2.Infrastructure.Files.StorageOptions.SectionName));

builder.Services.AddSingleton<OutPost2.Infrastructure.Security.IVaultTransitClient,
                              OutPost2.Infrastructure.Security.VaultTransitClient>();
builder.Services.AddScoped<OutPost2.Infrastructure.Security.ICredentialProtector,
                           OutPost2.Infrastructure.Security.CredentialProtector>();
builder.Services.AddScoped<IConnectionStore, ConnectionStore>();
builder.Services.AddScoped<IPendingConnectionStore, PendingConnectionStore>();
builder.Services.AddScoped<IMicrosoftConnectionService, MicrosoftConnectionService>();
builder.Services.AddHttpClient("graph").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IMicrosoftConnectionServiceAccessor>(
    sp => sp.GetRequiredService<IMicrosoftConnectionService>());
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.IMailService, OutPost2.Infrastructure.Mail.MailService>();
builder.Services.AddSingleton<OutPost2.Infrastructure.Mail.IAttachmentExtractor,
                              OutPost2.Infrastructure.Mail.AttachmentExtractor>();
builder.Services.AddSingleton<OutPost2.Infrastructure.Files.IObjectStore, OutPost2.Infrastructure.Files.S3ObjectStore>();
builder.Services.AddScoped<OutPost2.Infrastructure.Files.IStagingService, OutPost2.Infrastructure.Files.StagingService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Mail.ILargeAttachmentSender,
                           OutPost2.Infrastructure.Mail.LargeAttachmentSender>();
builder.Services.AddScoped<OutPost2.Infrastructure.Jobs.IJobService, OutPost2.Infrastructure.Jobs.JobService>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<HousekeepingService>();
builder.Services.AddHttpClient("youtrack").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("github").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<OutPost2.Infrastructure.Auth.IPreviewLinkService,
                           OutPost2.Infrastructure.Auth.PreviewLinkService>();
builder.Services.AddScoped<IGitHubConnectionService, GitHubConnectionService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Code.IGitHubService,
                           OutPost2.Infrastructure.Code.GitHubService>();
builder.Services.AddScoped<IYouTrackConnectionService, YouTrackConnectionService>();
builder.Services.AddScoped<OutPost2.Infrastructure.Issues.IYouTrackService,
                           OutPost2.Infrastructure.Issues.YouTrackService>();

// The app sits behind the Cloudflare tunnel, which terminates TLS and forwards plain HTTP
// to the container. Without honouring X-Forwarded-Proto, ASP.NET Core believes the request
// is http:// and builds the OAuth redirect_uri as http://outpost.brewingcoder.com/signin-github
// — which GitHub rejects as a mismatch against the registered https:// callback.
// cloudflared is an arbitrary address on the compose network, so the default known-proxy
// allowlist has to be cleared for the headers to be honoured at all.
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

// Dev convenience only. Production applies migrations as a deliberate deploy step, not on
// boot — concurrent replicas racing to migrate is a good way to corrupt a schema.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OutPostDbContext>().Database.MigrateAsync();
}

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

app.UseAuthorization();

// Required by [FromForm] in minimal APIs, and wanted anyway: these forms change
// connections and post credentials. SameSite=Lax already blocks cross-site POSTs, but
// an app custodying other people's mailboxes should not rely on one mechanism.
app.UseAntiforgery();
app.MapRazorPages();
app.MapConnectionEndpoints();
app.MapFileEndpoints();
var publicBaseUrl = builder.Configuration["OutPost2:PublicBaseUrl"] ?? "https://outpost.brewingcoder.com";
app.MapMcpOAuth(publicBaseUrl);
app.MapPreviewLinks();

// Bearer-only on /mcp. The cookie pipeline's 302-to-a-login-page is unusable by an MCP
// client, which needs a 401 plus WWW-Authenticate to discover where to authenticate.
app.MapWhen(c => c.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.UseMiddleware<McpBearerMiddleware>(publicBaseUrl);
        branch.UseRouting();
        branch.UseEndpoints(e => e.MapMcp("/mcp"));
    });

app.MapGet("/signin/github", (HttpContext ctx, GitHubOptions o) =>
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

app.Run();

// The endpoints an MCP client reaches with fetch(), and therefore the only ones that need
// to answer a preflight. /oauth/authorize is missing on purpose: the browser navigates to
// it, which is not a cross-origin request and must stay a normal cookie-bearing one.
static bool IsCrossOriginEndpoint(HttpContext c) =>
    c.Request.Path.StartsWithSegments("/mcp")
    || c.Request.Path.StartsWithSegments("/.well-known")
    || c.Request.Path.StartsWithSegments("/oauth/register")
    || c.Request.Path.StartsWithSegments("/oauth/token");

public partial class Program;
