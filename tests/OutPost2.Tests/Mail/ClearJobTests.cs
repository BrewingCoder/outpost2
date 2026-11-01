using OutPost2.Core.Jobs;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// A clear-out too big to do while anybody waits (OP2-231).
/// <para>
/// Reported against 430 messages: the client gave up with a generic "MCP tool call failed"
/// while the server carried on deleting, and every retry then hit the concurrency limit the
/// first attempt was still using. So the caller could not tell whether the work was running,
/// finished or failed — and the one thing it did was start more of it.
/// </para>
/// <para>
/// The job queue was built for exactly this (OP2-11) and already carries the hard part: an
/// idempotency key, so "a model retrying a timed-out tool call must not send twice".
/// </para>
/// </summary>
public class ClearJobTests
{
    private static readonly Guid Work = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();

    // ── the idempotency key ──────────────────────────────────────────────────

    [Fact]
    public void The_same_request_twice_is_the_same_job()
    {
        // The whole point. A retry after a timeout must join the job already running
        // instead of starting a second clear-out against the same mailbox.
        Assert.Equal(
            MailTools.Fingerprint(Work, "news@linkedin.com", null, null),
            MailTools.Fingerprint(Work, "news@linkedin.com", null, null));
    }

    [Fact]
    public void The_case_of_the_sender_does_not_make_a_second_job()
    {
        // It arrives by voice and is transcribed, so capitalisation is not a decision
        // anybody made.
        Assert.Equal(
            MailTools.Fingerprint(Work, "News@LinkedIn.com", null, null),
            MailTools.Fingerprint(Work, "news@linkedin.com", null, null));
    }

    [Fact]
    public void The_order_the_folders_came_in_does_not_make_a_second_job()
    {
        // The default scope is built by walking the folder list, whose order is Graph's and
        // not guaranteed. Two identical requests must not differ because of it.
        Assert.Equal(
            MailTools.Fingerprint(Work, "linkedin", ["a", "b"], null),
            MailTools.Fingerprint(Work, "linkedin", ["b", "a"], null));
    }

    [Theory]
    [InlineData("different sender")]
    public void A_different_request_is_a_different_job(string _)
    {
        var baseline = MailTools.Fingerprint(Work, "linkedin", null, null);

        Assert.NotEqual(baseline, MailTools.Fingerprint(Work, "twitter", null, null));
        Assert.NotEqual(baseline, MailTools.Fingerprint(Home, "linkedin", null, null));
        Assert.NotEqual(baseline, MailTools.Fingerprint(Work, "linkedin", ["inbox"], null));
        Assert.NotEqual(baseline,
            MailTools.Fingerprint(Work, "linkedin", null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Clearing_one_folder_is_a_different_job_from_clearing_another()
    {
        Assert.NotEqual(
            MailTools.Fingerprint(Work, "linkedin", ["inbox"], null),
            MailTools.Fingerprint(Work, "linkedin", ["archive"], null));
    }

    [Fact]
    public void A_key_is_short_enough_for_the_column_it_goes_in()
    {
        var key = MailTools.Fingerprint(Work, "news@linkedin.com", null, null);

        Assert.Equal(32, key.Length);
        Assert.Matches("^[0-9A-F]+$", key);
    }

    // ── what the queue reads back ────────────────────────────────────────────

    private static Job Clearing(JobState state, int percent = 0, string? detail = null) => new()
    {
        UserId = Work,
        Kind = JobKind.ClearMail,
        State = state,
        Description = "clear 430 from \"linkedin\" in the Inbox and Clutter",
        Payload = "{}",
        IdempotencyKey = "k",
        PercentComplete = percent,
        Detail = detail,
    };

    [Fact]
    public void A_running_clear_out_says_how_far_it_has_got()
    {
        // "Sending: clear 430 messages" would be a sentence about the wrong thing, and this
        // is read out loud while somebody drives.
        var said = Clearing(JobState.Sending, 46, "200 moved to Deleted Items").Spoken();

        Assert.Contains("Still clearing", said);
        Assert.Contains("200 moved", said);
        Assert.DoesNotContain("Sending", said);
    }

    [Fact]
    public void A_running_clear_out_with_no_detail_yet_falls_back_to_the_percentage()
    {
        Assert.Contains("46% done", Clearing(JobState.Sending, 46).Spoken());
    }

    [Fact]
    public void A_finished_clear_out_does_not_claim_to_have_sent_anything()
    {
        var said = Clearing(JobState.Sent, 100, "430 moved to Deleted Items").Spoken();

        Assert.StartsWith("Finished:", said);
        Assert.DoesNotContain("Sent:", said);
    }

    [Fact]
    public void Sending_mail_still_reads_as_sending()
    {
        // The states are shared, so the wording is picked by kind — which must not have
        // broken the kind that was already using them.
        var job = new Job
        {
            UserId = Work, Kind = JobKind.SendMail, State = JobState.Sending,
            Description = "Quarterly report to finance", Payload = "{}", IdempotencyKey = "k",
        };

        Assert.Equal("Sending: Quarterly report to finance", job.Spoken());

        job.State = JobState.Sent;
        Assert.Equal("Sent: Quarterly report to finance", job.Spoken());
    }

    [Fact]
    public void A_failed_clear_out_still_says_why_and_whether_to_retry()
    {
        var job = Clearing(JobState.Failed);
        job.Error = "home needs re-authorising, so I stopped after 180.";
        job.IsRetryable = false;

        var said = job.Spoken();

        Assert.Contains("Failed", said);
        Assert.Contains("stopped after 180", said);
        Assert.DoesNotContain("Worth trying again", said);
    }

    [Fact]
    public void A_queued_clear_out_says_so_before_the_worker_picks_it_up()
    {
        Assert.StartsWith("Queued:", Clearing(JobState.Queued).Spoken());
    }

    // ── where the line between inline and queued sits ────────────────────────

    [Fact]
    public void The_inline_limit_is_a_dozen_requests_not_a_hundred()
    {
        // 20 per batch plus a listing between each, so the limit times two over twenty is
        // roughly the request count. Comfortably inside a tool call; 430 was not.
        Assert.InRange(MailTools.InlineClearLimit, 20, 300);
        Assert.InRange(MailTools.InlineClearLimit / 20 * 2, 2, 40);
    }
}
