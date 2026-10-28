using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Auth;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Auth;
using OutPost2.Infrastructure.Data;
using OutPost2.Web.Auth;

namespace OutPost2.Tests.Auth;

public sealed class PreviewLinkTests : IAsyncLifetime
{
    private const string Admin =
        "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";

    private readonly string _dbName = "op2_prev_" + Guid.NewGuid().ToString("n")[..12];
    private OutPostDbContext _db = null!;
    private PreviewLinkService _svc = null!;
    private Guid _alice, _bob;
    private bool _ready;
    private string _why = "";

    public async Task InitializeAsync()
    {
        try
        {
            await using (var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
                await a.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();

            var alice = new User { GitHubUserId = 1, GitHubLogin = "alice" };
            var bob = new User { GitHubUserId = 2, GitHubLogin = "bob" };
            _db.Users.AddRange(alice, bob);
            await _db.SaveChangesAsync();
            _alice = alice.Id;
            _bob = bob.Id;

            _svc = new PreviewLinkService(_db, NullLogger<PreviewLinkService>.Instance);
            _ready = true;
        }
        catch (Exception ex) { _why = ex.Message; }
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.DisposeAsync();
        try
        {
            await using var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
            await a.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
        }
        catch { /* best effort */ }
    }

    // ── the token itself ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_minted_link_redeems_once_and_keeps_working_until_it_expires()
    {
        Skip.IfNot(_ready, _why);

        var (link, token) = await _svc.MintAsync(_alice, "Claude design");

        // Not single-use: a design tool loads a page, then its stylesheets, then the next
        // page. Burning the link on first contact would break it before it rendered.
        Assert.Equal(_alice, await _svc.RedeemAsync(token, "1.2.3.4 agent"));
        Assert.Equal(_alice, await _svc.RedeemAsync(token, "1.2.3.4 agent"));

        _db.ChangeTracker.Clear();
        var stored = await _db.PreviewLinks.SingleAsync(l => l.TokenHash == link.TokenHash);
        Assert.Equal(2, stored.UseCount);
        Assert.NotNull(stored.FirstUsedAt);
        Assert.Equal("1.2.3.4 agent", stored.FirstUsedFrom);
    }

    [SkippableFact]
    public async Task The_plaintext_token_is_never_stored()
    {
        Skip.IfNot(_ready, _why);

        var (_, token) = await _svc.MintAsync(_alice, "design");

        // A database read must not hand anybody a working link.
        var all = await _db.PreviewLinks.ToListAsync();
        Assert.DoesNotContain(all, l => l.TokenHash.Contains(token, StringComparison.Ordinal));
        Assert.All(all, l => Assert.Equal(64, l.TokenHash.Length));
    }

    [SkippableFact]
    public async Task A_revoked_link_stops_working_immediately()
    {
        Skip.IfNot(_ready, _why);

        var (link, token) = await _svc.MintAsync(_alice, "design");
        Assert.NotNull(await _svc.RedeemAsync(token, null));

        await _svc.RevokeAsync(_alice, link.TokenHash);
        _db.ChangeTracker.Clear();

        Assert.Null(await _svc.RedeemAsync(token, null));
    }

    [SkippableFact]
    public async Task An_expired_link_is_refused()
    {
        Skip.IfNot(_ready, _why);

        var (link, token) = await _svc.MintAsync(_alice, "design");

        link.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Null(await _svc.RedeemAsync(token, null));
    }

    [SkippableFact]
    public async Task One_user_cannot_revoke_anothers_link()
    {
        Skip.IfNot(_ready, _why);

        var (link, token) = await _svc.MintAsync(_alice, "alice's design link");

        await _svc.RevokeAsync(_bob, link.TokenHash);
        _db.ChangeTracker.Clear();

        // Bob knowing the hash must not be enough; ownership is in the WHERE clause.
        Assert.Equal(_alice, await _svc.RedeemAsync(token, null));
    }

    [SkippableFact]
    public async Task Lifetime_is_clamped_rather_than_rejected()
    {
        Skip.IfNot(_ready, _why);

        var (tooLong, _) = await _svc.MintAsync(_alice, "forever", TimeSpan.FromDays(3650));
        Assert.True(tooLong.ExpiresAt <= DateTimeOffset.UtcNow.Add(PreviewLink.MaxLifetime).AddMinutes(1));

        var (tooShort, _) = await _svc.MintAsync(_alice, "instant", TimeSpan.Zero);
        Assert.True(tooShort.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [SkippableFact]
    public async Task Garbage_is_refused_without_throwing()
    {
        Skip.IfNot(_ready, _why);

        Assert.Null(await _svc.RedeemAsync("", null));
        Assert.Null(await _svc.RedeemAsync("   ", null));
        Assert.Null(await _svc.RedeemAsync("not-a-real-token", null));
    }

    [SkippableFact]
    public async Task Only_live_links_are_listed()
    {
        Skip.IfNot(_ready, _why);

        var (revoked, _) = await _svc.MintAsync(_alice, "revoked one");
        var (expired, _) = await _svc.MintAsync(_alice, "expired one");
        await _svc.MintAsync(_alice, "live one");
        await _svc.MintAsync(_bob, "bob's one");

        await _svc.RevokeAsync(_alice, revoked.TokenHash);
        expired.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var listed = await _svc.ListAsync(_alice);
        Assert.Single(listed);
        Assert.Equal("live one", listed[0].Label);
    }
}

/// <summary>
/// The read-only boundary. These matter more than the token mechanics: a leaked link that
/// can only read is an embarrassment, and one that can write is an incident.
/// </summary>
public sealed class PreviewReadOnlyMiddlewareTests
{
    private static async Task<(int Status, bool Continued)> RunAsync(
        string method, string path, bool preview, bool authenticated = true)
    {
        var reached = false;
        var mw = new PreviewReadOnlyMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            NullLogger<PreviewReadOnlyMiddleware>.Instance);

        var claims = new List<Claim> { new("op2:uid", Guid.NewGuid().ToString()) };
        if (preview) claims.Add(new Claim(PreviewEndpoints.PreviewClaim, "1"));

        var ctx = new DefaultHttpContext
        {
            User = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))
                : new ClaimsPrincipal(new ClaimsIdentity()),
        };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        await mw.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, reached);
    }

    [Theory]
    [InlineData("/connections")]
    [InlineData("/jobs")]
    [InlineData("/preview-links")]
    [InlineData("/files")]
    [InlineData("/")]
    public async Task A_preview_session_may_read_pages(string path)
    {
        // /files is in here deliberately. Blocking the whole prefix to protect the one
        // route that serves bytes also broke the Files PAGE, which is just UI.
        var (_, continued) = await RunAsync("GET", path, preview: true);
        Assert.True(continued, $"GET {path} should have been allowed");
    }

    [Theory]
    [InlineData("POST", "/connections/youtrack/add")]
    [InlineData("POST", "/connections/abc/delete")]
    [InlineData("POST", "/connections/abc/default")]
    [InlineData("DELETE", "/connections/abc")]
    [InlineData("PUT", "/anything")]
    [InlineData("PATCH", "/anything")]
    public async Task A_preview_session_may_not_change_anything(string method, string path)
    {
        var (status, continued) = await RunAsync(method, path, preview: true);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(continued, "the request should not have reached the endpoint");
    }

    [Theory]
    [InlineData("GET", "/oauth/authorize")]
    [InlineData("POST", "/oauth/approve")]
    [InlineData("POST", "/oauth/token")]
    [InlineData("POST", "/oauth/register")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/mcp")]
    [InlineData("POST", "/preview/mint")]
    [InlineData("GET", "/files/abc/download")]
    public async Task A_preview_session_cannot_reach_the_paths_that_would_escalate_it(
        string method, string path)
    {
        // The OAuth ones are the point. Without this a read-only, expiring design link
        // could walk the consent flow and mint itself a permanent MCP bearer token —
        // turning a window into a door. /preview/mint would let it extend itself, and a
        // file download hands over bytes rather than UI.
        var (status, continued) = await RunAsync(method, path, preview: true);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(continued);
    }

    [Theory]
    [InlineData("GET", "/connections")]
    [InlineData("POST", "/connections/youtrack/add")]
    [InlineData("POST", "/oauth/approve")]
    public async Task A_normal_signed_in_session_is_untouched(string method, string path)
    {
        var (_, continued) = await RunAsync(method, path, preview: false);
        Assert.True(continued, "a real session should pass straight through");
    }

    [Fact]
    public async Task An_anonymous_request_is_untouched()
    {
        // Anonymous requests are the authorization layer's business, not this one's.
        // Returning 403 here would break the sign-in page.
        var (_, continued) = await RunAsync("GET", "/signin", preview: false, authenticated: false);
        Assert.True(continued);
    }
}
