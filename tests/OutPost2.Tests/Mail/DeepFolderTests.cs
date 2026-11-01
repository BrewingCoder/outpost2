using OutPost2.Core.Mail;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Naming a folder, and reading a folder list back, once there are a hundred of them
/// (OP2-213).
/// <para>
/// Fixing the walk changed what the surface has to cope with. The listing went from 25
/// folders two levels deep to 125 folders four levels deep on one real mailbox here, and
/// everything built on the assumption that a bare name picks out one folder stopped being
/// safe at that size: "Receipts" is a name three folders can share. So a name that matches
/// more than one is now refused rather than resolved, an id and a path are accepted, and the
/// listing collapses what it cannot read out loud.
/// </para>
/// </summary>
public sealed class DeepFolderTests
{
    private static MailFolder Folder(
        string path, int messages = 0, int children = 0, string? wellKnown = null,
        string? id = null) => new()
        {
            Id = id ?? "id-" + path,
            Name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
            Path = path,
            MessageCount = messages,
            ChildCount = children,
            WellKnownName = wellKnown,
        };

    /// <summary>The shape of the mailbox that reported OP2-213, cut down to what matters.</summary>
    private static readonly MailFolder[] Mailbox =
    [
        Folder("Inbox", messages: 7359, children: 11, wellKnown: "inbox"),
        Folder("Inbox/Subscriptions", messages: 42),
        Folder("Inbox/xInactive", children: 2),
        Folder("Inbox/xInactive/Receipts", messages: 310),
        Folder("Inbox/xInactive/Subscriptions", messages: 1180),
        Folder("Receipts", messages: 8),
        Folder("Archive", messages: 55, wellKnown: "archive"),
    ];

    // ── naming one ───────────────────────────────────────────────────────────

    [Fact]
    public void A_name_shared_by_two_folders_is_refused_rather_than_guessed()
    {
        // The folder this ticket was about is called Subscriptions, and so is one buried in
        // a dormant archive branch. Picking whichever came back first would file 42
        // messages of live mail into an archive nobody opens, and report success.
        var (folder, problem) = MailTools.Pick(Mailbox, "Subscriptions", "home");

        Assert.Null(folder);
        Assert.NotNull(problem);

        // Both paths said, because "say which one" is useless without them.
        Assert.Contains("Inbox/Subscriptions", problem);
        Assert.Contains("Inbox/xInactive/Subscriptions", problem);
        Assert.Contains("full path", problem);
    }

    [Fact]
    public void The_full_path_settles_it()
    {
        Assert.Equal("Inbox/Subscriptions",
            MailTools.Pick(Mailbox, "Inbox/Subscriptions", "home").Folder?.Path);

        Assert.Equal("Inbox/xInactive/Subscriptions",
            MailTools.Pick(Mailbox, "Inbox/xInactive/Subscriptions", "home").Folder?.Path);
    }

    [Fact]
    public void So_does_the_tail_of_a_path()
    {
        // "xInactive/Subscriptions" is what somebody says. The whole path, four levels of
        // it, is not — and a surface that only accepts the full thing has not solved the
        // ambiguity, it has moved it onto the user.
        Assert.Equal("Inbox/xInactive/Subscriptions",
            MailTools.Pick(Mailbox, "xInactive/Subscriptions", "home").Folder?.Path);
    }

    [Fact]
    public void A_folder_id_is_accepted_as_an_address()
    {
        // OP2-213 asked for this. It is what the model holds after a listing, and unlike a
        // name it cannot be ambiguous — so it is tried before anything spoken.
        Assert.Equal("Inbox/Subscriptions",
            MailTools.Pick(Mailbox, "id-Inbox/Subscriptions", "home").Folder?.Path);
    }

    [Fact]
    public void An_id_beats_a_name_that_happens_to_match_it()
    {
        // Contrived, but the order has to be decided somewhere: an id is exact and a name
        // is a guess, so the id wins.
        MailFolder[] odd =
        [
            Folder("Admin", id: "Receipts"),
            Folder("Receipts", id: "id-Receipts"),
        ];

        Assert.Equal("Admin", MailTools.Pick(odd, "Receipts", "home").Folder?.Path);
    }

    [Fact]
    public void An_unambiguous_bare_name_still_works()
    {
        // The common case has to stay the easy case: most folders have names nobody else
        // shares, and making everyone say paths would be a worse product.
        Assert.Equal("Receipts", MailTools.Pick(Mailbox, "Receipts", "home").Folder?.Path);
        Assert.Equal("Inbox/xInactive", MailTools.Pick(Mailbox, "xInactive", "home").Folder?.Path);
    }

    [Fact]
    public void An_exact_path_beats_a_bare_name_somewhere_else()
    {
        // "Receipts" exists at the top level and under xInactive. Said as a path it means
        // the deep one, and the top-level folder of that name must not shadow it.
        Assert.Equal("Inbox/xInactive/Receipts",
            MailTools.Pick(Mailbox, "Inbox/xInactive/Receipts", "home").Folder?.Path);
    }

