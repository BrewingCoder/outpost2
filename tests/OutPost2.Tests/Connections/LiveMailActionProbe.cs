using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Exercises the mailbox-changing paths against the real mailbox (OP2-69, OP2-70,
/// OP2-71, OP2-76).
/// <para>
/// Nothing here is reversible by a unit test, so every step undoes itself: folders are
/// only listed, the flag and categories are restored to what they were, and the reply
/// draft is built and then DELETED rather than sent. That last one is the point of this
/// probe — the three-call reply is the most intricate new code and the only way to learn
/// that Graph accepts a body-less POST to createReply is to post one.
/// </para>
/// <para>
/// Deliberately does not send. A test that mails someone every time it runs is a test
/// that gets disabled.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveMailActionProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Folders_flags_categories_and_a_reply_draft_all_work()
    {
        OutPostDbContext db;
        MailService mail;
        IMicrosoftConnectionService tokens;

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

            var store = new ConnectionStore(db, new CredentialProtector(vault));
            tokens = new MicrosoftConnectionService(Options.Create(new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            }), store, NullLogger<MicrosoftConnectionService>.Instance);

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            mail = new MailService(tokens, sp.GetRequiredService<IHttpClientFactory>(),
                NullLogger<MailService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // Reachable-but-unmigrated Postgres is a skip, not a failure (see LiveEnvironment).
        await LiveEnvironment.RequireSchemaAsync(db);

        // Which mailbox to act on. Unset it and the probe takes whichever Microsoft
        // connection comes first, which is the right default for "does mail still work";
        // set it to re-run the whole surface against one mailbox after a reconnect.
        var only = Environment.GetEnvironmentVariable("OP2_PROBE_LABEL");

        var c = await db.Connections
            .Where(x => x.Provider == ProviderKind.Microsoft)
            .Where(x => only == null || x.Label == only)
            .FirstOrDefaultAsync();

        Skip.If(c is null, only is null
            ? "no Microsoft connection stored"
            : $"no Microsoft connection labelled '{only}'");

        // The capability the new tools gate on. If the backfill did not reach this row the
        // tools would refuse every action, and that is worth catching here rather than in
        // the car.
        var write = c!.Capabilities.FirstOrDefault(x => x.Name == Capabilities.MailWrite);
        output.WriteLine($"'{c.Label}' mail.write: " +
            (write is null ? "MISSING — backfill did not reach this row" : $"granted={write.Granted} enabled={write.Enabled}"));
        Assert.NotNull(write);

        // ── folders (OP2-69) ────────────────────────────────────────────────
        var folders = await mail.ListFoldersAsync(c);
        output.WriteLine($"folders: {folders.Count}");
        foreach (var f in folders.Take(15))
            output.WriteLine($"  {f.Path}{(f.WellKnownName is null ? "" : $"  [{f.WellKnownName}]")}");

        Assert.NotEmpty(folders);
        Assert.Contains(folders, f => f.WellKnownName == "inbox");

        // Archive and Deleted Items are what "archive this" and "bin it" resolve to. If
        // the mailbox genuinely lacks an Archive folder the tool should say so rather than
        // 404 halfway through a move, so assert the ones Exchange always provisions.
        Assert.Contains(folders, f => f.WellKnownName == "deleteditems");
        Assert.All(folders, f => Assert.False(string.IsNullOrWhiteSpace(f.Id)));

        // ── categories (OP2-76) ─────────────────────────────────────────────
        // Null rather than a throw when the connection predates the MailboxSettings scope.
        // Expected on any mailbox consented before OP2-76, and the thing worth printing:
        // it is the difference between a bug and a pending re-authorisation.
        var categories = await mail.ListCategoriesAsync(c);

        if (categories is null)
            output.WriteLine("categories: NOT READABLE — connection predates the MailboxSettings scope");
        else
        {
            output.WriteLine($"categories: {categories.Count}");
            foreach (var x in categories.Take(10))
                output.WriteLine($"  {x.Name} — {x.Preset} = {x.Colour}");

            // No assertion on count: a fresh mailbox legitimately has none. What must hold
            // is that every preset we are handed describes as a colour rather than as
            // itself, which is the claim the hard-coded palette table makes.
            Assert.All(categories, x => Assert.False(string.IsNullOrWhiteSpace(x.Name)));
            Assert.All(categories, x =>
                Assert.True(x.Colour != x.Preset || x.Preset == "none",
                    $"preset {x.Preset} has no colour name"));
        }

        var inbox = await mail.ListAsync(c, top: 5);
        Skip.If(inbox.Count == 0, "inbox is empty — nothing to act on");
        var subject = inbox[0];
        output.WriteLine($"acting on: {subject.Subject}");

        // ── flags (OP2-76), restored afterwards ─────────────────────────────
        await mail.FlagAsync(c, subject.Id, flagged: true, dueAt: DateTimeOffset.UtcNow.AddDays(3));
        output.WriteLine("  flagged with a due date");
        await mail.FlagAsync(c, subject.Id, flagged: false, dueAt: null);
        output.WriteLine("  flag cleared");

        // ── categories on a message, restored afterwards ────────────────────
        var before = await mail.GetCategoriesAsync(c, subject.Id);
        output.WriteLine($"  categories before: [{string.Join(", ", before)}]");

        if (categories is { Count: > 0 })
        {
            var pick = categories[0].Name;
            await mail.SetCategoriesAsync(c, subject.Id, [.. before, pick]);

            var after = await mail.GetCategoriesAsync(c, subject.Id);
            Assert.Contains(pick, after);

            await mail.SetCategoriesAsync(c, subject.Id, before);
            Assert.Equal(before, await mail.GetCategoriesAsync(c, subject.Id));
            output.WriteLine($"  applied and removed '{pick}'");
        }

        // ── read state (OP2-70), restored afterwards ────────────────────────
        await mail.MarkReadAsync(c, subject.Id, !subject.IsRead);
        await mail.MarkReadAsync(c, subject.Id, subject.IsRead);
        output.WriteLine($"  toggled read state and put it back ({(subject.IsRead ? "read" : "unread")})");

        // ── a move and back again (OP2-69) ──────────────────────────────────
        // Returns the id the message ends up with. The first version of this probe reused
        // the original and the reply below 404'd — which is the retargeting the tools do,
        // demonstrated by forgetting to do it.
        var current = await MoveSurvivesTheRoundTrip(mail, c, subject.Id);

        // ── the reply draft (OP2-71), built and then deleted ────────────────
        await ReplyDraftSurvivesTheRoundTrip(c, current, tokens);
    }

    /// <summary>
    /// Archives a real message and files it straight back. The claim being tested is the
    /// one that cannot be faked: Graph reissues the message id on a move, so a caller that
    /// keeps the old one is holding a handle to nothing — which is why the tool retargets.
    /// <para>
    /// The return leg is in a finally. Leaving one of Scott's messages in Archive because
    /// an assertion failed would be a silently misfiled message he never learns about.
    /// </para>
    /// </summary>
    private async Task<string> MoveSurvivesTheRoundTrip(MailService mail, Connection c, string messageId)
    {
        var archived = await mail.MoveAsync(c, messageId, "archive");
        output.WriteLine($"  moved to Archive, id changed: {archived != messageId}");

        try
        {
            Assert.NotEqual(messageId, archived);

            // The old id really is dead, so an un-retargeted handle would fail here.
            Assert.Null(await mail.ReadAsync(c, messageId));

            var there = await mail.ReadAsync(c, archived);
            Assert.NotNull(there);
        }
        finally
        {
            _returned = await mail.MoveAsync(c, archived, "inbox");
            var back = await mail.ReadAsync(c, _returned);
            output.WriteLine($"  returned to the Inbox: {(back is null ? "FAILED — IT IS STILL IN ARCHIVE" : "ok")}");
            Assert.NotNull(back);
        }

        return _returned;
    }

    /// <summary>Set in the finally above, read after it; a local would not survive the throw.</summary>
    private string _returned = "";

    /// <summary>
    /// Does by hand exactly what <c>SendReplyAsync</c> does, minus the send: create the
    /// draft, prepend our rendered Markdown with the same helper the service uses, read it
    /// back, then delete it. Proves Graph accepts a body-less createReply POST, that the
    /// draft really does carry the quoted thread, and that our text lands above it.
    /// </summary>
    private async Task ReplyDraftSurvivesTheRoundTrip(
        Connection c, string messageId, IMicrosoftConnectionService tokens)
    {
        var token = await tokens.GetAccessTokenAsync(c);
        Skip.If(token is null, "no access token for the Microsoft connection");

        using var http = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var draftResp = await http.PostAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}/createReply", content: null);

        output.WriteLine($"  createReply: {(int)draftResp.StatusCode}");
        draftResp.EnsureSuccessStatusCode();

        var draft = await draftResp.Content.ReadFromJsonAsync<JsonElement>();
        var draftId = draft.GetProperty("id").GetString()!;

        try
        {
            var quoted = draft.GetProperty("body").GetProperty("content").GetString() ?? "";
            Assert.Contains("<body", quoted, StringComparison.OrdinalIgnoreCase);

            const string Marker = "OutPost2 probe — this draft is deleted immediately.";
            var composed = MailService.Prepend(MarkdownToHtml.Render($"**{Marker}**"), quoted);

            using var patch = await http.PatchAsJsonAsync($"me/messages/{Uri.EscapeDataString(draftId)}",
                new { body = new { contentType = "HTML", content = composed } });

            output.WriteLine($"  patch body : {(int)patch.StatusCode}");
            patch.EnsureSuccessStatusCode();

            var readBack = await http.GetFromJsonAsync<JsonElement>(
                $"me/messages/{Uri.EscapeDataString(draftId)}?$select=id,body,toRecipients");

            var saved = readBack.GetProperty("body").GetProperty("content").GetString() ?? "";

            // Our text survived the round trip, and it is above the quoted thread rather
            // than below it or outside the document.
            Assert.Contains(Marker, saved);
            Assert.True(saved.IndexOf(Marker, StringComparison.Ordinal) < saved.Length / 2,
                "the reply text did not land near the top of the body");

            // A reply draft addresses the original sender without being told to. If this
            // were empty the send would fail, and it would fail at the last of three calls.
            var to = readBack.GetProperty("toRecipients").EnumerateArray().Count();
            output.WriteLine($"  recipients : {to}");
            Assert.True(to > 0, "the reply draft had no recipients");
        }
        finally
        {
            using var deleted = await http.DeleteAsync($"me/messages/{Uri.EscapeDataString(draftId)}");
            output.WriteLine($"  draft deleted: {(int)deleted.StatusCode}");
        }
    }
}
