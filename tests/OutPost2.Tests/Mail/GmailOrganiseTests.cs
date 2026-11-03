using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Organising Gmail (OP2-282): labels standing in for folders and categories, archive,
/// Trash, Spam, stars, and clearing a sender out and back. Against a small stateful fake of
/// the Gmail API's documented endpoints, because what matters here is what the mailbox
/// looks like AFTERWARDS — "moved to Receipts" in Gmail is "labelled Receipts and no longer
/// in the Inbox", and only the end state shows whether that is what happened. The
/// confirmations are checked through the real MailTools, in Gmail's words.
/// </summary>
public sealed class GmailOrganiseTests
{
    private static readonly Guid User = Guid.CreateVersion7();

    // ── a fake Gmail with state ──────────────────────────────────────────────

    private sealed class Mailbox : HttpMessageHandler
    {
        public sealed class Msg
        {
            public required string Id { get; init; }
            public required string From { get; init; }
            public string Subject { get; init; } = "Hello";
            public HashSet<string> Labels { get; } = [];
        }

        public Dictionary<string, Msg> Messages { get; } = [];
        public Dictionary<string, string> Labels { get; } = new()    // id → name
        {
            ["INBOX"] = "INBOX", ["SENT"] = "SENT", ["TRASH"] = "TRASH", ["SPAM"] = "SPAM",
            ["STARRED"] = "STARRED", ["UNREAD"] = "UNREAD", ["DRAFT"] = "DRAFT",
        };
        public List<string> Calls { get; } = [];
        public int TrashCallsBeforeThrottle { get; set; } = int.MaxValue;
        private int _next = 100;

        public Msg Add(string id, string from, params string[] labels)
        {
            var m = new Msg { Id = id, From = from };
            foreach (var l in labels) m.Labels.Add(l);
            Messages[id] = m;
            return m;
        }

