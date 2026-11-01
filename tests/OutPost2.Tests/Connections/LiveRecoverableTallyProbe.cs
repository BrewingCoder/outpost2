using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// What is sitting in Recoverable Items, and whose it is (OP2-228).
/// <para>
/// The first version of <c>clear_mail</c> used <c>DELETE</c>, which leaves mail in
/// Recoverable Items rather than Deleted Items. The user cleared 188 messages on that
/// version and was told, wrongly, that they were in Deleted Items. This answers the only
/// question that matters next: are they still there, and under which sender, so they can be
/// brought back.
/// </para>
/// <para>
/// It also tidies up after <see cref="LiveDeleteSemanticsProbe"/>, whose own cleanup was
/// throttled mid-run and left a probe draft behind. A probe that leaves debris in somebody's
/// mailbox is worse than no probe.
/// </para>
/// <para>
/// Every request here retries on 429. Graph throttles this mailbox on
/// <c>MailboxConcurrency</c>, and a probe that reports "0 messages" because it was throttled
/// reads exactly like a probe reporting that the mail is gone.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveRecoverableTallyProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Says_whose_mail_is_waiting_in_recoverable_items()
    {
        HttpClient http;

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

            Skip.IfNot(await vault.HealthyAsync(), "dev Vault unreachable");

            var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "credentials.json")));

            var store = new ConnectionStore(db, new CredentialProtector(vault));
            var ms = new MicrosoftConnectionService(Options.Create(new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            }), store, NullLogger<MicrosoftConnectionService>.Instance);

            await LiveEnvironment.RequireSchemaAsync(db);

            var found = await db.Connections.FirstOrDefaultAsync(c =>
                c.Provider == ProviderKind.Microsoft && c.Label == "home");

            Skip.If(found is null, "the 'home' connection is not stored");

            var token = await ms.GetAccessTokenAsync(found!);
            Skip.If(token is null, "no access token");

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _disposables.Add(http);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // ── tidy up after the other probe first ─────────────────────────────
        foreach (var where in new[] { "me/messages", "me/mailFolders/recoverableitemsdeletions/messages" })
        {
            var leftovers = await Ask(http, HttpMethod.Get,
                $"{where}?$filter=" + Uri.EscapeDataString("startsWith(subject,'op2-delete-probe')")
                + "&$select=id,subject&$top=20");

            if (leftovers.Status != 200) { output.WriteLine($"cleanup scan {where}: {leftovers.Status}"); continue; }

            using var d = JsonDocument.Parse(leftovers.Body);
            var items = d.RootElement.GetProperty("value");

            output.WriteLine($"cleanup: {items.GetArrayLength()} probe message(s) in {where}");

            foreach (var m in items.EnumerateArray())
            {
                var id = m.GetProperty("id").GetString()!;
                var gone = await Ask(http, HttpMethod.Post,
                    $"me/messages/{Uri.EscapeDataString(id)}/permanentDelete");

                output.WriteLine($"  removed {m.GetProperty("subject").GetString()} -> {gone.Status}");
            }
        }

        output.WriteLine("");

        // ── what the folders say they hold ──────────────────────────────────
        foreach (var wk in new[] { "deleteditems", "recoverableitemsdeletions" })
        {
            var r = await Ask(http, HttpMethod.Get,
                $"me/mailFolders/{wk}?$select=displayName,totalItemCount");

            if (r.Status != 200) { output.WriteLine($"{wk}: {r.Status}"); continue; }

            using var d = JsonDocument.Parse(r.Body);
            output.WriteLine($"{d.RootElement.GetProperty("displayName").GetString()}: "
                           + $"{d.RootElement.GetProperty("totalItemCount").GetInt32()} items");
        }

        output.WriteLine("");

        // ── who the recoverable mail is from ────────────────────────────────
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        var next = "me/mailFolders/recoverableitemsdeletions/messages?$top=100&$select=from";

        for (var page = 0; page < 40 && next is not null; page++)
        {
            var r = await Ask(http, HttpMethod.Get, next);
            if (r.Status != 200)
            {
                output.WriteLine($"page {page + 1}: {r.Status} {Brief(r)} — stopping");
                break;
            }

            using var d = JsonDocument.Parse(r.Body);

            foreach (var m in d.RootElement.GetProperty("value").EnumerateArray())
            {
                seen++;
                var who = m.TryGetProperty("from", out var f)
                          && f.TryGetProperty("emailAddress", out var e)
                          && e.TryGetProperty("address", out var a)
                    ? a.GetString() ?? "(none)" : "(none)";

                tally[who] = tally.GetValueOrDefault(who) + 1;
            }

            next = d.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }

        output.WriteLine($"Recoverable Items: walked {seen}, {tally.Count} distinct senders");
        output.WriteLine("");

        foreach (var g in tally.OrderByDescending(x => x.Value).Take(15))
            output.WriteLine($"  {g.Value,4}  {g.Key}");

        // Can the recovery be scoped to WHEN something was deleted? Recoverable Items holds
        // years of the user's own deliberate deletions, so "restore everything from
        // github.com" would drag back a thousand messages he meant to lose. A move updates
        // lastModifiedDateTime, so if that is filterable here the window is expressible.
        output.WriteLine("");

        foreach (var (what, clause) in new[]
                 {
                     ("lastModifiedDateTime in the last day",
                         $"lastModifiedDateTime ge {DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}"),
                     ("lastModifiedDateTime in the last hour",
                         $"lastModifiedDateTime ge {DateTimeOffset.UtcNow.AddHours(-1):yyyy-MM-ddTHH:mm:ssZ}"),
                     ("…and a sender together",
                         $"lastModifiedDateTime ge {DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}"
                         + " and contains(from/emailAddress/address,'github')"),
                 })
        {
            var r = await Ask(http, HttpMethod.Get,
                "me/mailFolders/recoverableitemsdeletions/messages?$filter="
                + Uri.EscapeDataString(clause) + "&$count=true&$top=1&$select=id");

            var n = -1;
            if (r.Status == 200)
            {
                using var d = JsonDocument.Parse(r.Body);
                n = d.RootElement.TryGetProperty("@odata.count", out var c) ? c.GetInt32() : -1;
            }

            output.WriteLine($"filter: {what,-38} -> {r.Status}  count {n}"
                           + (r.Status == 200 ? "" : $"  {Brief(r)}"));
        }

        // Domains too, since clear_mail matches a fragment of the address and that is how
        // the user would have asked for it.
        output.WriteLine("");
        output.WriteLine("by domain:");

        foreach (var g in tally
                     .GroupBy(x => x.Key.Contains('@') ? x.Key.Split('@')[^1] : x.Key,
                         StringComparer.OrdinalIgnoreCase)
                     .Select(g => (Domain: g.Key, Count: g.Sum(x => x.Value)))
                     .OrderByDescending(x => x.Count)
                     .Take(10))
            output.WriteLine($"  {g.Count,4}  {g.Domain}");
    }

    private sealed record Answer(int Status, string Body, string? Code, string? Message);

    /// <summary>
    /// A request that waits out a 429 instead of reporting it as an answer. This mailbox
    /// throttles on MailboxConcurrency, and "0 messages" because of a throttle reads exactly
    /// like "the mail is gone" — which is the question being asked.
    /// </summary>
    private static async Task<Answer> Ask(
        HttpClient http, HttpMethod method, string url, object? payload = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (payload is not null) req.Content = JsonContent.Create(payload);

            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 6)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (attempt + 1));
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait);
                continue;
            }

            string? code = null, message = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e))
                {
                    code = e.TryGetProperty("code", out var c) ? c.GetString() : null;
                    message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                }
            }
            catch { /* not JSON */ }

            return new Answer((int)resp.StatusCode, body, code, message);
        }
    }

    private static string Brief(Answer a) =>
        a.Code is not null ? $"{a.Code}: {a.Message}" : a.Body[..Math.Min(140, a.Body.Length)];
}
