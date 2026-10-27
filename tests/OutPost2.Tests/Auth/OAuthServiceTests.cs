using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OutPost2.Infrastructure.Auth;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Tests.Auth;

/// <summary>
/// The token we mint for an MCP client transitively unlocks every connection its owner
/// has, and it lives on someone else's infrastructure. These tests pin the properties
/// that make that acceptable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OAuthServiceTests : IAsyncLifetime
{
    private const string Admin = "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";
    private readonly string _dbName = "op2_oauth_" + Guid.NewGuid().ToString("n")[..12];

    private OutPostDbContext _db = null!;
    private OAuthService _oauth = null!;
    private readonly Guid _alice = Guid.CreateVersion7();
    private readonly Guid _bob = Guid.CreateVersion7();
    private bool _ready;
    private string _why = "";

    private const string Redirect = "https://claude.ai/api/mcp/auth_callback";

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

            _oauth = new OAuthService(_db);
            _ready = true;
        }
        catch (Exception ex) { _why = $"Postgres unavailable: {ex.Message}"; }
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.DisposeAsync();
        await using var a = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await a.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string B64(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<(string ClientId, string Verifier, string Code)> SetupGrantAsync(
        Guid user, params string[] scope)
    {
        var client = await _oauth.RegisterClientAsync("Test client", [Redirect]);
        var (verifier, challenge) = Pkce();
        var code = await _oauth.IssueAuthCodeAsync(client.ClientId, user, Redirect, challenge, scope);
        return (client.ClientId, verifier, code);
    }

    // ── the happy path ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Redeems_a_code_for_a_usable_token()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);

        Assert.NotNull(grant);
        var token = await _oauth.ValidateAsync(grant!.AccessToken);
        Assert.NotNull(token);
        Assert.Equal(_alice, token!.UserId);
    }

    // ── replay, substitution and interception ────────────────────────────────

    [SkippableFact]
    public async Task A_code_cannot_be_redeemed_twice()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);

        Assert.NotNull(await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier));
        Assert.Null(await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier));
    }

    [SkippableFact]
    public async Task A_code_cannot_be_redeemed_by_a_different_client()
    {
        Skip.IfNot(_ready, _why);

        var (_, verifier, code) = await SetupGrantAsync(_alice);
        var other = await _oauth.RegisterClientAsync("Impostor", [Redirect]);

        Assert.Null(await _oauth.RedeemCodeAsync(code, other.ClientId, Redirect, verifier));
    }

    [SkippableFact]
    public async Task A_code_cannot_be_redeemed_against_a_different_redirect_uri()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);

        Assert.Null(await _oauth.RedeemCodeAsync(code, clientId, "https://evil.example/cb", verifier));
    }

    [SkippableFact]
    public async Task A_code_is_useless_without_the_PKCE_verifier()
    {
        Skip.IfNot(_ready, _why);

        // This is what makes an intercepted code worthless to the interceptor.
        var (clientId, _, code) = await SetupGrantAsync(_alice);

        Assert.Null(await _oauth.RedeemCodeAsync(code, clientId, Redirect, B64(RandomNumberGenerator.GetBytes(32))));
    }

    // ── token storage and lifecycle ──────────────────────────────────────────

    [SkippableFact]
    public async Task Tokens_are_not_stored_in_the_clear()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);

        // A database leak must not hand an attacker working credentials to every mailbox.
        var rows = await _db.OAuthTokens.AsNoTracking().ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.NotEqual(grant!.AccessToken, r.TokenHash);
            Assert.NotEqual(grant.RefreshToken, r.RefreshTokenHash);
        });
    }

    [SkippableFact]
    public async Task Refresh_rotates_and_kills_the_old_pair()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var first = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);

        var second = await _oauth.RefreshAsync(first!.RefreshToken, clientId);
        Assert.NotNull(second);
        Assert.NotEqual(first.AccessToken, second!.AccessToken);

        // A refresh token that still works after use cannot be told apart from a stolen
        // one being replayed, so the old pair must die.
        Assert.Null(await _oauth.ValidateAsync(first.AccessToken));
        Assert.Null(await _oauth.RefreshAsync(first.RefreshToken, clientId));
        Assert.NotNull(await _oauth.ValidateAsync(second.AccessToken));
    }

    [SkippableFact]
    public async Task Refresh_requires_the_owning_client()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);
        var other = await _oauth.RegisterClientAsync("Impostor", [Redirect]);

        Assert.Null(await _oauth.RefreshAsync(grant!.RefreshToken, other.ClientId));
    }

    [SkippableFact]
    public async Task Revocation_takes_effect_immediately()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);

        var token = await _oauth.ValidateAsync(grant!.AccessToken);
        await _oauth.RevokeAsync(_alice, token!.TokenHash);

        // "Disconnect Claude" has to mean it, on the very next call.
        Assert.Null(await _oauth.ValidateAsync(grant.AccessToken));
    }

    [SkippableFact]
    public async Task One_user_cannot_revoke_another_users_token()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);
        var token = await _oauth.ValidateAsync(grant!.AccessToken);

        await _oauth.RevokeAsync(_bob, token!.TokenHash);

        Assert.NotNull(await _oauth.ValidateAsync(grant.AccessToken));
    }

    [SkippableFact]
    public async Task An_expired_token_is_refused()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);

        var token = await _db.OAuthTokens.FirstAsync();
        token.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();

        Assert.Null(await _oauth.ValidateAsync(grant!.AccessToken));
    }

    [SkippableFact]
    public async Task A_garbage_token_is_refused_without_throwing()
    {
        Skip.IfNot(_ready, _why);

        Assert.Null(await _oauth.ValidateAsync("not-a-token"));
        Assert.Null(await _oauth.ValidateAsync(""));
    }

    // ── per-connection scoping: the OP2-5 recommendation taken at launch ─────

    [SkippableFact]
    public async Task An_unscoped_token_reaches_every_connection()
    {
        Skip.IfNot(_ready, _why);

        var (clientId, verifier, code) = await SetupGrantAsync(_alice);   // no scope
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);
        var token = await _oauth.ValidateAsync(grant!.AccessToken);

        Assert.True(token!.Allows("work"));
        Assert.True(token.Allows("home"));
        Assert.True(token.Allows("anything-added-later"));
    }

    [SkippableFact]
    public async Task A_scoped_token_cannot_reach_a_connection_outside_its_scope()
    {
        Skip.IfNot(_ready, _why);

        // The honest answer to "do I want work mail reachable from my personal assistant".
        var (clientId, verifier, code) = await SetupGrantAsync(_alice, "home");
        var grant = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);
        var token = await _oauth.ValidateAsync(grant!.AccessToken);

        Assert.True(token!.Allows("home"));
        Assert.True(token.Allows("HOME"));       // label comparison is case-insensitive
        Assert.False(token.Allows("work"));
    }

    [SkippableFact]
    public async Task Scope_survives_a_refresh()
    {
        Skip.IfNot(_ready, _why);

        // A scope that silently widened on renewal would be a privilege-escalation bug
        // nobody would notice until it mattered.
        var (clientId, verifier, code) = await SetupGrantAsync(_alice, "home");
        var first = await _oauth.RedeemCodeAsync(code, clientId, Redirect, verifier);
        var second = await _oauth.RefreshAsync(first!.RefreshToken, clientId);

        var token = await _oauth.ValidateAsync(second!.AccessToken);
        Assert.False(token!.Allows("work"));
        Assert.True(token.Allows("home"));
    }

    [SkippableFact]
    public async Task Listing_tokens_shows_only_the_users_own_live_ones()
    {
        Skip.IfNot(_ready, _why);

        var (c1, v1, code1) = await SetupGrantAsync(_alice);
        await _oauth.RedeemCodeAsync(code1, c1, Redirect, v1);

        var (c2, v2, code2) = await SetupGrantAsync(_bob);
        await _oauth.RedeemCodeAsync(code2, c2, Redirect, v2);

        Assert.Single(await _oauth.ListTokensAsync(_alice));
        Assert.Single(await _oauth.ListTokensAsync(_bob));
    }
}