    [Fact]
    public void Find_and_Pick_agree_about_what_is_unambiguous()
    {
        // Find is what the read-only paths use. It must not resolve something the write
        // paths refuse, or a search would scope to one folder and a move would go to
        // another under the same spoken name.
        foreach (var said in new[] { "Subscriptions", "Receipts", "Inbox/Subscriptions", "nonsense" })
            Assert.Equal(MailTools.Pick(Mailbox, said, "home").Folder?.Path,
                         MailTools.Find(Mailbox, said)?.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(null)]
    public void Nothing_said_matches_nothing(string? said)
    {
        Assert.Empty(MailTools.Matches(Mailbox, said));
    }

    // ── saying there is no such folder ───────────────────────────────────────

    [Fact]
    public void A_missing_folder_is_answered_with_the_near_misses_not_the_whole_mailbox()
    {
        // Before this, the refusal listed every folder it knew — tolerable only because it
        // knew 25. Reading 125 paths out to somebody driving is not an answer.
        var said = MailTools.NoFolder("home", "Subscription", Mailbox);

        Assert.Contains("Inbox/Subscriptions", said);
        Assert.DoesNotContain("Archive", said);
        Assert.Contains("make one", said);
    }

    [Fact]
    public void With_nothing_close_it_says_how_many_there_are_and_names_a_few()
    {
        var said = MailTools.NoFolder("home", "Quarterly VAT", Mailbox);

        Assert.Contains("7 folders", said);
        Assert.Contains("Inbox", said);
    }

    [Fact]
    public void A_listing_that_did_not_finish_says_so_before_offering_to_create_anything()
    {
        // Offering to create a folder that is really there, out past where the walk
        // stopped, is how a mailbox ends up with two folders of one name and mail in both.
        MailFolder[] partial = [Folder("Inbox", children: 90, wellKnown: "inbox")];

        Assert.Contains("out of my reach", MailTools.NoFolder("home", "Receipts", partial));
        Assert.DoesNotContain("out of my reach", MailTools.NoFolder("home", "Receipts", Mailbox));
    }

    // ── reading the list back ────────────────────────────────────────────────

    [Fact]
    public void The_default_listing_shows_the_top_level_and_says_what_it_collapsed()
    {
        var said = MailTools.Listing(Mailbox, root: null, "home");

        // The count is of the whole mailbox; the lines are of the top of it. Saying "3
        // folders" when there are 7 would be the old bug upside down.
        Assert.Contains("7 folders on home", said);
        Assert.Contains("3 of them at the top", said);

        Assert.Contains("Inbox", said);
        Assert.Contains("Receipts", said);

        // Not the deep ones, by name, on the top-level listing.
        Assert.DoesNotContain("xInactive", said);

        // And it says that filing still works on what it did not read out, because the
        // model will otherwise believe the list is the set of destinations.
        Assert.Contains("whether or not they're listed", said);
    }

    [Fact]
    public void A_branch_reports_everything_underneath_it_not_just_its_children()
    {
        // 4 is the number that answers "is it worth looking in there"; 2 is not.
        var said = MailTools.Listing(Mailbox, root: null, "home");

        Assert.Contains("4 folders inside", said);
    }

    [Fact]
    public void Opening_a_branch_names_its_children_relative_to_it()
    {
        var said = MailTools.Listing(Mailbox, Mailbox.Single(f => f.Path == "Inbox"), "home");

        Assert.Contains("Inside Inbox on home", said);
        Assert.Contains("  Subscriptions —", said);
        Assert.Contains("  xInactive —", said);

        // Relative, so a four-level mailbox does not read as a wall of repeated prefixes.
        Assert.DoesNotContain("Inbox/Subscriptions", said);
    }

    [Fact]
    public void A_branch_with_nothing_in_it_says_so_and_says_what_it_holds()
    {
        var said = MailTools.Listing(Mailbox, Mailbox.Single(f => f.Path == "Receipts"), "home");

        Assert.Contains("nothing inside it", said);
        Assert.Contains("8 messages", said);
    }

    [Fact]
    public void Outlook_s_own_folders_are_named_as_such_and_come_last()
    {
        var said = MailTools.Listing(Mailbox, root: null, "home");

        Assert.Contains("(Outlook's own)", said);
        Assert.True(said.IndexOf("Receipts", StringComparison.Ordinal)
                  < said.IndexOf("Archive", StringComparison.Ordinal),
            "a folder the user made should be read out before one Outlook made");
    }

    [Fact]
    public void A_listing_that_ran_out_of_requests_admits_it()
    {
        MailFolder[] partial =
        [
            Folder("Inbox", children: 90, wellKnown: "inbox"),
            Folder("Inbox/xInactive", children: 84),
        ];

        var said = MailTools.Listing(partial, root: null, "home");

        Assert.Contains("ran out of", said);
    }

    [Fact]
    public void A_folder_whose_children_were_not_reached_is_marked_rather_than_counted_as_empty()
    {
        MailFolder[] partial =
        [
            Folder("Inbox", children: 1, wellKnown: "inbox"),
            Folder("Inbox/xInactive", children: 84),
        ];

        var said = MailTools.Listing(partial, partial.Single(f => f.Path == "Inbox"), "home");

        Assert.Contains("84 inside I couldn't reach", said);
    }

    [Fact]
    public void An_empty_mailbox_does_not_pretend_to_list_anything()
    {
        Assert.Contains("no folders", MailTools.Listing([], root: null, "home"));
    }
}
