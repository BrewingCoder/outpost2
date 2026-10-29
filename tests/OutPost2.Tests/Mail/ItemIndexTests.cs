using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Addressing messages for a conversation that has no screen (OP2-73).
/// <para>
/// The behaviour under test is not "can it look something up" — it is that a handle means
/// the same message after the inbox has moved underneath it, because the alternative is
/// deleting the wrong mail from a car.
/// </para>
/// </summary>
public sealed class ItemIndexTests
{
    private static readonly Guid Scott = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();

    private static (string, string, string, string) Msg(string id, string subject = "s", string from = "f") =>
        (id, "work", subject, from);

    [Fact]
    public void Ordinals_resolve_against_the_last_listing()
    {
        var index = new ItemIndex();
        index.Remember(Scott, [Msg("aaa"), Msg("bbb"), Msg("ccc")]);

        Assert.Equal("aaa", index.Resolve(Scott, "1")!.ItemId);
        Assert.Equal("bbb", index.Resolve(Scott, "2")!.ItemId);
        Assert.Equal("ccc", index.Resolve(Scott, "3")!.ItemId);
    }

    [Fact]
    public void Handles_resolve_to_their_own_message()
    {
        var index = new ItemIndex();
        var refs = index.Remember(Scott, [Msg("aaa"), Msg("bbb")]);

        Assert.Equal("aaa", index.Resolve(Scott, refs[0].Ref)!.ItemId);
        Assert.Equal("bbb", index.Resolve(Scott, refs[1].Ref)!.ItemId);
    }

    /// <summary>
    /// The bug the ticket was filed about. Two messages arrive between the listing and the
    /// action: the ordinal now points somewhere else entirely, the handle does not.
    /// </summary>
    [Fact]
    public void New_mail_shifts_the_ordinal_but_never_the_handle()
    {
        var index = new ItemIndex();

        var first = index.Remember(Scott, [Msg("old-1"), Msg("old-2")]);
        var target = first[1];
        Assert.Equal("old-2", index.Resolve(Scott, "2")!.ItemId);

        // Inbox refreshed; two newer messages at the top.
        index.Remember(Scott, [Msg("new-1"), Msg("new-2"), Msg("old-1"), Msg("old-2")]);

        Assert.Equal("new-2", index.Resolve(Scott, "2")!.ItemId);
        Assert.Equal("old-2", index.Resolve(Scott, target.Ref)!.ItemId);
    }

    [Fact]
    public void The_same_message_keeps_its_handle_across_listings()
    {
        var index = new ItemIndex();

        var before = index.Remember(Scott, [Msg("aaa"), Msg("bbb")]);
        var after = index.Remember(Scott, [Msg("zzz"), Msg("bbb"), Msg("aaa")]);

        Assert.Equal(before[0].Ref, after[2].Ref);
        Assert.Equal(before[1].Ref, after[1].Ref);
    }

