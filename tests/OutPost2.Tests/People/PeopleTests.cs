using OutPost2.Core.Connections;
using OutPost2.Core.People;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.People;

/// <summary>
/// OP2-77. The feature exists to answer "email Justin", so the cases worth pinning are
/// the ones where answering it confidently would be wrong: two Justins, a distribution
/// list called Justin's team, and a directory entry with no address at all.
/// </summary>
public class PeopleTests
{
    private static Person P(string name, string address, double relevance,
                            PersonSource source = PersonSource.Relevant,
                            bool group = false, string? title = null) => new()
    {
        ConnectionLabel = "work",
        Name = name,
        Address = address,
        Relevance = relevance,
        Source = source,
        IsGroup = group,
        JobTitle = title,
    };

    [Fact]
    public void A_group_is_marked_so_it_cannot_be_mistaken_for_a_person()
    {
        // /me/people returns distribution lists from the same endpoint as humans, and in a
        // spoken list "Marketing" reads exactly like a name. Mailing a department when one
        // person was meant is the mistake this flag exists to prevent.
        var list = P("Marketing", "marketing@example.com", 30, group: true);
        var human = P("Justin Marks", "justinm@example.com", 20);

        Assert.True(list.IsGroup);
        Assert.False(human.IsGroup);
    }

    [Fact]
    public void A_person_with_no_address_is_representable_rather_than_dropped()
    {
        // A room, a shared mailbox or a directory entry with no mail attribute. Worth
        // showing — it answers "who is that" — but it must not be offered as a recipient,
        // so the empty address has to survive as far as the tool that formats it.
        var roomish = P("Conf Room Hood", "", 0, PersonSource.Directory);

        Assert.Equal("", roomish.Address);
        Assert.Empty(roomish.OtherAddresses);
    }

    [Fact]
    public void Source_distinguishes_the_three_places_a_name_can_come_from()
    {
        // Not cosmetic. "The Justin in your contacts" and "a Justin in the company
        // directory" are routinely different people, and the source is often the only
        // thing that tells them apart out loud.
        Assert.Equal(PersonSource.Relevant, P("a", "a@x.com", 20).Source);
        Assert.Equal(PersonSource.Contact, P("b", "b@x.com", 1, PersonSource.Contact).Source);
        Assert.Equal(PersonSource.Directory, P("c", "c@x.com", 0, PersonSource.Directory).Source);
    }

    [Fact]
    public void The_relevance_bands_keep_the_three_sources_in_the_right_order()
    {
        // Correspondents beat saved contacts beat bare directory entries. The bands are
        // 2.0+score, 1.0 and 0.0, and the ordering between them is what makes the first
        // result the one a spoken name almost always meant.
        var correspondent = P("Justin Marks", "jm@example.com", 2.0 + 20);
        var contact = P("Justin Reed", "jr@example.com", 1.0, PersonSource.Contact);
        var directory = P("Justin Webb", "jw@example.com", 0.0, PersonSource.Directory);

        var ordered = new[] { directory, contact, correspondent }
            .OrderByDescending(p => p.Relevance).ToList();

        Assert.Equal("Justin Marks", ordered[0].Name);
        Assert.Equal("Justin Reed", ordered[1].Name);
        Assert.Equal("Justin Webb", ordered[2].Name);
    }

    [Fact]
    public void An_empty_change_set_is_recognised_so_a_no_op_patch_is_never_sent()
    {
        // A PATCH with an empty body succeeds and changes nothing, so without this the
        // tool would report "updated" for a call that did not.
        Assert.True(new ContactDetails().IsEmpty);
        Assert.False(new ContactDetails { Name = "Justin" }.IsEmpty);
        Assert.False(new ContactDetails { Addresses = ["j@example.com"] }.IsEmpty);
    }

    [Fact]
    public void A_contacts_primary_address_is_the_first_and_absent_when_there_are_none()
    {
        var withTwo = new Contact
        {
            Id = "1", ConnectionLabel = "work", Name = "Justin Reed",
            Addresses = ["justin@home.example", "justin@work.example"],
        };

        Assert.Equal("justin@home.example", withTwo.PrimaryAddress);

        // A contact saved for a phone number alone is perfectly normal, and offering it
        // as an email recipient would produce a send that cannot go anywhere.
        var phoneOnly = new Contact
        {
            Id = "2", ConnectionLabel = "work", Name = "Plumber", MobilePhone = "07700 900000",
        };

        Assert.Null(phoneOnly.PrimaryAddress);
    }

    [Fact]
    public void People_capabilities_are_described_and_risk_rated_for_the_consent_screen()
    {
        // The connections page renders whatever is on the row through the catalogue, so a
        // capability missing from it shows up as a bare scope code next to a blank badge.
        Assert.Equal("Look people up and read your contacts",
            CapabilityCatalog.Verb(Capabilities.PeopleRead));
        Assert.Equal(CapabilityRisk.None, CapabilityCatalog.Risk(Capabilities.PeopleRead));

        Assert.Equal("Add and change your contacts",
            CapabilityCatalog.Verb(Capabilities.ContactsWrite));
        Assert.Equal(CapabilityRisk.Writes, CapabilityCatalog.Risk(Capabilities.ContactsWrite));
    }

    [Fact]
    public void A_contact_handle_is_distinguishable_from_a_mail_or_event_one()
    {
        var index = new ItemIndex();
        var user = Guid.CreateVersion7();

        var mail = index.Remember(user, [("msg-1", "work", "Invoice", "d@example.com")]);
        var ev = index.Remember(user, [("evt-1", "work", "Standup", "l@example.com")], ItemKind.Event);
        var contact = index.Remember(user, [("con-1", "work", "Justin Reed", "j@example.com")], ItemKind.Contact);

        Assert.Equal(ItemKind.Mail, index.Resolve(user, mail[0].Ref)!.Kind);
        Assert.Equal(ItemKind.Event, index.Resolve(user, ev[0].Ref)!.Kind);
        Assert.Equal(ItemKind.Contact, index.Resolve(user, contact[0].Ref)!.Kind);
    }
}
