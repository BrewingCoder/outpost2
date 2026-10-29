using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Calendar;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Infrastructure.Calendar;

public interface ICalendarService
{
    /// <summary>
    /// Occurrences between two instants, expanded — a weekly stand-up appears once per
    /// week rather than once as a series. Ordered by start.
    /// </summary>
    Task<IReadOnlyList<CalendarEventSummary>> ListAsync(
        Connection connection, DateTimeOffset from, DateTimeOffset to, int top = 50,
        CancellationToken ct = default);

    Task<CalendarEvent?> ReadAsync(Connection connection, string eventId, CancellationToken ct = default);

    /// <summary>Creates an event and returns it as stored, so the caller can read back the real times.</summary>
    Task<CalendarEvent> CreateAsync(Connection connection, OutgoingEvent ev, CancellationToken ct = default);

    /// <summary>Patches only the fields that were set. Returns the event as it now stands.</summary>
    Task<CalendarEvent> UpdateAsync(
        Connection connection, string eventId, EventChanges changes, CancellationToken ct = default);

    /// <summary>
    /// Cancels a meeting the user organised, notifying attendees, or deletes an
    /// appointment that has none. Returns what was actually done, because the two read
    /// back differently to the user.
    /// </summary>
    Task<string> CancelAsync(
        Connection connection, string eventId, string? message, CancellationToken ct = default);

    /// <summary>Answers an invitation. Always notifies the organiser.</summary>
    Task RespondAsync(
        Connection connection, string eventId, InviteReply reply, string? comment,
        CancellationToken ct = default);

    /// <summary>
    /// The mailbox's own timezone, as a .NET <see cref="TimeZoneInfo"/>. Everything a user
    /// says about time is in this zone, and it is cached per connection.
    /// </summary>
    Task<TimeZoneInfo> TimeZoneAsync(Connection connection, CancellationToken ct = default);
}

