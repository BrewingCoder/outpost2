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
/// Where a deleted message actually goes (OP2-228).
/// <para>
/// <c>clear_mail</c> told the user his 188 messages were in Deleted Items and recoverable.
/// He could not find them. That claim was mine and it has to be settled by experiment, not
/// by reading the documentation again — and the mailbox in question is a <b>personal</b>
/// account, where Graph's behaviour is not always the Exchange Online behaviour the docs
/// describe.
/// </para>
/// <para>
/// So: create a message of our own, delete it exactly the way <c>clear_mail</c> does, and
/// then go looking for it. Nothing of the user's is touched. <c>$filter</c> is used for the
/// looking rather than <c>$search</c>, because <c>$search</c> reads an index that lags a
/// move by minutes and would make a present message look missing — which is itself one of
/// the two hypotheses.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveDeleteSemanticsProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Says_where_a_deleted_message_goes_on_this_mailbox()
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

            // The mailbox the report came from: a personal account, which is the whole
            // point — the same call may behave differently on a tenant mailbox.
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

        // ── which folders even exist for deleted mail ───────────────────────
        foreach (var wk in new[]
                 { "deleteditems", "recoverableitemsdeletions", "recoverableitemspurges" })
        {
            var r = await Ask(http, HttpMethod.Get, $"me/mailFolders/{wk}?$select=id,displayName,totalItemCount");
            output.WriteLine($"folder {wk,-28} -> {r.Status}  {Brief(r)}");
        }

        output.WriteLine("");

        // ── the experiment ─────────────────────────────────────────────────
        // A draft of our own, so nothing of the user's is at risk. A draft is a real
        // message object and is deleted by the same route as any other.
        var tag = $"op2-delete-probe-{Guid.NewGuid():N}";

        var made = await Ask(http, HttpMethod.Post, "me/messages", new
        {
            subject = tag,
            body = new { contentType = "Text", content = "OP2-228 probe. Safe to delete." },
            toRecipients = new[]
            {
                new { emailAddress = new { address = "nobody@invalid.example" } },
            },
        });

        output.WriteLine($"1. created a draft                 -> {made.Status}");
        Skip.If(made.Status is not (200 or 201), $"could not create a probe message: {Brief(made)}");

        using var madeDoc = JsonDocument.Parse(made.Body);
        var id = madeDoc.RootElement.GetProperty("id").GetString()!;

        // Deleted the way clear_mail does it: a DELETE inside a $batch.
        var batch = await Ask(http, HttpMethod.Post, "$batch", new
        {
            requests = new[]
            {
                new { id = "1", method = "DELETE", url = $"/me/messages/{Uri.EscapeDataString(id)}" },
            },
        });

        var sub = -1;
        if (batch.Status == 200)
        {
            using var d = JsonDocument.Parse(batch.Body);
            sub = d.RootElement.GetProperty("responses")[0].GetProperty("status").GetInt32();
        }

        output.WriteLine($"2. DELETE inside a $batch          -> {batch.Status}, sub-request {sub}");
        output.WriteLine("");

        // ── where did it go ────────────────────────────────────────────────
        // $filter, not $search: the index lags a move by minutes, and "a search of Deleted
        // Items found nothing" is exactly the observation being explained.
        var quoted = Uri.EscapeDataString($"subject eq '{tag}'");

        foreach (var (what, where) in new[]
                 {
                     ("anywhere in the mailbox", "me/messages"),
                     ("Deleted Items", "me/mailFolders/deleteditems/messages"),
                     ("Recoverable Items / Deletions", "me/mailFolders/recoverableitemsdeletions/messages"),
                 })
        {
            var r = await Ask(http, HttpMethod.Get,
                $"{where}?$filter={quoted}&$select=id,subject,parentFolderId&$count=true");

            var n = -1;
            if (r.Status == 200)
            {
                using var d = JsonDocument.Parse(r.Body);
                n = d.RootElement.GetProperty("value").GetArrayLength();
            }

            output.WriteLine($"3. $filter in {what,-30} -> {r.Status}  found {n}"
                           + (r.Status == 200 ? "" : $"  {Brief(r)}"));
        }

        // And by id, which is the least ambiguous question there is: does the message still
        // exist at all, and if so which folder is it in?
        var byId = await Ask(http, HttpMethod.Get,
            $"me/messages/{Uri.EscapeDataString(id)}?$select=id,subject,parentFolderId");

        output.WriteLine("");
        output.WriteLine($"4. GET the message by its old id   -> {byId.Status}  {Brief(byId)}");

        if (byId.Status == 200)
        {
            using var d = JsonDocument.Parse(byId.Body);
            var parent = d.RootElement.GetProperty("parentFolderId").GetString();

            var folder = await Ask(http, HttpMethod.Get,
                $"me/mailFolders/{Uri.EscapeDataString(parent!)}?$select=displayName,wellKnownName");

            output.WriteLine($"   it is in: {Brief(folder)}");
        }

        output.WriteLine("");

        // ── and the comparison: what move_mail to 'trash' does instead ─────
        var second = await Ask(http, HttpMethod.Post, "me/messages", new
        {
            subject = tag + "-moved",
            body = new { contentType = "Text", content = "OP2-228 probe. Safe to delete." },
            toRecipients = new[]
            {
                new { emailAddress = new { address = "nobody@invalid.example" } },
            },
        });

        if (second.Status is 200 or 201)
        {
            using var d = JsonDocument.Parse(second.Body);
            var id2 = d.RootElement.GetProperty("id").GetString()!;

            var moved = await Ask(http, HttpMethod.Post,
                $"me/messages/{Uri.EscapeDataString(id2)}/move",
                new { destinationId = "deleteditems" });

            output.WriteLine($"5. POST /move to deleteditems      -> {moved.Status}");

            if (moved.Status is 200 or 201)
            {
                using var md = JsonDocument.Parse(moved.Body);
                var newId = md.RootElement.GetProperty("id").GetString()!;

                var check = await Ask(http, HttpMethod.Get,
                    $"me/mailFolders/deleteditems/messages?$filter="
                    + Uri.EscapeDataString($"subject eq '{tag}-moved'") + "&$select=id");

                var n = 0;
                if (check.Status == 200)
                {
                    using var cd = JsonDocument.Parse(check.Body);
                    n = cd.RootElement.GetProperty("value").GetArrayLength();
                }

                output.WriteLine($"   found in Deleted Items by $filter: {n}");

                // Tidy up: this one is ours and it really should go.
                await Ask(http, HttpMethod.Post,
                    $"me/messages/{Uri.EscapeDataString(newId)}/permanentDelete");
            }
        }

        output.WriteLine("");

        // ── 6. Is what was already deleted recoverable, and how ─────────────
        // The user's 188 messages are the urgent question. If they are sitting in
        // Recoverable Items then nothing is lost and the fix is a restore; if they are not,
        // this is a different conversation entirely.
        // Paged properly rather than one page of 200: the question is which sender the
        // user's 188 came from, and a single page cannot answer it.
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        var next = "me/mailFolders/recoverableitemsdeletions/messages"
                 + "?$top=100&$select=from,receivedDateTime";

        for (var page = 0; page < 30 && next is not null; page++)
        {
            var r = await Ask(http, HttpMethod.Get, next);
            if (r.Status != 200)
            {
                output.WriteLine($"6. listing Recoverable Items -> {r.Status}  {Brief(r)}");
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

            next = d.RootElement.TryGetProperty("@odata.nextLink", out var nl)
                ? nl.GetString() : null;
        }

        output.WriteLine($"6. Recoverable Items: {seen} message(s) walked, "
                       + $"{tally.Count} distinct senders");

        foreach (var g in tally.OrderByDescending(x => x.Value).Take(10))
            output.WriteLine($"   {g.Value,4} from {g.Key}");

        output.WriteLine("");

        // ── 7. Can something be brought BACK out of Recoverable Items ───────
        // Our own probe draft is still in there, so it is the right thing to try it on.
        var mine = await Ask(http, HttpMethod.Get,
            "me/mailFolders/recoverableitemsdeletions/messages?$filter="
            + Uri.EscapeDataString($"subject eq '{tag}'") + "&$select=id");

        if (mine.Status == 200)
        {
            using var d = JsonDocument.Parse(mine.Body);
            var found = d.RootElement.GetProperty("value");

            if (found.GetArrayLength() > 0)
            {
                var recoverId = found[0].GetProperty("id").GetString()!;

                var back = await Ask(http, HttpMethod.Post,
                    $"me/messages/{Uri.EscapeDataString(recoverId)}/move",
                    new { destinationId = "inbox" });

                output.WriteLine($"7. move it back to the Inbox       -> {back.Status}  "
                               + (back.Status is 200 or 201 ? "RECOVERABLE" : Brief(back)));

                // Put our probe message out of its misery wherever it ended up.
                if (back.Status is 200 or 201)
                {
                    using var bd = JsonDocument.Parse(back.Body);
                    var finalId = bd.RootElement.GetProperty("id").GetString()!;
                    await Ask(http, HttpMethod.Post,
                        $"me/messages/{Uri.EscapeDataString(finalId)}/permanentDelete");
                    output.WriteLine("   probe message removed for good");
                }
            }
        }

        output.WriteLine("");

        // ── 8. Can a batch carry moves, which is what the fix needs ─────────
        var third = await Ask(http, HttpMethod.Post, "me/messages", new
        {
            subject = tag + "-batchmove",
            body = new { contentType = "Text", content = "OP2-228 probe." },
            toRecipients = new[] { new { emailAddress = new { address = "nobody@invalid.example" } } },
        });

        if (third.Status is 200 or 201)
        {
            using var d = JsonDocument.Parse(third.Body);
            var id3 = d.RootElement.GetProperty("id").GetString()!;

            var bm = await Ask(http, HttpMethod.Post, "$batch", new
            {
                requests = new[]
                {
                    new
                    {
                        id = "1",
                        method = "POST",
                        url = $"/me/messages/{Uri.EscapeDataString(id3)}/move",
                        body = new { destinationId = "deleteditems" },
                        headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    },
                },
            });

            var status = -1;
            string? newId = null;
            if (bm.Status == 200)
            {
                using var bd = JsonDocument.Parse(bm.Body);
                var r0 = bd.RootElement.GetProperty("responses")[0];
                status = r0.GetProperty("status").GetInt32();
                if (r0.TryGetProperty("body", out var b) && b.TryGetProperty("id", out var ni))
                    newId = ni.GetString();
            }

            output.WriteLine($"8. a /move inside a $batch         -> {bm.Status}, sub-request {status}");
            output.WriteLine($"   new id returned in the batch body: {(newId is null ? "no" : "yes")}");

            if (newId is not null)
                await Ask(http, HttpMethod.Post,
                    $"me/messages/{Uri.EscapeDataString(newId)}/permanentDelete");
        }

        // Nothing is asserted. Which of the two explanations is true is the service's
        // answer to give, and the point of this probe is to put it on the record.
    }

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

    private static string Brief(Answer a)
    {
        if (a.Code is not null) return $"{a.Code}: {Flat(a.Message ?? "")}";
        return a.Status is 200 or 201 or 204 ? Flat(a.Body) : Flat(a.Body);
    }

    private static string Flat(string s)
    {
        var flat = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length > 200 ? flat[..200] + "…" : flat;
    }
}
