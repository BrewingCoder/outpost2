using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
/// Gmail against a real Gmail account (OP2-284). Everything in OP2-279 to OP2-282 was
/// written against Google's documentation and recorded shapes; this is what turns "should
/// work" into "does".
/// <para>
/// Skips, rather than fails, unless the environment is there: the dev Vault and database,
/// a Google OAuth client in <c>~/.claude/credentials.json</c>
/// (<c>outpost2_google_client_id</c>, <c>outpost2_google_client_secret</c>), and a Gmail
/// connection made through the app. <c>OP2_PROBE_LABEL</c> picks one by name.
/// </para>
/// <para>
/// Three parts, in increasing order of consequence:
/// </para>
/// <list type="number">
/// <item><b>Reading</b>, always: list, read, search across two pages, labels, an
/// attachment's bytes.</item>
/// <item><b>Organising, undone</b>, always: star, read state, a temporary label used as a
/// category and as a folder, out of the Inbox and back — each restored, and the label
/// deleted, before the test ends.</item>
/// <item><b>Sending</b>, only with <c>OP2_PROBE_SEND=1</c>: a message to the account
/// itself, a reply that must land in the same thread, Trash and back — then both moved to
/// Trash. It never mails anybody else, and is opt-in because a test that sends every time
/// it runs is a test that gets disabled.</item>
/// </list>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveGmailProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private sealed record World(OutPostDbContext Db, GmailService Gmail, IGoogleTokenAccessor Tokens, Connection Connection);

    private async Task<World> WorldAsync()
    {
        OutPostDbContext db;
        GmailService gmail;
        GoogleConnectionService google;

        try
        {
            db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
            _disposables.Add(db);

            var vault = new VaultTransitClient(Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);
            _disposables.Add(vault);
            Skip.IfNot(await vault.HealthyAsync(), "dev Vault unreachable");

            var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "credentials.json")));
            string Cred(string name) => creds.RootElement.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s
                ? s : throw new InvalidOperationException($"{name} is not in ~/.claude/credentials.json");

            var store = new SerialisedConnectionStore(new ConnectionStore(db, new CredentialProtector(vault)));
            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);
            var cache = new MemoryCache(new MemoryCacheOptions());
            _disposables.Add(cache);

            google = new GoogleConnectionService(Options.Create(new GoogleOptions
            {
                ClientId = Cred("outpost2_google_client_id"),
                ClientSecret = Cred("outpost2_google_client_secret"),
                RedirectUri = "https://localhost/signin-google",   // unused: refresh only
            }), store, sp.GetRequiredService<IHttpClientFactory>(), cache, NullLogger<GoogleConnectionService>.Instance);

            gmail = new GmailService(google, sp.GetRequiredService<IHttpClientFactory>(), NullLogger<GmailService>.Instance);
        }
        catch (Exception ex) when (ex is not Xunit.SkipException) { Skip.If(true, ex.Message); throw; }

        await LiveEnvironment.RequireSchemaAsync(db);

        var only = Environment.GetEnvironmentVariable("OP2_PROBE_LABEL");
        var c = await db.Connections
            .Where(x => x.Provider == ProviderKind.Google)
            .Where(x => only == null || x.Label == only)
            .FirstOrDefaultAsync();
        Skip.If(c is null, only is null ? "no Gmail connection stored" : $"no Gmail connection labelled '{only}'");

        output.WriteLine($"Gmail connection '{c!.Label}' — {c.AccountIdentifier}, state {c.State}");
        foreach (var cap in c.Capabilities)
            output.WriteLine($"  {cap.Name}: granted={cap.Granted} enabled={cap.Enabled}");

        return new World(db, gmail, google, c);
    }

    // ── 1. reading ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Reads_lists_searches_labels_and_attachments_from_a_real_account()
    {
        var w = await WorldAsync();
        var (gmail, c) = (w.Gmail, w.Connection);

        // The refresh path (OP2-279): a cold token from the stored refresh token.
        var token = await w.Tokens.GetAccessTokenAsync(c);
        Assert.False(string.IsNullOrWhiteSpace(token), "no access token — the stored sign-in did not refresh");
        output.WriteLine("token: refreshed from the stored sign-in");

        var inbox = await gmail.ListAsync(c, top: 10);
        output.WriteLine($"inbox: {inbox.Count}");
        foreach (var m in inbox)
            output.WriteLine($"  [{(m.IsRead ? ' ' : '*')}]{(m.HasAttachments ? " @" : "  ")} {m.ReceivedAt:yyyy-MM-dd HH:mm}  "
                           + $"{Trim(m.From, 34),-34}  {Trim(m.Subject, 50)}");
        Skip.If(inbox.Count == 0, "the inbox is empty — nothing to read");
        Assert.All(inbox, m => Assert.Equal(c.Label, m.ConnectionLabel));
        Assert.All(inbox, m => Assert.True(m.ReceivedAt > DateTimeOffset.UnixEpoch.AddYears(20), "internalDate not read"));
        Assert.Equal(inbox.OrderByDescending(m => m.ReceivedAt).Select(m => m.Id), inbox.Select(m => m.Id));

        var full = await gmail.ReadAsync(c, inbox[0].Id);
        Assert.NotNull(full);
        output.WriteLine($"read '{Trim(full!.Subject, 60)}': from {full.From}, sent {full.SentAt:u}, "
                       + $"quote stripped {full.QuotedHistoryStripped}, {full.Body.Length} chars");
        Assert.False(string.IsNullOrWhiteSpace(full.Body), "body came back empty");
        Assert.DoesNotContain("<div", full.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("&nbsp;", full.Body, StringComparison.OrdinalIgnoreCase);

        // Two pages of a search, with nothing repeated across them (OP2-280).
        var first = await gmail.SearchAsync(c, "in:anywhere", top: 3);
        output.WriteLine($"search page 1: {first.Items.Count}, more {first.More}");
        Assert.NotEmpty(first.Items);
        Assert.True(first.Items.Count <= 3, "search returned more than it was asked for");
        if (first.More)
        {
            var second = await gmail.SearchAsync(c, "ignored on resume", top: 3, continueFrom: first.Continuation);
            output.WriteLine($"search page 2: {second.Items.Count}");
            Assert.NotEmpty(second.Items);
            Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
        }

        var folders = await gmail.ListFoldersAsync(c);
        output.WriteLine($"labels as folders: {folders.Count}");
        foreach (var f in folders.Take(15))
            output.WriteLine($"  {f.Path}{(f.WellKnownName is null ? "" : $" [{f.WellKnownName}]")}  {f.MessageCount} msg, {f.ChildCount} child");
        Assert.Contains(folders, f => f.WellKnownName == "inbox" && f.MessageCount > 0);
        Assert.Contains(folders, f => f.WellKnownName == "deleteditems");
        Assert.DoesNotContain(folders, f => f.Id is "STARRED" or "UNREAD" or "IMPORTANT");

        // An attachment's bytes, opened twice: Gmail issues a new attachment id on every
        // fetch, and opening by part id must survive that (OP2-280).
        var withFiles = await gmail.SearchAsync(c, "has:attachment", top: 5);
        foreach (var hit in withFiles.Items)
        {
            var files = await gmail.ListAttachmentsAsync(c, hit.Id);
            var file = files.FirstOrDefault(a => !a.IsEmbeddedItem && a.SizeBytes > 0);
            if (file is null) continue;

            var a = await Bytes(await gmail.OpenAttachmentAsync(c, hit.Id, file.Id));
            var b = await Bytes(await gmail.OpenAttachmentAsync(c, hit.Id, file.Id));
            output.WriteLine($"attachment '{file.Name}' ({file.ContentType}, listed {file.SizeBytes} B): "
                           + $"{a.Length} B, sha256 {Convert.ToHexString(SHA256.HashData(a))[..16]}…");
            Assert.NotEmpty(a);
            Assert.Equal(a, b);
            return;
        }
        output.WriteLine("no message with an attachment found — attachment step not exercised");
    }

    // ── 2. organising, undone ────────────────────────────────────────────────

    [SkippableFact]
    public async Task Stars_labels_and_moves_a_real_message_and_puts_everything_back()
    {
        var w = await WorldAsync();
        var (gmail, c) = (w.Gmail, w.Connection);

        var inbox = await gmail.ListAsync(c, top: 1);
        Skip.If(inbox.Count == 0, "the inbox is empty — nothing to act on");
        var m = inbox[0];
        var before = await LabelsOnAsync(w, m.Id);
        output.WriteLine($"acting on '{Trim(m.Subject, 60)}'; labels before: [{string.Join(", ", before)}]");

        var probe = $"Outpost probe {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        MailFolder? label = null;
        try
        {
            await gmail.FlagAsync(c, m.Id, true, null);
            Assert.Contains("STARRED", await LabelsOnAsync(w, m.Id));
            await gmail.FlagAsync(c, m.Id, before.Contains("STARRED"), null);
            output.WriteLine("  starred and restored");

            await gmail.MarkReadAsync(c, m.Id, !m.IsRead);
            Assert.Equal(m.IsRead, (await LabelsOnAsync(w, m.Id)).Contains("UNREAD"));
            await gmail.MarkReadAsync(c, m.Id, m.IsRead);
            output.WriteLine("  read state toggled and restored");

            label = await gmail.CreateFolderAsync(c, probe);
            output.WriteLine($"  made label {label.Path} ({label.Id})");

            var categories = await gmail.GetCategoriesAsync(c, m.Id);
            await gmail.SetCategoriesAsync(c, m.Id, [.. categories, probe]);
            Assert.Contains(label.Id, await LabelsOnAsync(w, m.Id));
            Assert.Contains("INBOX", await LabelsOnAsync(w, m.Id));   // categorising never moves
            await gmail.SetCategoriesAsync(c, m.Id, categories);
            Assert.DoesNotContain(label.Id, await LabelsOnAsync(w, m.Id));
            output.WriteLine("  applied as a category and removed");

            await gmail.MoveAsync(c, m.Id, label.Id);
            var moved = await LabelsOnAsync(w, m.Id);
            Assert.Contains(label.Id, moved);
            Assert.DoesNotContain("INBOX", moved);
            await gmail.MoveAsync(c, m.Id, "inbox");
            Assert.Contains("INBOX", await LabelsOnAsync(w, m.Id));
            output.WriteLine("  moved to the label (out of the Inbox) and back");
        }
        finally
        {
            // Restore exactly: the probe label off, the Inbox back, then the label gone.
            if (label is not null)
            {
                var now = await LabelsOnAsync(w, m.Id);
                if (now.Contains(label.Id) || !now.Contains("INBOX"))
                    await gmail.MoveAsync(c, m.Id, "inbox");
                await gmail.SetCategoriesAsync(c, m.Id,
                    (await gmail.GetCategoriesAsync(c, m.Id)).Where(x => x != probe).ToList());
                await gmail.DeleteFolderAsync(c, label.Id);
                output.WriteLine($"  deleted label {label.Path}");
            }
        }

        var after = await LabelsOnAsync(w, m.Id);
        output.WriteLine($"labels after: [{string.Join(", ", after)}]");
        Assert.Equal(before.OrderBy(x => x), after.OrderBy(x => x));
        Assert.DoesNotContain(await gmail.ListFoldersAsync(c), f => f.Path == probe);
    }

    // ── 3. sending, opt-in ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task Sends_to_itself_replies_in_the_thread_and_tidies_up()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("OP2_PROBE_SEND") == "1",
            "sending is opt-in: set OP2_PROBE_SEND=1 to mail this account to itself");

        var w = await WorldAsync();
        var (gmail, c) = (w.Gmail, w.Connection);
        var me = c.AccountIdentifier;
        Skip.If(string.IsNullOrWhiteSpace(me), "the connection has no account address to send to");

        var tag = Guid.NewGuid().ToString("N")[..12];
        var subject = $"Outpost probe {tag}";

        await gmail.SendAsync(c, new OutgoingMail
        {
            To = [me!], Subject = subject, Body = $"Sent by **LiveGmailProbe** — {tag}.", Importance = MailImportance.High,
        });
        output.WriteLine($"sent '{subject}' to {me}");

        var arrived = await WaitForAsync(gmail, c, $"subject:\"{tag}\" in:inbox", 1);
        var original = arrived.Single();
        output.WriteLine($"  arrived: {original.Id}, importance {original.Importance}");
        Assert.Equal(MailImportance.High, original.Importance);

        var read = await gmail.ReadAsync(c, original.Id);
        Assert.Contains(tag, read!.Body);
        Assert.Contains("LiveGmailProbe", read.Body);

        var reply = await gmail.SendReplyAsync(c, original.Id, ReplyKind.Sender, $"Reply to {tag}.");
        output.WriteLine($"  replied: '{reply.Subject}' to {string.Join(", ", reply.To.Select(r => r.Address))}");
        Assert.Equal($"Re: {subject}", reply.Subject);

        var thread = await WaitForAsync(gmail, c, $"subject:\"{tag}\"", 2, includeAll: true);
        var threads = new HashSet<string>();
        foreach (var t in thread) threads.Add(await ThreadOfAsync(w, t.Id));
        output.WriteLine($"  {thread.Count} message(s) carrying the tag, in {threads.Count} thread(s)");
        Assert.Single(threads);   // the reply landed in the same thread

        // Trash and back, then Trash for good — the probe's own mail only.
        await gmail.MoveAsync(c, original.Id, "deleteditems");
        Assert.Contains("TRASH", await LabelsOnAsync(w, original.Id));
        await gmail.MoveAsync(c, original.Id, "inbox");
        Assert.DoesNotContain("TRASH", await LabelsOnAsync(w, original.Id));
        output.WriteLine("  trashed and brought back");

        foreach (var t in await gmail.SearchAsync(c, $"subject:\"{tag}\" in:anywhere", top: 10) is { } all ? all.Items : [])
            await gmail.MoveAsync(c, t.Id, "deleteditems");
        output.WriteLine("  the probe's messages are in Trash");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<MailSummary>> WaitForAsync(
        GmailService gmail, Connection c, string query, int atLeast, bool includeAll = false)
    {
        for (var i = 0; i < 30; i++)
        {
            var hits = (await gmail.SearchAsync(c, query + (includeAll ? " in:anywhere" : ""), top: 10)).Items;
            if (hits.Count >= atLeast) return hits;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.Fail($"'{query}' did not reach {atLeast} message(s) within a minute");
        return [];
    }

    /// <summary>The raw labels on a message, straight from Gmail — what the tools' results claim, checked.</summary>
    private static async Task<IReadOnlyList<string>> LabelsOnAsync(World w, string id) =>
        (await RawAsync(w, $"messages/{id}?format=minimal")).GetProperty("labelIds").EnumerateArray()
            .Select(x => x.GetString()!).ToList();

    private static async Task<string> ThreadOfAsync(World w, string id) =>
        (await RawAsync(w, $"messages/{id}?format=minimal")).GetProperty("threadId").GetString()!;

    private static async Task<JsonElement> RawAsync(World w, string path)
    {
        var token = await w.Tokens.GetAccessTokenAsync(w.Connection);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.GetFromJsonAsync<JsonElement>("https://gmail.googleapis.com/gmail/v1/users/me/" + path);
    }

    private static async Task<byte[]> Bytes(Stream? s)
    {
        Assert.NotNull(s);
        await using (s)
        {
            using var ms = new MemoryStream();
            await s!.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