/// <summary>
/// Microsoft 365 calendar over Graph (OP2-75).
///
/// <para>
/// <b>Timezone is the whole problem.</b> Graph stores events as a naive local time plus a
/// zone name, and will return them in any zone the caller asks for. The user, meanwhile,
/// says "two o'clock" and means two o'clock where their mailbox lives — which is not
/// necessarily where the server lives, and is an hour different from itself twice a year.
/// </para>
///
/// <para>
/// So: the mailbox's own timezone is read once from mailbox settings and cached, reads ask
/// Graph to return times already converted into it via the <c>Prefer: outlook.timezone</c>
/// header, and writes send a naive local time alongside that same zone name and let Graph
/// resolve it. The DST arithmetic is therefore done once, by the system that owns the
/// event, instead of twice by us on either side of a round trip.
/// </para>
/// </summary>
public sealed class CalendarService(
    IMicrosoftConnectionServiceAccessor tokens,
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<CalendarService> log) : ICalendarService
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>What Graph asks for and gives back: a Windows zone id such as "GMT Standard Time".</summary>
    private sealed record ZoneCache(string GraphName, TimeZoneInfo Zone);

    /// <summary>
    /// The zone lives in the shared memory cache rather than in a field, because this
    /// service is SCOPED — it resolves an access token per request — so an instance field
    /// would be thrown away every tool call and re-read mailbox settings every time.
    /// A mailbox timezone changes when somebody travels and resets it, not often.
    /// </summary>
    private static readonly TimeSpan ZoneLifetime = TimeSpan.FromHours(6);

    private static string ZoneKey(Connection c) => $"op2:calzone:{c.Id}";

    private const string FullSelect =
        "id,subject,organizer,start,end,isAllDay,location,isOnlineMeeting,onlineMeeting," +
        "responseStatus,showAs,type,isCancelled,attendees,body,recurrence,isOrganizer,seriesMasterId";

    private const string ListSelect =
        "id,subject,organizer,start,end,isAllDay,location,isOnlineMeeting," +
        "responseStatus,showAs,type,isCancelled,seriesMasterId";

    // ── plumbing ─────────────────────────────────────────────────────────────

    private async Task<HttpClient> ClientAsync(Connection connection, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(connection, ct)
            ?? throw new MailAuthRequiredException(connection.Label);

        var http = httpFactory.CreateClient("graph");
        http.BaseAddress = new Uri(Graph + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<TimeZoneInfo> TimeZoneAsync(Connection connection, CancellationToken ct = default) =>
        (await ZoneAsync(connection, ct)).Zone;

    private async Task<ZoneCache> ZoneAsync(Connection connection, CancellationToken ct)
    {
        if (cache.TryGetValue<ZoneCache>(ZoneKey(connection), out var cached) && cached is not null)
            return cached;

        var fallback = new ZoneCache("UTC", TimeZoneInfo.Utc);

        try
        {
            using var http = await ClientAsync(connection, ct);
            using var resp = await http.GetAsync("me/mailboxSettings?$select=timeZone", ct);

            // MailboxSettings is an OPTIONAL scope, so a connection consented before it was
            // asked for answers 403 here. That must not break the calendar — it only means
            // we do not know the user's zone and have to say so rather than guess.
            if (!resp.IsSuccessStatusCode)
            {
                log.LogInformation(
                    "Mailbox timezone unavailable for {Label} (HTTP {Status}); using UTC.",
                    connection.Label, (int)resp.StatusCode);
                return Remember(connection, fallback);
            }

            var settings = await resp.Content.ReadFromJsonAsync<MailboxSettings>(ct);
            var name = settings?.TimeZone;
            if (string.IsNullOrWhiteSpace(name)) return Remember(connection, fallback);

            return Remember(connection, new ZoneCache(name, Resolve(name)));
        }
        catch (MailAuthRequiredException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the mailbox timezone for {Label}; using UTC.", connection.Label);
            return Remember(connection, fallback);
        }
    }

    private ZoneCache Remember(Connection connection, ZoneCache zone)
    {
        cache.Set(ZoneKey(connection), zone, ZoneLifetime);
        return zone;
    }

    /// <summary>
    /// Graph speaks Windows zone ids ("GMT Standard Time"), but a mailbox can also be set
    /// to an IANA one ("Europe/London") and .NET resolves whichever the host understands
    /// natively. Trying both covers a Linux container and a Windows workstation without
    /// shipping a mapping table of our own.
    /// </summary>
    private static TimeZoneInfo Resolve(string name)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var iana))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
            catch (TimeZoneNotFoundException) { }
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out var windows))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
            catch (TimeZoneNotFoundException) { }
        }

        return TimeZoneInfo.Utc;
    }

    // ── reads ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CalendarEventSummary>> ListAsync(
        Connection connection, DateTimeOffset from, DateTimeOffset to, int top = 50,
        CancellationToken ct = default)
    {
        var zone = await ZoneAsync(connection, ct);
        using var http = await ClientAsync(connection, ct);

        // calendarView, not /events. /events returns the SERIES, so a weekly stand-up shows
        // up once with its original start date and "what's on Thursday" comes back empty.
        // calendarView expands occurrences across the window, which is the question a
        // person is actually asking.
        var q = $"me/calendarView?startDateTime={Iso(from)}&endDateTime={Iso(to)}" +
                $"&$top={Math.Clamp(top, 1, 200)}" +
                "&$orderby=start/dateTime" +
                $"&$select={ListSelect}";

        using var req = new HttpRequestMessage(HttpMethod.Get, q);
        WithZone(req, zone.GraphName);

        using var resp = await http.SendAsync(req, ct);
        await ThrowIfFailed(resp, connection, "read the calendar", ct);

        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphEvent>>(ct);

        return (page?.Value ?? [])
            .Select(e => e.ToSummary(connection.Label, zone.Zone))
            .OrderBy(e => e.Start)
            .ToList();
    }

    public async Task<CalendarEvent?> ReadAsync(
        Connection connection, string eventId, CancellationToken ct = default)
    {
        var zone = await ZoneAsync(connection, ct);
        using var http = await ClientAsync(connection, ct);

        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"me/events/{Uri.EscapeDataString(eventId)}?$select={FullSelect}");
        WithZone(req, zone.GraphName);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ThrowIfFailed(resp, connection, "read that event", ct);

        var e = await resp.Content.ReadFromJsonAsync<GraphEvent>(ct);
        return e?.ToEvent(connection.Label, zone.Zone);
    }

    // ── writes ───────────────────────────────────────────────────────────────

    public async Task<CalendarEvent> CreateAsync(
        Connection connection, OutgoingEvent ev, CancellationToken ct = default)
    {
        var zone = await ZoneAsync(connection, ct);
        using var http = await ClientAsync(connection, ct);

        var payload = new Dictionary<string, object?>
        {
            ["subject"] = ev.Subject,
            ["start"] = Naive(ev.Start, zone.GraphName),
            ["end"] = Naive(ev.End, zone.GraphName),
            ["isAllDay"] = ev.IsAllDay,
            ["showAs"] = GraphShowAs(ev.ShowAs),
            ["attendees"] =
                ev.RequiredAttendees.Select(a => Attendee(a, "required"))
                  .Concat(ev.OptionalAttendees.Select(a => Attendee(a, "optional")))
                  .ToArray(),
        };

        if (!string.IsNullOrWhiteSpace(ev.Location))
            payload["location"] = new { displayName = ev.Location };

        if (!string.IsNullOrWhiteSpace(ev.Body))
            payload["body"] = new { contentType = "HTML", content = MarkdownToHtml.Render(ev.Body) };

        if (ev.IsOnlineMeeting)
        {
            payload["isOnlineMeeting"] = true;
            payload["onlineMeetingProvider"] = "teamsForBusiness";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "me/events")
        {
            Content = JsonContent.Create(payload),
        };
        WithZone(req, zone.GraphName);

        using var resp = await http.SendAsync(req, ct);
        await ThrowIfFailed(resp, connection, "create that event", ct);

        var created = await resp.Content.ReadFromJsonAsync<GraphEvent>(ct)
            ?? throw new CalendarException("Microsoft accepted the event but returned nothing to read back.");

        return created.ToEvent(connection.Label, zone.Zone);
    }

    public async Task<CalendarEvent> UpdateAsync(
        Connection connection, string eventId, EventChanges changes, CancellationToken ct = default)
    {
        if (changes.IsEmpty)
            throw new CalendarException("Nothing to change was given.");

        var zone = await ZoneAsync(connection, ct);
        using var http = await ClientAsync(connection, ct);

        // Only what was asked for. Echoing unchanged fields back would clobber a
        // concurrent edit, and for a meeting it re-notifies every attendee about
        // properties nobody touched.
        var payload = new Dictionary<string, object?>();
        if (changes.Subject is { } s) payload["subject"] = s;
        if (changes.Start is { } st) payload["start"] = Naive(st, zone.GraphName);
        if (changes.End is { } en) payload["end"] = Naive(en, zone.GraphName);
        if (changes.Location is { } loc) payload["location"] = new { displayName = loc };
        if (changes.ShowAs is { } sa) payload["showAs"] = GraphShowAs(sa);

        using var req = new HttpRequestMessage(
            HttpMethod.Patch, $"me/events/{Uri.EscapeDataString(eventId)}")
        {
            Content = JsonContent.Create(payload),
        };
        WithZone(req, zone.GraphName);

        using var resp = await http.SendAsync(req, ct);
        await ThrowIfFailed(resp, connection, "change that event", ct);

        var updated = await resp.Content.ReadFromJsonAsync<GraphEvent>(ct)
            ?? throw new CalendarException("Microsoft accepted the change but returned nothing to read back.");

        return updated.ToEvent(connection.Label, zone.Zone);
    }

    public async Task<string> CancelAsync(
        Connection connection, string eventId, string? message, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // Which verb is correct depends on who owns the meeting, and getting it wrong is
        // visible to other people: /cancel on someone else's event is refused, while
        // DELETE on your own silently removes it from your calendar and leaves every
        // attendee holding a meeting that no longer exists.
        var existing = await ReadAsync(connection, eventId, ct)
            ?? throw new CalendarException("That event no longer exists.");

        if (existing.IOrganised && existing.Attendees.Count > 0)
        {
            using var resp = await http.PostAsJsonAsync(
                $"me/events/{Uri.EscapeDataString(eventId)}/cancel",
                new { Comment = message ?? "" }, ct);

            await ThrowIfFailed(resp, connection, "cancel that meeting", ct);

            var who = existing.Attendees.Count == 1 ? "the attendee" : $"all {existing.Attendees.Count} attendees";
            return $"Cancelled \"{existing.Subject}\" and told {who}.";
        }

        using (var resp = await http.DeleteAsync($"me/events/{Uri.EscapeDataString(eventId)}", ct))
        {
            await ThrowIfFailed(resp, connection, "remove that event", ct);
        }

        return existing.IOrganised
            ? $"Deleted \"{existing.Subject}\". It had no attendees, so nobody was notified."
            : $"Removed \"{existing.Subject}\" from your calendar. The organiser was not told — "
              + "decline it instead if you want them to know.";
    }

    public async Task RespondAsync(
        Connection connection, string eventId, InviteReply reply, string? comment,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var verb = reply switch
        {
            InviteReply.Accept => "accept",
            InviteReply.Decline => "decline",
            InviteReply.Tentative => "tentativelyAccept",
            _ => throw new ArgumentOutOfRangeException(nameof(reply)),
        };

        // sendResponse: the organiser finds out. Suppressing it would leave them chasing
        // an answer the user believes they have given.
        using var resp = await http.PostAsJsonAsync(
            $"me/events/{Uri.EscapeDataString(eventId)}/{verb}",
            new { Comment = comment ?? "", SendResponse = true }, ct);

        await ThrowIfFailed(resp, connection, $"{verb} that invitation", ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks Graph to return every time already converted into the mailbox's zone, so the
    /// conversion is done by the system that owns the DST rules for the event.
    /// </summary>
    private static void WithZone(HttpRequestMessage req, string graphZoneName) =>
        req.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{graphZoneName}\"");

    private static string Iso(DateTimeOffset t) =>
        Uri.EscapeDataString(t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));

    /// <summary>
    /// A wall-clock time with no offset, plus the zone it is to be read in. Sending an
    /// offset instead would be correct today and wrong across a DST change, because the
    /// offset a user means by "9am every Monday" is not fixed.
    /// </summary>
    private static object Naive(DateTime local, string graphZoneName) => new
    {
        dateTime = local.ToString("yyyy-MM-ddTHH:mm:ss"),
        timeZone = graphZoneName,
    };

    private static object Attendee(string address, string type) => new
    {
        emailAddress = new { address },
        type,
    };

    private static string GraphShowAs(Availability a) => a switch
    {
        Availability.Free => "free",
        Availability.Tentative => "tentative",
        Availability.OutOfOffice => "oof",
        Availability.WorkingElsewhere => "workingElsewhere",
        _ => "busy",
    };

    private static async Task ThrowIfFailed(
        HttpResponseMessage resp, Connection connection, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct);

        // 403 on the calendar means one specific thing and it has one specific fix: the
        // connection was consented before calendar access was asked for. Saying "forbidden"
        // sends the user looking for a permissions problem in their tenant.
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden)
            throw new CalendarException(
                $"{connection.Label} hasn't been given calendar access yet — reconnect it to grant it.");

        throw new CalendarException(
            $"Couldn't {what} on {connection.Label}: {(int)resp.StatusCode}. {Clamp(body)}");
    }

    private static string Clamp(string s) => s.Length <= 400 ? s : s[..400] + "…";

    // ── wire types ───────────────────────────────────────────────────────────

    private sealed record MailboxSettings(
        [property: JsonPropertyName("timeZone")] string? TimeZone);

    private sealed record GraphList<T>([property: JsonPropertyName("value")] List<T>? Value);

    private sealed record GraphEvent(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("organizer")] GraphRecipient? Organizer,
        [property: JsonPropertyName("start")] GraphTime? Start,
        [property: JsonPropertyName("end")] GraphTime? End,
        [property: JsonPropertyName("isAllDay")] bool IsAllDay,
        [property: JsonPropertyName("location")] GraphLocation? Location,
        [property: JsonPropertyName("isOnlineMeeting")] bool IsOnlineMeeting,
        [property: JsonPropertyName("onlineMeeting")] GraphOnlineMeeting? OnlineMeeting,
        [property: JsonPropertyName("responseStatus")] GraphResponseStatus? ResponseStatus,
        [property: JsonPropertyName("showAs")] string? ShowAs,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("isCancelled")] bool IsCancelled,
        [property: JsonPropertyName("isOrganizer")] bool IsOrganizer,
        [property: JsonPropertyName("seriesMasterId")] string? SeriesMasterId,
        [property: JsonPropertyName("attendees")] List<GraphAttendee>? Attendees,
        [property: JsonPropertyName("body")] GraphBody? Body,
        [property: JsonPropertyName("recurrence")] GraphRecurrence? Recurrence)
    {
        public CalendarEventSummary ToSummary(string label, TimeZoneInfo zone) => new()
        {
            Id = Id,
            ConnectionLabel = label,
            Subject = string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject,
            Organiser = Organizer?.EmailAddress?.Describe() ?? "",
            Start = Start.Resolve(zone),
            End = End.Resolve(zone),
            IsAllDay = IsAllDay,
            Location = string.IsNullOrWhiteSpace(Location?.DisplayName) ? null : Location.DisplayName,
            IsOnlineMeeting = IsOnlineMeeting,
            MyResponse = ParseResponse(ResponseStatus?.Response),
            ShowAs = ParseShowAs(ShowAs),
            IsRecurring = SeriesMasterId is not null
                          || string.Equals(Type, "occurrence", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(Type, "exception", StringComparison.OrdinalIgnoreCase),
            IsCancelled = IsCancelled,
        };

        public CalendarEvent ToEvent(string label, TimeZoneInfo zone)
        {
            var s = ToSummary(label, zone);

            var text = string.Equals(Body?.ContentType, "html", StringComparison.OrdinalIgnoreCase)
                ? HtmlToText.Convert(Body?.Content)
                : (Body?.Content ?? "");

            return new CalendarEvent
            {
                Id = s.Id,
                ConnectionLabel = s.ConnectionLabel,
                Subject = s.Subject,
                Organiser = s.Organiser,
                Start = s.Start,
                End = s.End,
                IsAllDay = s.IsAllDay,
                Location = s.Location,
                MyResponse = s.MyResponse,
                ShowAs = s.ShowAs,
                IsRecurring = s.IsRecurring,
                IsCancelled = s.IsCancelled,
                IOrganised = IsOrganizer,
                Attendees = (Attendees ?? [])
                    .Select(a => new EventAttendee
                    {
                        Name = a.EmailAddress?.Name ?? a.EmailAddress?.Address ?? "(unknown)",
                        Address = a.EmailAddress?.Address ?? "",
                        IsOptional = string.Equals(a.Type, "optional", StringComparison.OrdinalIgnoreCase),
                        Response = ParseResponse(a.Status?.Response),
                    })
                    .ToList(),
                OnlineMeetingUrl = OnlineMeeting?.JoinUrl,
                Body = HtmlToText.TrimSignature(text),
                Recurrence = Recurrence?.Describe(),
            };
        }
    }

    private static InviteStatus ParseResponse(string? r) => r?.ToLowerInvariant() switch
    {
        "organizer" => InviteStatus.Organiser,
        "accepted" => InviteStatus.Accepted,
        "tentativelyaccepted" => InviteStatus.TentativelyAccepted,
        "declined" => InviteStatus.Declined,
        "notresponded" => InviteStatus.NotResponded,
        _ => InviteStatus.None,
    };

    private static Availability ParseShowAs(string? s) => s?.ToLowerInvariant() switch
    {
        "free" => Availability.Free,
        "tentative" => Availability.Tentative,
        "busy" => Availability.Busy,
        "oof" => Availability.OutOfOffice,
        "workingelsewhere" => Availability.WorkingElsewhere,
        _ => Availability.Unknown,
    };

    private sealed record GraphRecipient(
        [property: JsonPropertyName("emailAddress")] GraphAddress? EmailAddress);

    private sealed record GraphAddress(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("address")] string? Address)
    {
        public string Describe() =>
            string.IsNullOrWhiteSpace(Name) ? (Address ?? "(unknown)") : Name;
    }

    private sealed record GraphAttendee(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("status")] GraphResponseStatus? Status,
        [property: JsonPropertyName("emailAddress")] GraphAddress? EmailAddress);

    private sealed record GraphResponseStatus(
        [property: JsonPropertyName("response")] string? Response);

    private sealed record GraphLocation(
        [property: JsonPropertyName("displayName")] string? DisplayName);

    private sealed record GraphOnlineMeeting(
        [property: JsonPropertyName("joinUrl")] string? JoinUrl);

    private sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record GraphRecurrence(
        [property: JsonPropertyName("pattern")] GraphRecurrencePattern? Pattern)
    {
        /// <summary>A phrase someone would say, not Graph's shape. "every 2 weeks on Tuesday".</summary>
        public string? Describe()
        {
            if (Pattern is null) return null;

            var every = Pattern.Interval <= 1 ? "every" : $"every {Pattern.Interval}";
            var unit = Pattern.Type?.ToLowerInvariant() switch
            {
                "daily" => Pattern.Interval <= 1 ? "day" : "days",
                "weekly" => Pattern.Interval <= 1 ? "week" : "weeks",
                "absolutemonthly" or "relativemonthly" => Pattern.Interval <= 1 ? "month" : "months",
                "absoluteyearly" or "relativeyearly" => Pattern.Interval <= 1 ? "year" : "years",
                _ => null,
            };

            if (unit is null) return null;

            var days = Pattern.DaysOfWeek is { Count: > 0 }
                ? " on " + string.Join(", ", Pattern.DaysOfWeek.Select(Capitalise))
                : "";

            return $"{every} {unit}{days}";
        }

        private static string Capitalise(string d) =>
            d.Length == 0 ? d : char.ToUpperInvariant(d[0]) + d[1..];
    }

    private sealed record GraphRecurrencePattern(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("daysOfWeek")] List<string>? DaysOfWeek);
}