    [Fact]
    public void Handles_are_distinct_within_a_listing()
    {
        var index = new ItemIndex();
        var refs = index.Remember(Scott, Enumerable.Range(0, 60).Select(i => Msg($"id-{i}")).ToList());

        Assert.Equal(60, refs.Select(r => r.Ref).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Mint_is_deterministic_and_avoids_collisions()
    {
        var empty = new Dictionary<string, ItemRef>();
        Assert.Equal(ItemIndex.Mint("abc", empty), ItemIndex.Mint("abc", empty));

        var first = ItemIndex.Mint("abc", empty);
        var taken = new Dictionary<string, ItemRef>(StringComparer.OrdinalIgnoreCase)
        {
            [first] = new() { Ref = first, ItemId = "other", ConnectionLabel = "work", Subject = "s", From = "f" },
        };

        Assert.NotEqual(first, ItemIndex.Mint("abc", taken));
    }

    [Theory]
    [InlineData("i")]
    [InlineData("l")]
    [InlineData("o")]
    [InlineData("0")]
    [InlineData("1")]
    public void Handles_avoid_the_characters_that_get_misheard(string banned)
    {
        var index = new ItemIndex();
        var refs = index.Remember(Scott, Enumerable.Range(0, 200).Select(i => Msg($"id-{i}")).ToList());

        Assert.DoesNotContain(refs, r => r.Ref.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The alphabet contains digits, so "2345" is a legitimate handle as well as a
    /// legitimate position. Reading it as a position would act on the wrong message
    /// silently, which is the exact failure the handles exist to prevent.
    /// </summary>
    [Fact]
    public void A_numeric_handle_wins_over_the_same_number_as_a_position()
    {
        var empty = new Dictionary<string, ItemRef>();

        // Eight of the alphabet's thirty-one characters are digits, so an all-digit handle
        // turns up roughly once in two hundred. Found rather than assumed, so this test
        // fails loudly if the alphabet ever changes out from under it.
        var id = Enumerable.Range(0, 50_000)
            .Select(i => $"graph-id-{i}")
            .FirstOrDefault(x => ItemIndex.Mint(x, empty).All(char.IsDigit));

        Assert.NotNull(id);

        var handle = ItemIndex.Mint(id!, empty);
        var index = new ItemIndex();

        // Padded out so the handle read as a position would land on a real, different
        // message — otherwise the wrong reading would fail for the wrong reason.
        var listing = Enumerable.Range(0, int.Parse(handle) + 2)
            .Select(i => Msg($"filler-{i}"))
            .Append(Msg(id!))
            .ToList();

        index.Remember(Scott, listing);

        // The handle wins its own string...
        Assert.Equal(id, index.Resolve(Scott, handle)!.ItemId);

        // ...while the number either side of it is still read as a position, so this is
        // precedence rather than numbers having stopped working.
        var next = int.Parse(handle) + 1;
        Assert.Equal($"filler-{next - 1}", index.Resolve(Scott, next.ToString())!.ItemId);
    }

    [Fact]
    public void Decoration_around_a_handle_is_ignored()
    {
        var index = new ItemIndex();
        var refs = index.Remember(Scott, [Msg("aaa")]);
        var r = refs[0].Ref;

        Assert.Equal("aaa", index.Resolve(Scott, $"[{r}]")!.ItemId);
        Assert.Equal("aaa", index.Resolve(Scott, $" #{r} ")!.ItemId);
        Assert.Equal("aaa", index.Resolve(Scott, r.ToUpperInvariant())!.ItemId);
    }

    [Fact]
    public void Nonsense_and_out_of_range_resolve_to_nothing()
    {
        var index = new ItemIndex();
        index.Remember(Scott, [Msg("aaa"), Msg("bbb")]);

        Assert.Null(index.Resolve(Scott, "9"));
        Assert.Null(index.Resolve(Scott, "0"));
        Assert.Null(index.Resolve(Scott, "-1"));
        Assert.Null(index.Resolve(Scott, ""));
        Assert.Null(index.Resolve(Scott, "   "));
        Assert.Null(index.Resolve(Scott, "zzzzzzzz"));
    }

    [Fact]
    public void Another_users_handles_are_invisible()
    {
        var index = new ItemIndex();
        var mine = index.Remember(Scott, [Msg("private")]);

        Assert.Null(index.Resolve(Someone, mine[0].Ref));
        Assert.Null(index.Resolve(Someone, "1"));
    }

    [Fact]
    public void Nothing_resolves_before_a_listing()
    {
        var index = new ItemIndex();
        Assert.Null(index.Resolve(Scott, "1"));
        Assert.Null(index.Resolve(Scott, "abcd"));
    }

    /// <summary>A move reissues Graph's id; the handle has to follow the message to it.</summary>
    [Fact]
    public void Retarget_moves_the_handle_onto_the_new_id()
    {
        var index = new ItemIndex();
        var refs = index.Remember(Scott, [Msg("before")]);

        index.Retarget(Scott, refs[0].Ref, "after");

        Assert.Equal("after", index.Resolve(Scott, refs[0].Ref)!.ItemId);
        Assert.Equal("after", index.Resolve(Scott, "1")!.ItemId);
    }

    /// <summary>
    /// After a move the message is listed again under its NEW id. Without the retarget it
    /// would mint a second handle for the same message, and the first would address a
    /// message that no longer exists.
    /// </summary>
    [Fact]
    public void A_retargeted_message_relists_under_its_existing_handle()
    {
        var index = new ItemIndex();
        var before = index.Remember(Scott, [Msg("before")]);
        index.Retarget(Scott, before[0].Ref, "after");

        var again = index.Remember(Scott, [Msg("after")]);

        Assert.Equal(before[0].Ref, again[0].Ref);
    }

    [Fact]
    public void Retargeting_an_unknown_handle_does_nothing()
    {
        var index = new ItemIndex();
        index.Remember(Scott, [Msg("aaa")]);

        index.Retarget(Scott, "nope", "whatever");
        index.Retarget(Someone, "nope", "whatever");

        Assert.Equal("aaa", index.Resolve(Scott, "1")!.ItemId);
    }

    [Fact]
    public void The_subject_and_sender_ride_along_so_an_action_can_read_back_what_it_did()
    {
        var index = new ItemIndex();
        index.Remember(Scott, [("id", "home", "Invoice 4471", "Accounts <ap@example.com>")]);

        var found = index.Resolve(Scott, "1")!;
        Assert.Equal("Invoice 4471", found.Subject);
        Assert.Equal("Accounts <ap@example.com>", found.From);
        Assert.Equal("home", found.ConnectionLabel);
    }

    [Fact]
    public void Handles_are_capped_so_a_long_session_cannot_grow_without_bound()
    {
        var index = new ItemIndex();

        var first = index.Remember(Scott, [Msg("very-first")]);
        for (var batch = 0; batch < 8; batch++)
            index.Remember(Scott, Enumerable.Range(0, 50).Select(i => Msg($"b{batch}-{i}")).ToList());

        // Evicted oldest-first: the earliest handle is gone, the most recent listing works.
        Assert.Null(index.Resolve(Scott, first[0].Ref));
        Assert.NotNull(index.Resolve(Scott, "1"));
    }
}
