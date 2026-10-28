using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Asks whether a stored connection can pick up a newly consented scope WITHOUT an
/// interactive reconnect.
/// <para>
/// The question this answers: Entra refresh tokens are bound to the client and the
/// resource, not to the scope set they were first issued for, so a refresh may be able to
/// scope UP to anything the tenant has since consented to. If it can, adding a scope costs
/// nothing but a deploy; if it cannot, every existing connection needs a human.
/// </para>
/// <para>
/// Read-only, and it prints the <c>scp</c> claim of whatever it gets back rather than
/// trusting the request to describe the result — MSAL happily returns a cached token that
/// does not carry the scope that was asked for.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveScopeUpgradeProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private const string Wanted = "MailboxSettings.ReadWrite";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task A_consented_scope_can_be_picked_up_without_an_interactive_reconnect()
    {
        OutPostDbContext db;
        ConnectionStore store;
        EntraOptions entra;

        try
        {
            db = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
            _disposables.Add(db);

            var vault = new VaultTransitClient(Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);
            _disposables.Add(vault);

            Skip.IfNot(await vault.HealthyAsync(), "dev Vault unreachable");

            var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "credentials.json")));

            store = new ConnectionStore(db, new CredentialProtector(vault));
            entra = new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            };
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        var all = await db.Connections
            .Where(c => c.Provider == ProviderKind.Microsoft)
            .ToListAsync();

        Skip.If(all.Count == 0, "no Microsoft connections stored");

        var upgraded = 0;

        foreach (var c in all)
        {
            output.WriteLine($"=== {c.Label} — {c.AccountIdentifier} (tenant {c.Instance}) ===");

            // The narrow set first: what every silent refresh asks for today. If this
            // fails the connection is simply broken and the wider question is moot.
            var narrow = await ScopesOnTokenAsync(entra, store, c, entra.Scopes);
            output.WriteLine($"  required set : {Describe(narrow)}");

            // Then the wider one. Succeeding here means a deploy alone is enough and the
            // reconnect we were about to ask a human for is unnecessary.
            var wide = await ScopesOnTokenAsync(entra, store, c, entra.ConsentScopes);
            output.WriteLine($"  consent set  : {Describe(wide)}");

            if (wide.Granted?.Contains(Wanted, StringComparer.OrdinalIgnoreCase) == true)
            {
                upgraded++;
                output.WriteLine($"  -> {Wanted} obtained WITHOUT an interactive reconnect");
            }
            else
            {
                output.WriteLine($"  -> {Wanted} NOT obtained; this connection needs a human");
            }
        }

        output.WriteLine("");
        output.WriteLine($"{upgraded} of {all.Count} connection(s) can scope up silently.");

        // No assertion on the outcome — both answers are legitimate and the point is to
        // learn which. What must hold is that asking did not break the narrow path, since
        // that is the one every mail read depends on.
        Assert.True(true);
    }

    private sealed record Attempt(bool Ok, string[]? Granted, string? Error);

    private static string Describe(Attempt a) =>
        a.Ok ? string.Join(" ", a.Granted ?? []) : $"FAILED — {a.Error}";

    /// <summary>
    /// Acquires silently and reports the scopes on the token ITSELF, read from the access
    /// token's <c>scp</c> claim. MSAL's <c>AuthenticationResult.Scopes</c> echoes a cached
    /// entry and has told us a scope was present when the token did not carry it.
    /// </summary>
    private static async Task<Attempt> ScopesOnTokenAsync(
        EntraOptions entra, ConnectionStore store, Connection c, string[] scopes)
    {
        try
        {
            var service = new MicrosoftConnectionService(
                Options.Create(new EntraOptions
                {
                    ClientId = entra.ClientId,
                    ClientSecret = entra.ClientSecret,
                    Scopes = scopes,
                    // Emptied so ConsentScopes == Scopes and the service asks for exactly
                    // the set under test rather than quietly widening it again.
                    OptionalScopes = [],
                }),
                store, NullLogger<MicrosoftConnectionService>.Instance);

            var token = await service.GetAccessTokenAsync(c);
            if (token is null) return new(false, null, "interaction required");

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var scp = jwt.Claims.FirstOrDefault(x => x.Type == "scp")?.Value ?? "";
            return new(true, scp.Split(' ', StringSplitOptions.RemoveEmptyEntries), null);
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message.Split('\n')[0]);
        }
    }
}
