using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Files;

namespace OutPost2.Tests.Files;

/// <summary>
/// One-use links to an attachment's original bytes (OP2-265). The inbound mirror of
/// <see cref="UploadTicketTests"/>, and the same thing is really under test: a bearer
/// credential on an endpoint with no sign-in — only here what it opens is somebody's mail.
/// </summary>
[Collection("database")]
public class DownloadTicketTests : IAsyncLifetime
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private OutPostDbContext _db = null!;
    private DownloadTicketService _tickets = null!;
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _connection = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        _db = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
        await _db.Database.MigrateAsync();
        _tickets = new DownloadTicketService(_db, NullLogger<DownloadTicketService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DownloadTickets.Where(t => t.UserId == _user).ExecuteDeleteAsync();
        await _db.DisposeAsync();
    }

    private static DownloadRequest File(string name = "invoice.pdf") => new("att-" + name, name, 2048);

    private async Task<string> OneAsync(string name = "invoice.pdf") =>
        (await _tickets.MintAsync(_user, _connection, "msg-1", [File(name)]))[0].Token;

    [Fact]
    public async Task A_ticket_can_be_spent_once()
    {
        var token = await OneAsync();

        var first = await _tickets.RedeemAsync(token, "1.2.3.4");
        Assert.NotNull(first);
        Assert.Equal("att-invoice.pdf", first.AttachmentId);
        Assert.Equal(_connection, first.ConnectionId);
        Assert.Equal("1.2.3.4", first.UsedFrom);

        Assert.Null(await _tickets.RedeemAsync(token, "1.2.3.4"));
    }

    [Fact]
    public async Task Two_requests_racing_the_same_token_cannot_both_pass()
    {
        var token = await OneAsync();

        // Separate contexts: one DbContext cannot run two commands at once, which is the
        // very thing OP2-239 and OP2-262 were about.
        await using var a = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
        await using var b = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
        var ta = new DownloadTicketService(a, NullLogger<DownloadTicketService>.Instance);
        var tb = new DownloadTicketService(b, NullLogger<DownloadTicketService>.Instance);

        var results = await Task.WhenAll(ta.RedeemAsync(token, "a"), tb.RedeemAsync(token, "b"));

        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public async Task An_expired_ticket_is_refused()
    {
        var token = await OneAsync();
        await _db.DownloadTickets.Where(t => t.UserId == _user)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null(await _tickets.RedeemAsync(token, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task A_made_up_token_is_refused(string token)
    {
        Assert.Null(await _tickets.RedeemAsync(token, null));
    }

    [Fact]
    public async Task The_token_itself_is_never_stored()
    {
        var token = await OneAsync();
        var stored = await _db.DownloadTickets.AsNoTracking().SingleAsync(t => t.UserId == _user);

        Assert.NotEqual(token, stored.TokenHash);
        Assert.DoesNotContain(token, stored.TokenHash);
    }

    [Fact]
    public async Task All_attachments_on_a_message_mint_distinct_tickets_in_order()
    {
        var minted = await _tickets.MintAsync(_user, _connection, "msg-1",
            [File("a.pdf"), File("b.xlsx"), File("c.png")]);

        Assert.Equal(["a.pdf", "b.xlsx", "c.png"], minted.Select(m => m.Ticket.FileName));
        Assert.Equal(3, minted.Select(m => m.Token).Distinct().Count());
    }

    [Fact]
    public async Task The_outstanding_cap_refuses_the_whole_batch_rather_than_part_of_it()
    {
        await _tickets.MintAsync(_user, _connection, "msg-1",
            Enumerable.Range(0, DownloadTicket.MaxOutstanding - 1).Select(i => File($"f{i}.png")).ToList());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tickets.MintAsync(_user, _connection, "msg-2", [File("x.pdf"), File("y.pdf")]));
        Assert.Contains("limit", ex.Message);

        // All or none: half a message's attachments silently missing is the failure to avoid.
        Assert.Equal(DownloadTicket.MaxOutstanding - 1,
            await _db.DownloadTickets.CountAsync(t => t.UserId == _user));
    }
}
