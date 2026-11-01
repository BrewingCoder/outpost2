using OutPost2.Core.Mail;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Making, naming and deleting mail folders (OP2-188).
/// <para>
/// The friction report asked only for folders to exist. The part that needed designing is
/// <b>delete</b>: this product is driven by voice, so a folder name arrives misheard rather
/// than mistyped, and "Receipts" can hold four hundred messages. The gate tested here is
/// the only thing between those two facts.
/// </para>
/// </summary>
public sealed class FolderToolTests
{
    private static MailFolder Folder(
        string path, int messages = 0, int children = 0, string? wellKnown = null) => new()
        {
            Id = "id-" + path,
            Name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
            Path = path,
            MessageCount = messages,
            ChildCount = children,
            WellKnownName = wellKnown,
        };

    // ── the delete gate ──────────────────────────────────────────────────────

    [Fact]
    public void An_empty_folder_goes_without_ceremony()
    {
        // Nothing to lose, so asking would be noise.
        Assert.Null(MailTools.Refuse(Folder("Receipts"), confirm: false));
    }

    [Fact]
    public void A_folder_with_messages_in_it_is_refused_until_the_count_has_been_said()
    {
        var stop = MailTools.Refuse(Folder("Receipts", messages: 412), confirm: false);

        Assert.NotNull(stop);
        Assert.Contains("412 messages", stop);
        Assert.Contains("Deleted Items", stop);

        // Says plainly that it did not happen. A refusal the model reads as a failure gets
        // retried; one it reads as a question gets asked.
        Assert.Contains("Nothing has been deleted", stop);
    }

    [Fact]
    public void A_folder_whose_only_contents_are_subfolders_is_refused_too()
    {
        // Deleting it takes the children and everything in them, which the message has to
        // mention — a folder reading "0 messages" otherwise looks safe.
        var stop = MailTools.Refuse(Folder("Clients", children: 3), confirm: false);

        Assert.NotNull(stop);
        Assert.Contains("3 subfolders", stop);
    }

    [Fact]
    public void Both_counts_are_named_when_both_are_there()
    {
        var stop = MailTools.Refuse(Folder("Clients", messages: 9, children: 2), confirm: false);

        Assert.Contains("9 messages", stop);
        Assert.Contains("2 subfolders", stop);
    }

    [Theory]
    [InlineData(1, 0, "1 message")]
    [InlineData(2, 0, "2 messages")]
    [InlineData(0, 1, "1 subfolder")]
    [InlineData(0, 2, "2 subfolders")]
    public void The_counts_are_spoken_rather_than_printed(int messages, int children, string expected)
    {
        // It is read aloud in a car. "1 messages" is the kind of thing that makes a product
        // sound broken even when it is right.
        Assert.Contains(expected, MailTools.Refuse(Folder("X", messages, children), confirm: false));
    }

    [Fact]
    public void Confirming_lets_a_full_folder_go()
    {
        Assert.Null(MailTools.Refuse(Folder("Receipts", messages: 412, children: 2), confirm: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outlooks_own_folders_are_refused_however_hard_you_confirm(bool confirm)
    {
        // Confirm is for "I know what is in it", not for "delete my Inbox". Graph allows
        // some of these and a mailbox missing its Archive is repaired by hand.
        var stop = MailTools.Refuse(Folder("Archive", messages: 3, wellKnown: "archive"), confirm);

        Assert.NotNull(stop);
        Assert.Contains("Outlook's own", stop);
    }

    [Fact]
    public void An_empty_well_known_folder_is_still_refused()
    {
        Assert.NotNull(MailTools.Refuse(Folder("Drafts", wellKnown: "drafts"), confirm: false));
    }

    // ── finding one by what was said ─────────────────────────────────────────

    private static readonly MailFolder[] Mailbox =
    [
        Folder("Inbox", messages: 120, wellKnown: "inbox"),
        Folder("Receipts", messages: 8),
        Folder("Clients/Acme", messages: 4),
    ];

    [Theory]
    [InlineData("Receipts")]
    [InlineData("receipts")]
    [InlineData("  RECEIPTS  ")]
    public void A_folder_is_found_by_its_bare_name(string said)
    {
        Assert.Equal("Receipts", MailTools.Find(Mailbox, said)?.Path);
    }

    [Theory]
    [InlineData("Clients/Acme")]
    [InlineData("Acme")]
    public void A_child_folder_answers_to_its_path_or_its_own_name(string said)
    {
        Assert.Equal("Clients/Acme", MailTools.Find(Mailbox, said)?.Path);
    }

    [Theory]
    [InlineData("Receipt")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Clients")]
    public void Anything_else_is_not_a_folder(string? said)
    {
        // "Clients" is deliberately in this list: the listing holds "Clients/Acme" but no
        // folder of its own called Clients, and guessing the parent would file mail
        // somewhere the user never made.
        Assert.Null(MailTools.Find(Mailbox, said));
    }

    // ── how deep the listing goes ────────────────────────────────────────────

    [Fact]
    public void A_folder_whose_children_are_listed_is_not_counted_as_hidden()
    {
        MailFolder[] mailbox =
        [
            Folder("Inbox", messages: 10, children: 1, wellKnown: "inbox"),
            Folder("Inbox/Receipts", messages: 4),
        ];

        Assert.Equal(0, MailTools.Deeper(mailbox));
    }

    [Fact]
    public void A_folder_with_children_too_deep_to_list_is_counted()
    {
        // Taken from a real mailbox: Inbox/xInactive reads "0 messages" and holds 84
        // folders, none of which the listing reaches. Saying "25 folders" and stopping
        // would be a lie, and worse, "no folder called that" would offer to create a
        // duplicate of one that already exists three levels down.
        MailFolder[] mailbox =
        [
            Folder("Inbox", messages: 7359, children: 6, wellKnown: "inbox"),
            Folder("Inbox/xInactive", messages: 0, children: 84),
            Folder("Inbox/MUFG", messages: 53, children: 2),
        ];

        Assert.Equal(2, MailTools.Deeper(mailbox));
    }

    [Fact]
    public void A_mailbox_of_flat_folders_hides_nothing()
    {
        Assert.Equal(0, MailTools.Deeper(Mailbox));
    }

    // ── names ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Receipts")]
    [InlineData("2026 Taxes")]
    [InlineData("Acme & Co.")]
    public void An_ordinary_name_is_accepted(string name)
    {
        Assert.Null(MailTools.Invalid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_folder_needs_a_name(string? name)
    {
        Assert.NotNull(MailTools.Invalid(name));
    }

    [Fact]
    public void A_slash_is_refused_because_this_surface_means_inside_by_it()
    {
        // A folder called "Clients/Acme" could never be named back unambiguously — not by
        // the user and not by us, since Find reads a slash as a parent.
        var why = MailTools.Invalid("Clients/Acme");

        Assert.NotNull(why);
        Assert.Contains("slash", why);

        // And says what to do instead, rather than only that it was wrong.
        Assert.Contains("which folder to put it in", why);
    }

    [Fact]
    public void An_absurdly_long_name_is_refused_here_rather_than_by_graph()
    {
        Assert.NotNull(MailTools.Invalid(new string('x', 300)));
    }
}
