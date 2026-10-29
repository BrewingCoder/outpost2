using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Core.People;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.People;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// People lookup and the address book (OP2-77).
///
/// <para>
/// This exists because of one sentence: <i>"email Justin about the invoice"</i>. Nothing
/// else in the product can turn a spoken first name into an address, and without it every
/// such request either fails or — far worse — guesses. Three sources are merged, and each
/// result says which one it came from, because "the Justin in your contacts" and "a
/// Justin in the company directory" are different people often enough to matter.
/// </para>
///
/// <para>
/// Results are deliberately verbose about job title and department. Two people with the
/// same first name is the normal case in any organisation of size, and a list of bare
/// names read aloud gives the user no way to pick.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class PeopleTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    IPeopleService people,
    IItemIndex index,
    PublicUrls urls)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    [McpServerTool(Name = "find_person", ReadOnly = true, OpenWorld = true)]
    [Description("Find somebody's email address by name. Searches the people you deal with most, the company directory and your own contacts. Use this before sending mail or inviting someone when you only have a name.")]
    public async Task<string> FindPerson(
        [Description("A name, partial name, or part of an email address.")] string name,
        [Description("Connection name such as 'work' or 'home'. Use 'all' to search everywhere. Omit only if a default has been set.")] string? connection = null,
        [Description("How many matches to return per connection.")] int count = 8,
        CancellationToken ct = default)
    {
        var all = await connections.ListAsync(UserId, ct);
        var resolution = ConnectionResolver.Resolve(
            all, Capabilities.PeopleRead, connection, isWrite: false);

        if (!resolution.IsActionable) return Explain(resolution, Capabilities.PeopleRead, all);

        var found = new List<Person>();
        var failures = new List<string>();

        await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                foreach (var p in await people.FindAsync(c, name, Math.Clamp(count, 1, 25), ct))
                    lock (found) found.Add(p);
            }
            catch (MailAuthRequiredException) { lock (failures) failures.Add($"{c.Label} needs re-authorising"); }
            catch (PeopleException ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
            catch (Exception ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
        }));

        var sb = new StringBuilder();

        if (found.Count == 0)
        {
            sb.AppendLine($"Nobody found matching \"{Untrusted.Scrub(name)}\".");
        }
        else
        {
            var ordered = found.OrderByDescending(p => p.Relevance).ThenBy(p => p.IsGroup).ToList();

            sb.AppendLine(ordered.Count == 1
                ? "One match:"
                : $"{ordered.Count} matches, most likely first:");

            foreach (var p in ordered)
            {
                sb.Append("- ");
                if (resolution.Connections.Count > 1) sb.Append($"[{p.ConnectionLabel}] ");
                sb.Append(Untrusted.Scrub(p.Name));

                // An address is the point of the tool, so its absence is stated rather
                // than left as a blank the model might fill in.
                sb.Append(p.Address.Length > 0
                    ? $" — {Untrusted.Scrub(p.Address)}"
                    : " — no email address on file");

                var detail = new List<string>();
                if (p.JobTitle is not null) detail.Add(Untrusted.Scrub(p.JobTitle));
                if (p.Department is not null) detail.Add(Untrusted.Scrub(p.Department));
                if (p.Company is not null) detail.Add(Untrusted.Scrub(p.Company));
                if (detail.Count > 0) sb.Append($" ({string.Join(", ", detail)})");

                // A distribution list looks exactly like a person in a spoken list, and
                // mailing one by accident is the mistake worth preventing.
                if (p.IsGroup) sb.Append(" — this is a GROUP, not a person");

                sb.Append(p.Source switch
                {
                    PersonSource.Contact => " [your contacts]",
                    PersonSource.Directory => " [company directory]",
                    _ => "",
                });

                sb.AppendLine();
            }

            if (ordered.Count > 1)
                sb.AppendLine("Confirm which one before sending anything.");
        }

        foreach (var f in failures) sb.AppendLine($"Note: {f}.");
        foreach (var d in resolution.Degraded) sb.AppendLine($"Note: {d.Label} is unavailable ({d.State}).");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list_contacts", ReadOnly = true, OpenWorld = true)]
    [Description("The user's own saved contacts, optionally filtered by name, company or address. Each carries a handle in square brackets — pass that to update one.")]
    public async Task<string> ListContacts(
        [Description("Filter by name, company or email address. Omit for everything.")] string? search = null,
        [Description("Connection name. Use 'all' for every address book. Omit only if a default has been set.")] string? connection = null,
        [Description("How many to return per connection.")] int count = 25,
        CancellationToken ct = default)
    {
        var all = await connections.ListAsync(UserId, ct);
        var resolution = ConnectionResolver.Resolve(
            all, Capabilities.PeopleRead, connection, isWrite: false);

        if (!resolution.IsActionable) return Explain(resolution, Capabilities.PeopleRead, all);

        var found = new List<Contact>();
        var failures = new List<string>();

        await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                foreach (var x in await people.ListContactsAsync(c, search, Math.Clamp(count, 1, 100), ct))
                    lock (found) found.Add(x);
            }
            catch (MailAuthRequiredException) { lock (failures) failures.Add($"{c.Label} needs re-authorising"); }
            catch (PeopleException ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
            catch (Exception ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
        }));

        var ordered = found.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var refs = index.Remember(UserId,
            ordered.Select(c => (c.Id, c.ConnectionLabel, c.Name, c.PrimaryAddress ?? "")).ToList(),
            ItemKind.Contact);

        var sb = new StringBuilder();

        if (ordered.Count == 0)
        {
            sb.AppendLine(search is null
                ? "No contacts saved."
                : $"No contacts matching \"{Untrusted.Scrub(search)}\".");
        }
        else
        {
            sb.AppendLine($"{ordered.Count} contact{(ordered.Count == 1 ? "" : "s")}:");

            for (var n = 0; n < ordered.Count; n++)
            {
                var c = ordered[n];
                sb.Append($"{n + 1}. [{refs[n].Ref}] ");
                if (resolution.Connections.Count > 1) sb.Append($"[{c.ConnectionLabel}] ");
                sb.Append(Untrusted.Scrub(c.Name));

                if (c.PrimaryAddress is { } a) sb.Append($" — {Untrusted.Scrub(a)}");

                var detail = new List<string>();
                if (c.JobTitle is not null) detail.Add(Untrusted.Scrub(c.JobTitle));
                if (c.Company is not null) detail.Add(Untrusted.Scrub(c.Company));
                if (c.MobilePhone is not null) detail.Add(Untrusted.Scrub(c.MobilePhone));
                if (detail.Count > 0) sb.Append($" ({string.Join(", ", detail)})");

                sb.AppendLine();
            }
        }

        foreach (var f in failures) sb.AppendLine($"Note: {f}.");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "save_contact", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Add somebody to the user's own contacts. Always name the connection when more than one address book could be used.")]
    public async Task<string> SaveContact(
        [Description("Their name, as it should appear.")] string name,
        [Description("Email address.")] string? email = null,
        [Description("Mobile number.")] string? phone = null,
        [Description("Company.")] string? company = null,
        [Description("Job title.")] string? title = null,
        [Description("Anything worth remembering about them.")] string? notes = null,
        [Description("Which connection's address book, e.g. 'work'. Required when more than one could be used.")] string? connection = null,
        CancellationToken ct = default)
    {
        var all = await connections.ListAsync(UserId, ct);
        var resolution = ConnectionResolver.Resolve(
            all, Capabilities.ContactsWrite, connection, isWrite: true);

        if (!resolution.IsActionable) return Explain(resolution, Capabilities.ContactsWrite, all);

        // Saving the same person into three address books is not what "all" means to
        // anyone, and FanOut carries no single Connection to dereference.
        if (resolution.Kind is ResolutionKind.FanOut)
            return "I can only save it to one address book — which one: "
                 + string.Join(", ", resolution.Connections.Select(x => x.Label)) + "?";

        var c = resolution.Connection!;

        try
        {
            var saved = await people.CreateContactAsync(c, new ContactDetails
            {
                Name = name,
                Addresses = string.IsNullOrWhiteSpace(email) ? null : [email.Trim()],
                MobilePhone = phone,
                Company = company,
                JobTitle = title,
                Notes = notes,
            }, ct);

            var handle = index.Remember(UserId,
                [(saved.Id, c.Label, saved.Name, saved.PrimaryAddress ?? "")], ItemKind.Contact)[0];

            return $"Saved {Untrusted.Scrub(saved.Name)}"
                 + (saved.PrimaryAddress is { } a ? $" ({Untrusted.Scrub(a)})" : "")
                 + $" to {c.Label}. Handle {handle.Ref}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (PeopleException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "update_contact", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Change a saved contact. Pass the handle from the last listing. Only what you give is changed; everything else is left alone.")]
    public async Task<string> UpdateContact(
        [Description("The handle in square brackets from the listing.")] string contact,
        [Description("New name.")] string? name = null,
        [Description("New email address. Replaces the existing addresses.")] string? email = null,
        [Description("New mobile number.")] string? phone = null,
        [Description("New company.")] string? company = null,
        [Description("New job title.")] string? title = null,
        [Description("New notes. Replaces what is there.")] string? notes = null,
        CancellationToken ct = default)
    {
        var item = index.Resolve(UserId, contact);
        if (item is null)
            return "I don't have that one — list your contacts first, then tell me which.";

        if (item.Kind is not ItemKind.Contact)
            return $"\"{item.Ref}\" is "
                 + (item.Kind is ItemKind.Event ? "a calendar event" : "an email")
                 + $" — {Untrusted.Scrub(item.Subject)}. That isn't a contact.";

        var c = await connections.FindByLabelAsync(UserId, item.ConnectionLabel, ct);
        if (c is null) return $"The {item.ConnectionLabel} connection has gone.";

        var cap = c.Capabilities.FirstOrDefault(x => x.Name == Capabilities.ContactsWrite);
        if (cap is null)
            return $"{c.Label} was connected before Outpost asked for permission to change "
                 + $"contacts. Reconnecting it at {urls.Connections} fixes that.";
        if (!cap.Usable)
            return ConnectionResolution.Unavailable(c, Capabilities.ContactsWrite).SpokenSummary;

        var changes = new ContactDetails
        {
            Name = name,
            Addresses = string.IsNullOrWhiteSpace(email) ? null : [email.Trim()],
            MobilePhone = phone,
            Company = company,
            JobTitle = title,
            Notes = notes,
        };

        if (changes.IsEmpty) return "Tell me what to change.";

        try
        {
            var saved = await people.UpdateContactAsync(c, item.ItemId, changes, ct);

            return $"Updated {Untrusted.Scrub(saved.Name)}"
                 + (saved.PrimaryAddress is { } a ? $" ({Untrusted.Scrub(a)})" : "")
                 + $" on {c.Label}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (PeopleException ex) { return ex.Message; }
    }

    /// <summary>
    /// A connection that predates these scopes has no such capability at all, which the
    /// generic "no connection set up for that yet" describes uselessly — the user has the
    /// connection, and the fix is one reconnection rather than a new setup.
    /// <para>
    /// Conditional on them HAVING a Microsoft connection, though. "Reconnect one" is
    /// useless advice to somebody who has not connected anything, and the resolution
    /// cannot tell the two apart on its own: a connection predating the scope does not
    /// declare the capability, so it is absent from Degraded as well as from the
    /// candidates.
    /// </para>
    /// </summary>
    private string Explain(
        ConnectionResolution resolution, string capability, IReadOnlyList<Connection> all)
    {
        if (resolution.Kind is not ResolutionKind.None) return resolution.SpokenSummary;

        var microsoft = all.Count(c => c.Provider == ProviderKind.Microsoft);
        if (microsoft == 0) return resolution.SpokenSummary;

        var what = capability == Capabilities.ContactsWrite ? "change your contacts" : "look people up";

        return $"None of your {(microsoft == 1 ? "connection" : "connections")} can {what} yet — "
             + $"that permission was added after {(microsoft == 1 ? "it was" : "they were")} set up. "
             + $"Reconnecting at {urls.Connections} grants it.";
    }
}