/// <summary>
/// A Graph time: a naive local string plus the zone it belongs to. Graph returns the zone
/// we asked for via <c>Prefer</c>, but it is honoured on a best-effort basis and an
/// unrecognised request comes back in UTC — so the response is always read as what it says
/// it is rather than as what was requested.
/// </summary>
public sealed record GraphTime(
    [property: JsonPropertyName("dateTime")] string? DateTime,
    [property: JsonPropertyName("timeZone")] string? TimeZone);

public static class GraphTimeExtensions
{
    public static DateTimeOffset Resolve(this GraphTime? t, TimeZoneInfo fallbackZone)
    {
        if (t?.DateTime is null) return default;

        if (!System.DateTime.TryParse(
                t.DateTime, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var naive))
            return default;

        // Graph's "UTC" is the one zone name it always honours and always returns when it
        // did not honour the request, so treat it as authoritative rather than assuming
        // the Prefer header took.
        var zone = string.Equals(t.TimeZone, "UTC", StringComparison.OrdinalIgnoreCase)
            ? TimeZoneInfo.Utc
            : fallbackZone;

        naive = System.DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);

        // An invalid local time is a clock that skipped forward over it. Graph should never
        // produce one, but a meeting silently landing at default(DateTimeOffset) would be
        // far worse than one landing an hour late, so it is nudged rather than dropped.
        if (zone.IsInvalidTime(naive)) naive = naive.AddHours(1);

        return new DateTimeOffset(naive, zone.GetUtcOffset(naive));
    }
}

public sealed class CalendarException(string message) : Exception(message);
