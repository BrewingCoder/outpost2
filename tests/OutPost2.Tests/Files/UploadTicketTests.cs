using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Files;
using OutPost2.Web.Files.Internal;

namespace OutPost2.Tests.Files;

/// <summary>
/// One-use permission to stage a file (OP2-252).
/// <para>
/// <c>send_mail_with_attachments</c> takes the names of staged files, and the only way to
/// stage one was a human uploading it in a browser — so an agent that had just written a
/// report had to hand the job back for the one part of it that is pure mechanics. It cannot
/// simply pass a path: this server runs in a container with nothing but <c>/app</c> mounted
/// and may not even be on the same machine.
/// </para>
/// <para>
/// So it gets a ticket, and a ticket is a bearer credential on an endpoint with no sign-in
/// behind it. That is what these tests are really about.
/// </para>
/// </summary>
[Collection("database")]
public class UploadTicketTests : IAsyncLifetime
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private OutPostDbContext _db = null!;
    private UploadTicketService _tickets = null!;
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _other = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        _db = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);

        await _db.Database.MigrateAsync();

        _tickets = new UploadTicketService(_db, NullLogger<UploadTicketService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.UploadTickets.Where(t => t.UserId == _user || t.UserId == _other)
            .ExecuteDeleteAsync();

        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_ticket_can_be_spent_once()
    {
        // The whole security model. A replayable ticket is a ticket somebody else can
        // replay, and it lets an unauthenticated caller write repeatedly.
        var (_, token) = await _tickets.MintAsync(_user, "report", 1024);

        var first = await _tickets.RedeemAsync(token, "1.2.3.4");
        Assert.NotNull(first);
        Assert.Equal(_user, first.UserId);
        Assert.Equal("report", first.Name);

        Assert.Null(await _tickets.RedeemAsync(token, "1.2.3.4"));
    }

    [Fact]
    public async Task Two_requests_racing_the_same_ticket_cannot_both_win()
    {
        // "One use" has to hold when the two arrive together, which a read-then-write would
        // not give. The claim is a conditional UPDATE for exactly this reason.
        var (_, token) = await _tickets.MintAsync(_user, "report", 1024);

        // Separate contexts, because one DbContext is not usable concurrently either.
        async Task<UploadTicket?> Race()
        {
            await using var db = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);

            var svc = new UploadTicketService(db, NullLogger<UploadTicketService>.Instance);
            return await svc.RedeemAsync(token, "1.2.3.4");
        }

        var results = await Task.WhenAll(Race(), Race(), Race(), Race());

        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Fact]
    public async Task The_plaintext_token_is_never_stored()
    {
        // It exists once, in the answer that minted it. A database copy must not be a set
        // of working upload links.
        var (ticket, token) = await _tickets.MintAsync(_user, "report", 1024);

        Assert.DoesNotContain(token, ticket.TokenHash);
        Assert.Equal(UploadTicketService.HashPublic(token), ticket.TokenHash);

        var stored = await _db.UploadTickets.AsNoTracking()
            .FirstAsync(t => t.TokenHash == ticket.TokenHash);

        Assert.Equal(64, stored.TokenHash.Length);
    }

    [Fact]
    public async Task A_token_is_long_enough_that_guessing_is_not_a_strategy()
    {
        // Not a short spoken handle like a message reference: this one is the only thing
        // between a guess and a write.
        var (_, token) = await _tickets.MintAsync(_user, "report", 1024);

        Assert.True(token.Length >= 40, $"token was only {token.Length} characters");
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public async Task An_expired_ticket_is_refused()
    {
        var (ticket, token) = await _tickets.MintAsync(_user, "report", 1024);

        await _db.UploadTickets.Where(t => t.TokenHash == ticket.TokenHash)
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null(await _tickets.RedeemAsync(token, null));
    }

    [Fact]
    public async Task An_unknown_token_is_refused_without_ceremony()
    {
        Assert.Null(await _tickets.RedeemAsync("not-a-real-token", null));
        Assert.Null(await _tickets.RedeemAsync("", null));
        Assert.Null(await _tickets.RedeemAsync("   ", null));
    }

    [Fact]
    public async Task A_ticket_carries_the_user_it_was_minted_for()
    {
        // The endpoint has no sign-in, so the ticket is the only thing saying whose staging
        // area this goes into. If that were wrong, one user's upload would land in
        // another's attachments.
        var (_, mine) = await _tickets.MintAsync(_user, "mine", 1024);
        var (_, theirs) = await _tickets.MintAsync(_other, "theirs", 1024);

        Assert.Equal(_user, (await _tickets.RedeemAsync(mine, null))!.UserId);
        Assert.Equal(_other, (await _tickets.RedeemAsync(theirs, null))!.UserId);
    }

    [Fact]
    public async Task The_size_is_agreed_when_the_ticket_is_minted()
    {
        var (_, token) = await _tickets.MintAsync(_user, "report", 4096);

        Assert.Equal(4096, (await _tickets.RedeemAsync(token, null))!.MaxBytes);
    }

    [Fact]
    public async Task Where_it_was_used_from_is_recorded()
    {
        // Worth having afterwards if a ticket turns out to have leaked into a transcript.
        var (ticket, token) = await _tickets.MintAsync(_user, "report", 1024);
        await _tickets.RedeemAsync(token, "198.51.100.7");

        var stored = await _db.UploadTickets.AsNoTracking()
            .FirstAsync(t => t.TokenHash == ticket.TokenHash);

        Assert.Equal("198.51.100.7", stored.UsedFrom);
        Assert.NotNull(stored.UsedAt);
    }

    [Fact]
    public async Task Only_so_many_may_be_outstanding_at_once()
    {
        // A bound on how much unauthenticated write surface one user can open.
        for (var i = 0; i < UploadTicket.MaxOutstanding; i++)
            await _tickets.MintAsync(_user, $"file {i}", 1024);

        var tooMany = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _tickets.MintAsync(_user, "one more", 1024));

        Assert.Contains("waiting to be used", tooMany.Message);
    }

    [Fact]
    public async Task Spending_one_makes_room_for_another()
    {
        var first = "";
        for (var i = 0; i < UploadTicket.MaxOutstanding; i++)
        {
            var (_, token) = await _tickets.MintAsync(_user, $"file {i}", 1024);
            if (i == 0) first = token;
        }

        await _tickets.RedeemAsync(first, null);

        // Does not throw.
        await _tickets.MintAsync(_user, "room now", 1024);
    }

    [Fact]
    public async Task An_expired_ticket_does_not_hold_a_place_in_the_queue()
    {
        for (var i = 0; i < UploadTicket.MaxOutstanding; i++)
            await _tickets.MintAsync(_user, $"file {i}", 1024);

        await _db.UploadTickets.Where(t => t.UserId == _user)
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await _tickets.MintAsync(_user, "after the sweep", 1024);
    }

    [Fact]
    public async Task One_users_tickets_do_not_crowd_out_another()
    {
        for (var i = 0; i < UploadTicket.MaxOutstanding; i++)
            await _tickets.MintAsync(_user, $"file {i}", 1024);

        await _tickets.MintAsync(_other, "unaffected", 1024);
    }

    // ── the size cap on the body itself ──────────────────────────────────────

    [Fact]
    public async Task A_body_within_the_agreed_size_reads_normally()
    {
        using var source = new MemoryStream(new byte[100]);
        await using var capped = new CappedStream(source, 100);

        using var sink = new MemoryStream();
        await capped.CopyToAsync(sink);

        Assert.Equal(100, sink.Length);
    }

    [Fact]
    public async Task A_body_past_the_agreed_size_is_refused_rather_than_truncated()
    {
        // Content-Length is written by the sender, so the count has to be of what actually
        // arrives. And it throws rather than truncating: a short file would still go out as
        // an attachment, and a corrupt document delivered successfully is worse than a
        // refused upload.
        using var source = new MemoryStream(new byte[5000]);
        await using var capped = new CappedStream(source, 1000);

        using var sink = new MemoryStream();

        await Assert.ThrowsAsync<BodyTooLargeException>(() => capped.CopyToAsync(sink));
    }

    [Fact]
    public async Task A_lying_content_length_does_not_help()
    {
        // The stream does not consult any header, which is the point — this is the same
        // test as above expressed as the attack it prevents.
        using var source = new MemoryStream(new byte[64 * 1024]);
        await using var capped = new CappedStream(source, 16);

        using var sink = new MemoryStream();
        await Assert.ThrowsAsync<BodyTooLargeException>(() => capped.CopyToAsync(sink));

        Assert.True(sink.Length <= 16 + 81920, "it should have stopped early");
    }

    [Fact]
    public void A_capped_stream_refuses_to_be_written_to_or_seeked()
    {
        using var source = new MemoryStream();
        using var capped = new CappedStream(source, 10);

        Assert.True(capped.CanRead);
        Assert.False(capped.CanWrite);
        Assert.False(capped.CanSeek);
        Assert.Throws<NotSupportedException>(() => capped.Write([1, 2, 3], 0, 3));
        Assert.Throws<NotSupportedException>(() => capped.Seek(0, SeekOrigin.Begin));
    }
}
