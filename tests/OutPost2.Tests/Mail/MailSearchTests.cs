using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Searching a mailbox: how far it reaches, and where the hits live (OP2-189, OP2-196).
/// <para>
/// Both tickets came out of one bulk clean-up. A search that silently returned the first
/// fifty of two hundred made a delete-everything-from-this-sender pass report success with
/// most of the mail still in place; a search that could not say which folder a hit was in
/// made a sort-into-folders pass keep picking up its own finished work.
/// </para>
/// <para>
/// Driven through a stubbed transport, because what is under test is the conversation with
/// Graph — how many pages were asked for, with which query, and whether the headers
/// <c>$search</c> requires went on every one of them.
/// </para>
/// </summary>
public class MailSearchTests
{
    private static readonly Connection Work = new()
    {
        UserId = Guid.Empty, Label = "work", Provider = ProviderKind.Microsoft,
        Instance = "tenant", AuthKind = AuthKind.OAuth2, SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private sealed record Call(string Url, bool HadConsistencyLevel);

    private sealed class Transport(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add(new Call(r.RequestUri!.AbsoluteUri,
                r.Headers.TryGetValues("ConsistencyLevel", out var v) && v.Contains("eventual")));

            return Task.FromResult(respond(Calls.Count - 1));
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

    private static HttpResponseMessage Page(int count, string? next, int from = 0, string folder = "AAA") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  {{(next is null ? "" : $"\"@odata.nextLink\": \"{next}\",")}}
                  "value": [
                    {{string.Join(",", Enumerable.Range(from, count).Select(i => $$"""
                      {
                        "id": "m{{i}}",
                        "subject": "Message {{i}}",
                        "from": { "emailAddress": { "name": "Sender", "address": "s@example.com" } },
                        "receivedDateTime": "2026-10-{{(i % 28) + 1:00}}T09:00:00Z",
                        "isRead": false,
                        "hasAttachments": false,
                        "bodyPreview": "preview",
                        "parentFolderId": "{{folder}}"
                      }
                    """))}}
                  ]
                }
                """, Encoding.UTF8, "application/json"),
        };

    private static (MailService Mail, Transport Wire) Build(Func<int, HttpResponseMessage> respond)
    {
        var wire = new Transport(respond);
        return (new MailService(new Tokens(), new OneClient(wire), NullLogger<MailService>.Instance), wire);
    }

    // ── paging (OP2-189) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Asking_for_more_than_one_page_follows_graphs_next_link()
    {
        // The reported failure: count=200 returned 50 and there was no way to reach the
        // rest. Graph caps a search page and hands back a nextLink; it has to be followed.
        var (mail, wire) = Build(call => call switch
        {
            0 => Page(50, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2"),
            1 => Page(50, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p3", from: 50),
            _ => Page(20, null, from: 100),
        });

        var result = await mail.SearchAsync(Work, "invoice", top: 200);

        Assert.Equal(120, result.Items.Count);
        Assert.Equal(3, wire.Calls.Count);

        // Ran out of results before running out of appetite, so there is nothing more.
        Assert.False(result.More);
    }

    [Fact]
    public async Task Every_page_carries_the_header_that_search_requires()
    {
        // A nextLink carries the query but not the headers. Dropping ConsistencyLevel on
        // page two gets a 400 from Graph halfway through a clean-up.
        var (mail, wire) = Build(call => call == 0
            ? Page(50, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2")
            : Page(5, null, from: 50));

        await mail.SearchAsync(Work, "invoice", top: 100);

        Assert.Equal(2, wire.Calls.Count);
        Assert.All(wire.Calls, c => Assert.True(c.HadConsistencyLevel));
    }

    [Fact]
    public async Task It_stops_at_what_was_asked_for_and_says_there_is_more()
    {
        // The flag is the point: a bulk action told "that is all of them" when it is not
        // leaves mail behind and reports success.
        var (mail, _) = Build(_ => Page(50, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2"));

        var result = await mail.SearchAsync(Work, "invoice", top: 10);

        Assert.Equal(10, result.Items.Count);
        Assert.True(result.More);
    }

    [Fact]
    public async Task A_last_page_that_fills_the_request_exactly_still_reports_more_when_graph_offers_it()
    {
        var (mail, _) = Build(_ => Page(10, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2"));

        var result = await mail.SearchAsync(Work, "invoice", top: 10);

        Assert.Equal(10, result.Items.Count);
        Assert.True(result.More);
    }

    [Fact]
    public async Task A_single_short_page_is_the_whole_answer()
    {
        var (mail, wire) = Build(_ => Page(3, null));

        var result = await mail.SearchAsync(Work, "invoice", top: 50);

        Assert.Equal(3, result.Items.Count);
        Assert.False(result.More);
        Assert.Single(wire.Calls);
    }

    [Fact]
    public async Task The_cap_holds_however_much_is_asked_for()
    {
        // Every result becomes a handle held for the session, so this is bounded no matter
        // what the caller says.
        var (mail, _) = Build(_ => Page(100, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=next"));

        var result = await mail.SearchAsync(Work, "invoice", top: 99_999);

        Assert.Equal(MailService.MaxSearchResults, result.Items.Count);
        Assert.True(result.More);
    }

    [Fact]
    public async Task A_page_size_is_never_requested_above_what_graph_will_give()
    {
        var (mail, wire) = Build(_ => Page(1, null));

        await mail.SearchAsync(Work, "invoice", top: 200);

        // '$' is a legal sub-delimiter, so it survives into AbsoluteUri unescaped while the
        // quotes around the search phrase do not. Asserting the escaped form of both was
        // wrong about the URL, not about the code.
        Assert.Contains("$top=100", wire.Calls[0].Url);
    }

    [Fact]
    public async Task A_service_that_offers_pages_for_ever_does_not_hang_the_call()
    {
        // An empty page carrying a nextLink makes no progress, so the walk is bounded by a
        // page count as well as by the result count. Unbounded, this is a tool call that
        // never returns — in a car, with no way to cancel it.
        var (mail, wire) = Build(_ => Page(0, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=again"));

        var result = await mail.SearchAsync(Work, "invoice", top: 200);

        Assert.Empty(result.Items);
        Assert.InRange(wire.Calls.Count, 1, 25);

        // And it still admits it did not finish, rather than reporting "no matches".
        Assert.True(result.More);
    }

    // ── carrying on (OP2-208) ────────────────────────────────────────────────

    [Fact]
    public async Task Stopping_short_hands_back_the_link_that_carries_on()
    {
        // Counting the rest is not reaching them. Without this, re-running the same search
        // returns the same first page for ever.
        var (mail, _) = Build(_ => Page(50, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2"));

        var result = await mail.SearchAsync(Work, "invoice", top: 10);

        Assert.True(result.More);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2", result.Continuation);
    }

    [Fact]
    public async Task Finishing_the_results_hands_back_nothing_to_carry_on_with()
    {
        var (mail, _) = Build(_ => Page(3, null));

        var result = await mail.SearchAsync(Work, "invoice", top: 50);

        Assert.False(result.More);
        Assert.Null(result.Continuation);
    }

    [Fact]
    public async Task A_continuation_is_followed_instead_of_building_a_new_query()
    {
        var (mail, wire) = Build(_ => Page(5, null, from: 50));

        var result = await mail.SearchAsync(
            Work, "ignored", top: 50,
            continueFrom: "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2");

        Assert.Equal(5, result.Items.Count);

        // The link carries the query, so none is rebuilt — and "ignored" never appears.
        Assert.Single(wire.Calls);
        Assert.Contains("$skiptoken=p2", wire.Calls[0].Url);
        Assert.DoesNotContain("ignored", wire.Calls[0].Url);

        // And it is still a search, so it still needs the header.
        Assert.True(wire.Calls[0].HadConsistencyLevel);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/me/messages")]
    [InlineData("http://graph.microsoft.com/v1.0/me/messages")]
    [InlineData("/me/messages?$skiptoken=p2")]
    public async Task A_continuation_that_is_not_ours_is_refused_rather_than_fetched(string url)
    {
        // These links reach this method from a store, but the model that carries the token
        // around reads mail bodies written by strangers all day. Whatever the route, a
        // request made with the user's access token goes to Graph or nowhere.
        var (mail, wire) = Build(_ => Page(1, null));

        await Assert.ThrowsAsync<MailException>(
            () => mail.SearchAsync(Work, "invoice", top: 10, continueFrom: url));

        Assert.Empty(wire.Calls);
    }

    // ── scope (OP2-196) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Unscoped_search_covers_the_whole_mailbox()
    {
        var (mail, wire) = Build(_ => Page(1, null));

        await mail.SearchAsync(Work, "invoice", top: 10);

        Assert.Contains("/me/messages?", wire.Calls[0].Url);
        Assert.DoesNotContain("mailFolders", wire.Calls[0].Url);
    }

    [Theory]
    [InlineData("inbox")]
    [InlineData("archive")]
    [InlineData("AAMkADk0_long_folder_id==")]
    public async Task A_scoped_search_asks_only_that_folder(string folderId)
    {
        // Without this, mail already filed came back as a hit and the sorting pass could
        // not tell it from mail still in the Inbox.
        var (mail, wire) = Build(_ => Page(1, null));

        await mail.SearchAsync(Work, "invoice", top: 10, folderId: folderId);

        Assert.Contains("/me/mailFolders/", wire.Calls[0].Url);
        Assert.Contains("/messages?", wire.Calls[0].Url);
    }

    [Fact]
    public async Task A_folder_id_is_escaped_rather_than_pasted_into_the_url()
    {
        // Graph folder ids carry '=' and '/' and arrive from the provider, not the user.
        var (mail, wire) = Build(_ => Page(1, null));

        await mail.SearchAsync(Work, "invoice", top: 10, folderId: "AAA/BBB==");

        Assert.Contains("AAA%2FBBB%3D%3D", wire.Calls[0].Url);
    }

    [Fact]
    public async Task Each_hit_carries_the_folder_it_is_in()
    {
        var (mail, _) = Build(_ => Page(2, null, folder: "folder-7"));

        var result = await mail.SearchAsync(Work, "invoice", top: 10);

        Assert.All(result.Items, m => Assert.Equal("folder-7", m.FolderId));
    }

    [Fact]
    public async Task The_query_is_escaped_and_quoted()
    {
        var (mail, wire) = Build(_ => Page(1, null));

        await mail.SearchAsync(Work, "from a sender & co", top: 10);

        // Quoted, so a multi-word search is one phrase rather than Graph's own operators,
        // and escaped, so an ampersand cannot start a new query parameter.
        Assert.Contains("$search=%22from%20a%20sender%20%26%20co%22", wire.Calls[0].Url);
    }

    // ── failure ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_mailbox_that_needs_reauthorising_says_so_rather_than_throwing_a_bare_http_error()
    {
        // This path used to call EnsureSuccessStatusCode, which threw HttpRequestException
        // and so skipped the "needs re-authorising" handling every other call here has.
        // The search tool catches MailAuthRequiredException by name.
        var (mail, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"token expired"}}"""),
        });

        await Assert.ThrowsAsync<MailAuthRequiredException>(
            () => mail.SearchAsync(Work, "invoice", top: 10));
    }

    [Fact]
    public async Task A_refusal_names_the_mailbox_and_what_was_being_done()
    {
        var (mail, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"The query is not supported."}}"""),
        });

        var ex = await Assert.ThrowsAsync<MailException>(
            () => mail.SearchAsync(Work, "invoice", top: 10));

        Assert.Contains("work", ex.Message);
        Assert.Contains("search", ex.Message);
        Assert.Contains("The query is not supported", ex.Message);
    }

    [Fact]
    public async Task A_failure_on_the_second_page_does_not_silently_return_the_first()
    {
        // Returning page one and swallowing the error would be the same lie OP2-189 is
        // about, with a worse cause.
        var (mail, _) = Build(call => call == 0
            ? Page(50, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=p2")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":{"message":"throttled"}}"""),
            });

        await Assert.ThrowsAsync<MailException>(() => mail.SearchAsync(Work, "invoice", top: 200));
    }
}
