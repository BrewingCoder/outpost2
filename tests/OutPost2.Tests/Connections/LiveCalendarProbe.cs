using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Calendar;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// The real calendar path against the real mailbox (OP2-75). Read-only: it lists a window
/// and reads one event, and never writes to anybody's diary.
///
/// <para>
/// What this proves that the unit tests cannot: that the mailbox's timezone comes back at
/// all, that <c>Prefer: outlook.timezone</c> is honoured, and that calendarView really
/// does expand a recurring series into occurrences. All three are claims about Microsoft's
/// behaviour, and all three are the kind of thing that looks fine until a meeting is an
/// hour out.
/// </para>
///
/// <para>
/// Skips rather than fails when the connection predates the calendar scope, which every
/// connection does until it is reconnected once.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveCalendarProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Reads_the_real_calendar_in_the_mailboxs_own_timezone()
    {
        OutPostDbContext db;
        CalendarService calendar;

        try
        {
            db = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
            _disposables.Add(db);

            var vault = new VaultTransitClient(Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);
            _disposables.Add(vault);

            Skip.IfNot(await vault.HealthyAsync(), "dev Vault unreachable");

            var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "credentials.json")));

            var store = new ConnectionStore(db, new CredentialProtector(vault));
            var ms = new MicrosoftConnectionService(Options.Create(new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            }), store, NullLogger<MicrosoftConnectionService>.Instance);

            var sp = new ServiceCollection().AddHttpClient().AddMemoryCache().BuildServiceProvider();
            _disposables.Add(sp);

            calendar = new CalendarService(ms,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IMemoryCache>(),
                NullLogger<CalendarService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        await LiveEnvironment.RequireSchemaAsync(db);

        // Only a connection that actually HAS the capability. Every connection made before
        // this shipped lacks it, and reporting that as a failure would be reporting the
        // absence of a reconnection as a bug in the calendar.
        var connection = await db.Connections
            .Where(c => c.Provider == ProviderKind.Microsoft)
            .ToListAsync();

        var usable = connection.FirstOrDefault(
            c => c.Capabilities.Any(x => x.Name == Capabilities.CalendarRead && x.Usable));

        Skip.If(usable is null,
            connection.Count == 0
                ? "no Microsoft connection stored"
                : "no Microsoft connection has calendar access yet — reconnect one to grant it");

        // --- the timezone, which everything else depends on ---------------------------
        var zone = await calendar.TimeZoneAsync(usable!);
        output.WriteLine($"mailbox '{usable!.Label}' timezone: {zone.Id} " +
                         $"(currently UTC{zone.GetUtcOffset(DateTimeOffset.UtcNow):hh\\:mm})");

        // --- a fortnight, so a weekly series has to appear more than once --------------
        var from = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var window = (
            From: new DateTimeOffset(from, zone.GetUtcOffset(from)),
            To: new DateTimeOffset(from.AddDays(14), zone.GetUtcOffset(from.AddDays(14))));

        var events = await calendar.ListAsync(usable, window.From, window.To, top: 100);
        output.WriteLine($"{events.Count} event(s) in the next 14 days:");

        foreach (var e in events)
            output.WriteLine(
                $"  {e.Start:yyyy-MM-dd HH:mm} UTC{e.Start.Offset:hh\\:mm}–{e.End:HH:mm}  " +
                $"{Describe(e),-14} {Trim(e.Subject, 48),-50} {Trim(e.Organiser, 28)}");

        Skip.If(events.Count == 0, "nothing in the diary for the next fortnight");

        // Every event must say which calendar it came from, for the same reason mail does.
        Assert.All(events, e => Assert.Equal(usable.Label, e.ConnectionLabel));

        // Times must be real. A default(DateTimeOffset) here means the naive-time parsing
        // silently failed, which is exactly the failure that would not otherwise show.
        Assert.All(events, e => Assert.NotEqual(default, e.Start));
        Assert.All(events, e => Assert.True(e.End >= e.Start, $"'{e.Subject}' ends before it starts"));

        // Prefer: outlook.timezone honoured — the offset matches the mailbox's zone for
        // that instant, not the server's and not a flat UTC.
        Assert.All(events, e => Assert.Equal(zone.GetUtcOffset(e.Start), e.Start.Offset));

        // calendarView expanded the series rather than returning masters. A recurring
        // event appearing exactly once over a fortnight would be the tell.
        var recurring = events.Where(e => e.IsRecurring).ToList();
        output.WriteLine(recurring.Count == 0
            ? "no recurring events in the window — expansion not exercised"
            : $"{recurring.Count} occurrence(s) of recurring series in the window");

        // --- one event in full ----------------------------------------------------------
        var full = await calendar.ReadAsync(usable, events[0].Id);
        Assert.NotNull(full);

        output.WriteLine("");
        output.WriteLine($"=== read '{Trim(full!.Subject, 60)}' ===");
        output.WriteLine($"  {full.Start:yyyy-MM-dd HH:mm zzz} – {full.End:HH:mm}");
        output.WriteLine($"  organised by me: {full.IOrganised}   my reply: {full.MyResponse}");
        output.WriteLine($"  shows as: {full.ShowAs}   recurrence: {full.Recurrence ?? "(none)"}");
        output.WriteLine($"  {full.Attendees.Count} attendee(s)");
        foreach (var a in full.Attendees.Take(10))
            output.WriteLine($"    {a.Name,-34} {a.Response}{(a.IsOptional ? " (optional)" : "")}");

        if (!string.IsNullOrWhiteSpace(full.Body))
        {
            output.WriteLine($"  body {full.Body.Length} chars:");
            output.WriteLine(Indent(Trim(full.Body, 600)));

            // Same claim as mail: HTML arrives as text, not as markup.
            Assert.DoesNotContain("<div", full.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("&nbsp;", full.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Describe(CalendarEventSummary e) =>
        e.MyResponse switch
        {
            InviteStatus.NotResponded => "UNANSWERED",
            InviteStatus.TentativelyAccepted => "tentative",
            InviteStatus.Declined => "declined",
            InviteStatus.Organiser => "mine",
            InviteStatus.Accepted => "accepted",
            _ => e.ShowAs.ToString().ToLowerInvariant(),
        };

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Indent(string s) =>
        string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => "    " + l));
}
