using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Teams;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.Teams;

namespace OutPost2.Tests.Teams;

/// <summary>
/// OP2-79, driven through the real service against canned Graph JSON.
///
/// <para>
/// Every trap in this feature is in the SHAPE of what Graph sends back, not in the
/// logic sitting on top of it — a system notice that is not called what the docs say it
/// is called, a sender that is an app rather than a person, a chat with no name. None of
/// that is reachable by testing a record in isolation, so these go through
/// <see cref="TeamsService"/> itself with the HTTP boundary stubbed.
/// </para>
/// </summary>
public class TeamsParsingTests
{
    private const string MyOid = "8ea0e38b-efb3-4757-924a-5f94061cf8c2";
    private const string TheirOid = "d74fc2ed-cb0e-4288-a219-b5c71abaf2aa";

    private static Connection Conn() => new()
    {
        UserId = Guid.NewGuid(),
        Label = "work",
        Provider = ProviderKind.Microsoft,
        Instance = "7b444cd6-6fa0-4c40-8fea-89438daf5de5",
        AuthKind = AuthKind.OAuth2,
        SealedCredential = "{}",

        // <oid>.<tid> — the service splits this to learn who "me" is, and the object id
        // is what chat membership carries. Getting this wrong makes the user appear as a
        // participant in their own one-to-one chats.
        HomeAccountId = $"{MyOid}.7b444cd6-6fa0-4c40-8fea-89438daf5de5",
        AccountIdentifier = "scott@example.com",
    };

    private static TeamsService Service(params (string UrlContains, string Json)[] routes) =>
        new(new StubTokens(), new StubFactory(routes), NullLogger<TeamsService>.Instance);

    // ── system notices ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_system_notice_is_dropped_under_BOTH_names_Graph_gives_it()
    {
        // The trap this whole test file exists for. WITHOUT the
        // "Prefer: include-unknown-enum-members" header Graph reports a join/rename notice
        // as messageType "unknownFutureValue"; WITH it, as "systemEventMessage". Filtering
        // on the documented name alone lets every one of them through in the default case,
        // and each reads aloud as a blank message from nobody.
        var messages = Page($$"""
            {{Msg("1", "a real message", TheirOid, "Darren Jones")}},
            {
              "id": "2", "messageType": "unknownFutureValue",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "from": null,
              "body": { "contentType": "html", "content": "<systemEventMessage/>" }
            },
            {
              "id": "3", "messageType": "systemEventMessage",
              "createdDateTime": "2026-10-28T09:01:00Z",
              "from": null,
              "body": { "contentType": "html", "content": "<systemEventMessage/>" }
            }
            """);

        var svc = Service(("/messages", messages), ("chats/", OneOnOneChat()));
        var t = await svc.ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.Single(t.Messages);
        Assert.Equal("a real message", t.Messages[0].Text);
        Assert.Equal(2, t.SystemNoticesSkipped);
    }

    [Fact]
    public async Task A_chat_that_is_only_system_notices_says_so_rather_than_reading_as_silence()
    {
        // "Nothing said yet" and "I found no messages" are different answers, and the
        // second one invites the user to wonder whether the tool is broken.
        var messages = Page("""
            {
              "id": "2", "messageType": "unknownFutureValue",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "from": null,
              "body": { "contentType": "html", "content": "<systemEventMessage/>" }
            }
            """);

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.Empty(t.Messages);
        Assert.Equal(1, t.SystemNoticesSkipped);
    }

    [Fact]
    public async Task A_bot_message_is_kept_and_labelled_rather_than_filtered_with_the_notices()
    {
        // A bot has from.user == null, which is one field away from the system-notice
        // shape. Dropping it would silently hide build failures and alerts — exactly the
        // messages somebody driving might want read out.
        var messages = Page("""
            {
              "id": "1", "messageType": "message",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "from": { "user": null, "application": { "id": "bot-1", "displayName": "Azure Pipelines" } },
              "body": { "contentType": "text", "content": "Build 412 failed" }
            }
            """);

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        var only = Assert.Single(t.Messages);
        Assert.True(only.FromBot);
        Assert.Equal("Azure Pipelines", only.From);
        Assert.Equal("Build 412 failed", only.Text);
    }

