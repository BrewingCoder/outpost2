using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// What <c>$expand=childFolders</c> really hands back, and what the mailbox really holds
/// (OP2-213).
/// <para>
/// OP2-213 reported a folder that exists in Outlook, and that mail had already been filed
/// into earlier in the same session, being refused as nonexistent. Two separate causes turned
/// out to be in play, and this probe measures both because each one alone looks like the
/// whole story:
/// </para>
/// <list type="number">
/// <item>the expanded <c>childFolders</c> collection stops at <b>ten</b>, with no nextLink
/// offered and a nested <c>$top</c> that Graph accepts but does not honour — so the eleventh
/// child of a folder is invisible at any depth;</item>
/// <item>the expand only reaches one level down, so a mailbox with 84 folders under one of
/// them had 87 of its 112 below the horizon.</item>
/// </list>
/// <para>
/// It runs over <b>every</b> mailbox, because the first one looked at here has a six-child
/// Inbox and shows no sign of the ten cap at all. One mailbox is not evidence.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveFolderTreeProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Says_where_the_folder_listing_stops_on_every_mailbox()
    {
        MicrosoftConnectionService ms;
        IHttpClientFactory factory;
        MailService mail;
        List<Connection> mailboxes;

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
            ms = new MicrosoftConnectionService(Options.Create(new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            }), store, NullLogger<MicrosoftConnectionService>.Instance);

            await LiveEnvironment.RequireSchemaAsync(db);

            mailboxes = await db.Connections
                .Where(c => c.Provider == ProviderKind.Microsoft)
                .ToListAsync();

            Skip.If(mailboxes.Count == 0, "no Microsoft connection stored");

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            factory = sp.GetRequiredService<IHttpClientFactory>();
            mail = new MailService(ms, factory, NullLogger<MailService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        var capped = new List<string>();
        var incomplete = new List<string>();
        var asked = 0;

        foreach (var c in mailboxes)
        {
            output.WriteLine($"── {c.Label} " + new string('─', 50));

            HttpClient http;
            try
            {
                var token = await ms.GetAccessTokenAsync(c);
                if (token is null) { output.WriteLine("   no access token"); continue; }

                http = factory.CreateClient();
                http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                _disposables.Add(http);
            }
            catch (Exception ex) { output.WriteLine($"   {ex.Message}"); continue; }

            asked++;

            // 1. The shape the service used from OP2-188 until OP2-213.
            using var doc = await Get(http, "me/mailFolders?$top=100&$expand=childFolders");
            var top = doc.RootElement.GetProperty("value");

            output.WriteLine($"   $top=100&$expand=childFolders -> {top.GetArrayLength()} top-level, "
                           + $"outer nextLink {(doc.RootElement.TryGetProperty("@odata.nextLink", out _) ? "yes" : "no")}");

            // 2. What the expand gave against what each folder says it has. childFolderCount
            //    comes from the folder itself, so the two disagreeing is the bug in one line.
            foreach (var f in top.EnumerateArray())
            {
                var name = f.GetProperty("displayName").GetString() ?? "(unnamed)";
                var says = f.GetProperty("childFolderCount").GetInt32();
                var gave = f.TryGetProperty("childFolders", out var kids) ? kids.GetArrayLength() : 0;
                if (says == gave) continue;

                var link = f.TryGetProperty("childFolders@odata.nextLink", out _)
                    ? ", and a nextLink for the rest" : ", with no nextLink";

                output.WriteLine($"   SHORT  {name}: claims {says}, expand gave {gave}{link}");

                // Ten exactly is the cap, not a coincidence — and the folder that falls off
                // is the eleventh, which is how OP2-213 was reported.
                if (gave == 10) capped.Add($"{c.Label}/{name} ({says})");
            }

            // 3. Whether the cap can be lifted where it is applied. Graph answers 200 to a
            //    nested $top, which is worse than refusing it: it looks like it worked.
            var nested = await Ask(http, "me/mailFolders?$top=100&$expand=childFolders($top=200)");
            output.WriteLine($"   $expand=childFolders($top=200) -> {nested.Status}"
                           + (nested.Error is null ? "" : $"  {nested.Error}"));

            if (nested.Status == 200)
            {
                using var nd = await Get(http, "me/mailFolders?$top=100&$expand=childFolders($top=200)");
                var still = nd.RootElement.GetProperty("value").EnumerateArray()
                    .Where(f => f.GetProperty("childFolderCount").GetInt32() >
                                (f.TryGetProperty("childFolders", out var k) ? k.GetArrayLength() : 0))
                    .Select(f => f.GetProperty("displayName").GetString())
                    .ToList();

                output.WriteLine($"   …and with it, still short: "
                               + (still.Count == 0 ? "nothing" : string.Join(", ", still)));
            }

            // 4. What the service reports now. OP2-213 was filed against a personal mailbox,
            //    so a walk proven on a tidy one has not been proven.
            IReadOnlyList<MailFolder> listed;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { listed = await mail.ListFoldersAsync(c); }
            catch (Exception ex) { output.WriteLine($"   ListFoldersAsync: {ex.Message}"); continue; }
            clock.Stop();

            var depth = listed.Count == 0 ? 0 : listed.Max(f => f.Path.Count(ch => ch == '/') + 1);

            // The number that decides whether this design is usable: move_mail calls it
            // before every non-standard destination, and the user is driving.
            output.WriteLine($"   ListFoldersAsync -> {listed.Count} folders, {depth} levels deep, "
                           + $"{clock.ElapsedMilliseconds} ms");

            foreach (var f in listed.Where(x => x.ChildCount > 0))
            {
                var below = listed.Count(x =>
                    x.Path.StartsWith(f.Path + "/", StringComparison.OrdinalIgnoreCase));

                if (below < f.ChildCount)
                {
                    output.WriteLine($"   MISSING under {f.Path}: claims {f.ChildCount}, listed {below}");
                    incomplete.Add($"{c.Label} {f.Path} ({f.ChildCount} vs {below})");
                }
            }

            // Two folders with one path cannot be told apart by anything downstream, so the
            // surface has to refuse rather than pick — which only works if it sees both.
            foreach (var dup in listed.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                output.WriteLine($"   duplicate path: {dup.Key} x{dup.Count()}");

            // And the reason a bare name is no longer allowed to resolve on its own. This
            // is the number that justifies refusing an ambiguous name instead of picking,
            // so it is measured rather than assumed — a deeper tree is only more likely to
            // reuse a name, but "likely" is not evidence.
            var shared = listed.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            output.WriteLine($"   names shared by more than one folder: {shared.Count}");
            foreach (var g in shared.OrderByDescending(g => g.Count()).Take(6))
                output.WriteLine($"     \"{g.Key}\" x{g.Count()}: "
                               + string.Join(" | ", g.Select(f => $"{f.Path} ({f.MessageCount})")));

            // The folder OP2-213 was about, by name, wherever it turned out to live.
            foreach (var hit in listed.Where(x =>
                         x.Name.Contains("subscri", StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"   \"Subscri*\" -> {hit.Path} ({hit.MessageCount} messages)");
        }

        Skip.If(asked == 0, "no mailbox could be reached");

        output.WriteLine("");
        output.WriteLine(capped.Count > 0
            ? $"The ten cap is real and bites here: {string.Join(", ", capped)}"
            : "No folder on these mailboxes has more than ten children, so the ten cap "
              + "cannot be seen from here — which is exactly why it was missed.");

        // The assertion: every folder the mailbox says has children must have them listed. A
        // folder that is not in the list cannot be filed into, and that is OP2-213.
        Assert.True(incomplete.Count == 0,
            $"{incomplete.Count} folder(s) have children that ListFoldersAsync never returned: " +
            string.Join(", ", incomplete));
    }

    private static async Task<JsonDocument> Get(HttpClient http, string url)
    {
        using var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<(int Status, int Count, string? Next, string? Error)> Ask(
        HttpClient http, string url)
    {
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return ((int)resp.StatusCode, 0, null, Message(body));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return ((int)resp.StatusCode,
            root.TryGetProperty("value", out var v) ? v.GetArrayLength() : 0,
            root.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null,
            null);
    }

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e)
                   && e.TryGetProperty("message", out var m)
                ? m.GetString() ?? body
                : body;
        }
        catch { return body.Length > 300 ? body[..300] : body; }
    }
}
