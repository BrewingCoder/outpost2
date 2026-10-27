using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Files;

public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
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
        }).DisableAntiforgery();

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