    [Fact]
    public async Task A_deleted_message_is_dropped_rather_than_read_out_empty()
    {
        var messages = Page("""
            {
              "id": "1", "messageType": "message",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "deletedDateTime": "2026-10-28T09:05:00Z",
              "from": { "user": { "id": "x", "displayName": "Darren Jones" } },
              "body": { "contentType": "text", "content": "" }
            }
            """);

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.Empty(t.Messages);
    }

    // ── bodies ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_plain_text_body_is_not_put_through_the_html_converter()
    {
        // Teams sends contentType text and html per message, in the same conversation.
        // Running plain text through the tag stripper eats everything from a "<" onwards,
        // so "if a < b then" silently becomes "if a".
        var messages = Page("""
            {
              "id": "1", "messageType": "message",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "from": { "user": { "id": "x", "displayName": "Darren Jones" } },
              "body": { "contentType": "text", "content": "check if a < b then bail" }
            }
            """);

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.Equal("check if a < b then bail", t.Messages[0].Text);
    }

    [Fact]
    public async Task An_image_only_message_says_so_instead_of_reading_as_a_blank_line()
    {
        // A screenshot pasted into Teams is an <img> and nothing else. Converted to text
        // it is the empty string, which under somebody's name reads as though they sent
        // nothing at all.
        var messages = Page("""
            {
              "id": "1", "messageType": "message",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "from": { "user": { "id": "x", "displayName": "Darren Jones" } },
              "body": { "contentType": "html", "content": "<div><img src=\"https://graph.microsoft.com/x/hostedContents/y/$value\" width=\"400\" height=\"300\"></div>" }
            }
            """);

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.Equal("(sent an image)", Assert.Single(t.Messages).Text);
    }

    [Fact]
    public async Task A_mention_keeps_the_name_it_wraps()
    {
        // Teams writes mentions as <at id="0">Scott</at>. The generic tag stripper keeps
        // the text between the tags, so the name survives — pinned because a mention
        // losing its name turns "can @Scott look at this" into "can look at this".
        var messages = Page("""
            {
              "id": "1", "messageType": "message",
              "createdDateTime": "2026-10-28T09:00:00Z",
              "from": { "user": { "id": "x", "displayName": "Darren Jones" } },
              "body": { "contentType": "html", "content": "<div>can <at id=\"0\">Scott</at> look at this</div>" }
            }
            """);

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.Contains("Scott", t.Messages[0].Text);
    }

    // ── ordering ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_window_keeps_the_NEWEST_messages_and_reads_them_oldest_first()
    {
        // Graph only sorts descending, so the newest arrive first. Both halves matter:
        // trimming before the reverse keeps the recent end of the conversation, and
        // reversing makes it readable. Trimming after reversing would return the OLDEST
        // messages — a plausible-looking answer to a completely different question.
        var messages = Page(string.Join(",\n",
            Msg("5", "newest", TheirOid, "Darren Jones", "2026-10-28T13:00:00Z"),
            Msg("4", "fourth", TheirOid, "Darren Jones", "2026-10-28T12:00:00Z"),
            Msg("3", "third", TheirOid, "Darren Jones", "2026-10-28T11:00:00Z"),
            Msg("2", "second", TheirOid, "Darren Jones", "2026-10-28T10:00:00Z"),
            Msg("1", "oldest", TheirOid, "Darren Jones", "2026-10-28T09:00:00Z")));

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2", count: 2);

        Assert.Equal(["fourth", "newest"], t.Messages.Select(m => m.Text));
        Assert.True(t.MoreBefore);
    }

