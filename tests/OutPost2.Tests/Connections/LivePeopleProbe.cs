using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.People;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// People lookup and free/busy against the real tenant (OP2-77, OP2-78). Read-only: it
/// searches, lists and asks about availability, and creates nothing.
///
/// <para>
/// What this proves that no unit test can: that the three people sources answer at all
/// under the scopes actually granted. Each one fails independently and silently — a
/// tenant can withhold People.Read while granting the directory, or the other way about —
/// so the probe reports which of the three produced results rather than asserting that
/// all did.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LivePeopleProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Looks_people_up_and_reads_free_busy_against_the_real_tenant()
    {
        OutPostDbContext db;
        PeopleService people;
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

            var factory = sp.GetRequiredService<IHttpClientFactory>();
            people = new PeopleService(ms, factory, NullLogger<PeopleService>.Instance);
            calendar = new CalendarService(ms, factory,
                sp.GetRequiredService<IMemoryCache>(), NullLogger<CalendarService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        await LiveEnvironment.RequireSchemaAsync(db);

        var all = await db.Connections.Where(c => c.Provider == ProviderKind.Microsoft).ToListAsync();
        var usable = all.FirstOrDefault(
            c => c.Capabilities.Any(x => x.Name == Capabilities.PeopleRead && x.Usable));

        Skip.If(usable is null,
            all.Count == 0
                ? "no Microsoft connection stored"
                : "no Microsoft connection can look people up yet — reconnect one to grant it");

        // --- find somebody ----------------------------------------------------------
        // The signed-in user's own surname: guaranteed to exist in the directory, and
        // needs no hard-coded colleague in a public repository.
        var self = usable!.AccountIdentifier ?? "";
        var seed = self.Split('@')[0].Split('.').LastOrDefault() ?? "a";

        output.WriteLine($"connection '{usable.Label}' as {self}");
        output.WriteLine($"searching for \"{seed}\"");

        var matches = await people.FindAsync(usable, seed, top: 10);
        output.WriteLine($"{matches.Count} match(es):");

        foreach (var m in matches)
            output.WriteLine(
                $"  {m.Source,-9} {(m.IsGroup ? "GROUP" : "     ")} {Trim(m.Name, 30),-32} " +
                $"{Trim(m.Address, 38),-40} {Trim(m.JobTitle ?? "", 28)}");

        // Each result must be usable as a recipient or be honest that it is not.
        Assert.All(matches, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));

        var bySource = matches.GroupBy(m => m.Source).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"sources: {string.Join(", ", bySource.Select(kv => $"{kv.Key}={kv.Value}"))}");

        // --- the address book -------------------------------------------------------
        try
        {
            var contacts = await people.ListContactsAsync(usable, top: 10);
            output.WriteLine("");
            output.WriteLine($"{contacts.Count} contact(s) in the address book:");
            foreach (var c in contacts)
                output.WriteLine($"  {Trim(c.Name, 30),-32} {Trim(c.PrimaryAddress ?? "(no address)", 38)}");
        }
        catch (PeopleException ex)
        {
            output.WriteLine($"contacts unavailable: {ex.Message}");
        }

        // --- free/busy for the user's own mailbox -----------------------------------
        // Their own address, so the probe needs nobody else's consent and discloses
        // nothing about a colleague into a test log.
        Skip.If(string.IsNullOrWhiteSpace(self), "connection has no account identifier recorded");

        var canCalendar = usable.Capabilities.Any(x => x.Name == Capabilities.CalendarRead && x.Usable);
        Skip.IfNot(canCalendar, "connection has no calendar access — reconnect it to grant it");

        var zone = await calendar.TimeZoneAsync(usable);
        var day = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;

        var schedule = await calendar.GetScheduleAsync(
            usable, [self], day.AddHours(8), day.AddHours(18), 30);

        output.WriteLine("");
        output.WriteLine($"=== free/busy for {self} on {day:yyyy-MM-dd}, 8am–6pm ===");

        // One row per address asked about, always — a dropped row reads as "free".
        Assert.Single(schedule);

        var mine = schedule[0];
        output.WriteLine($"  known: {mine.IsKnown}{(mine.Error is null ? "" : $" ({mine.Error})")}");
        output.WriteLine($"  working hours: " + (mine.WorkingHours is { } wh
            ? $"{wh.Start}–{wh.End} on {string.Join(",", wh.Days)}"
            : "(not published)"));

        foreach (var b in mine.Busy)
            output.WriteLine($"  {b.Start:HH:mm}–{b.End:HH:mm}  {b.Status,-16} " +
                             $"{(b.Blocks ? "blocks" : "open  ")}  {b.Subject ?? "(free/busy only)"}");

        Assert.True(mine.IsKnown, $"Exchange could not read the user's own calendar: {mine.Error}");

        // Times must be real, for the same reason as the calendar probe: a silent parse
        // failure would present as the epoch rather than as an error.
        Assert.All(mine.Busy, b => Assert.NotEqual(default, b.Start));
        Assert.All(mine.Busy, b => Assert.True(b.End >= b.Start));
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
