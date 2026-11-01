using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Reaching the second page of a search (OP2-208).
/// <para>
/// Graph settles the shape, not taste. Asked live, it answers <c>400 "The query parameter
/// '$skip' is not supported with '$search'"</c> and the same for <c>$filter</c> — so an
/// offset and a before-this-date cut-off, the two things the ticket asked for, cannot be
/// built. An opaque <c>@odata.nextLink</c> is all there is, which is why this exists.
/// </para>
/// <para>
/// The link is held here rather than handed out: it is a URL we would fetch with the user's
/// access token, and the model that would carry it back spends its day reading mail written
/// by strangers.
/// </para>
/// </summary>
public sealed class SearchContinuationTests
{
    private static readonly Guid Scott = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();

    private static Continuation Page(string query = "invoice", int count = 50) => new()
    {
        Query = query,
        Count = count,
        Next = new Dictionary<string, string>
        {
            ["work"] = "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=abc",
        },
    };

    [Fact]
    public void A_token_brings_back_the_search_it_came_from()
    {
        // The query, folder and count ride along so the caller does not restate them —
        // two statements of the same search are two things that can disagree.
        var store = new SearchContinuations();

        var token = store.Remember(Scott, new Continuation
        {
            Query = "from accounts", Folder = "inbox", Count = 200,
            Next = new Dictionary<string, string> { ["work"] = "https://graph.microsoft.com/v1.0/x" },
        });

        var back = store.Resolve(Scott, token);

        Assert.NotNull(back);
        Assert.Equal("from accounts", back.Query);
        Assert.Equal("inbox", back.Folder);
        Assert.Equal(200, back.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/x", back.Next["work"]);
    }

    [Fact]
    public void A_token_is_short_enough_to_be_said_out_loud()
    {
        var token = new SearchContinuations().Remember(Scott, Page());

        Assert.Equal(4, token.Length);

        // Same alphabet as a message handle, for the same reason: it is read back by a
        // model working from a transcript, and i/l/o/0/1 are the pairs that get confused.
        Assert.DoesNotContain(token, "ilo01");
    }

    [Fact]
    public void A_page_is_good_for_one_use()
    {
        // Serving it twice would hand back the same messages under a token that reads like
        // progress — which is the exact failure OP2-208 reported about re-running a search.
        var store = new SearchContinuations();
        var token = store.Remember(Scott, Page());

        Assert.NotNull(store.Resolve(Scott, token));
        Assert.Null(store.Resolve(Scott, token));
        Assert.True(store.Forgot(Scott, token));
    }

    [Fact]
    public void A_used_token_is_reported_as_used_rather_than_unknown()
    {
        var store = new SearchContinuations();
        var token = store.Remember(Scott, Page());
        store.Resolve(Scott, token);

        Assert.True(store.Forgot(Scott, token));
        Assert.False(store.Forgot(Scott, "zzzz"));
    }

    [Fact]
    public void Another_users_token_is_invisible()
    {
        var store = new SearchContinuations();
        var token = store.Remember(Scott, Page());

        Assert.Null(store.Resolve(Someone, token));
        Assert.False(store.Forgot(Someone, token));
    }

    [Fact]
    public void Nothing_resolves_before_a_search()
    {
        Assert.Null(new SearchContinuations().Resolve(Scott, "abcd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void Nonsense_resolves_to_nothing_rather_than_throwing(string said)
    {
        var store = new SearchContinuations();
        store.Remember(Scott, Page());

        Assert.Null(store.Resolve(Scott, said));
        Assert.False(store.Forgot(Scott, said));
    }

    [Fact]
    public void Decoration_around_a_token_is_ignored()
    {
        // The model quotes things back as "page [k3m9]" or "page 'k3m9'".
        var store = new SearchContinuations();
        var token = store.Remember(Scott, Page());

        Assert.NotNull(store.Resolve(Scott, $" [{token}] "));
    }

    [Fact]
    public void Old_tokens_are_dropped_and_known_to_have_been_dropped()
    {
        // Bounded, like the handle index — but unlike handles, only the newest matter: a
        // search is resumed within a turn or two, or not at all.
        var store = new SearchContinuations();

        var first = store.Remember(Scott, Page("the first search"));
        for (var i = 0; i < 25; i++) store.Remember(Scott, Page($"search {i}"));

        Assert.Null(store.Resolve(Scott, first));
        Assert.True(store.Forgot(Scott, first));
    }

    [Fact]
    public void Each_search_gets_its_own_token()
    {
        var store = new SearchContinuations();

        var tokens = Enumerable.Range(0, 20)
            .Select(i => store.Remember(Scott, Page($"search {i}")))
            .ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Only_the_mailboxes_with_more_to_give_are_carried_forward()
    {
        // A continuation that re-asked an exhausted mailbox would repeat its first page
        // under a token that claims to be the next one.
        var store = new SearchContinuations();

        var token = store.Remember(Scott, new Continuation
        {
            Query = "invoice", Count = 50,
            Next = new Dictionary<string, string>
            {
                ["work"] = "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=w",
            },
        });

        var back = store.Resolve(Scott, token);

        Assert.NotNull(back);
        Assert.True(back.Next.ContainsKey("work"));
        Assert.False(back.Next.ContainsKey("home"));
    }
}
