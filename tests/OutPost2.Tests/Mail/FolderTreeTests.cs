using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Walking the whole folder tree (OP2-213).
/// <para>
/// One symptom — a folder visible in Outlook, already filed into earlier in the same
/// session, refused as nonexistent — and two causes underneath it, each of which looks like
/// the whole story on its own:
/// </para>
/// <list type="number">
/// <item>the expanded <c>childFolders</c> collection stops at <b>ten</b> and offers no
/// nextLink to carry on with, so the eleventh child of a folder is invisible. The mailbox
/// that reported this has an Inbox with eleven children and the eleventh was the folder;</item>
/// <item>the expand reaches one level down, so another mailbox here had 87 of its 112
/// folders below the horizon.</item>
/// </list>
/// <para>
/// Driven through a stubbed transport, because what is under test is the conversation with
/// Graph: which folders got asked about, and whether the walk knew what it was still missing.
/// </para>
/// </summary>
public class FolderTreeTests
{
    private static readonly Connection Work = new()
    {
        UserId = Guid.Empty, Label = "work", Provider = ProviderKind.Microsoft,
        Instance = "tenant", AuthKind = AuthKind.OAuth2, SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private sealed class Transport(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Tokens : IMicrosoftConnectionServiceAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection c, CancellationToken ct = default) =>
            Task.FromResult<string?>("token");
    }

    /// <summary>A folder as Graph writes one, with its children inline when asked to expand.</summary>
    private static string Folder(
        string id, string name, int childCount = 0, string? wellKnown = null,
        IEnumerable<string>? inline = null, int messages = 0)
        => $$"""
            {
              "id": "{{id}}",
              "displayName": "{{name}}",
              {{(wellKnown is null ? "" : $"\"wellKnownName\": \"{wellKnown}\",")}}
              "totalItemCount": {{messages}},
              "childFolderCount": {{childCount}}
              {{(inline is null ? "" : ", \"childFolders\": [" + string.Join(",", inline) + "]")}}
            }
            """;

