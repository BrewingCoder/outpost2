using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Who a reply actually goes to (OP2-187).
/// <para>
/// This is the function that decides whether a person stays on a thread, and a reply cannot
/// be unsent. The tests are therefore weighted towards the two ways it could be quietly
/// wrong: dropping somebody nobody asked to drop, and <b>failing</b> to drop somebody who
/// was asked for — which sends to the wrong address, the exact outcome the caller was
/// trying to avoid.
/// </para>
/// </summary>
public sealed class ReplyRecipientTests
{
    private static readonly MailRecipient Alice = new("Alice Brown", "alice@example.com");
    private static readonly MailRecipient Bob = new("Bob Carter", "bob@example.com");
    private static readonly MailRecipient Dana = new(null, "dana@example.com");

    private static RecipientEdit Edit(
        string[]? addTo = null, string[]? addCc = null, string[]? drop = null) => new()
        {
            AddTo = addTo ?? [], AddCc = addCc ?? [], Drop = drop ?? [],
        };

    // ── the no-op case ───────────────────────────────────────────────────────

    [Fact]
    public void With_no_edit_the_draft_graph_composed_is_left_exactly_as_it_is()
    {
        var plan = MailService.PlanRecipients([Alice, Bob], [Dana], null);

        Assert.Equal([Alice, Bob], plan.To);
        Assert.Equal([Dana], plan.Cc);
        Assert.Empty(plan.Dropped);
        Assert.Empty(plan.Unmatched);
    }

    [Fact]
    public void An_edit_that_asks_for_nothing_is_the_same_as_no_edit()
    {
        var plan = MailService.PlanRecipients([Alice], [], Edit());

        Assert.Equal([Alice], plan.To);
        Assert.Empty(plan.Dropped);
    }

    // ── the ticket's own scenario ────────────────────────────────────────────

    [Fact]
    public void A_wrong_address_comes_off_and_the_corrected_one_goes_on()
    {
        // OP2-187 verbatim: replying-all to a thread where one participant's address is
        // wrong. Before this, the only way through was reply-all to the bad address plus a
        // separate forward — which splits the thread for the person you were correcting.
        var wrong = new MailRecipient("Bob Carter", "bob@exmaple.com");

        var plan = MailService.PlanRecipients(
            [Alice, wrong], [],
            Edit(addTo: ["Bob Carter <bob@example.com>"], drop: ["bob@exmaple.com"]));

        Assert.Equal(["alice@example.com", "bob@example.com"], plan.To.Select(r => r.Address));
        Assert.Equal([wrong], plan.Dropped);
        Assert.Empty(plan.Unmatched);

        // The name survives the round trip, so the reply is addressed to a person rather
        // than to a bare mailbox string.
        Assert.Equal("Bob Carter", plan.To[1].Name);
    }

    // ── dropping: the dangerous half ─────────────────────────────────────────

    [Fact]
    public void A_drop_that_matches_nobody_is_reported_rather_than_ignored()
    {
        // The whole point of the refusal. If this returned a plan, the reply would go to
        // the wrong address anyway and read as a success.
        var plan = MailService.PlanRecipients(
            [Alice, Bob], [], Edit(drop: ["bob@elsewhere.com"]));

        Assert.Equal(["bob@elsewhere.com"], plan.Unmatched);
    }

    [Fact]
    public void Each_unmatched_drop_is_named_so_the_caller_can_see_which_one_was_wrong()
    {
        var plan = MailService.PlanRecipients(
            [Alice], [], Edit(drop: ["nobody@example.com", "Alice Brown", "also@nowhere.com"]));

        Assert.Equal(["nobody@example.com", "also@nowhere.com"], plan.Unmatched);

        // The one that did match is still accounted for — a partial failure must not look
        // like a total one.
        Assert.Equal([Alice], plan.Dropped);
    }

    [Theory]
    [InlineData("bob@example.com")]
    [InlineData("BOB@EXAMPLE.COM")]
    [InlineData("  bob@example.com  ")]
    [InlineData("Bob Carter")]
    [InlineData("bob carter")]
    [InlineData("Bob Carter <bob@example.com>")]
    public void A_recipient_can_be_named_by_address_by_name_or_by_what_a_listing_printed(string said)
    {
        // read_mail prints "Name <addr>", so that form has to work: it is what a caller
        // naturally pastes back.
        var plan = MailService.PlanRecipients([Alice, Bob], [], Edit(drop: [said]));

        Assert.Equal([Alice], plan.To);
        Assert.Equal([Bob], plan.Dropped);
        Assert.Empty(plan.Unmatched);
    }