    // ── identity ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_one_to_one_chat_is_named_after_the_other_person()
    {
        // Graph returns topic: null for every one-to-one chat, so without subtracting the
        // signed-in user from the membership there is no name to say at all.
        var chats = await Service(("me/chats", Page(OneOnOneChatBody()))).ListChatsAsync(Conn());

        var chat = Assert.Single(chats);
        Assert.Null(chat.Topic);
        Assert.Equal("Darren Jones", chat.DisplayName);
        Assert.Equal(ChatKind.OneOnOne, chat.Kind);
    }

    [Fact]
    public async Task Self_is_matched_on_the_object_id_not_on_an_email_address()
    {
        // A member's email is frequently null, and a UPN is not always the mailbox
        // address. The object id out of HomeAccountId is the only field that matches
        // reliably — and when it does not match, the user appears in their own chat's
        // name, which is how this was noticed.
        var body = $$"""
            {
              "id": "19:abc@unq.gbl.spaces", "topic": null, "chatType": "oneOnOne",
              "lastUpdatedDateTime": "2026-10-28T09:00:00Z",
              "members": [
                { "displayName": "Scott Singleton", "userId": "{{MyOid}}", "email": null },
                { "displayName": "Darren Jones", "userId": "{{TheirOid}}", "email": null }
              ]
            }
            """;

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());

