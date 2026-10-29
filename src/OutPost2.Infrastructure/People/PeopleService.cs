using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.People;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Infrastructure.People;

public interface IPeopleService
{
    /// <summary>
    /// Everyone matching a name, across relevance, the directory and the address book,
    /// merged and deduplicated by address. Ordered best-first.
    /// </summary>
    Task<IReadOnlyList<Person>> FindAsync(
        Connection connection, string name, int top = 10, CancellationToken ct = default);

    /// <summary>
    /// The user's own contacts. <paramref name="nameLike"/> is matched HERE, not by Graph
    /// — see the note on <see cref="PeopleService"/>.
    /// </summary>
    Task<IReadOnlyList<Contact>> ListContactsAsync(
        Connection connection, string? nameLike = null, int top = 50, CancellationToken ct = default);

    Task<Contact> CreateContactAsync(
        Connection connection, ContactDetails details, CancellationToken ct = default);

    Task<Contact> UpdateContactAsync(
        Connection connection, string contactId, ContactDetails changes, CancellationToken ct = default);
}

/// <summary>
/// People lookup and personal contacts over Graph (OP2-77).
///
/// <para>
/// Three sources answer "who is Justin", and they are genuinely different questions.
/// <c>/me/people</c> ranks by how much the user actually deals with someone, which is
/// almost always the right answer for a spoken name. <c>/users</c> is the organisation's
/// directory, which knows the colleague you have never emailed. <c>/me/contacts</c> is
/// the user's own address book, which is the only one of the three that holds people
/// outside the company. All three are queried and merged, and each result says where it
/// came from, because "in your contacts" and "in the company directory" tell the user
/// different things about who they are about to write to.
/// </para>
///
/// <para>
/// <b>Contacts cannot be searched by name at the server.</b> Graph supports exactly one
/// filter on <c>/me/contacts</c> — equality on an email address — and no <c>$search</c>,
/// no <c>startswith</c>, nothing on <c>displayName</c>. Verified against the v1.0
/// reference rather than assumed, because the obvious <c>$filter=startswith(displayName…)</c>
/// returns a 400 that reads like a malformed query. So contacts are paged down and matched
/// in memory, which is fine for an address book and would not be for a mailbox.
/// </para>
/// </summary>
public sealed class PeopleService(
    IMicrosoftConnectionServiceAccessor tokens,
    IHttpClientFactory httpFactory,
    ILogger<PeopleService> log) : IPeopleService
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>How deep to page the address book when matching a name locally.</summary>
    private const int ContactScanLimit = 500;

    private async Task<HttpClient> ClientAsync(Connection connection, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(connection, ct)
            ?? throw new MailAuthRequiredException(connection.Label);

        var http = httpFactory.CreateClient("graph");
        http.BaseAddress = new Uri(Graph + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    // ── lookup ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Person>> FindAsync(
        Connection connection, string name, int top = 10, CancellationToken ct = default)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return [];

        using var http = await ClientAsync(connection, ct);

        var relevant = await RelevantAsync(http, connection, wanted, top, ct);
        var contacts = await ContactMatchesAsync(connection, wanted, ct);

        // The directory is only worth a round trip when the other two came up short. It is
        // the slowest of the three and the least likely to be what was meant: somebody the
        // user has never corresponded with and never saved is rarely who "email Justin"
        // refers to.
        var directory = relevant.Count + contacts.Count >= 3
            ? []
            : await DirectoryAsync(http, connection, wanted, top, ct);

        // Dedupe by address. The same colleague legitimately appears in all three, and
        // reading the same name out three times is worse than any of the sources missing.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<Person>();

        foreach (var p in relevant.Concat(contacts).Concat(directory))
        {
            // An entry with no address is still worth showing — it answers "who is that"
            // even though it cannot be written to — but it cannot be deduped by address.
            if (p.Address.Length > 0 && !seen.Add(p.Address)) continue;
            merged.Add(p);
        }

        return merged
            .OrderByDescending(p => p.Relevance)
            .ThenBy(p => p.IsGroup)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(top, 1, 50))
            .ToList();
    }

    private async Task<List<Person>> RelevantAsync(
        HttpClient http, Connection connection, string name, int top, CancellationToken ct)
    {
        // $search on /me/people is fuzzy and needs no ConsistencyLevel, unlike /users.
        // $skip is not supported alongside it, so there is no paging here by design.
        var q = $"me/people?$search={Uri.EscapeDataString($"\"{name}\"")}&$top={Math.Clamp(top, 1, 50)}";

        try
        {
            using var resp = await http.GetAsync(q, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogInformation("People search unavailable on {Label}: HTTP {Status}.",
                    connection.Label, (int)resp.StatusCode);
                return [];
            }

            var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphPerson>>(ct);
            return (page?.Value ?? []).Select(p => p.ToPerson(connection.Label)).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "People search failed on {Label}.", connection.Label);
            return [];
        }
    }

    private async Task<List<Person>> DirectoryAsync(
        HttpClient http, Connection connection, string name, int top, CancellationToken ct)
    {
        // startswith rather than $search: $search on /users requires ConsistencyLevel
        // eventual AND $count=true, and silently returns nothing useful without both.
        // startswith needs neither header and is predictable, at the cost of not matching
        // mid-word — acceptable, since a spoken name is nearly always a prefix.
        var esc = name.Replace("'", "''");
        var filter =
            $"startswith(displayName,'{esc}') or startswith(givenName,'{esc}') " +
            $"or startswith(surname,'{esc}') or startswith(mail,'{esc}')";

        var q = $"users?$filter={Uri.EscapeDataString(filter)}" +
                $"&$top={Math.Clamp(top, 1, 50)}" +
                "&$select=id,displayName,mail,userPrincipalName,jobTitle,department,officeLocation";

        try
        {
            using var resp = await http.GetAsync(q, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogInformation("Directory lookup unavailable on {Label}: HTTP {Status}.",
                    connection.Label, (int)resp.StatusCode);
                return [];
            }

            var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphUser>>(ct);
            return (page?.Value ?? []).Select(u => u.ToPerson(connection.Label)).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Directory lookup failed on {Label}.", connection.Label);
            return [];
        }
    }

    private async Task<List<Person>> ContactMatchesAsync(
        Connection connection, string name, CancellationToken ct)
    {
        try
        {
            var contacts = await ListContactsAsync(connection, name, 50, ct);
            return contacts.Select(c => new Person
            {
                Id = c.Id,
                ConnectionLabel = c.ConnectionLabel,
                Name = c.Name,
                Address = c.PrimaryAddress ?? "",
                OtherAddresses = c.Addresses.Skip(1).ToList(),
                JobTitle = c.JobTitle,
                Company = c.Company,
                Phone = c.MobilePhone ?? (c.BusinessPhones.Count > 0 ? c.BusinessPhones[0] : null),
                Source = PersonSource.Contact,

                // Above an unweighted directory hit and below a real relevance score: a
                // person the user bothered to save is a better guess than one who merely
                // exists in the tenant.
                Relevance = 1.0,
            }).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Contact lookup failed on {Label}.", connection.Label);
            return [];
        }
    }

    // ── contacts ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Contact>> ListContactsAsync(
        Connection connection, string? nameLike = null, int top = 50, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var all = new List<Contact>();
        var url = "me/contacts?$top=100&$orderby=displayName" +
                  "&$select=id,displayName,emailAddresses,mobilePhone,businessPhones," +
                  "companyName,jobTitle,personalNotes";

        // Paged because the name match happens here rather than at the server, so a
        // contact on page three is invisible unless page three is fetched. Capped, because
        // walking an unbounded address book inside a voice turn is its own failure.
        while (url is not null && all.Count < ContactScanLimit)
        {
            using var resp = await http.GetAsync(url, ct);

            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden)
                throw new PeopleException(
                    $"{connection.Label} hasn't been given access to your contacts — reconnect it to grant it.");

            if (!resp.IsSuccessStatusCode)
                throw new PeopleException(
                    $"Couldn't read contacts on {connection.Label}: {(int)resp.StatusCode}.");

            var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphContact>>(ct);
            all.AddRange((page?.Value ?? []).Select(c => c.ToContact(connection.Label)));

            url = Next(page?.NextLink);
        }

        var matched = string.IsNullOrWhiteSpace(nameLike)
            ? all
            : all.Where(c => Matches(c, nameLike)).ToList();

        return matched.Take(Math.Clamp(top, 1, 200)).ToList();
    }

    /// <summary>
    /// Name, company and address all count. Somebody searching "acme" means the person
    /// they know through Acme, and a match that only looked at displayName would miss
    /// the one thing they could remember.
    /// </summary>
    private static bool Matches(Contact c, string said)
    {
        var needle = said.Trim();

        return c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (c.Company?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || c.Addresses.Any(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Contact> CreateContactAsync(
        Connection connection, ContactDetails details, CancellationToken ct = default)
    {
        if (details.IsEmpty) throw new PeopleException("Nothing was given to save.");

        using var http = await ClientAsync(connection, ct);

        using var resp = await http.PostAsJsonAsync("me/contacts", Payload(details), ct);
        await ThrowIfFailed(resp, connection, "save that contact", ct);

        var created = await resp.Content.ReadFromJsonAsync<GraphContact>(ct)
            ?? throw new PeopleException("Microsoft saved the contact but returned nothing to read back.");

        return created.ToContact(connection.Label);
    }

    public async Task<Contact> UpdateContactAsync(
        Connection connection, string contactId, ContactDetails changes, CancellationToken ct = default)
    {
        if (changes.IsEmpty) throw new PeopleException("Nothing to change was given.");

        using var http = await ClientAsync(connection, ct);

        using var resp = await http.PatchAsJsonAsync(
            $"me/contacts/{Uri.EscapeDataString(contactId)}", Payload(changes), ct);

        await ThrowIfFailed(resp, connection, "change that contact", ct);

        var updated = await resp.Content.ReadFromJsonAsync<GraphContact>(ct)
            ?? throw new PeopleException("Microsoft accepted the change but returned nothing to read back.");

        return updated.ToContact(connection.Label);
    }

    /// <summary>Only the fields that were set, so a PATCH never clears what it was not asked about.</summary>
    private static Dictionary<string, object?> Payload(ContactDetails d)
    {
        var p = new Dictionary<string, object?>();

        if (d.Name is { } n) p["displayName"] = n;
        if (d.GivenName is { } g) p["givenName"] = g;
        if (d.Surname is { } s) p["surname"] = s;
        if (d.MobilePhone is { } m) p["mobilePhone"] = m;
        if (d.Company is { } c) p["companyName"] = c;
        if (d.JobTitle is { } j) p["jobTitle"] = j;
        if (d.Notes is { } notes) p["personalNotes"] = notes;

        if (d.Addresses is { } addresses)
            p["emailAddresses"] = addresses
                .Select(a => new { address = a, name = d.Name ?? a })
                .ToArray();

        return p;
    }

    private static string? Next(string? absolute)
    {
        if (string.IsNullOrWhiteSpace(absolute)) return null;

        // Graph hands back an absolute nextLink and the client has a BaseAddress, so the
        // relative form is what keeps the two consistent.
        return absolute.StartsWith(Graph + "/", StringComparison.OrdinalIgnoreCase)
            ? absolute[(Graph.Length + 1)..]
            : absolute;
    }

    private static async Task ThrowIfFailed(
        HttpResponseMessage resp, Connection connection, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden)
            throw new PeopleException(
                $"{connection.Label} hasn't been given permission to change your contacts — reconnect it to grant it.");

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new PeopleException(
            $"Couldn't {what} on {connection.Label}: {(int)resp.StatusCode}. "
            + (body.Length <= 300 ? body : body[..300] + "…"));
    }

    // ── wire types ───────────────────────────────────────────────────────────

    private sealed record GraphList<T>(
        [property: JsonPropertyName("value")] List<T>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink = null);

    private sealed record GraphPerson(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("jobTitle")] string? JobTitle,
        [property: JsonPropertyName("department")] string? Department,
        [property: JsonPropertyName("companyName")] string? CompanyName,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName,
        [property: JsonPropertyName("scoredEmailAddresses")] List<ScoredAddress>? ScoredEmailAddresses,
        [property: JsonPropertyName("phones")] List<GraphPhone>? Phones,
        [property: JsonPropertyName("personType")] GraphPersonType? PersonType)
    {
        public Person ToPerson(string label)
        {
            var ranked = (ScoredEmailAddresses ?? [])
                .OrderByDescending(a => a.RelevanceScore)
                .Select(a => a.Address)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a!)
                .ToList();

            // userPrincipalName is a usable address and is sometimes the only one present,
            // but it comes back as "" rather than null for a group, so it is checked for
            // content rather than for null.
            if (ranked.Count == 0 && !string.IsNullOrWhiteSpace(UserPrincipalName))
                ranked.Add(UserPrincipalName);

            return new Person
            {
                Id = Id,
                ConnectionLabel = label,
                Name = string.IsNullOrWhiteSpace(DisplayName) ? (ranked.FirstOrDefault() ?? "(unknown)") : DisplayName,
                Address = ranked.FirstOrDefault() ?? "",
                OtherAddresses = ranked.Skip(1).ToList(),
                JobTitle = Blank(JobTitle),
                Department = Blank(Department),
                Company = Blank(CompanyName),
                Phone = Phones?.FirstOrDefault()?.Number,
                Source = PersonSource.Relevant,
                IsGroup = string.Equals(PersonType?.Class, "Group", StringComparison.OrdinalIgnoreCase),

                // Graph's own ranking, offset so any relevance beats a contact (1.0) and a
                // contact beats a bare directory entry (0.0).
                Relevance = 2.0 + ((ScoredEmailAddresses ?? []).Select(a => a.RelevanceScore).DefaultIfEmpty(0).Max()),
            };
        }
    }

    private sealed record ScoredAddress(
        [property: JsonPropertyName("address")] string? Address,
        [property: JsonPropertyName("relevanceScore")] double RelevanceScore);

    private sealed record GraphPhone(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("number")] string? Number);

    private sealed record GraphPersonType(
        [property: JsonPropertyName("class")] string? Class,
        [property: JsonPropertyName("subclass")] string? Subclass);

    private sealed record GraphUser(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("mail")] string? Mail,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName,
        [property: JsonPropertyName("jobTitle")] string? JobTitle,
        [property: JsonPropertyName("department")] string? Department,
        [property: JsonPropertyName("officeLocation")] string? OfficeLocation)
    {
        public Person ToPerson(string label) => new()
        {
            Id = Id,
            ConnectionLabel = label,

            // mail before userPrincipalName. A UPN is often an address and sometimes is
            // not one at all, so preferring it would hand back something unmailable
            // whenever the mailbox is under a different domain from the sign-in name.
            Address = Blank(Mail) ?? Blank(UserPrincipalName) ?? "",
            Name = Blank(DisplayName) ?? Blank(Mail) ?? "(unknown)",
            JobTitle = Blank(JobTitle),
            Department = Blank(Department),
            Source = PersonSource.Directory,
            Relevance = 0.0,
        };
    }

    private sealed record GraphContact(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("emailAddresses")] List<GraphEmailAddress>? EmailAddresses,
        [property: JsonPropertyName("mobilePhone")] string? MobilePhone,
        [property: JsonPropertyName("businessPhones")] List<string>? BusinessPhones,
        [property: JsonPropertyName("companyName")] string? CompanyName,
        [property: JsonPropertyName("jobTitle")] string? JobTitle,
        [property: JsonPropertyName("personalNotes")] string? PersonalNotes)
    {
        public Contact ToContact(string label) => new()
        {
            Id = Id,
            ConnectionLabel = label,
            Name = Blank(DisplayName) ?? "(no name)",
            Addresses = (EmailAddresses ?? [])
                .Select(a => a.Address)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a!)
                .ToList(),
            MobilePhone = Blank(MobilePhone),
            BusinessPhones = BusinessPhones ?? [],
            Company = Blank(CompanyName),
            JobTitle = Blank(JobTitle),
            Notes = Blank(PersonalNotes),
        };
    }

    private sealed record GraphEmailAddress(
        [property: JsonPropertyName("address")] string? Address,
        [property: JsonPropertyName("name")] string? Name);

    /// <summary>Graph returns "" as well as null for an absent field, and the two must read alike.</summary>
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public sealed class PeopleException(string message) : Exception(message);