    private static HttpResponseMessage Page(string? next, params string[] folders) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  {{(next is null ? "" : $"\"@odata.nextLink\": \"{next}\",")}}
                  "value": [{{string.Join(",", folders)}}]
                }
                """),
        };

    private static (MailService Service, Transport Wire) Service(Func<string, HttpResponseMessage> respond)
    {
        var wire = new Transport(respond);
        return (new MailService(new Tokens(), new OneClient(wire), NullLogger<MailService>.Instance), wire);
    }

    [Fact]
    public async Task The_eleventh_child_of_a_folder_is_still_found()
    {
        // The whole of OP2-213 as reported: an Inbox that says it has eleven children, an
        // expand that hands back ten of them, and no nextLink to carry on with. The
        // eleventh was "Subscriptions", which had 42 messages in it and had been filed into
        // by this very tool earlier in the session.
        var ten = Enumerable.Range(1, 10).Select(i => Folder($"c{i}", $"Child {i}")).ToList();

        var (mail, wire) = Service(url =>
            url.Contains("/childFolders")
                ? Page(null, Enumerable.Range(1, 11)
                    .Select(i => Folder($"c{i}", i == 11 ? "Subscriptions" : $"Child {i}")).ToArray())
                : Page(null, Folder("inbox", "Inbox", childCount: 11, wellKnown: "inbox", inline: ten)));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Contains(folders, f => f.Path == "Inbox/Subscriptions");

        // And it asked the only way that could have found it: the folder's own collection,
        // not the expand it had already been given.
        Assert.Contains(wire.Urls, u => u.Contains("mailFolders/inbox/childFolders"));
    }

    [Fact]
    public async Task A_folder_that_gave_all_its_children_is_not_asked_again()
    {
        // The expand is kept precisely because it saves this round trip on a tidy mailbox.
        // If the walk re-asked regardless, every mailbox would pay for the worst one.
        var (mail, wire) = Service(_ => Page(null,
            Folder("inbox", "Inbox", childCount: 2, wellKnown: "inbox",
                inline: [Folder("a", "Receipts"), Folder("b", "Travel")])));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Equal(3, folders.Count);
        Assert.Single(wire.Urls);
    }

    [Fact]
    public async Task The_walk_goes_deeper_than_two_levels()
    {
        // The other half of OP2-213. A mailbox here keeps 84 folders under one of them, and
        // before this nothing below the second level could be named at all.
        var (mail, _) = Service(url =>
            url.Contains("mailFolders/l2/childFolders") ? Page(null, Folder("l3", "Third", childCount: 1))
            : url.Contains("mailFolders/l3/childFolders") ? Page(null, Folder("l4", "Fourth"))
            : Page(null, Folder("l1", "First", childCount: 1,
                inline: [Folder("l2", "Second", childCount: 1)])));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Equal(
            ["First", "First/Second", "First/Second/Third", "First/Second/Third/Fourth"],
            folders.Select(f => f.Path));
    }

    [Fact]
    public async Task A_folder_with_more_children_than_one_page_is_paged_through()
    {
        // The direct collection, unlike the expand, does hand back a nextLink — so the one
        // mailbox shape that most needs the walk is also the one it can serve.
        var (mail, wire) = Service(url =>
            url.Contains("page2") ? Page(null, Folder("b", "Second hundred"))
            : url.Contains("/childFolders") ? Page("https://graph.microsoft.com/v1.0/page2",
                Folder("a", "First hundred"))
            : Page(null, Folder("big", "Big", childCount: 2)));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Equal(["Big", "Big/First hundred", "Big/Second hundred"], folders.Select(f => f.Path));
        Assert.Contains(wire.Urls, u => u.Contains("page2"));
    }

    [Fact]
    public async Task The_top_level_is_paged_through_too()
    {
        var (mail, _) = Service(url =>
            url.Contains("page2") ? Page(null, Folder("b", "Later"))
            : Page("https://graph.microsoft.com/v1.0/page2", Folder("a", "Early")));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Equal(["Early", "Later"], folders.Select(f => f.Path));
    }

    [Fact]
    public async Task A_count_that_cannot_be_delivered_is_corrected_rather_than_believed()
    {
        // Graph's childFolderCount can simply be wrong: every mailbox here has a
        // Conversation History that claims one child and returns none, even with
        // includeHiddenFolders=true. Left alone, that would make the surface warn for ever
        // that it cannot see everything — and a standing false alarm is how a real one gets
        // ignored. So the walk records what was actually there.
        var (mail, _) = Service(url =>
            url.Contains("/childFolders") ? Page(null) : Page(null,
                Folder("ch", "Conversation History", childCount: 1)));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Equal(0, Assert.Single(folders).ChildCount);
    }

    [Fact]
    public async Task A_walk_cut_short_keeps_the_count_it_could_not_deliver()
    {
        // The other side of that correction, and the reason it is conditional. A folder the
        // budget never reached must keep Graph's number, because that number is the only
        // thing left saying the list is incomplete.
        var deep = Enumerable.Range(0, 200).ToList();

        var (mail, _) = Service(url =>
        {
            // Every folder has one child, for ever: the pathological tree the budget exists
            // for. Each answer names a fresh folder that itself claims a child.
            var n = url.Length;
            return url.Contains("/childFolders")
                ? Page(null, Folder($"d{n}", $"Deeper {n}", childCount: 1))
                : Page(null, Folder("d0", "Deep", childCount: 1));
        });

        var folders = await mail.ListFoldersAsync(Work);

        // It stopped, and the last folder it reached still says it has a child, which is
        // what MailTools.Deeper reads to admit the list is partial.
        Assert.InRange(folders.Count, 2, 200);
        Assert.Equal(1, folders[^1].ChildCount);
        Assert.NotEmpty(deep);
    }

    [Fact]
    public async Task A_folder_returned_twice_is_listed_once()
    {
        // Nothing in a tree should repeat. The budget is spent on the assumption that it
        // cannot, so the walk makes sure of it rather than trusting the shape.
        var (mail, _) = Service(url =>
            url.Contains("/childFolders")
                ? Page(null, Folder("dup", "Shared"), Folder("dup", "Shared"))
                : Page(null, Folder("root", "Root", childCount: 2)));

        var folders = await mail.ListFoldersAsync(Work);

        Assert.Equal(["Root", "Root/Shared"], folders.Select(f => f.Path));
    }

    [Fact]
    public async Task Message_counts_and_well_known_names_survive_the_walk()
    {
        // The delete gate reads IsWellKnown and MessageCount off these, so a walk that lost
        // them would let delete_folder take Outlook's own folders.
        var (mail, _) = Service(url =>
            url.Contains("/childFolders")
                ? Page(null, Folder("r", "Receipts", messages: 42))
                : Page(null, Folder("in", "Inbox", childCount: 1, wellKnown: "inbox", messages: 7)));

        var folders = await mail.ListFoldersAsync(Work);

        var inbox = folders.Single(f => f.Path == "Inbox");
        Assert.True(inbox.IsWellKnown);
        Assert.Equal(7, inbox.MessageCount);

        var receipts = folders.Single(f => f.Path == "Inbox/Receipts");
        Assert.False(receipts.IsWellKnown);
        Assert.Equal(42, receipts.MessageCount);
    }

    [Fact]
    public async Task The_expand_is_still_asked_for_on_the_first_request()
    {
        // Dropping it would cost a round trip per top-level folder on every mailbox, which
        // for a tool answering someone who is driving is the difference between one second
        // and ten.
        var (mail, wire) = Service(_ => Page(null, Folder("a", "Only")));

        await mail.ListFoldersAsync(Work);

        Assert.Contains("$expand=childFolders", wire.Urls[0]);
        Assert.Contains("$top=100", wire.Urls[0]);
    }
}