        Assert.Equal("Darren Jones", chats[0].DisplayName);
        Assert.DoesNotContain("Scott Singleton", chats[0].Others);
    }

    [Fact]
    public async Task A_message_the_user_sent_is_marked_as_theirs()
    {
        var messages = Page(Msg("1", "on my way", MyOid, "Scott Singleton"));

        var t = await Service(("/messages", messages), ("chats/", OneOnOneChat()))
            .ReadChatAsync(Conn(), "19:abc@thread.v2");

        Assert.True(t.Messages[0].FromMe);
    }

    // ── unread, and the absence of an answer ─────────────────────────────────

    [Fact]
    public async Task Unread_is_true_when_the_last_message_postdates_the_last_read()
    {
        var body = ChatWithPreview(
            lastMessageAt: "2026-10-28T12:00:00Z", lastReadAt: "2026-10-28T11:00:00Z");

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());
        Assert.True(chats[0].IsUnread);
    }

    [Fact]
    public async Task Unread_is_NULL_rather_than_false_when_it_cannot_be_worked_out()
    {
        // The distinction is the point. "You have nothing waiting" is a claim the tool
        // would be making on no evidence; a chat with no viewpoint has to report that the
        // question could not be answered, not answer it reassuringly.
        var body = """
            {
              "id": "19:abc@unq.gbl.spaces", "topic": null, "chatType": "oneOnOne",
              "lastUpdatedDateTime": "2026-10-28T09:00:00Z",
              "lastMessagePreview": {
                "id": "9", "messageType": "message", "createdDateTime": "2026-10-28T09:00:00Z",
                "from": { "user": { "id": "x", "displayName": "Darren Jones" } },
                "body": { "contentType": "text", "content": "hello" }
              },
              "members": []
            }
            """;

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());
        Assert.Null(chats[0].IsUnread);
    }

    [Fact]
    public async Task A_chat_never_opened_counts_as_unread()
    {
        // Graph returns 0001-01-01 for lastMessageReadDateTime on a chat the user has
        // never opened. It is a sentinel rather than a date, and comparing against it
        // happens to give the right answer — pinned so nobody "fixes" it by discarding
        // the sentinel and turning a genuinely unread chat into a read one.
        var body = ChatWithPreview(
            lastMessageAt: "2026-10-28T12:00:00Z", lastReadAt: "0001-01-01T00:00:00Z");

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());
        Assert.True(chats[0].IsUnread);
    }

    [Fact]
    public async Task A_system_notice_preview_does_not_become_the_last_message_line()
    {
        // "X added Y to the chat" arrives as a preview with from: null, and rendering it
        // would put an attributionless blank under the chat's name in every listing.
        var body = """
            {
              "id": "19:abc@thread.v2", "topic": "Project Hood", "chatType": "group",
              "lastUpdatedDateTime": "2026-10-28T09:00:00Z",
              "lastMessagePreview": {
                "id": "9", "messageType": "unknownFutureValue",
                "createdDateTime": "2026-10-28T09:00:00Z",
                "from": null,
                "body": { "contentType": "html", "content": "<systemEventMessage/>" }
              },
              "members": []
            }
            """;

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());

        Assert.Null(chats[0].LastMessage);
        Assert.Null(chats[0].LastMessageFrom);
        Assert.Equal("Project Hood", chats[0].DisplayName);
    }

    // ── listing behaviour ────────────────────────────────────────────────────

    [Fact]
    public async Task A_chat_the_user_hid_in_Teams_stays_hidden()
    {
        // Hiding a chat is a decision the user already made. Reading it back out in a
        // spoken list undoes it, and they cannot see a checkbox to re-hide it from a car.
        var body = """
            {
              "id": "19:hidden@unq.gbl.spaces", "topic": null, "chatType": "oneOnOne",
              "lastUpdatedDateTime": "2026-10-28T09:00:00Z",
              "viewpoint": { "isHidden": true, "lastMessageReadDateTime": "2026-10-28T09:00:00Z" },
              "members": []
            }
            """;

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());
        Assert.Empty(chats);
    }

    [Fact]
    public async Task An_unnamed_group_chat_is_described_by_who_is_in_it()
    {
        // A group chat with no topic and no description reads as "a chat", which is
        // useless when there are eleven of them.
        var body = """
            {
              "id": "19:grp@thread.v2", "topic": null, "chatType": "group",
              "lastUpdatedDateTime": "2026-10-28T09:00:00Z",
              "members": [
                { "displayName": "Darren Jones", "userId": "a", "email": null },
                { "displayName": "Priya Shah", "userId": "b", "email": null },
                { "displayName": "Tom Reed", "userId": "c", "email": null },
                { "displayName": "Mia Fox", "userId": "d", "email": null }
              ]
            }
            """;

        var chats = await Service(("me/chats", Page(body))).ListChatsAsync(Conn());
        Assert.Equal("Darren Jones, Priya Shah and 2 others", chats[0].DisplayName);
    }

    [Fact]
    public async Task Finding_a_chat_by_name_prefers_the_direct_one_over_a_group()
    {
        // "Message Darren" means Darren, not the six-person thread he happens to be in.
        var body = string.Join(",\n", """
            {
              "id": "19:grp@thread.v2", "topic": "Project Hood", "chatType": "group",
              "lastUpdatedDateTime": "2026-10-28T13:00:00Z",
              "members": [ { "displayName": "Darren Jones", "userId": "a", "email": null } ]
            }
            """, OneOnOneChatBody());

        var matches = await Service(("me/chats", Page(body)))
            .FindChatsWithAsync(Conn(), "Darren");

        Assert.Equal(2, matches.Count);
        Assert.Equal(ChatKind.OneOnOne, matches[0].Kind);
    }

    // ── failures ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_403_asks_for_a_reconnection_and_a_404_does_not()
    {
        // Two different fixes. Telling somebody to reconnect because a chat was deleted
        // sends them to a browser to solve a problem that is not there.
        var forbidden = Service(("chats/", (HttpStatusCode.Forbidden, "{}")));
        var ex403 = await Assert.ThrowsAsync<TeamsException>(
            () => forbidden.ReadChatAsync(Conn(), "19:abc@thread.v2"));
        Assert.Contains("reconnect", ex403.Message, StringComparison.OrdinalIgnoreCase);

        var missing = Service(("chats/", (HttpStatusCode.NotFound, "{}")));
        var ex404 = await Assert.ThrowsAsync<TeamsException>(
            () => missing.ReadChatAsync(Conn(), "19:abc@thread.v2"));
        Assert.DoesNotContain("reconnect", ex404.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no longer", ex404.Message);
    }

    [Fact]
    public async Task Sending_reads_the_chat_first_so_the_confirmation_can_name_it()
    {
        // "Sent." is not a confirmation when the caller could not see which conversation
        // was chosen — and for a name-resolved send, that choice was ours to get wrong.
        var svc = Service(("/messages", Page("")), ("chats/", OneOnOneChat()));

        var chat = await svc.SendAsync(Conn(), "19:abc@unq.gbl.spaces", "on my way");
        Assert.Equal("Darren Jones", chat.DisplayName);
    }

    // ── canned payloads ──────────────────────────────────────────────────────

    private static string Page(string inner) => $$"""{ "value": [ {{inner}} ] }""";

    private static string Msg(
        string id, string text, string fromOid, string fromName, string at = "2026-10-28T09:00:00Z") => $$"""
        {
          "id": "{{id}}", "messageType": "message",
          "createdDateTime": "{{at}}",
          "from": { "user": { "id": "{{fromOid}}", "displayName": "{{fromName}}" } },
          "body": { "contentType": "text", "content": "{{text}}" }
        }
        """;

    private static string OneOnOneChatBody() => $$"""
        {
          "id": "19:abc@unq.gbl.spaces", "topic": null, "chatType": "oneOnOne",
          "lastUpdatedDateTime": "2026-10-28T09:00:00Z",
          "viewpoint": { "isHidden": false, "lastMessageReadDateTime": "2026-10-28T09:00:00Z" },
          "members": [
            { "displayName": "Scott Singleton", "userId": "{{MyOid}}", "email": "scott@example.com" },
            { "displayName": "Darren Jones", "userId": "{{TheirOid}}", "email": "darren@example.com" }
          ]
        }
        """;

    /// <summary>A single chat, as GET /chats/{id} returns it — not wrapped in a value array.</summary>
    private static string OneOnOneChat() => OneOnOneChatBody();

    private static string ChatWithPreview(string lastMessageAt, string lastReadAt) => $$"""
        {
          "id": "19:abc@unq.gbl.spaces", "topic": null, "chatType": "oneOnOne",
          "lastUpdatedDateTime": "{{lastMessageAt}}",
          "viewpoint": { "isHidden": false, "lastMessageReadDateTime": "{{lastReadAt}}" },
          "lastMessagePreview": {
            "id": "9", "messageType": "message", "createdDateTime": "{{lastMessageAt}}",
            "from": { "user": { "id": "{{TheirOid}}", "displayName": "Darren Jones" } },
            "body": { "contentType": "text", "content": "are you around?" }
          },
          "members": [
            { "displayName": "Scott Singleton", "userId": "{{MyOid}}", "email": null },
            { "displayName": "Darren Jones", "userId": "{{TheirOid}}", "email": null }
          ]
        }
        """;

    // ── the HTTP boundary ────────────────────────────────────────────────────

    private sealed class StubTokens : IMicrosoftConnectionServiceAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default) =>
            Task.FromResult<string?>("stub-token");
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly List<(string Match, HttpStatusCode Status, string Json)> _routes = [];

        public StubFactory((string UrlContains, string Json)[] routes)
        {
            foreach (var (match, json) in routes) _routes.Add((match, HttpStatusCode.OK, json));
        }

        public StubFactory((string UrlContains, (HttpStatusCode, string) Response)[] routes)
        {
            foreach (var (match, (status, json)) in routes) _routes.Add((match, status, json));
        }

        public HttpClient CreateClient(string name) => new(new Handler(_routes));
    }

    private sealed class Handler(List<(string Match, HttpStatusCode Status, string Json)> routes)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            // Longest match wins, so a "/messages" route is not shadowed by a "chats/" one
            // — both appear in the same URL and the order they were registered in should
            // not decide which answers.
            var route = routes
                .Where(r => url.Contains(r.Match, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Match.Length)
                .FirstOrDefault();

            if (route.Match is null)
                throw new InvalidOperationException($"No stubbed route for {url}");

            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static TeamsService Service(params (string UrlContains, (HttpStatusCode, string) Response)[] routes) =>
        new(new StubTokens(), new StubFactory(routes), NullLogger<TeamsService>.Instance);
}
