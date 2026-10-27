using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Users;
using OutPost2.Web.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

var gh = builder.Configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>()
         ?? new GitHubOptions();
builder.Services.AddSingleton(gh);

builder.Services.AddDbContext<OutPostDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddScoped<IUserStore, UserStore>();

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

                ctx.Fail($"GitHub user {login} is not authorised to use OutPost2.");
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
            ctx.Response.Redirect("/denied");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.MapGet("/signin", (HttpContext ctx, GitHubOptions o) =>
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

public partial class Program;