    [Theory]
    [InlineData("bob")]
    [InlineData("example.com")]
    [InlineData("Bob")]
    [InlineData("@example.com")]
    public void Matching_is_exact_so_a_fragment_cannot_take_the_wrong_person_off(string said)
    {
        // "bob" must not remove bob@example.com on a guess, and "example.com" must not
        // remove the entire thread. Refusing is recoverable; a silent removal is not.
        var plan = MailService.PlanRecipients([Alice, Bob], [], Edit(drop: [said]));

        Assert.Equal([Alice, Bob], plan.To);
        Assert.Empty(plan.Dropped);
        Assert.Equal([said], plan.Unmatched);
    }

    [Fact]
    public void Somebody_copied_in_can_be_dropped_too()
    {
        var plan = MailService.PlanRecipients([Alice], [Bob, Dana], Edit(drop: ["bob@example.com"]));

        Assert.Equal([Alice], plan.To);
        Assert.Equal([Dana], plan.Cc);
        Assert.Equal([Bob], plan.Dropped);
    }

    [Fact]
    public void Dropping_everyone_leaves_an_empty_To_for_the_caller_to_refuse()
    {
        // The planner reports it rather than inventing a recipient; SendReplyAsync turns an
        // empty To into a refusal before anything is patched.
        var plan = MailService.PlanRecipients(
            [Alice], [], Edit(drop: ["alice@example.com"]));

        Assert.Empty(plan.To);
        Assert.Empty(plan.Unmatched);
    }

    [Fact]
    public void A_person_on_both_lines_is_only_counted_once_as_dropped()
    {
        var plan = MailService.PlanRecipients(
            [Alice, Bob], [Bob], Edit(drop: ["bob@example.com", "Bob Carter"]));

        Assert.Equal([Alice], plan.To);
        Assert.Empty(plan.Cc);
        Assert.Equal([Bob], plan.Dropped);
    }

    // ── adding ───────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_somebody_already_on_the_thread_does_not_duplicate_them()
    {
        // Correcting an address is drop-then-add, and the corrected address is often a
        // near-miss of the wrong one — so the add frequently names someone already there.
        var plan = MailService.PlanRecipients(
            [Alice, Bob], [], Edit(addTo: ["BOB@example.com", "Alice Brown <alice@example.com>"]));

        Assert.Equal([Alice, Bob], plan.To);
    }

    [Fact]
    public void Adding_to_cc_somebody_already_in_to_leaves_them_in_to()
    {
        // Moving someone from To to Cc is not something a reply should do silently: they
        // were addressed directly on the thread.
        var plan = MailService.PlanRecipients([Alice], [], Edit(addCc: ["alice@example.com"]));

        Assert.Equal([Alice], plan.To);
        Assert.Empty(plan.Cc);
    }

    [Fact]
    public void A_bare_address_is_accepted_without_a_name()
    {
        var plan = MailService.PlanRecipients([Alice], [], Edit(addCc: ["new@example.com"]));

        Assert.Equal("new@example.com", Assert.Single(plan.Cc).Address);
        Assert.Null(Assert.Single(plan.Cc).Name);
        Assert.Equal("new@example.com", plan.Cc[0].Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_entries_are_skipped_rather_than_added_as_a_recipient(string blank)
    {
        var plan = MailService.PlanRecipients([Alice], [], Edit(addTo: [blank], drop: [blank]));

        Assert.Equal([Alice], plan.To);
        Assert.Empty(plan.Unmatched);
    }

    // ── the display form ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Bob Carter <bob@example.com>", "Bob Carter", "bob@example.com")]
    [InlineData("  Bob Carter   <bob@example.com>  ", "Bob Carter", "bob@example.com")]
    [InlineData("\"Carter, Bob\" <bob@example.com>", "Carter, Bob", "bob@example.com")]
    [InlineData("bob@example.com", null, "bob@example.com")]
    public void A_recipient_parses_from_either_form(string said, string? name, string address)
    {
        var parsed = MailService.ParseRecipient(said);

        Assert.Equal(name, parsed.Name);
        Assert.Equal(address, parsed.Address);
    }

    [Fact]
    public void A_name_with_an_angle_bracket_in_it_still_yields_the_real_address()
    {
        // Last bracket wins, so a display name that itself contains one cannot shift the
        // address — an address taken from the wrong side of this would send to a stranger.
        var parsed = MailService.ParseRecipient("Bob <the builder> <bob@example.com>");

        Assert.Equal("bob@example.com", parsed.Address);
    }

    [Fact]
    public void A_recipient_with_no_name_displays_as_the_address_alone()
    {
        Assert.Equal("dana@example.com", Dana.Display);
        Assert.Equal("Alice Brown <alice@example.com>", Alice.Display);
    }
}