        public string AddLabel(string name)
        {
            var id = $"Label_{_next++}";
            Labels[id] = name;
            return id;
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode code) =>
            new(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        private object LabelJson(string id) => new
        {
            id, name = Labels[id], type = id.StartsWith("Label_") ? "user" : "system",
            messagesTotal = Messages.Values.Count(m => m.Labels.Contains(id)), messagesUnread = 0,
        };

        private object MessageJson(Msg m) => new
        {
            id = m.Id, threadId = m.Id, labelIds = m.Labels.ToArray(), snippet = "", internalDate = "1762162200000",
            payload = new
            {
                partId = "", mimeType = "text/plain", filename = "",
                headers = new[] { new { name = "From", value = m.From }, new { name = "Subject", value = m.Subject } },
                body = new { size = 2, data = "aGk" },
            },
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(r.RequestUri!.AbsolutePath.Replace("/gmail/v1/users/me/", ""));
            var query = System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
            var body = r.Content is null ? null : JsonNode.Parse(await r.Content.ReadAsStringAsync(ct) is { Length: > 0 } t ? t : "{}");
            lock (Calls) Calls.Add($"{r.Method} {path}");

            lock (this)
            {
                // ── labels ──
                if (path == "labels" && r.Method == HttpMethod.Get)
                    return Json(new { labels = Labels.Keys.Select(LabelJson).ToArray() });

                if (path == "labels" && r.Method == HttpMethod.Post)
                {
                    var name = body!["name"]!.GetValue<string>();
                    if (Labels.Values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                        return Status(HttpStatusCode.Conflict);
                    return Json(LabelJson(AddLabel(name)));
                }

                if (path.StartsWith("labels/"))
                {
                    var id = path["labels/".Length..];
                    if (!Labels.ContainsKey(id)) return Status(HttpStatusCode.NotFound);
                    if (r.Method == HttpMethod.Get) return Json(LabelJson(id));
                    if (r.Method == HttpMethod.Patch)
                    {
                        Labels[id] = body!["name"]!.GetValue<string>();
                        return Json(LabelJson(id));
                    }
                    if (r.Method == HttpMethod.Delete)
                    {
                        Labels.Remove(id);
                        foreach (var m in Messages.Values) m.Labels.Remove(id);
                        return Status(HttpStatusCode.NoContent);
                    }
                }

                // ── listing ──
                if (path == "messages" && r.Method == HttpMethod.Get)
                {
                    var q = query["q"] ?? "";
                    var from = System.Text.RegularExpressions.Regex.Match(q, "from:\"?([^\" ]+)").Groups[1].Value;
                    var labels = query.GetValues("labelIds") ?? [];
                    var bins = query["includeSpamTrash"] == "true";

                    var hits = Messages.Values
                        .Where(m => from.Length == 0 || m.From.Contains(from, StringComparison.OrdinalIgnoreCase))
                        .Where(m => labels.All(m.Labels.Contains))
                        .Where(m => bins || labels.Contains("TRASH") || labels.Contains("SPAM")
                                    || !(m.Labels.Contains("TRASH") || m.Labels.Contains("SPAM")))
                        .OrderBy(m => m.Id).ToList();

                    var max = int.Parse(query["maxResults"] ?? "100");

                    // As Gmail does: maxResults must be at least 1.
                    if (max < 1)
                        return Json(new { error = new { code = 400, message = "Invalid maxResults", status = "INVALID_ARGUMENT" } },
                            HttpStatusCode.BadRequest);
                    var skip = int.Parse(query["pageToken"] ?? "0");
                    var page = hits.Skip(skip).Take(max).ToList();
                    var next = skip + page.Count < hits.Count ? (skip + page.Count).ToString() : null;
                    return next is null
                        ? Json(new { messages = page.Select(m => new { id = m.Id, threadId = m.Id }).ToArray(), resultSizeEstimate = hits.Count })
                        : Json(new { messages = page.Select(m => new { id = m.Id, threadId = m.Id }).ToArray(), nextPageToken = next, resultSizeEstimate = hits.Count });
                }

                // ── one message ──
                if (path.StartsWith("messages/"))
                {
                    var rest = path["messages/".Length..].Split('/');
                    if (!Messages.TryGetValue(rest[0], out var m)) return Status(HttpStatusCode.NotFound);

                    if (rest.Length == 1 && r.Method == HttpMethod.Get) return Json(MessageJson(m));

                    switch (rest[1])
                    {
                        case "modify":
                            foreach (var a in body!["addLabelIds"]!.AsArray()) m.Labels.Add(a!.GetValue<string>());
                            foreach (var d in body["removeLabelIds"]!.AsArray()) m.Labels.Remove(d!.GetValue<string>());
                            return Json(MessageJson(m));
                        case "trash":
                            if (TrashCallsBeforeThrottle-- <= 0)
                                return Json(new { error = new { code = 429, errors = new[] { new { reason = "rateLimitExceeded" } } } },
                                    HttpStatusCode.TooManyRequests);
                            m.Labels.Add("TRASH");
                            return Json(MessageJson(m));
                        case "untrash":
                            m.Labels.Remove("TRASH");
                            return Json(MessageJson(m));
                    }
                }
            }

            return Status(HttpStatusCode.NotFound);
        }
    }

    private sealed class OneClient(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private sealed class Tokens : IGoogleTokenAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default) =>
            Task.FromResult<string?>("ya29.token");
    }

    private static Connection Personal()
    {
        var c = new Connection
        {
            UserId = User, Label = "personal", Provider = ProviderKind.Google, Instance = "google",
            AuthKind = AuthKind.OAuth2, SealedCredential = "s", State = ConnectionState.Active,
            AccountIdentifier = "me@example.com", IsDefaultForProvider = true,
        };
        foreach (var n in CapabilityCatalog.DeclaredFor(ProviderKind.Google))
            c.Capabilities.Add(new Capability { Name = n, Granted = true, Enabled = true });
        return c;
    }

    private static (GmailService Gmail, Mailbox Box) Provider()
    {
        var box = new Mailbox();
        return (new GmailService(new Tokens(), new OneClient(box), NullLogger<GmailService>.Instance), box);
    }

