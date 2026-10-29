using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Jobs;
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

    private static async Task RunSendMailAsync(
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

        var progress = new Progress<(int Percent, string Detail)>(p =>
            _ = jobs.UpdateProgressAsync(job.Id,
                p.Percent >= 95 ? JobState.Sending : JobState.Uploading,
                p.Percent, p.Detail, CancellationToken.None));

        await jobs.UpdateProgressAsync(job.Id, JobState.Staging, 0, "preparing", ct);

        // What a previous attempt got as far as. Null on a first run, and unreadable
        // state is treated as none — a resume that cannot be parsed is a send that
        // starts again, which is slow, where guessing at it is a send that goes wrong.
        SendResumeState? resume = null;
        if (!string.IsNullOrWhiteSpace(job.ResumeState))
        {
            try { resume = JsonSerializer.Deserialize<SendResumeState>(job.ResumeState); }
            catch (JsonException) { resume = null; }
        }

        var outcome = await sender.SendAsync(connection, new Core.Mail.OutgoingMail
        {
            To = payload.To, Cc = payload.Cc, Subject = payload.Subject, Body = payload.Body, IsHtml = true,
        }, attachments, resume,
            (state, c) => jobs.SetResumeStateAsync(job.Id, JsonSerializer.Serialize(state), c),
            progress, ct);

        await jobs.CompleteAsync(job.Id, outcome.Ok, outcome.Error, outcome.Retryable, ct);
    }
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

                var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();
                var codes = await db.OAuthAuthCodes
                    .Where(c => c.ExpiresAt < DateTimeOffset.UtcNow.AddHours(-1))
                    .ExecuteDeleteAsync(stoppingToken);

                if (purged + pendings + codes > 0)
                    log.LogInformation(
                        "Housekeeping removed {Files} staged file(s), {Pendings} pending connection(s), {Codes} auth code(s).",
                        purged, pendings, codes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Housekeeping pass failed."); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
