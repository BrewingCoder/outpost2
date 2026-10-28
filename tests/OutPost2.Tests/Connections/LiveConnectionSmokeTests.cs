using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Reads the REAL dev database and proves a stored connection still works: Vault decrypts
/// the credential, MSAL refreshes silently from the cached token, and Graph accepts the
/// result.
/// <para>
/// Deliberately calls <c>/me</c> only. That is enough to prove the whole chain, and it
/// avoids reading the owner's actual mail just to satisfy a test.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveConnectionSmokeTests : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private (OutPostDbContext Db, ConnectionStore Store, MicrosoftConnectionService Ms)? Build(out string why)
    {
        why = "";
        try
        {
            var db = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
            _disposables.Add(db);

            var vault = new VaultTransitClient(Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);
            _disposables.Add(vault);

            if (!vault.HealthyAsync().GetAwaiter().GetResult()) { why = "dev Vault unreachable"; return null; }

            var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "credentials.json")));

            var entra = new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            };

            var store = new ConnectionStore(db, new CredentialProtector(vault));

            var ms = new MicrosoftConnectionService(
                Options.Create(entra), store, NullLogger<MicrosoftConnectionService>.Instance);

            return (db, store, ms);
        }
        catch (Exception ex) { why = ex.Message; return null; }
    }

    [SkippableFact]
    public async Task A_stored_microsoft_connection_still_authenticates_against_graph()
    {
        var built = Build(out var why);
        Skip.If(built is null, why);
        var (db, store, ms) = built!.Value;

        // Reachable-but-unmigrated Postgres is a skip, not a failure (see LiveEnvironment).
        await LiveEnvironment.RequireSchemaAsync(db);

        var connection = await db.Connections
            .FirstOrDefaultAsync(c => c.Provider == ProviderKind.Microsoft);

        Skip.If(connection is null, "no Microsoft connection in the dev database yet");

        // 1. Vault decrypts the stored MSAL cache.
        var sealedBlob = await store.GetCredentialAsync(connection!);
        Assert.False(string.IsNullOrWhiteSpace(sealedBlob));

        // 2. MSAL refreshes silently from it — no browser, no user present.
        var token = await ms.GetAccessTokenAsync(connection!);
        Assert.NotNull(token);

        // 3. Graph accepts the token.
        using var http = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.GetAsync("me?$select=userPrincipalName,displayName");
        Assert.True(resp.IsSuccessStatusCode,
            $"Graph refused the token: {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        using var me = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var upn = me.RootElement.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null;

        Assert.False(string.IsNullOrWhiteSpace(upn));
    }
}