    /// <summary>The real MailTools over the real GmailService over the fake mailbox.</summary>
    private static (MailTools Tools, Mailbox Box) Tools()
    {
        var (gmail, box) = Provider();
        var conn = Personal();
        IReadOnlyList<Connection> all = [conn];

        var (store, _) = Stub<IConnectionStore>.Create(new()
        {
            ["ListAsync"] = _ => Task.FromResult(all),
            ["FindByLabelAsync"] = a => Task.FromResult(all.FirstOrDefault(c => c.Label == (string)a[1]!)),
        });
        var (calendar, _) = Stub<ICalendarService>.Create([]);
        var (jobs, _) = Stub<IJobService>.Create([]);
        var (downloads, _) = Stub<IDownloadTicketService>.Create([]);
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("op2:uid", User.ToString())], "t")),
            },
        };

        var tools = new MailTools(http, store, new MailServiceRouter([gmail]), new ItemIndex(), new SearchContinuations(),
            new AttachmentExtractor(NullLogger<AttachmentExtractor>.Instance), jobs, calendar, downloads,
            new PublicUrls("https://op2.example"));
        return (tools, box);
    }

    // ── moving, in Gmail's terms ─────────────────────────────────────────────

    [Fact]
    public async Task Moving_to_a_label_labels_it_and_takes_it_out_of_the_inbox()
    {
        var (gmail, box) = Provider();
        var receipts = box.AddLabel("Receipts");
        var m = box.Add("m1", "shop@example.com", "INBOX", "UNREAD");

        var id = await gmail.MoveAsync(Personal(), "m1", receipts);

        Assert.Equal("m1", id);   // Gmail ids do not change on a move
        Assert.Contains(receipts, m.Labels);
        Assert.DoesNotContain("INBOX", m.Labels);
        Assert.Contains("UNREAD", m.Labels);   // nothing else touched
    }

    [Fact]
    public async Task Archiving_takes_it_out_of_the_inbox_and_nothing_more()
    {
        var (gmail, box) = Provider();
        var receipts = box.AddLabel("Receipts");
        var m = box.Add("m1", "a@example.com", "INBOX", receipts);

        await gmail.MoveAsync(Personal(), "m1", "archive");

        Assert.Equal([receipts], m.Labels);
    }

    [Fact]
    public async Task Trash_is_gmails_trash_and_back_to_the_inbox_untrashes_first()
    {
        var (gmail, box) = Provider();
        var m = box.Add("m1", "a@example.com", "INBOX");

        await gmail.MoveAsync(Personal(), "m1", "deleteditems");
        Assert.Contains("TRASH", m.Labels);
        Assert.Contains("POST messages/m1/trash", box.Calls);

        await gmail.MoveAsync(Personal(), "m1", "inbox");
        Assert.DoesNotContain("TRASH", m.Labels);
        Assert.Contains("INBOX", m.Labels);
        Assert.Contains("POST messages/m1/untrash", box.Calls);
    }

    [Fact]
    public async Task Spam_adds_spam_and_leaves_the_inbox()
    {
        var (gmail, box) = Provider();
        var m = box.Add("m1", "a@example.com", "INBOX");

        Assert.Equal("m1", await gmail.MarkAsJunkAsync(Personal(), "m1"));
        Assert.Equal(["SPAM"], m.Labels);
    }

    [Fact]
    public async Task Gmail_will_not_take_mail_into_sent_or_drafts_and_says_so()
    {
        var (gmail, box) = Provider();
        box.Add("m1", "a@example.com", "INBOX");

        var ex = await Assert.ThrowsAsync<MailException>(() => gmail.MoveAsync(Personal(), "m1", "sentitems"));
        Assert.Contains("Sent or Drafts", ex.Message);
    }

    [Fact]
    public async Task Read_unread_and_stars_are_the_labels_gmail_uses_for_them()
    {
        var (gmail, box) = Provider();
        var m = box.Add("m1", "a@example.com", "INBOX", "UNREAD");

        await gmail.MarkReadAsync(Personal(), "m1", read: true);
        Assert.DoesNotContain("UNREAD", m.Labels);
        await gmail.MarkReadAsync(Personal(), "m1", read: false);
        Assert.Contains("UNREAD", m.Labels);

        await gmail.FlagAsync(Personal(), "m1", true, DateTimeOffset.UtcNow.AddDays(2));
        Assert.Contains("STARRED", m.Labels);
        await gmail.FlagAsync(Personal(), "m1", false, null);
        Assert.DoesNotContain("STARRED", m.Labels);
    }

    // ── labels as categories ────────────────────────────────────────────────

    [Fact]
    public async Task Categorising_adds_and_removes_only_your_labels_and_leaves_the_inbox_alone()
    {
        var (gmail, box) = Provider();
        var urgent = box.AddLabel("Urgent");
        var receipts = box.AddLabel("Receipts");
        var m = box.Add("m1", "a@example.com", "INBOX", "STARRED", receipts);

        Assert.Equal(["Receipts"], await gmail.GetCategoriesAsync(Personal(), "m1"));

        await gmail.SetCategoriesAsync(Personal(), "m1", ["Receipts", "urgent"]);
        Assert.True(m.Labels.SetEquals(["INBOX", "STARRED", receipts, urgent]), string.Join(",", m.Labels));

        await gmail.SetCategoriesAsync(Personal(), "m1", ["Urgent"]);
        Assert.True(m.Labels.SetEquals(["INBOX", "STARRED", urgent]), string.Join(",", m.Labels));
    }

    [Fact]
    public async Task Categorising_with_a_label_that_does_not_exist_changes_nothing()
    {
        var (gmail, box) = Provider();
        var m = box.Add("m1", "a@example.com", "INBOX");

        await Assert.ThrowsAsync<MailException>(() => gmail.SetCategoriesAsync(Personal(), "m1", ["Nope"]));
        Assert.Equal(["INBOX"], m.Labels);
    }

    [Fact]
    public async Task Categories_are_your_labels_without_gmails_own()
    {
        var (gmail, box) = Provider();
        box.AddLabel("Receipts");
        box.AddLabel("Clients/Acme");

        var all = await gmail.ListCategoriesAsync(Personal());
        Assert.Equal(["Clients/Acme", "Receipts"], all!.Select(c => c.Name));
    }

    // ── making, renaming and deleting labels ─────────────────────────────────

    [Fact]
    public async Task A_label_can_be_made_at_the_top_or_inside_another()
    {
        var (gmail, box) = Provider();
        var receipts = box.AddLabel("Receipts");

        var top = await gmail.CreateFolderAsync(Personal(), "Travel");
        Assert.Equal("Travel", top.Path);

        var inner = await gmail.CreateFolderAsync(Personal(), "2026", receipts);
        Assert.Equal(("2026", "Receipts/2026"), (inner.Name, inner.Path));

        // A system label cannot be a parent: it goes at the top level.
        var underInbox = await gmail.CreateFolderAsync(Personal(), "Later", "INBOX");
        Assert.Equal("Later", underInbox.Path);

        var ex = await Assert.ThrowsAsync<MailException>(() => gmail.CreateFolderAsync(Personal(), "receipts"));
        Assert.Contains("already has a label", ex.Message);
    }

    [Fact]
    public async Task Renaming_a_label_takes_the_labels_inside_it_along()
    {
        var (gmail, box) = Provider();
        var receipts = box.AddLabel("Receipts");
        var year = box.AddLabel("Receipts/2026");
        var travel = box.AddLabel("Receipts/2026/Travel");
        var other = box.AddLabel("ReceiptsArchive");

        var renamed = await gmail.RenameFolderAsync(Personal(), receipts, "Expenses");

        Assert.Equal("Expenses", renamed.Path);
        Assert.Equal("Expenses", box.Labels[receipts]);
        Assert.Equal("Expenses/2026", box.Labels[year]);
        Assert.Equal("Expenses/2026/Travel", box.Labels[travel]);
        Assert.Equal("ReceiptsArchive", box.Labels[other]);   // a prefix, not a child
    }

    [Fact]
    public async Task Deleting_a_label_deletes_no_mail()
    {
        var (gmail, box) = Provider();
        var receipts = box.AddLabel("Receipts");
        var year = box.AddLabel("Receipts/2026");
        var m = box.Add("m1", "a@example.com", receipts, year);

        await gmail.DeleteFolderAsync(Personal(), receipts);

        Assert.False(box.Labels.ContainsKey(receipts));
        Assert.False(box.Labels.ContainsKey(year));
        Assert.True(box.Messages.ContainsKey("m1"));   // still there, in All Mail
        Assert.Empty(m.Labels);
    }

    [Theory]
    [InlineData("INBOX")]
    [InlineData("SENT")]
    public async Task Gmails_own_labels_cannot_be_renamed_or_deleted(string id)
    {
        var (gmail, _) = Provider();
        await Assert.ThrowsAsync<MailException>(() => gmail.RenameFolderAsync(Personal(), id, "x"));
        await Assert.ThrowsAsync<MailException>(() => gmail.DeleteFolderAsync(Personal(), id));
    }

    // ── clearing a sender out, and back ──────────────────────────────────────

    private static Mailbox Cluttered(Mailbox box)
    {
        var filed = box.AddLabel("Jobs");
        for (var i = 0; i < 5; i++) box.Add($"li{i}", "LinkedIn <news@linkedin.com>", "INBOX");
        box.Add("li-filed", "LinkedIn <news@linkedin.com>", filed);
        box.Add("keep", "Ann <ann@example.com>", "INBOX");
        return box;
    }

    [Fact]
    public async Task Clearing_a_sender_from_the_inbox_trashes_exactly_those_and_nothing_else()
    {
        var (gmail, box) = Provider();
        Cluttered(box);

        var outcome = await gmail.ClearAsync(Personal(), new ClearTarget { From = "linkedin", FolderIds = ["INBOX"] });

        Assert.Equal(5, outcome.Deleted);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Contains("TRASH", box.Messages[$"li{i}"].Labels));
        Assert.DoesNotContain("TRASH", box.Messages["li-filed"].Labels);   // filed, out of scope
        Assert.DoesNotContain("TRASH", box.Messages["keep"].Labels);
        Assert.DoesNotContain(box.Calls, c => c.StartsWith("DELETE messages"));   // never a hard delete
    }

    [Fact]
    public async Task A_ceiling_filled_by_the_first_folder_does_not_ask_gmail_for_nothing_from_the_second()
    {
        var (gmail, box) = Provider();
        var jobs = Cluttered(box).Labels.Single(l => l.Value == "Jobs").Key;

        var outcome = await gmail.ClearAsync(Personal(),
            new ClearTarget { From = "linkedin", FolderIds = ["INBOX", jobs] }, ceiling: 5);

        Assert.Equal(5, outcome.Deleted);   // and no 400 from asking the second folder for zero
    }

    [Fact]
    public async Task The_preview_counts_what_is_in_scope_and_what_is_filed_elsewhere()
    {
        var (gmail, box) = Provider();
        Cluttered(box);

        var p = await gmail.PreviewClearAsync(Personal(), new ClearTarget { From = "linkedin", FolderIds = ["INBOX"] });

        Assert.Equal(5, p.Matched);
        Assert.Equal(6, p.Everywhere);
        var sender = Assert.Single(p.Senders);
        Assert.Equal(("LinkedIn", "news@linkedin.com", 6), (sender.Display, sender.Address, sender.Count));
        Assert.Single(p.SkippedFolderIds);   // Jobs
        Assert.False(p.Sampled);
    }

    [Fact]
    public async Task A_throttle_part_way_through_reports_what_was_done()
    {
        var (gmail, box) = Provider();
        Cluttered(box);
        box.TrashCallsBeforeThrottle = 2;

        var outcome = await gmail.ClearAsync(Personal(), new ClearTarget { From = "linkedin", FolderIds = ["INBOX"] });

        Assert.True(outcome.Throttled);
        Assert.InRange(outcome.Deleted, 1, 2);
        Assert.Equal(outcome.Deleted, Enumerable.Range(0, 5).Count(i => box.Messages[$"li{i}"].Labels.Contains("TRASH")));
    }

    [Fact]
    public async Task Recovering_brings_a_sender_back_out_of_trash_into_the_inbox()
    {
        var (gmail, box) = Provider();
        box.Add("t1", "news@linkedin.com", "TRASH");
        box.Add("t2", "news@linkedin.com", "TRASH");
        box.Add("t3", "ann@example.com", "TRASH");

        Assert.Equal(2, await gmail.RecoverableCountAsync(Personal(), "linkedin"));

        var outcome = await gmail.RecoverAsync(Personal(), "linkedin");

        Assert.Equal(2, outcome.Deleted);
        Assert.Equal(["INBOX"], box.Messages["t1"].Labels);
        Assert.Equal(["TRASH"], box.Messages["t3"].Labels);
    }

    [Theory]
    [InlineData("linkedin", null, "from:linkedin")]
    [InlineData("LinkedIn News", null, "from:\"LinkedIn News\"")]
    [InlineData("say \"hi\"", null, "from:\"say hi\"")]
    [InlineData("linkedin", "2025-01-01", "from:linkedin before:2025/01/01")]
    public void The_sender_query_is_quoted_so_a_space_cannot_widen_it(string from, string? before, string expected)
    {
        DateTimeOffset? b = before is null ? null : DateTimeOffset.Parse(before + "T00:00:00Z");
        Assert.Equal(expected, GmailService.SenderQuery(from, b));
    }

    // ── what the user is told, through the real tools ────────────────────────

    private static async Task<string> Handle(MailTools tools)
    {
        await tools.ListMail("personal");
        return "1";
    }

    [Fact]
    public async Task Move_says_labelled_and_out_of_the_inbox()
    {
        var (tools, box) = Tools();
        box.AddLabel("Receipts");
        box.Add("m1", "shop@example.com", "INBOX");

        var said = await tools.MoveMail(await Handle(tools), "Receipts");

        Assert.StartsWith("Labelled \"Hello\" Receipts and took it out of the Inbox in personal.", said);
    }

    [Theory]
    [InlineData("archive", "Archived \"Hello\" in personal — it's out of the Inbox and still in All Mail.")]
    [InlineData("trash", "Moved \"Hello\" to Trash in personal. Gmail keeps it there for 30 days.")]
    [InlineData("junk", "Moved \"Hello\" to Spam in personal.")]
    public async Task Move_to_a_well_known_place_says_it_in_gmails_words(string destination, string expected)
    {
        var (tools, box) = Tools();
        box.Add("m1", "shop@example.com", "INBOX");

        Assert.StartsWith(expected, await tools.MoveMail(await Handle(tools), destination));
    }

    [Fact]
    public async Task A_star_with_a_due_date_says_the_date_was_not_kept()
    {
        var (tools, box) = Tools();
        box.Add("m1", "a@example.com", "INBOX");

        var said = await tools.FlagMail(await Handle(tools), true, dueInDays: 3);

        Assert.StartsWith("Starred \"Hello\" in personal.", said);
        Assert.Contains("Gmail stars have no due date", said);
        Assert.Contains("STARRED", box.Messages["m1"].Labels);
    }

    [Fact]
    public async Task Reporting_spam_does_not_claim_to_block_the_sender()
    {
        var (tools, box) = Tools();
        box.Add("m1", "spammer@example.com", "INBOX");

        var said = await tools.ReportJunk(await Handle(tools));

        Assert.Contains("It's in Spam", said);
        Assert.Contains("doesn't let Outpost block a sender", said);
        Assert.DoesNotContain("is now blocked", said);
    }

    [Fact]
    public async Task Clearing_proposes_trash_in_gmails_terms_and_does_not_mention_clutter()
    {
        var (tools, box) = Tools();
        Cluttered(box);

        var said = await tools.ClearMail("linkedin", "personal");

        Assert.Contains("5 messages match \"linkedin\" in the Inbox in personal", said);
        Assert.Contains("go to Trash, where Gmail keeps them for 30 days", said);
        Assert.Contains("another 1 from them in folders you've filed into — Jobs", said);
        Assert.DoesNotContain("Clutter", said);
        Assert.DoesNotContain("Deleted Items", said);
    }

    [Fact]
    public async Task Clearing_confirmed_says_trash_and_thirty_days()
    {
        var (tools, box) = Tools();
        Cluttered(box);

        var said = await tools.ClearMail("linkedin", "personal", confirm: true);

        Assert.StartsWith("Moved 5 messages from \"linkedin\" in the Inbox to Trash in personal.", said);
        Assert.Contains("Gmail keeps Trash for 30 days", said);
    }

    [Fact]
    public async Task Recover_speaks_of_trash_not_recoverable_items()
    {
        var (tools, box) = Tools();
        box.Add("t1", "news@linkedin.com", "TRASH");

        var said = await tools.RecoverMail("linkedin", "personal");
        Assert.Contains("1 message from \"linkedin\" is in Trash on personal", said);
        Assert.DoesNotContain("Recoverable Items", said);
    }

    [Fact]
    public async Task Deleting_a_label_with_mail_on_it_asks_and_explains_nothing_is_deleted()
    {
        var (tools, box) = Tools();
        var receipts = box.AddLabel("Receipts");
        box.Add("m1", "a@example.com", receipts);

        var asked = await tools.DeleteFolder("Receipts", "personal");
        Assert.Contains("takes the label off those messages — they stay in All Mail", asked);
        Assert.Contains("Nothing has been deleted", asked);
        Assert.True(box.Labels.ContainsKey(receipts));

        var done = await tools.DeleteFolder("Receipts", "personal", confirm: true);
        Assert.Contains("Its 1 message is still in All Mail — only the label is gone.", done);
        Assert.True(box.Messages.ContainsKey("m1"));
    }

    [Fact]
    public async Task Making_a_label_inside_the_inbox_says_where_it_actually_went()
    {
        var (tools, box) = Tools();

        var said = await tools.CreateFolder("Later", "personal", parent: "inbox");

        Assert.StartsWith("Made a label called Later at the top level (Gmail labels can't go inside the Inbox)", said);
        Assert.Contains("Later", box.Labels.Values);
    }

    [Fact]
    public async Task Categories_are_listed_as_labels_without_outlook_colours()
    {
        var (tools, box) = Tools();
        box.AddLabel("Receipts");

        var said = await tools.ListCategories("personal");
        Assert.Equal("1 label on personal: Receipts.", said);
    }

    [Fact]
    public async Task Categorising_says_labelled_and_staying_put()
    {
        var (tools, box) = Tools();
        box.AddLabel("Urgent");
        box.Add("m1", "a@example.com", "INBOX");

        var said = await tools.CategoriseMail(await Handle(tools), "Urgent");

        Assert.Contains("is now labelled Urgent in personal, and stays where it was", said);
        Assert.Contains("INBOX", box.Messages["m1"].Labels);
    }

    // ── and Outlook's words are unchanged ────────────────────────────────────

    [Fact]
    public void Outlook_keeps_its_own_words()
    {
        Assert.Equal("Deleted Items", MailTools.Spoken("trash", ProviderKind.Microsoft));
        Assert.Equal("Trash", MailTools.Spoken("trash", ProviderKind.Google));
        Assert.Equal("Moved \"x\" to Receipts in work.",
            MailTools.Moved("x", null, "Receipts", "work", ProviderKind.Microsoft));

        var folder = new MailFolder { Id = "f", Name = "R", Path = "R", MessageCount = 3 };
        Assert.Contains("Deleting it sends all of that to Deleted Items", MailTools.Refuse(folder, false)!);
        Assert.Contains("they stay in All Mail", MailTools.Refuse(folder, false, ProviderKind.Google)!);
    }
}
