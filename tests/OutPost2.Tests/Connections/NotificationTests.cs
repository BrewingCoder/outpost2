using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Notifications;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Notifications;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Telling a user something happened while they were not looking (OP2-165).
/// <para>
/// The property worth most here is not that a message arrives — it is that <b>nothing
/// breaks when one does not</b>. A notification sits on top of work that has already
/// succeeded: the administrator has approved, the mail has gone. If a dead bot token or
/// an unconfigured user could turn any of that into an error, the feature would be worse
/// than its absence.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationTests : IAsyncLifetime
{
    private const string Admin = "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";
    private readonly string _dbName = "op2_notify_" + Guid.NewGuid().ToString("n")[..12];
    private OutPostDbContext _db = null!;
    private bool _ready;

    // Real Postgres rather than a fake: the channel's only database interaction is one
    // projected scalar read, and the thing that would break it in production -- a null
    // column on a row that exists -- is exactly what an in-memory stand-in would get
    // right by accident.
    public async Task InitializeAsync()
    {
        try
        {
            await using (var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
                await a.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();
            _ready = true;
        }
        catch (Exception) { _ready = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        await _db.DisposeAsync();

        await using var admin = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Bodies.Add(r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct));
            return respond(r);
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Exploding : INotificationChannel
    {
        public string Name => "Exploding";

        public Task<bool> SendAsync(Guid userId, Notification n, CancellationToken ct = default) =>
            throw new HttpRequestException("the channel is down");
    }

    private sealed class Counting : INotificationChannel
    {
        public int Sent { get; private set; }
        public string Name => "Counting";

        public Task<bool> SendAsync(Guid userId, Notification n, CancellationToken ct = default)
        {
            Sent++;
            return Task.FromResult(true);
        }
    }

    // ── the contract: a notifier never throws ────────────────────────────────

    [Fact]
    public async Task A_channel_that_throws_does_not_take_the_caller_with_it()
    {
        var notifier = new Notifier([new Exploding()], NullLogger<Notifier>.Instance);

        // No assertion needed beyond this returning: the call site is a handler that has
        // just recorded an approval, and an exception here would turn that into a 500
        // page for an administrator who did everything right.
        await notifier.NotifyAsync(Guid.NewGuid(), new Notification("anything"));
    }

    [Fact]
    public async Task One_broken_channel_does_not_stop_the_others()
    {
        var good = new Counting();
        var notifier = new Notifier([new Exploding(), good], NullLogger<Notifier>.Instance);

        await notifier.NotifyAsync(Guid.NewGuid(), new Notification("anything"));

        Assert.Equal(1, good.Sent);
    }

    [Fact]
    public async Task No_channels_at_all_is_a_supported_configuration()
    {
        await new Notifier([], NullLogger<Notifier>.Instance)
            .NotifyAsync(Guid.NewGuid(), new Notification("anything"));
    }

    // ── Telegram ─────────────────────────────────────────────────────────────

    private (TelegramChannel Channel, Stub Wire, Guid UserId) Build(
        string? chatId, string botToken = "123:ABC",
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var user = new User
        {
            GitHubUserId = Random.Shared.NextInt64(1, long.MaxValue),
            GitHubLogin = "alice-" + Guid.NewGuid().ToString("n")[..6],
            TelegramChatId = chatId,
        };
        _db.Users.Add(user);
        _db.SaveChanges();

        var wire = new Stub(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        }));

        return (new TelegramChannel(_db, new OneClient(wire),
            Options.Create(new TelegramOptions { BotToken = botToken }),
            NullLogger<TelegramChannel>.Instance), wire, user.Id);
    }

    [SkippableFact]
    public async Task A_user_with_a_chat_id_is_sent_the_whole_message()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var (channel, wire, userId) = Build("987654");

        var sent = await channel.SendAsync(userId, new Notification(
            "work has been approved", "Sign in once more to finish.", "https://outpost.example/connections"));

        Assert.True(sent);

        using var doc = JsonDocument.Parse(Assert.Single(wire.Bodies));
        var text = doc.RootElement.GetProperty("text").GetString()!;

        Assert.Equal("987654", doc.RootElement.GetProperty("chat_id").GetString());
        Assert.Contains("work has been approved", text);
        Assert.Contains("Sign in once more", text);
        Assert.Contains("https://outpost.example/connections", text);
    }

    [SkippableFact]
    public async Task A_label_with_markdown_characters_arrives_intact()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        // No parse_mode is set, deliberately. A connection label is the user's own words,
        // and a stray underscore would either break the message or silently eat part of
        // it — on the one message that must arrive readable.
        var (channel, wire, userId) = Build("987654");

        await channel.SendAsync(userId, new Notification("work_mail *primary* has been approved"));

        using var doc = JsonDocument.Parse(wire.Bodies[0]);
        Assert.Contains("work_mail *primary*", doc.RootElement.GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("parse_mode", out _));
    }

    [SkippableFact]
    public async Task A_user_who_has_set_nothing_up_is_not_an_error()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var (channel, wire, userId) = Build(chatId: null);

        Assert.False(await channel.SendAsync(userId, new Notification("anything")));
        Assert.Empty(wire.Bodies);
    }

    [SkippableFact]
    public async Task A_deployment_with_no_bot_token_sends_nothing_and_says_nothing()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        // The user has a chat id, but the deployment was never given a bot. Reaching
        // Telegram at all here would be an unauthenticated call to a public API with a
        // user's chat id in it.
        var (channel, wire, userId) = Build("987654", botToken: "");

        Assert.False(await channel.SendAsync(userId, new Notification("anything")));
        Assert.Empty(wire.Bodies);
    }

    [SkippableFact]
    public async Task Telegram_refusing_the_message_is_reported_as_not_sent_rather_than_thrown()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var (channel, _, userId) = Build("987654", respond: _ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"ok":false,"description":"chat not found"}"""),
            });

        Assert.False(await channel.SendAsync(userId, new Notification("anything")));
    }

    [SkippableFact]
    public async Task The_bot_token_is_in_the_url_and_never_in_the_body()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        // It is logged on failure, and the body is what gets logged. A token in the body
        // would mean the first refused message puts a credential in the log.
        var (channel, wire, userId) = Build("987654");

        await channel.SendAsync(userId, new Notification("anything"));

        Assert.DoesNotContain("123:ABC", wire.Bodies[0]);
    }
}
