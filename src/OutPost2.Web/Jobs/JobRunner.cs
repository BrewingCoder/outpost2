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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Job runner started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobService>();

                var job = await jobs.ClaimNextAsync(ct);
                if (job is null)
                {
                    await Task.Delay(Idle, ct);
                    continue;
                }

                await RunAsync(scope.ServiceProvider, jobs, job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything a single job can throw, or one bad payload
                // stops every other user's work.
                log.LogError(ex, "Job runner loop error.");
                await Task.Delay(Idle, ct);
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
        var staging = sp.GetRequiredService<IStagingService>();
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

        var outcome = await sender.SendAsync(connection, new Core.Mail.OutgoingMail
        {
            To = payload.To, Cc = payload.Cc, Subject = payload.Subject, Body = payload.Body, IsHtml = true,
        }, attachments, progress, ct);

        await jobs.CompleteAsync(job.Id, outcome.Ok, outcome.Error, outcome.Retryable, ct);
    }
}

/// <summary>Housekeeping: expired staging files, spent auth codes and pending connections.</summary>
public sealed class HousekeepingService(IServiceScopeFactory scopes, ILogger<HousekeepingService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait before the first pass so startup is never slowed by housekeeping.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var purged = await scope.ServiceProvider
                    .GetRequiredService<IStagingService>().PurgeExpiredAsync(ct);

                var pendings = await scope.ServiceProvider
                    .GetRequiredService<IPendingConnectionStore>().PurgeExpiredAsync(ct);

                var db = scope.ServiceProvider.GetRequiredService<OutPostDbContext>();
                var codes = await db.OAuthAuthCodes
                    .Where(c => c.ExpiresAt < DateTimeOffset.UtcNow.AddHours(-1))
                    .ExecuteDeleteAsync(ct);

                if (purged + pendings + codes > 0)
                    log.LogInformation(
                        "Housekeeping removed {Files} staged file(s), {Pendings} pending connection(s), {Codes} auth code(s).",
                        purged, pendings, codes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Housekeeping pass failed."); }

            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}
