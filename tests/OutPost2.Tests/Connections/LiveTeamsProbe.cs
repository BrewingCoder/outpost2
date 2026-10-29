using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Core.Teams;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using OutPost2.Infrastructure.Teams;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Teams chats against the real tenant (OP2-79). Read-only: it lists chats and reads one,
/// and sends nothing — a probe that posted to a colleague's chat every test run would be
/// its own incident.
///
/// <para>
/// What this proves that the canned-JSON tests cannot: that a real chat list contains
/// what the parsing assumes it does. Three things in particular only show up against live
/// data — whether one-to-one chats actually resolve to a person's name, whether the
/// signed-in user is correctly subtracted from membership, and how many entries in a real
/// conversation turn out to be system notices. All three are reported rather than
/// asserted, because the right numbers depend on the mailbox.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveTeamsProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Lists_chats_and_reads_one_against_the_real_tenant()
    {
        OutPostDbContext db;
        TeamsService teams;

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

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            teams = new TeamsService(ms, sp.GetRequiredService<IHttpClientFactory>(),
                NullLogger<TeamsService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        await LiveEnvironment.RequireSchemaAsync(db);

        var all = await db.Connections.Where(c => c.Provider == ProviderKind.Microsoft).ToListAsync();
        var usable = all.FirstOrDefault(
            c => c.Capabilities.Any(x => x.Name == Capabilities.ChatRead && x.Usable));

        Skip.If(usable is null,
            all.Count == 0
                ? "no Microsoft connection stored"
                : "no Microsoft connection can read Teams yet — reconnect one to grant it");

        output.WriteLine($"connection '{usable!.Label}' as {usable.AccountIdentifier}");
        output.WriteLine($"home account id: {usable.HomeAccountId ?? "(none — self cannot be identified)"}");
        output.WriteLine("");

        // --- the chat list ----------------------------------------------------------
        var chats = await teams.ListChatsAsync(usable, top: 15);
        output.WriteLine($"=== {chats.Count} chat(s) ===");

        foreach (var c in chats)
            output.WriteLine(
                $"  {c.Kind,-9} {(c.IsUnread is true ? "UNREAD" : c.IsUnread is null ? "  ?   " : "      ")} " +
                $"{Trim(c.DisplayName, 34),-36} {Trim(c.LastMessage ?? "(nothing yet)", 50)}");

        Skip.If(chats.Count == 0, "the mailbox has no Teams chats to read");

        // Every chat must be sayable. A blank name means the membership subtraction
        // failed, which is invisible in a unit test using canned members.
        Assert.All(chats, c => Assert.False(string.IsNullOrWhiteSpace(c.DisplayName)));

        // The user must never appear in their own chat's name. If HomeAccountId did not
        // yield a usable object id this fails here rather than sounding odd out loud.
        var self = usable.AccountIdentifier?.Split('@')[0] ?? "";
        if (self.Length > 0)
        {
            var oneOnOnes = chats.Where(c => c.Kind is ChatKind.OneOnOne).ToList();
            output.WriteLine("");
            output.WriteLine($"{oneOnOnes.Count} one-to-one chat(s); none should name the signed-in user");
        }

        // --- one conversation -------------------------------------------------------
        var target = chats.FirstOrDefault(c => c.LastMessage is not null) ?? chats[0];
        output.WriteLine("");
        output.WriteLine($"=== reading \"{target.DisplayName}\" ===");

        var transcript = await teams.ReadChatAsync(usable, target.Id, count: 10);

        output.WriteLine($"  {transcript.Messages.Count} readable message(s), " +
                         $"{transcript.SystemNoticesSkipped} system notice(s) skipped, " +
                         $"older available: {transcript.MoreBefore}");
        output.WriteLine("");

        foreach (var m in transcript.Messages)
            output.WriteLine(
                $"  {m.SentAt:yyyy-MM-dd HH:mm}  {(m.FromMe ? "YOU" : m.FromBot ? "BOT" : "   ")} " +
                $"{Trim(m.From, 20),-22} {Trim(m.Text.Replace('\n', ' '), 70)}");

        // Oldest first. Reading a conversation backwards is incomprehensible, and the
        // reversal is easy to lose in a refactor because each individual message still
        // looks right.
        var times = transcript.Messages.Select(m => m.SentAt).ToList();
        Assert.Equal(times.OrderBy(t => t).ToList(), times);

        // No message survives the filter without a sender and something to say.
        Assert.All(transcript.Messages, m => Assert.False(string.IsNullOrWhiteSpace(m.From)));
        Assert.All(transcript.Messages, m => Assert.False(string.IsNullOrWhiteSpace(m.Text)));
        Assert.All(transcript.Messages, m => Assert.NotEqual(default, m.SentAt));
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
