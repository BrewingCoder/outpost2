using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Calendar;

/// <summary>
/// Mail, calendar, contacts and Teams chats share one handle index (OP2-75, OP2-79), so
/// that "the second one" always means the second of whatever was listed LAST rather than
/// depending on which family of tool the model reaches for next. These pin the
/// consequences of that choice.
/// </summary>
public class SharedHandleSpaceTests
{
    private static readonly Guid User = Guid.CreateVersion7();

    [Fact]
    public void An_item_remembers_which_family_it_belongs_to()
    {
        var index = new ItemIndex();

        var mail = index.Remember(User, [("AAMk-message-id", "work", "Invoice", "darren@example.com")]);
        var events = index.Remember(User, [("AAMk-event-id", "work", "Standup", "lee@example.com")], ItemKind.Event);

        Assert.Equal(ItemKind.Mail, index.Resolve(User, mail[0].Ref)!.Kind);
        Assert.Equal(ItemKind.Event, index.Resolve(User, events[0].Ref)!.Kind);
    }

    [Fact]
    public void An_ordinal_resolves_against_the_most_recent_listing_whatever_it_held()
    {
        var index = new ItemIndex();

        index.Remember(User, [("msg-1", "work", "Invoice", "darren@example.com")]);
        index.Remember(User, [("evt-1", "work", "Standup", "lee@example.com")], ItemKind.Event);

        // "1" now means the standup, because that is what was just read out. A separate
        // index per family would make this depend on which tool the model guessed at,
        // and the user has no way to know which listing the number is counting.
        var first = index.Resolve(User, "1");

        Assert.NotNull(first);
        Assert.Equal("Standup", first!.Subject);
        Assert.Equal(ItemKind.Event, first.Kind);
    }

    [Fact]
    public void Handles_minted_for_different_items_do_not_collide_across_the_families()
    {
        var index = new ItemIndex();

        var mail = index.Remember(User,
        [
            ("msg-1", "work", "Invoice", "darren@example.com"),
            ("msg-2", "work", "Re: Invoice", "darren@example.com"),
        ]);

        var events = index.Remember(User,
        [
            ("evt-1", "work", "Standup", "lee@example.com"),
            ("evt-2", "work", "Review", "lee@example.com"),
        ], ItemKind.Event);

        var all = mail.Concat(events).Select(x => x.Ref).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // And each still resolves to its own item, which is the thing the uniqueness is for.
        Assert.Equal("evt-2", index.Resolve(User, events[1].Ref)!.ItemId);
        Assert.Equal("msg-1", index.Resolve(User, mail[0].ItemId is null ? "" : mail[0].Ref)!.ItemId);
    }

    [Fact]
    public void A_chat_handle_is_distinguishable_from_every_other_family()
    {
        // The cost of one shared index is that a handle can arrive at the wrong family of
        // tool, so each tool checks the kind before acting. Four families now share the
        // space, and sending a Teams message to something that turns out to be a calendar
        // event is exactly the mistake the check exists to catch.
        var index = new ItemIndex();

        var chat = index.Remember(User, [("19:abc@unq.gbl.spaces", "work", "Darren Jones", "")], ItemKind.Chat);
        var mail = index.Remember(User, [("msg-1", "work", "Invoice", "darren@example.com")]);

        Assert.Equal(ItemKind.Chat, index.Resolve(User, chat[0].Ref)!.Kind);
        Assert.Equal(ItemKind.Mail, index.Resolve(User, mail[0].Ref)!.Kind);

        // And the chat's subject is its display name, so a tool refusing a misaimed
        // handle can say WHICH chat it was rather than quoting a thread id.
        Assert.Equal("Darren Jones", index.Resolve(User, chat[0].Ref)!.Subject);
    }

    [Fact]
    public void A_listing_repeated_keeps_the_handle_and_the_kind_it_already_had()
    {
        var index = new ItemIndex();

        var first = index.Remember(User, [("evt-1", "work", "Standup", "lee@example.com")], ItemKind.Event);
        var again = index.Remember(User, [("evt-1", "work", "Standup", "lee@example.com")], ItemKind.Event);

        Assert.Equal(first[0].Ref, again[0].Ref);
        Assert.Equal(ItemKind.Event, again[0].Kind);
    }
}
