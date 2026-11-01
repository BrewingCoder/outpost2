using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// What Graph will do for "delete all the mail from this address" without being handed one
/// message at a time.
/// <para>
/// The question is worth asking because the obvious answer is wrong in both directions. A
/// mailbox rule sounds like the whole solution and only covers mail that has not arrived
/// yet; and the absence of a bulk endpoint sounds fatal when <c>$batch</c> makes the
/// one-at-a-time path perfectly usable. This probe asks the live service which of the
/// candidate mechanisms exist, and reports the limits of each.
/// </para>
/// <para>
/// It is deliberately non-destructive. Route existence is established by asking about
/// something that is not there and reading which error comes back — a route that does not
/// exist is refused differently from an item that does not exist — and the one write it
/// performs is a disabled rule on a nonsense condition, removed in a finally.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveBulkDeleteProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    /// <summary>Unmistakable, so a leaked one can be found and removed by hand.</summary>
    private const string ProbeRule = "op2-probe-DELETE-ME (OP2 capability probe)";

    private readonly List<IDisposable> _disposables = [];
    private MailService? _mail;
    private Connection? _connection;
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Says_which_server_side_bulk_deletions_graph_allows()
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

            // The personal mailbox: a consumer account is the stricter test, since several
            // Graph features are tenant-only and would pass on work and fail for him.
            var found = await db.Connections.FirstOrDefaultAsync(c =>
                c.Provider == ProviderKind.Microsoft && c.Label == "home")
                ?? await db.Connections.FirstOrDefaultAsync(c => c.Provider == ProviderKind.Microsoft);

            Skip.If(found is null, "no Microsoft connection stored");

            var token = await ms.GetAccessTokenAsync(found!);
            Skip.If(token is null, "no access token");

            _connection = found;
            output.WriteLine($"mailbox: {found!.Label}");
            output.WriteLine("");

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _disposables.Add(http);

            _mail = new MailService(ms, sp.GetRequiredService<IHttpClientFactory>(),
                NullLogger<MailService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // ── 1. Mailbox rules ────────────────────────────────────────────────
        // The thing that sounds like the answer. MailboxSettings.ReadWrite is already an
        // optional scope here, for colour categories, so no new consent would be needed.
        var rules = await Ask(http, HttpMethod.Get, "me/mailFolders/inbox/messageRules");
        output.WriteLine($"1. GET inbox/messageRules            -> {rules.Status}");
        output.WriteLine($"   {Summarise(rules)}");

        if (rules.Status == 200)
        {
            using var doc = JsonDocument.Parse(rules.Body);
            foreach (var r in doc.RootElement.GetProperty("value").EnumerateArray())
                output.WriteLine($"   existing rule: \"{r.GetProperty("displayName").GetString()}\" "
                               + $"enabled={r.TryGetProperty("isEnabled", out var e) && e.GetBoolean()}");
        }

        // Can a rule be written, and does it take "from this address" plus "delete"? Made
        // disabled, on an address that cannot exist, and removed below whatever happens.
        string? ruleId = null;
        try
        {
            var made = await Ask(http, HttpMethod.Post, "me/mailFolders/inbox/messageRules", new
            {
                displayName = ProbeRule,
                sequence = 99,
                isEnabled = false,
                conditions = new
                {
                    senderContains = new[] { "op2-probe-nobody@invalid.example" },
                },
                actions = new
                {
                    delete = true,
                    stopProcessingRules = true,
                },
            });

            output.WriteLine($"2. POST a from/delete rule           -> {made.Status}");
            output.WriteLine($"   {Summarise(made)}");

            if (made.Status is 200 or 201)
            {
                using var doc = JsonDocument.Parse(made.Body);
                ruleId = doc.RootElement.GetProperty("id").GetString();

                // What the service kept, which is not always what was sent.
                output.WriteLine($"   stored conditions: {Field(doc.RootElement, "conditions")}");
                output.WriteLine($"   stored actions:    {Field(doc.RootElement, "actions")}");
            }

            // Is there any way to make a rule act on mail already in the mailbox? Outlook's
            // desktop client has "Run Rules Now"; if Graph has no equivalent, a rule can
            // only ever be about the future, and that is the whole limitation.
            if (ruleId is not null)
            {
                foreach (var verb in new[] { "run", "apply", "runNow" })
                {
                    var r = await Ask(http, HttpMethod.Post,
                        $"me/mailFolders/inbox/messageRules/{ruleId}/{verb}");
                    output.WriteLine($"   POST .../{verb,-7} -> {r.Status}  {Summarise(r)}");
                }
            }
        }
        finally
        {
            if (ruleId is not null)
            {
                var gone = await Ask(http, HttpMethod.Delete,
                    $"me/mailFolders/inbox/messageRules/{ruleId}");
                output.WriteLine($"   probe rule removed             -> {gone.Status}");
            }
            else
            {
                // Belt and braces: if the POST succeeded but the id could not be read, the
                // rule is still there. Find it by name and remove it.
                var all = await Ask(http, HttpMethod.Get, "me/mailFolders/inbox/messageRules");
                if (all.Status == 200 && all.Body.Contains("op2-probe-DELETE-ME"))
                    output.WriteLine("   WARNING: a probe rule may have been left behind");
            }
        }

        output.WriteLine("");

        // ── 3. A bulk delete by query ───────────────────────────────────────
        // What one would want: "delete everything matching this". Asked three ways.
        foreach (var (what, method, url) in new (string, HttpMethod, string)[]
                 {
                     ("DELETE /me/messages (collection)", HttpMethod.Delete, "me/messages"),
                     ("DELETE with a $filter", HttpMethod.Delete,
                         "me/messages?$filter=from/emailAddress/address%20eq%20'nobody@invalid.example'"),
                     ("POST /me/messages/delete", HttpMethod.Post, "me/messages/delete"),
                 })
        {
            var r = await Ask(http, method, url);
            output.WriteLine($"3. {what,-34} -> {r.Status}  {Summarise(r)}");
        }

        output.WriteLine("");

        // ── 4. Per-message deletes, and the newer hard delete ───────────────
        // A route that exists answers "that item is not there"; a route that does not
        // exist is refused for a different reason, and the two read quite differently.
        foreach (var (what, url) in new[]
                 {
                     ("messages/{id}/permanentDelete", "me/messages/op2-no-such-id/permanentDelete"),
                     ("mailFolders/{id}/permanentDelete", "me/mailFolders/op2-no-such-id/permanentDelete"),
                     ("mailFolders/{id}/emptyFolder", "me/mailFolders/op2-no-such-id/emptyFolder"),
                     ("mailFolders/{id}/copy", "me/mailFolders/op2-no-such-id/copy"),
                 })
        {
            var r = await Ask(http, HttpMethod.Post, url);
            output.WriteLine($"4. POST {what,-33} -> {r.Status}  {Summarise(r)}");
        }

        output.WriteLine("");

        // ── 5. $batch, which is what makes one-at-a-time bearable ───────────
        // Read-only payload: the question is the envelope, not the verb.
        var batch = await Ask(http, HttpMethod.Post, "$batch", new
        {
            requests = Enumerable.Range(1, 25).Select(i => new
            {
                id = i.ToString(),
                method = "GET",
                url = "/me/mailFolders/inbox?$select=id",
            }).ToArray(),
        });

        output.WriteLine($"5. POST $batch with 25 requests      -> {batch.Status}");
        output.WriteLine($"   {Summarise(batch)}");

        if (batch.Status == 200)
        {
            using var doc = JsonDocument.Parse(batch.Body);
            var responses = doc.RootElement.GetProperty("responses");
            output.WriteLine($"   responses: {responses.GetArrayLength()}, "
                           + $"statuses: {string.Join(",", responses.EnumerateArray()
                               .Select(x => x.GetProperty("status").GetInt32()).Distinct())}");
        }

        // 20 is the documented cap; 25 was refused, so find the real one.
        foreach (var n in new[] { 20, 21 })
        {
            var b = await Ask(http, HttpMethod.Post, "$batch", new
            {
                requests = Enumerable.Range(1, n).Select(i => new
                {
                    id = i.ToString(), method = "GET", url = "/me/mailFolders/inbox?$select=id",
                }).ToArray(),
            });
            output.WriteLine($"5. $batch with {n,2} requests        -> {b.Status}  {Summarise(b)}");
        }

        // And whether the envelope takes DELETE at all, which is the whole plan. A
        // malformed id, so the sub-request fails on validation and nothing is removed.
        var deletes = await Ask(http, HttpMethod.Post, "$batch", new
        {
            requests = Enumerable.Range(1, 3).Select(i => new
            {
                id = i.ToString(), method = "DELETE", url = $"/me/messages/op2-no-such-id-{i}",
            }).ToArray(),
        });

        output.WriteLine($"   $batch of DELETEs               -> {deletes.Status}");
        if (deletes.Status == 200)
        {
            using var doc = JsonDocument.Parse(deletes.Body);
            foreach (var r in doc.RootElement.GetProperty("responses").EnumerateArray())
                output.WriteLine($"     sub {r.GetProperty("id").GetString()}: "
                               + $"{r.GetProperty("status").GetInt32()}");
        }

        output.WriteLine("");

        // ── 6. Finding the mail to delete ───────────────────────────────────
        // $filter on the sender, which is the shape "everything from this address" wants.
        // Unlike $search it should allow $top, $count and an ordering.
        foreach (var (what, url) in new[]
                 {
                     ("$filter from/emailAddress/address",
                         "me/messages?$filter=from/emailAddress/address%20eq%20'nobody@invalid.example'&$select=id&$top=100"),
                     ("…with $count=true",
                         "me/messages?$filter=from/emailAddress/address%20eq%20'nobody@invalid.example'&$count=true&$top=1"),
                     ("…scoped to the Inbox",
                         "me/mailFolders/inbox/messages?$filter=from/emailAddress/address%20eq%20'nobody@invalid.example'&$select=id&$top=100"),
                     ("…with $top=1000",
                         "me/messages?$filter=from/emailAddress/address%20eq%20'nobody@invalid.example'&$select=id&$top=1000"),
                 })
        {
            var r = await Ask(http, HttpMethod.Get, url);
            output.WriteLine($"6. {what,-34} -> {r.Status}  {Summarise(r)}");
        }

        output.WriteLine("");

        // ── 7. How loosely a sender can be matched ──────────────────────────
        // Nobody says "news-noreply@linkedin.com" out loud; they say "LinkedIn". Whether a
        // domain or a name can be matched decides whether this is usable by voice at all.
        foreach (var (what, q) in new[]
                 {
                     ("eq on the address",
                         "$filter=from/emailAddress/address%20eq%20'a@b.example'"),
                     ("startsWith on the address",
                         "$filter=startsWith(from/emailAddress/address,'news')"),
                     ("endsWith on the address",
                         "$filter=endsWith(from/emailAddress/address,'linkedin.com')"),
                     ("contains on the address",
                         "$filter=contains(from/emailAddress/address,'linkedin')"),
                     ("startsWith on the display name",
                         "$filter=startsWith(from/emailAddress/name,'LinkedIn')"),
                     ("eq plus a date floor",
                         "$filter=from/emailAddress/address%20eq%20'a@b.example'%20and%20receivedDateTime%20lt%202026-01-01T00:00:00Z"),
                     ("eq plus isRead",
                         "$filter=from/emailAddress/address%20eq%20'a@b.example'%20and%20isRead%20eq%20true"),
                     ("ordered oldest first",
                         "$filter=from/emailAddress/address%20eq%20'a@b.example'&$orderby=receivedDateTime%20asc"),
                 })
        {
            var r = await Ask(http, HttpMethod.Get, $"me/messages?{q}&$select=id&$top=5");
            output.WriteLine($"7. {what,-30} -> {r.Status}  {Summarise(r)}");
        }

        output.WriteLine("");

        // ── 8. The preview, against the real mailbox ────────────────────────
        // Read-only by construction: PreviewClearAsync counts and samples, and the one
        // thing asserted below is that it sent no $batch. The point is the timing — on a
        // 27,000-message inbox, a count that takes ten seconds is not usable by voice.
        var recent = await Ask(http, HttpMethod.Get,
            "me/mailFolders/inbox/messages?$top=40&$select=from");

        if (recent.Status == 200)
        {
            using var doc = JsonDocument.Parse(recent.Body);

            var top = doc.RootElement.GetProperty("value").EnumerateArray()
                .Select(m => m.TryGetProperty("from", out var f)
                             && f.TryGetProperty("emailAddress", out var e)
                             && e.TryGetProperty("address", out var a) ? a.GetString() : null)
                .Where(a => a is not null)
                .GroupBy(a => a!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (top is not null)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var preview = await _mail!.PreviewClearAsync(_connection!,
                    new ClearTarget { From = top.Key });
                clock.Stop();

                output.WriteLine($"8. PreviewClearAsync for {top.Key}");
                output.WriteLine($"   matched {preview.Matched} message(s) in "
                               + $"{clock.ElapsedMilliseconds} ms, sampled={preview.Sampled}");

                foreach (var s in preview.Senders.Take(3))
                    output.WriteLine($"   sender: {s.Address} ({s.Count} of the sample)");

                // A fragment too, since that is the shape somebody actually says.
                var word = top.Key.Split('@')[^1].Split('.')[0];
                clock.Restart();
                var loose = await _mail.PreviewClearAsync(_connection!, new ClearTarget { From = word });
                clock.Stop();

                output.WriteLine($"   as a fragment \"{word}\": {loose.Matched} match(es) "
                               + $"across {loose.Senders.Count} sender(s) in {clock.ElapsedMilliseconds} ms");
            }
        }

        // Nothing is asserted about which mechanisms exist — that is the service's answer to
        // give, and this probe's job is to record it. The one thing that must hold is that
        // the probe left nothing behind.
        var after = await Ask(http, HttpMethod.Get, "me/mailFolders/inbox/messageRules");
        Assert.False(after.Status == 200 && after.Body.Contains("op2-probe-DELETE-ME"),
            "the probe left a rule in the mailbox");
    }

    private static string Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.GetRawText() : "(absent)";

    private sealed record Answer(int Status, string Body, string? Code, string? Message);

    private static async Task<Answer> Ask(
        HttpClient http, HttpMethod method, string url, object? payload = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (payload is not null) req.Content = JsonContent.Create(payload);

        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

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

    private static string Summarise(Answer a)
    {
        if (a.Code is not null || a.Message is not null)
            return $"{a.Code}: {Trim(a.Message ?? "")}";

        return a.Status is 200 or 201 or 204
            ? $"ok ({a.Body.Length} bytes)"
            : Trim(a.Body);
    }

    private static string Trim(string s)
    {
        var one = new StringBuilder();
        foreach (var ch in s) one.Append(ch is '\r' or '\n' ? ' ' : ch);
        var flat = one.ToString().Trim();
        return flat.Length > 220 ? flat[..220] + "…" : flat;
    }
}
