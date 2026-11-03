using OutPost2.Core.Connections;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Jobs;
using OutPost2.Core.Notifications;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Web.Jobs;

/// <summary>
/// Drains the job queue (OP2-11).
/// <para>
/// In-process and Postgres-backed, on the cluster team's advice — there is no shared broker in
/// the cluster, and the resume semantics are custom enough (<c>nextExpectedRanges</c>) that
/// a generic framework would fight us.
/// </para>
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, ILogger<JobRunner> log) : BackgroundService
{
    /// <summary>
    /// Polling, not notification. A send is already an asynchronous job the user is told
    /// about, so a second or two of latency costs nothing, and LISTEN/NOTIFY would add a
    /// persistent connection for no benefit at this scale.
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a claimed job may go without a heartbeat before another worker takes it
    /// back (OP2-101). Progress is reported once per 8 MiB chunk, so five minutes of
    /// silence is a dead process rather than a slow one.
    /// </summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Picked up this many times and still not finishing, a job is failed rather than
    /// requeued again. Something about it is killing the worker.
    /// </summary>
    private const int MaxAttempts = 5;

    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Job runner started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobService>();

                // Swept on the idle path, so a busy queue is never slowed by it and a
                // deploy in the middle of an upload is picked up within the minute.
                if (DateTimeOffset.UtcNow - _lastSweep > SweepEvery)
                {
                    _lastSweep = DateTimeOffset.UtcNow;
                    await jobs.RequeueAbandonedAsync(Abandoned, MaxAttempts, stoppingToken);
                }

                var job = await jobs.ClaimNextAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(Idle, stoppingToken);
                    continue;
                }

                await RunAsync(scope.ServiceProvider, jobs, job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything a single job can throw, or one bad payload
                // stops every other user's work.
                log.LogError(ex, "Job runner loop error.");
                await Task.Delay(Idle, stoppingToken);
            }
        }

        log.LogInformation("Job runner stopped.");
    }

    private async Task RunAsync(IServiceProvider sp, IJobService jobs, Job job, CancellationToken ct)
    {
        try
        {
            switch (job.Kind)
            {
                case JobKind.SendMail:
                    await RunSendMailAsync(sp, jobs, job, ct);
                    break;

                case JobKind.ClearMail:
                    await RunClearMailAsync(sp, jobs, job, ct);
                    break;

                default:
                    await jobs.CompleteAsync(job.Id, false, $"Unknown job kind {job.Kind}.", false, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {JobId} threw.", job.Id);
            await jobs.CompleteAsync(job.Id, false, ex.Message, true, ct);
        }
    }

    private async Task RunSendMailAsync(
        IServiceProvider sp, IJobService jobs, Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SendMailPayload>(job.Payload)
            ?? throw new InvalidOperationException("Unreadable SendMail payload.");

        var db = sp.GetRequiredService<OutPostDbContext>();
        var sender = sp.GetRequiredService<ILargeAttachmentSender>();

        var connection = await db.Connections.FirstOrDefaultAsync(
            c => c.Id == payload.ConnectionId && c.UserId == job.UserId, ct);

        if (connection is null)
        {
            await jobs.CompleteAsync(job.Id, false, "That connection no longer exists.", false, ct);
            return;
        }

        var attachments = await db.StagedFiles
            .Where(f => f.UserId == job.UserId && payload.AttachmentIds.Contains(f.Id))
            .ToListAsync(ct);

        // Refuse rather than send a message missing an attachment the user asked for.
        // Quietly dropping one would be worse than failing: they would believe it went.
        if (attachments.Count != payload.AttachmentIds.Count)
        {
            await jobs.CompleteAsync(job.Id, false,
                "One of the attachments has expired or been removed. Re-upload it and try again.", false, ct);
            return;
        }

        await jobs.UpdateProgressAsync(job.Id, JobState.Staging, 0, "preparing", ct);

        // From here every write to the job goes through one gate (OP2-262). They all share
        // this scope's DbContext, and the fire-and-forget progress writes that used to run
        // beside the resume and outcome writes are what turned a sent message into
        // "Failed … Worth trying again".
        var writes = new JobWrites(jobs, job.Id, log);

        // What a previous attempt got as far as. Null on a first run, and unreadable
        // state is treated as none — a resume that cannot be parsed is a send that
        // starts again, which is slow, where guessing at it is a send that goes wrong.
        SendResumeState? resume = null;
        if (!string.IsNullOrWhiteSpace(job.ResumeState))
        {
            try { resume = JsonSerializer.Deserialize<SendResumeState>(job.ResumeState); }
            catch (JsonException) { resume = null; }
        }

        SendOutcome outcome;
        try
        {
            outcome = await sender.SendAsync(connection, new Core.Mail.OutgoingMail
            {
                To = payload.To, Cc = payload.Cc, Subject = payload.Subject, Body = payload.Body, IsHtml = true,
                Importance = payload.Importance,
            }, attachments, resume,
                (state, c) => writes.ResumeAsync(JsonSerializer.Serialize(state), c),
                writes, ct);
        }
        catch (Exception ex)
        {
            // The sender handles its own failures, so this is belt and braces — but it
            // matters which side of the send request it lands on. After it: "may have
            // gone", never "worth trying again". Before it: an ordinary failure, recorded
            // through the gate below rather than by RunAsync's catch-all, which would write
            // on the context while a progress write could still be running on it.
            log.LogError(ex, "Job {JobId} threw while sending.", job.Id);
            outcome = writes.MayHaveSent
                ? new SendOutcome(false, SendOutcome.MaybeSentMessage, false, null, MaybeSent: true)
                : new SendOutcome(false, ex.Message, true, null);
        }

        // Shut down before the send request went out: nothing has left the mailbox, and
        // the job is still Staging or Uploading. Left exactly there, the abandoned-job
        // sweep resumes it after the restart from its saved state. Recording it as a
        // failure would turn every deploy during an upload into "Failed: A task was
        // canceled" for the user to sort out.
        if (!outcome.Ok && !outcome.MaybeSent && ct.IsCancellationRequested)
        {
            await writes.CloseAsync();
            log.LogInformation("Job {JobId} interrupted by shutdown before sending; left to resume.", job.Id);
            return;
        }

        if (!await writes.FinishAsync(outcome.Ok, outcome.Error, outcome.Retryable))
        {
            // Nothing else here may record a different outcome in its place — in
            // particular not the catch-all in RunAsync, which would call it a retryable
            // failure. The job stays where it was; the user is still told below.
            log.LogCritical("Job {JobId} {Outcome}, but that could not be recorded.",
                job.Id, outcome.Ok ? "SENT" : "failed");
        }

        // A send with attachments is a job precisely because it takes long enough that
        // nobody waits for it, and until now there was nowhere to say it had landed
        // (OP2-11 left progress with no destination; OP2-165 gave it one). Failure is
        // worth more than success here: an unsent message the user believes went is the
        // expensive outcome.
        //
        // A notification that fails is logged and nothing more. It runs after the outcome
        // is recorded, and an exception here used to reach the catch-all and overwrite a
        // Sent job with a retryable failure (OP2-262).
        try
        {
            await sp.GetRequiredService<INotifier>().NotifyAsync(job.UserId, outcome.Ok
                ? new Notification($"Sent: {job.Description}", Kind: NotificationKind.JobFinished)
                : outcome.MaybeSent
                    ? new Notification(
                        $"May have sent: {job.Description}",
                        outcome.Error ?? SendOutcome.MaybeSentMessage,
                        sp.GetRequiredService<PublicUrls>().Jobs,
                        NotificationKind.JobFinished)
                    : new Notification(
                        $"Could not send: {job.Description}",
                        (outcome.Error ?? "No reason given.")
                        + (outcome.Retryable ? " Worth trying again." : "")
                        + " Nothing was sent.",
                        sp.GetRequiredService<PublicUrls>().Jobs,
                        NotificationKind.JobFinished),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Job {JobId} finished but its notification could not be delivered.", job.Id);
        }
    }

    /// <summary>
    /// Clears a sender out of a mailbox, in the background (OP2-231).
    /// <para>
    /// It ran inside the tool call before, and 430 messages did not fit: the client gave up
    /// with a generic failure while the server carried on deleting, so nobody could say
    /// whether it was running, finished or failed — and every retry hit the concurrency
    /// limit the first attempt was still using.
    /// </para>
    /// <para>
    /// Out here the constraints invert. Nobody is waiting on the call, so a throttle can
    /// simply be waited out for as long as Graph asks, and progress can be written down
    /// after every pass for <c>check_queue</c> to read back.
    /// </para>
    /// </summary>
    private async Task RunClearMailAsync(
        IServiceProvider sp, IJobService jobs, Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ClearMailPayload>(job.Payload)
            ?? throw new InvalidOperationException("Unreadable ClearMail payload.");

        var db = sp.GetRequiredService<OutPostDbContext>();
        var mail = sp.GetRequiredService<IMailService>();

        var connection = await db.Connections.FirstOrDefaultAsync(
            c => c.Id == payload.ConnectionId && c.UserId == job.UserId, ct);

        if (connection is null)
        {
            await jobs.CompleteAsync(job.Id, false, "That mailbox no longer exists.", false, ct);
            return;
        }

        var target = new ClearTarget
        {
            From = payload.From,
            FolderIds = payload.FolderIds,
            Before = payload.Before,
        };

        var cleared = 0;
        var failed = 0;

        // A pass at a time, so progress is recorded as it goes and a worker that dies is
        // resumed by the next one having lost at most one pass.
        for (var pass = 0; pass < MaxClearPasses && !ct.IsCancellationRequested; pass++)
        {
            ClearOutcome outcome;
            try
            {
                outcome = await mail.ClearAsync(connection, target, ceiling: ClearPerPass, ct);
            }
            catch (MailThrottledException throttle)
            {
                // Nobody is waiting, so wait properly. This is the whole reason the job
                // belongs out here rather than inside the tool call.
                var wait = throttle.RetryAfter ?? TimeSpan.FromSeconds(30);
                if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);

                log.LogWarning(throttle, "Clear job {JobId} throttled; waiting {Seconds}s.",
                    job.Id, wait.TotalSeconds);

                await jobs.UpdateProgressAsync(job.Id, JobState.Sending, Percent(cleared, payload.Expected),
                    $"{cleared} moved so far, paused for {(int)wait.TotalSeconds}s because {(connection.Provider == ProviderKind.Google ? "Google" : "Microsoft")} is "
                    + "rate-limiting the mailbox", ct);

                await Task.Delay(wait, ct);
                continue;
            }
            catch (MailAuthRequiredException)
            {
                await jobs.CompleteAsync(job.Id, false,
                    $"{connection.Label} needs re-authorising, so I stopped after {cleared}.", false, ct);
                return;
            }

            cleared += outcome.Deleted;
            failed += outcome.Failed;

            await jobs.UpdateProgressAsync(job.Id, JobState.Sending, Percent(cleared, payload.Expected),
                $"{cleared} moved to {TrashName(connection)}", ct);

            // Nothing moved and nothing refused: there is nothing left matching, so this is
            // done rather than stalled.
            if (outcome.Deleted == 0) break;

            if (outcome.Throttled)
            {
                var wait = outcome.RetryAfter ?? TimeSpan.FromSeconds(30);
                if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);
                await Task.Delay(wait, ct);
            }
        }

        var summary = $"{cleared} message{(cleared == 1 ? "" : "s")} from \"{payload.From}\" "
                    + $"moved to {TrashName(connection)} in {connection.Label}"
                    + (failed > 0 ? $", {failed} refused" : "")
                    + ". They stay there until you empty it.";

        await jobs.UpdateProgressAsync(job.Id, JobState.Sending, 100, summary, ct);
        await jobs.CompleteAsync(job.Id, true, null, false, ct);

        log.LogInformation("Clear job {JobId} finished: {Cleared} moved, {Failed} refused.",
            job.Id, cleared, failed);
    }

    private static int Percent(int done, int expected) =>
        expected <= 0 ? 0 : Math.Clamp((int)(100.0 * done / expected), 0, 99);

    /// <summary>How much one pass clears. Small enough that progress moves visibly.</summary>
    private const int ClearPerPass = 200;

    /// <summary>
    /// A ceiling on passes, so a mailbox that keeps handing back the same undeletable page
    /// cannot occupy the worker for ever. 200 passes is 40,000 messages.
    /// </summary>
    private const int MaxClearPasses = 200;

    /// <summary>Where a clear puts mail, in the provider's own word (OP2-282).</summary>
    private static string TrashName(Connection c) =>
        c.Provider == ProviderKind.Google ? "Trash" : "Deleted Items";
}

/// <summary>Housekeeping: expired staging files, spent auth codes and pending connections.</summary>
public sealed class HousekeepingService(IServiceScopeFactory scopes, ILogger<HousekeepingService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait before the first pass so startup is never slowed by housekeeping.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var purged = await scope.ServiceProvider
                    .GetRequiredService<IStagingService>().PurgeExpiredAsync(stoppingToken);

                var pendings = await scope.ServiceProvider
                    .GetRequiredService<IPendingConnectionStore>().PurgeExpiredAsync(stoppingToken);

                var downloads = await scope.ServiceProvider
                    .GetRequiredService<IDownloadTicketService>().PurgeExpiredAsync(stoppingToken);

                var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();
                var codes = await db.OAuthAuthCodes
                    .Where(c => c.ExpiresAt < DateTimeOffset.UtcNow.AddHours(-1))
                    .ExecuteDeleteAsync(stoppingToken);

                if (purged + pendings + codes + downloads > 0)
                    log.LogInformation(
                        "Housekeeping removed {Files} staged file(s), {Pendings} pending connection(s), {Codes} auth code(s), {Downloads} download ticket(s).",
                        purged, pendings, codes, downloads);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Housekeeping pass failed."); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
