using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Diagnostic: prints exactly which identity a stored connection resolves to, and what we
/// recorded against it. Exists because the recorded tenant and the account the user
/// believed they picked disagreed.
/// </summary>
[Trait("Category", "Live")]
public sealed class WhoAmIProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Print_identity_behind_each_microsoft_connection()
    {
        OutPostDbContext db;
        ConnectionStore store;
        MicrosoftConnectionService ms;

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
            ms = new MicrosoftConnectionService(Options.Create(new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            }), store, NullLogger<MicrosoftConnectionService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // Reachable-but-unmigrated Postgres is a skip, not a failure (see LiveEnvironment).
        await LiveEnvironment.RequireSchemaAsync(db);

        var connections = await db.Connections
            .Where(c => c.Provider == ProviderKind.Microsoft).ToListAsync();

        Skip.If(connections.Count == 0, "no Microsoft connections stored");

        foreach (var c in connections)
        {
            output.WriteLine($"=== label '{c.Label}' ===");
            output.WriteLine($"  stored Instance (tenant) : {c.Instance}");
            output.WriteLine($"  stored HomeAccountId     : {c.HomeAccountId}");

            var token = await ms.GetAccessTokenAsync(c);
            if (token is null) { output.WriteLine("  token: UNAVAILABLE"); continue; }

            using var http = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.GetAsync("me?$select=userPrincipalName,displayName,mail,id");
            var body = await resp.Content.ReadAsStringAsync();
            output.WriteLine($"  graph /me HTTP {(int)resp.StatusCode}");
            output.WriteLine($"  {body}");

            // The tid claim in the access token is the authoritative tenant.
            var parts = token.Split('.');
            if (parts.Length > 1)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                try
                {
                    using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                    foreach (var k in (string[])["tid", "upn", "preferred_username", "unique_name", "idp"])
                        if (claims.RootElement.TryGetProperty(k, out var v))
                            output.WriteLine($"  token claim {k,-20}: {v}");
                }
                catch (Exception ex) { output.WriteLine($"  (token not a readable JWT: {ex.Message})"); }
            }
        }
    }
}
