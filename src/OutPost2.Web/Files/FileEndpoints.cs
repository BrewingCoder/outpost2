using OutPost2.Infrastructure.Files;
using OutPost2.Web.Files.Internal;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Files;

public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        // Redeeming an upload ticket, deliberately OUTSIDE the authorised group (OP2-252).
        // The ticket is the credential: whatever is calling this has no cookie and no OAuth
        // token, which is the whole point — it is an agent that was handed a one-use URL.
        //
        // Everything that makes that safe is checked in UploadTicketService.RedeemAsync,
        // which claims the ticket with a conditional UPDATE so two requests racing the same
        // token cannot both pass. What is left here is the body, and the body is the part
        // the caller controls.
        app.MapPut("/files/stage/{token}", async (
                HttpContext ctx, IUploadTicketService tickets, IStagingService staging,
                ILogger<Program> log, string token) =>
            {
                var from = ctx.Connection.RemoteIpAddress?.ToString();

                var ticket = await tickets.RedeemAsync(token, from, ctx.RequestAborted);

                // One answer for unknown, spent and expired. Telling them apart would say
                // whether a guessed token had ever existed.
                if (ticket is null)
                    return Results.Problem(
                        "That upload link is not valid any more. Ask for a new one.",
                        statusCode: StatusCodes.Status403Forbidden);

                try
                {
                    // Capped while reading, not from Content-Length, which the sender
                    // writes. A truncating stream means a liar gets a 413 rather than a
                    // quota's worth of someone else's disk.
                    await using var capped = new CappedStream(ctx.Request.Body, ticket.MaxBytes);

                    var staged = await staging.UploadAsync(
                        ticket.UserId, ticket.Name,
                        ctx.Request.Headers["X-File-Name"].ToString() is { Length: > 0 } given
                            ? Path.GetFileName(given)
                            : ticket.Name,
                        string.IsNullOrWhiteSpace(ctx.Request.ContentType)
                            ? "application/octet-stream"
                            : ctx.Request.ContentType,
                        capped, ctx.RequestAborted);

                    await tickets.RecordAsync(ticket.TokenHash, staged.Id, ctx.RequestAborted);

                    log.LogInformation(
                        "Staged {Bytes} bytes as \"{Name}\" for {User} from an upload ticket.",
                        staged.SizeBytes, staged.Name, ticket.UserId);

                    // The name back, explicitly, because that is what the caller passes to
                    // send_mail_with_attachments and it must never have to guess it.
                    return Results.Ok(new
                    {
                        name = staged.Name,
                        bytes = staged.SizeBytes,
                        expiresAt = staged.ExpiresAt,
                    });
                }
                catch (BodyTooLargeException)
                {
                    return Results.Problem(
                        $"That is larger than the {ticket.MaxBytes} bytes this link was for. "
                        + "Ask for a new one with the right size.",
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                catch (InvalidOperationException ex)
                {
                    // The staging quota, which is the user's own limit and worth quoting.
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                }
                catch (Exception ex)
                {
                    // Anything else — the object store refusing, most likely. Logged in full
                    // and answered in one line: this endpoint takes no sign-in, so whoever
                    // reaches it is the last party who should be handed a stack trace. The
                    // ticket is already spent, which is the right outcome; a new one costs a
                    // tool call.
                    log.LogError(ex, "An upload ticket redeemed by {User} could not be stored.",
                        ticket.UserId);

                    return Results.Problem(
                        "The file could not be stored. The link is spent — ask for a new one "
                        + "and try again; if it keeps happening the storage behind this is at fault.",
                        statusCode: StatusCodes.Status502BadGateway);
                }
            })
            // No form, so nothing for antiforgery to validate, and no cookie for it to
            // protect: the request carries its own single-use credential. Said explicitly
            // rather than relying on that inference.
            .DisableAntiforgery()
            .AllowAnonymous();

        var files = app.MapGroup("/files").RequireAuthorization();

        // Upload is deliberately a plain multipart form, not an API. The human does this
        // at a desk with a screen — which is exactly why the conversational handle can be
        // a NAME later, instead of a token nobody could paste hands-free (OP2-9).
        files.MapPost("/upload", async (HttpContext ctx, IStagingService staging) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files["file"];
            if (file is null || file.Length == 0)
                return Results.Redirect("/files?error=Choose+a+file+first.");

            var name = form["name"].ToString();

            try
            {
                await using var stream = file.OpenReadStream();
                var staged = await staging.UploadAsync(ctx.User.UserId(), name, file.FileName,
                    file.ContentType ?? "application/octet-stream", stream);

                return Results.Redirect($"/files?added={Uri.EscapeDataString(staged.Name)}");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Redirect($"/files?error={Uri.EscapeDataString(ex.Message)}");
            }
        });

        files.MapPost("/{id:guid}/delete", async (HttpContext ctx, IStagingService staging, Guid id) =>
        {
            await staging.DeleteAsync(ctx.User.UserId(), id);
            return Results.Redirect("/files");
        });

        // Flow D: the human wants the bytes. Resolved through the user's own listing, so
        // knowing an id is not enough to reach someone else's file.
        files.MapGet("/{id:guid}/download", async (HttpContext ctx, IStagingService staging, Guid id) =>
        {
            var all = await staging.ListAsync(ctx.User.UserId());
            var f = all.FirstOrDefault(x => x.Id == id);
            if (f is null) return Results.NotFound();

            var stream = await staging.OpenAsync(f);
            return Results.Stream(stream, f.ContentType, f.FileName);
        });

        // ── job queue: the approval surface AND the progress surface (OP2-11) ──
        var jobs = app.MapGroup("/jobs").RequireAuthorization();

        jobs.MapPost("/{id:guid}/approve", async (HttpContext ctx, IJobService svc, Guid id) =>
        {
            await svc.ApproveAsync(ctx.User.UserId(), id);
            return Results.Redirect("/jobs");
        });

        jobs.MapPost("/{id:guid}/cancel", async (HttpContext ctx, IJobService svc, Guid id) =>
        {
            await svc.CancelAsync(ctx.User.UserId(), id);
            return Results.Redirect("/jobs");
        });
    }
}
