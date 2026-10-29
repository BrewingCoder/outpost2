using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Core.Calendar;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// The calendar surface (OP2-75). Same four rules as <see cref="MailTools"/> — speakable
/// handles rather than Graph ids, prose rather than tables, ordinals for reads and handles
/// for writes, and no write ever riding on a default connection.
///
/// <para>
/// Two things are specific to time and both are load-bearing.
/// </para>
///
/// <para>
/// <b>Everything is in the mailbox's own timezone.</b> The user says "two o'clock" and
/// means two o'clock where they are, which is a property of their mailbox and not of this
/// server. Times go in and come out in that zone; see <see cref="CalendarService"/> for
/// how. A tool here never builds a <c>DateTimeOffset</c> from user input.
/// </para>
///
/// <para>
/// <b>A calendar read is a summary, not a dump.</b> Spoken aloud, six events with
/// attendee lists is unusable. Listings give the time, the subject and whether a reply is
/// outstanding; everything else waits for <c>read_event</c> on one of them.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class CalendarTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    ICalendarService calendar,
    IItemIndex index,
    PublicUrls urls)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    // ── reading ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_events", ReadOnly = true, OpenWorld = true)]
    [Description("What is on the calendar. Defaults to today. Pass 'days' for a longer window, or 'date' for a specific day. Omit 'connection' for the default calendar, or pass 'all' to check every one. Each result carries a handle in square brackets — pass that to read or change it.")]
    public async Task<string> ListEvents(
        [Description("Connection name such as 'work' or 'home'. Use 'all' for every calendar. Omit only if a default has been set.")] string? connection = null,
        [Description("How many days from the start date to include. 1 is just that day; 7 is the week ahead.")] int days = 1,
        [Description("The day to start from, as yyyy-MM-dd in the mailbox's own timezone. Omit for today.")] string? date = null,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.CalendarRead, connection, isWrite: false);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        days = Math.Clamp(days, 1, 31);

        var found = new List<(string Label, CalendarEventSummary Item)>();
        var failures = new List<string>();

        // Fan out, as mail does: one slow tenant must not hold up the answer, and a
        // partial calendar now beats a complete one after the turn has moved on.
        await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                // The window is computed PER CONNECTION, because "today" is a different
                // span of instants for a mailbox in London and one in Denver, and a user
                // with both would otherwise see one of them shifted by the other's day.
                var zone = await calendar.TimeZoneAsync(c, ct);
                var (from, to) = Window(zone, date, days);

                foreach (var e in await calendar.ListAsync(c, from, to, 100, ct))
                    lock (found) found.Add((c.Label, e));
            }
            catch (MailAuthRequiredException) { lock (failures) failures.Add($"{c.Label} needs re-authorising"); }
            catch (CalendarException ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
            catch (Exception ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
        }));

        // A cancelled occurrence still comes back from calendarView. It is not something
        // the user has to be anywhere for, so it does not belong in "what's on today".
        var ordered = found.Where(x => !x.Item.IsCancelled).OrderBy(x => x.Item.Start).ToList();

        var refs = index.Remember(UserId,
            ordered.Select(x => (x.Item.Id, x.Label, x.Item.Subject, x.Item.Organiser)).ToList(),
            ItemKind.Event);

        var sb = new StringBuilder();

        if (ordered.Count == 0)
        {
            sb.AppendLine(days == 1 ? "Nothing on." : $"Nothing on in the next {days} days.");
        }
        else
        {
            sb.AppendLine($"{ordered.Count} event{(ordered.Count == 1 ? "" : "s")}:");

            string? lastDay = null;
            for (var n = 0; n < ordered.Count; n++)
            {
                var (label, e) = ordered[n];

                // Group under a day heading once the window spans more than one, so a
                // week read aloud does not become forty undifferentiated lines.
                if (days > 1)
                {
                    var day = DayHeading(e.Start);
                    if (day != lastDay) { sb.AppendLine($"{day}:"); lastDay = day; }
                }

                sb.Append($"{n + 1}. [{refs[n].Ref}] ");
                if (resolution.Connections.Count > 1) sb.Append($"[{label}] ");
                sb.Append($"{When(e)} — {Untrusted.Scrub(e.Subject)}");

                if (e.IsRecurring) sb.Append(" (recurring)");
                if (e.ShowAs is Availability.Free) sb.Append(" (marked free)");
                if (e.ShowAs is Availability.OutOfOffice) sb.Append(" (out of office)");
                if (e.MyResponse is InviteStatus.NotResponded) sb.Append(" — NOT yet answered");
                if (e.MyResponse is InviteStatus.TentativelyAccepted) sb.Append(" — tentative");
                if (e.IsOnlineMeeting) sb.Append(" (online)");
                else if (!string.IsNullOrWhiteSpace(e.Location)) sb.Append($" — {Untrusted.Scrub(e.Location)}");

                sb.AppendLine();
            }
        }

        foreach (var f in failures) sb.AppendLine($"Note: {f}.");
        foreach (var d in resolution.Degraded) sb.AppendLine($"Note: {d.Label} is unavailable ({d.State}).");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "read_event", ReadOnly = true, OpenWorld = true)]
    [Description("Read one event in full — attendees, who has replied, the body and the join link. Pass the handle from the last listing, e.g. 'a7kq', or the number, e.g. '2'.")]
    public async Task<string> ReadEvent(
        [Description("The handle in square brackets from the listing, or the number shown beside it.")] string ev,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(ev, ct, Capabilities.CalendarRead);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            var e = await calendar.ReadAsync(c, item.ItemId, ct);
            if (e is null) return "That event no longer exists.";

            var sb = new StringBuilder();
            sb.AppendLine($"{Untrusted.Scrub(e.Subject)} ({c.Label})");
            sb.AppendLine(When(e.Start, e.End, e.IsAllDay, full: true));
            if (e.Recurrence is not null) sb.AppendLine($"Repeats {e.Recurrence}.");
            if (e.IsCancelled) sb.AppendLine("This event has been CANCELLED.");

            sb.AppendLine(e.IOrganised
                ? "You organised this."
                : $"Organiser: {Untrusted.Scrub(e.Organiser)}");

            if (!string.IsNullOrWhiteSpace(e.Location)) sb.AppendLine($"Location: {Untrusted.Scrub(e.Location)}");
            if (e.OnlineMeetingUrl is not null) sb.AppendLine($"Online: {e.OnlineMeetingUrl}");
            if (e.ShowAs is not Availability.Busy) sb.AppendLine($"Shows as: {Describe(e.ShowAs)}.");

            if (!e.IOrganised)
                sb.AppendLine($"Your reply: {Describe(e.MyResponse)}.");

            if (e.Attendees.Count > 0)
            {
                // Counts first. "Eleven people, two haven't replied" is the answer; the
                // roll call is only useful when it is short enough to hear.
                var answered = e.Attendees.Count(a => a.Response is InviteStatus.Accepted or InviteStatus.Organiser);
                var waiting = e.Attendees.Count(a => a.Response is InviteStatus.NotResponded);
                var declined = e.Attendees.Count(a => a.Response is InviteStatus.Declined);

                sb.Append($"{e.Attendees.Count} attendee{(e.Attendees.Count == 1 ? "" : "s")}: {answered} accepted");
                if (waiting > 0) sb.Append($", {waiting} not replied");
                if (declined > 0) sb.Append($", {declined} declined");
                sb.AppendLine(".");

                if (e.Attendees.Count <= 8)
                    foreach (var a in e.Attendees)
                        sb.AppendLine($"  - {Untrusted.Scrub(a.Name)}"
                            + $" — {Describe(a.Response)}{(a.IsOptional ? ", optional" : "")}");
                else if (declined > 0 || waiting > 0)
                    foreach (var a in e.Attendees.Where(x =>
                                 x.Response is InviteStatus.Declined or InviteStatus.NotResponded).Take(8))
                        sb.AppendLine($"  - {Untrusted.Scrub(a.Name)} — {Describe(a.Response)}");
            }

            sb.AppendLine($"Handle: {item.Ref}");

            if (!string.IsNullOrWhiteSpace(e.Body))
            {
                sb.AppendLine();

                // Fenced for the same reason a mail body is (OP2-125): an invitation body
                // is written by whoever sent it, and anyone who knows the address can send
                // one. A meeting request is a particularly good delivery vehicle, because
                // it arrives in the calendar rather than the inbox.
                sb.AppendLine(Untrusted.Fence("event description", HtmlToText.Clamp(e.Body, 4000)));
            }

            return sb.ToString().TrimEnd();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (CalendarException ex) { return ex.Message; }
    }

    // ── writing ──────────────────────────────────────────────────────────────

    // Destructive, like send_mail and unlike create_issue. With attendees this puts an
    // entry in other people's calendars and mails them an invitation under the user's own
    // name, which cannot be taken back — the same test the mail tools are classified by.
    [McpServerTool(Name = "create_event", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Put something in the calendar, optionally inviting people. Times are the mailbox's own local time, as yyyy-MM-ddTHH:mm. Always name the connection when more than one could be used — inviting people from the wrong account cannot be undone.")]
    public async Task<string> CreateEvent(
        [Description("What it is called.")] string subject,
        [Description("Start, as yyyy-MM-ddTHH:mm in the mailbox's local time, e.g. 2026-11-03T14:00.")] string start,
        [Description("How long, in minutes. Ignored if 'end' is given.")] int minutes = 30,
        [Description("End, as yyyy-MM-ddTHH:mm. Optional — give this or 'minutes'.")] string? end = null,
        [Description("Who to invite, comma separated email addresses. Omit for an appointment just for you.")] string? invite = null,
        [Description("Who to invite as optional, comma separated.")] string? optional = null,
        [Description("Where, or a room name.")] string? location = null,
        [Description("Notes for the invitation. Markdown is supported.")] string? body = null,
        [Description("Attach a Teams meeting link.")] bool online = false,
        [Description("Mark the time as free rather than busy — for a reminder or a hold.")] bool free = false,
        [Description("Which connection, e.g. 'work'. Required when more than one could be used.")] string? connection = null,
        CancellationToken ct = default)
    {
        // isWrite: true. Inviting people names an identity to every recipient, and there
        // is no unsending it — so an ambiguous calendar asks rather than guessing.
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.CalendarWrite, connection, isWrite: true);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        // "all" is a sensible thing to say to a read and a meaningless one here: the event
        // would exist several times over and every attendee would get duplicate invitations
        // from different addresses. Resolve() answers FanOut with no single Connection, so
        // this is also what stops the dereference below being null.
        if (resolution.Kind is ResolutionKind.FanOut)
            return "I can only put it in one calendar — which one: "
                 + string.Join(", ", resolution.Connections.Select(x => x.Label)) + "?";

        var c = resolution.Connection!;

        if (!TryParseLocal(start, out var from))
            return $"I couldn't read \"{start}\" as a time. Use yyyy-MM-ddTHH:mm, e.g. 2026-11-03T14:00.";

        DateTime to;
        if (!string.IsNullOrWhiteSpace(end))
        {
            if (!TryParseLocal(end, out to))
                return $"I couldn't read \"{end}\" as a time. Use yyyy-MM-ddTHH:mm.";

            if (to <= from) return "The end is not after the start.";
        }
        else
        {
            to = from.AddMinutes(Math.Clamp(minutes, 1, 24 * 60));
        }

        var required = Addresses(invite);
        var opt = Addresses(optional);

        try
        {
            var created = await calendar.CreateAsync(c, new OutgoingEvent
            {
                Subject = subject,
                Start = from,
                End = to,
                RequiredAttendees = required,
                OptionalAttendees = opt,
                Location = location,
                Body = body,
                IsOnlineMeeting = online,
                ShowAs = free ? Availability.Free : Availability.Busy,
            }, ct);

            // Handle minted immediately, so the next turn can say "move it" without
            // listing the calendar again.
            var handle = index.Remember(UserId,
                [(created.Id, c.Label, created.Subject, created.Organiser)], ItemKind.Event)[0];

            var sb = new StringBuilder();
            sb.Append($"Added \"{created.Subject}\" on {c.Label}, {When(created.Start, created.End, created.IsAllDay, full: true)}");

            // Read back the invitation as a fact, not an intention. Attendees were mailed
            // the moment Graph accepted this, and the user needs to know it has happened.
            var invited = required.Count + opt.Count;
            if (invited > 0)
                sb.Append($". Invitations have gone to {invited} {(invited == 1 ? "person" : "people")}");

            if (created.OnlineMeetingUrl is not null) sb.Append(". Teams link attached");
            sb.Append($". Handle {handle.Ref}.");

            return sb.ToString();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (CalendarException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "update_event", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Change an event — move it, rename it, or change where it is. Pass the handle from a listing. Only what you give is changed. Attendees are notified automatically.")]
    public async Task<string> UpdateEvent(
        [Description("The handle in square brackets from the listing.")] string ev,
        [Description("New start, as yyyy-MM-ddTHH:mm in the mailbox's local time.")] string? start = null,
        [Description("New end, as yyyy-MM-ddTHH:mm. Omit to keep the same length when moving.")] string? end = null,
        [Description("New title.")] string? subject = null,
        [Description("New location.")] string? location = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(ev, ct, Capabilities.CalendarWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        if (start is null && end is null && subject is null && location is null)
            return "Tell me what to change — a new time, title or location.";

        try
        {
            var before = await calendar.ReadAsync(c, item.ItemId, ct);
            if (before is null) return "That event no longer exists.";

            DateTime? newStart = null, newEnd = null;

            if (start is not null)
            {
                if (!TryParseLocal(start, out var s))
                    return $"I couldn't read \"{start}\" as a time. Use yyyy-MM-ddTHH:mm.";
                newStart = s;

                // Moving an event keeps its LENGTH. Sending a new start on its own leaves
                // the old end in place, which silently turns a 30-minute call moved two
                // hours later into a two-and-a-half-hour one — or an end before its start.
                if (end is null) newEnd = s.Add(before.End - before.Start);
            }

            if (end is not null)
            {
                if (!TryParseLocal(end, out var e))
                    return $"I couldn't read \"{end}\" as a time. Use yyyy-MM-ddTHH:mm.";
                newEnd = e;
            }

            if (newStart is not null && newEnd is not null && newEnd <= newStart)
                return "That would end before it starts.";

            var after = await calendar.UpdateAsync(c, item.ItemId, new EventChanges
            {
                Subject = subject,
                Start = newStart,
                End = newEnd,
                Location = location,
            }, ct);

            var sb = new StringBuilder($"Updated \"{after.Subject}\" on {c.Label}");

            if (newStart is not null || newEnd is not null)
                sb.Append($" — now {When(after.Start, after.End, after.IsAllDay, full: true)}");

            if (after.Attendees.Count > 0 && after.IOrganised)
                sb.Append($". {after.Attendees.Count} attendee{(after.Attendees.Count == 1 ? " has" : "s have")} been told");

            return sb.Append('.').ToString();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (CalendarException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "cancel_event", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Cancel a meeting you organised, telling the attendees, or remove an appointment from your own calendar. Pass the handle from a listing. This cannot be undone.")]
    public async Task<string> CancelEvent(
        [Description("The handle in square brackets from the listing.")] string ev,
        [Description("A note to send to attendees, if it is a meeting you organised.")] string? message = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(ev, ct, Capabilities.CalendarWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            // The service decides between cancelling and deleting, because that turns on
            // who organised it, and reads back which one it did.
            return await calendar.CancelAsync(c, item.ItemId, message, ct);
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (CalendarException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "respond_to_invite", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Accept, decline or tentatively accept a meeting invitation. The organiser is told. Pass the handle from a listing.")]
    public async Task<string> RespondToInvite(
        [Description("The handle in square brackets from the listing.")] string ev,
        [Description("One of: accept, decline, tentative.")] string reply,
        [Description("A note for the organiser.")] string? comment = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(ev, ct, Capabilities.CalendarWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var what = (reply ?? "").Trim().ToLowerInvariant() switch
        {
            "accept" or "accepted" or "yes" or "going" => (InviteReply?)InviteReply.Accept,
            "decline" or "declined" or "no" or "reject" => InviteReply.Decline,
            "tentative" or "tentatively" or "maybe" => InviteReply.Tentative,
            _ => null,
        };

        if (what is null) return $"I can accept, decline or tentatively accept — not \"{reply}\".";

        try
        {
            var e = await calendar.ReadAsync(c, item.ItemId, ct);
            if (e is null) return "That event no longer exists.";

            // Answering your own meeting is not a thing Graph will do, and the refusal is
            // an opaque 400. Say what is actually wrong.
            if (e.IOrganised)
                return $"You organised \"{e.Subject}\" — there is nothing to reply to. Cancel it instead if you can't make it.";

            await calendar.RespondAsync(c, item.ItemId, what.Value, comment, ct);

            var verb = what switch
            {
                InviteReply.Accept => "Accepted",
                InviteReply.Decline => "Declined",
                _ => "Tentatively accepted",
            };

            return $"{verb} \"{e.Subject}\" ({When(e.Start, e.End, e.IsAllDay, full: true)}). "
                 + $"{Untrusted.Scrub(e.Organiser)} has been told.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (CalendarException ex) { return ex.Message; }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Resolved(ItemRef? Item, Connection? Connection, string? Problem);

    /// <summary>
    /// Turns what the conversation said into the event AND the calendar holding it. The
    /// capability is checked here rather than at the Graph call for the same reason it is
    /// in <see cref="MailTools"/>: the handle decides the connection, so no resolver step
    /// ever ran to catch one with the calendar switched off.
    /// </summary>
    private async Task<Resolved> ResolveAsync(
        string said, CancellationToken ct, params string[] capabilities)
    {
        var item = index.Resolve(UserId, said);
        if (item is null)
            return new(null, null, "I don't have that one — list your calendar first, then tell me which.");

        if (item.Kind is not ItemKind.Event)
            return new(null, null,
                $"\"{item.Ref}\" is an email — {item.Subject}. Use the mail tools for it.");

        var c = await connections.FindByLabelAsync(UserId, item.ConnectionLabel, ct);
        if (c is null) return new(null, null, $"The {item.ConnectionLabel} connection has gone.");

        foreach (var capability in capabilities)
        {
            var cap = c.Capabilities.FirstOrDefault(x => x.Name == capability);

            // A connection made before calendar access was asked for has no calendar
            // capability at all, which the generic "can't do that" describes uselessly.
            if (cap is null && capability.StartsWith("calendar.", StringComparison.Ordinal))
                return new(null, null,
                    $"{c.Label} was connected before Outpost asked for calendar access. "
                    + $"Reconnecting it at {urls.Connections} fixes that.");

            if (cap is null || !cap.Usable)
                return new(null, null, ConnectionResolution.Unavailable(c, capability).SpokenSummary);
        }

        return new(item, c, null);
    }

    private static List<string> Addresses(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Reads a wall-clock time with no offset. Deliberately strict about that: accepting
    /// an offset and silently honouring it would let a model's guess at the user's zone
    /// override the mailbox's own, which is the one thing known to be right.
    /// </summary>
    public static bool TryParseLocal(string said, out DateTime local)
    {
        local = default;
        var text = (said ?? "").Trim();
        if (text.Length == 0) return false;

        string[] formats =
        [
            "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd",
        ];

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
        {
            local = DateTime.SpecifyKind(exact, DateTimeKind.Unspecified);
            return true;
        }

        // A trailing Z or offset means the caller resolved the zone itself. Strip it rather
        // than refuse — but keep the wall-clock reading, since the digits are what the user
        // said out loud and the offset is the part that was guessed.
        //
        // Via DateTimeOffset, whose .DateTime is the time AS WRITTEN. DateTime.TryParse
        // converts an explicit offset into the SERVER's local time instead, which turned
        // "14:00-07:00" into 16:00 on a machine in New York — the digits the user said,
        // moved by a zone neither they nor their mailbox is in.
        //
        // Gated on a leading four-digit year so this stays a relaxation of the ISO formats
        // above and nothing more. Without it a bare "14:00" parses against the SERVER's
        // today, which for a mailbox several zones away is the wrong day roughly as often
        // as it is the right one.
        if (text.Length >= 5 && text.AsSpan(0, 4).ToArray().All(char.IsAsciiDigit) && text[4] == '-'
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            local = DateTime.SpecifyKind(loose.DateTime, DateTimeKind.Unspecified);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The window to ask Graph for, in the mailbox's zone. "Today" has to be computed
    /// there and not on the server, or a container in UTC answers "what's on today" with
    /// tomorrow's first meeting for anybody west of Greenwich late in the evening.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window(
        TimeZoneInfo zone, string? date, int days)
    {
        var startOfDay = date is not null && TryParseLocal(date, out var said)
            ? said.Date
            : TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;

        var from = new DateTimeOffset(startOfDay, zone.GetUtcOffset(startOfDay));
        var endLocal = startOfDay.AddDays(days);

        return (from, new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)));
    }

    private static string When(CalendarEventSummary e) => When(e.Start, e.End, e.IsAllDay, full: false);

    /// <summary>
    /// A time a person would say. Within a listing the date is already implied by the
    /// heading, so only <paramref name="full"/> spells it out — a confirmation has to,
    /// because "moved it to 3pm" on its own does not say which day.
    /// </summary>
    private static string When(DateTimeOffset start, DateTimeOffset end, bool allDay, bool full)
    {
        if (allDay) return full ? $"all day on {start:dddd d MMMM}" : "all day";

        var time = $"{Clock(start)}–{Clock(end)}";
        return full ? $"{start:dddd d MMMM}, {time}" : time;
    }

    /// <summary>12-hour, because that is how the time gets read out loud.</summary>
    private static string Clock(DateTimeOffset t) =>
        t.Minute == 0 ? t.ToString("h tt").ToLowerInvariant() : t.ToString("h:mm tt").ToLowerInvariant();

    private static string DayHeading(DateTimeOffset t)
    {
        // "Today" judged at the event's OWN offset, which is the mailbox's. Against
        // DateTimeOffset.UtcNow.Date the server decides what today is, and for a mailbox
        // a few zones away every heading is wrong for part of each day.
        var today = DateTimeOffset.UtcNow.ToOffset(t.Offset).Date;
        var d = t.Date;

        if (d == today) return "Today";
        if (d == today.AddDays(1)) return "Tomorrow";
        return d < today.AddDays(7) ? t.ToString("dddd") : t.ToString("dddd d MMMM");
    }

    private static string Describe(InviteStatus s) => s switch
    {
        InviteStatus.Accepted => "accepted",
        InviteStatus.Declined => "declined",
        InviteStatus.TentativelyAccepted => "tentative",
        InviteStatus.NotResponded => "no reply yet",
        InviteStatus.Organiser => "organiser",
        _ => "not applicable",
    };

    private static string Describe(Availability a) => a switch
    {
        Availability.Free => "free",
        Availability.Tentative => "tentative",
        Availability.OutOfOffice => "out of office",
        Availability.WorkingElsewhere => "working elsewhere",
        Availability.Busy => "busy",
        _ => "unknown",
    };
}
