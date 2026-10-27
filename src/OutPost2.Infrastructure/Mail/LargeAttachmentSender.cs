using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Files;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// Sends mail with attachments of any size (OP2-10).
/// <para>
/// D3 made this a resumable, asynchronous job rather than a call. The sequence is
/// create draft → createUploadSession per large attachment → chunked PUT → send, and each
/// phase can fail independently.
/// </para>
/// </summary>
public interface ILargeAttachmentSender
{
    Task<SendOutcome> SendAsync(
        Connection connection,
        Core.Mail.OutgoingMail mail,
        IReadOnlyList<StagedFile> attachments,
        IProgress<(int Percent, string Detail)>? progress = null,
        CancellationToken ct = default);
}

public sealed record SendOutcome(bool Ok, string? Error, bool Retryable, string? DraftId);

public sealed class LargeAttachmentSender(
    IMicrosoftConnectionServiceAccessor tokens,
    IStagingService staging,
    IHttpClientFactory httpFactory,
    ILogger<LargeAttachmentSender> log) : ILargeAttachmentSender
{
    /// <summary>
    /// Graph REQUIRES upload-session chunks to be a multiple of 320 KiB. Not a guideline —
    /// a non-conforming chunk is rejected outright.
    /// </summary>
    private const int ChunkUnit = 320 * 1024;

    /// <summary>8 MiB = 25 units. Large enough to be efficient, small enough to retry cheaply.</summary>
    private const int ChunkSize = ChunkUnit * 25;

    /// <summary>Below this, an attachment can ride inline on the draft and skip the session dance.</summary>
    private const long InlineLimit = 3 * 1024 * 1024;

    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(c, ct)
            ?? throw new MailAuthRequiredException(c.Label);

        var http = httpFactory.CreateClient("graph");
        http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromMinutes(10);
        return http;
    }

    public async Task<SendOutcome> SendAsync(
        Connection connection,
        Core.Mail.OutgoingMail mail,
        IReadOnlyList<StagedFile> attachments,
        IProgress<(int Percent, string Detail)>? progress = null,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        string? draftId = null;

        try
        {
            progress?.Report((0, "creating draft"));

            var (contentType, content) = mail.IsHtml
                ? ("HTML", MarkdownToHtml.Render(mail.Body))
                : ("Text", mail.Body);

            var small = attachments.Where(a => a.SizeBytes <= InlineLimit).ToList();
            var large = attachments.Where(a => a.SizeBytes > InlineLimit).ToList();

            // Small attachments ride inline on the draft; only the big ones pay for a
            // session. Chosen by size automatically — the caller should never have to know.
            var inline = new List<object>();
            foreach (var a in small)
            {
                await using var s = await staging.OpenAsync(a, ct);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);

                inline.Add(new
                {
                    @odata_type = "#microsoft.graph.fileAttachment",
                    name = a.FileName,
                    contentType = a.ContentType,
                    contentBytes = Convert.ToBase64String(ms.ToArray()),
                });
            }

            var draftBody = JsonSerializer.SerializeToNode(new
            {
                subject = mail.Subject,
                body = new { contentType, content },
                toRecipients = mail.To.Select(a => new { emailAddress = new { address = a } }).ToArray(),
                ccRecipients = mail.Cc.Select(a => new { emailAddress = new { address = a } }).ToArray(),
            })!.AsObject();

            if (inline.Count > 0)
            {
                // "@odata.type" cannot be expressed as a C# identifier, so patch it in.
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var a in small)
                {
                    await using var s = await staging.OpenAsync(a, ct);
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms, ct);

                    arr.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["@odata.type"] = "#microsoft.graph.fileAttachment",
                        ["name"] = a.FileName,
                        ["contentType"] = a.ContentType,
                        ["contentBytes"] = Convert.ToBase64String(ms.ToArray()),
                    });
                }
                draftBody["attachments"] = arr;
            }

            using var draftResp = await http.PostAsJsonAsync("me/messages", draftBody, ct);
            if (!draftResp.IsSuccessStatusCode)
                return Fail("creating the draft", await draftResp.Content.ReadAsStringAsync(ct), draftResp.StatusCode);

            draftId = (await draftResp.Content.ReadFromJsonAsync<GraphId>(ct))?.Id;
            if (draftId is null) return new SendOutcome(false, "Graph returned a draft with no id.", true, null);

            // ── large attachments, one upload session each ──────────────────
            var done = 0;
            foreach (var a in large)
            {
                progress?.Report((Percent(done, large.Count, 0), $"uploading {a.Name}"));

                var sessionResp = await http.PostAsJsonAsync(
                    $"me/messages/{draftId}/attachments/createUploadSession",
                    new
                    {
                        AttachmentItem = new
                        {
                            attachmentType = "file",
                            name = a.FileName,
                            size = a.SizeBytes,
                            contentType = a.ContentType,
                        },
                    }, ct);

                if (!sessionResp.IsSuccessStatusCode)
                    return Fail($"starting the upload for {a.Name}",
                        await sessionResp.Content.ReadAsStringAsync(ct), sessionResp.StatusCode, draftId);

                var session = await sessionResp.Content.ReadFromJsonAsync<UploadSession>(ct);
                sessionResp.Dispose();

                if (session?.UploadUrl is null)
                    return new SendOutcome(false, "Graph returned an upload session with no URL.", true, draftId);

                var outcome = await UploadChunksAsync(a, session.UploadUrl, (pct, detail) =>
                    progress?.Report((Percent(done, large.Count, pct), detail)), ct);

                if (!outcome.Ok) return outcome with { DraftId = draftId };
                done++;
            }

            // ── send ────────────────────────────────────────────────────────
            progress?.Report((95, "sending"));

            using var sendResp = await http.PostAsync($"me/messages/{draftId}/send", null, ct);
            if (!sendResp.IsSuccessStatusCode)
            {
                var body = await sendResp.Content.ReadAsStringAsync(ct);

                // The limit that bites: Graph's upload ceiling is not the tenant's send
                // limit. An attachment can upload to the draft perfectly and then be
                // refused here, so the message has to explain that rather than looking
                // like a random failure.
                var hint = body.Contains("size", StringComparison.OrdinalIgnoreCase)
                    ? " The attachment uploaded, but the mailbox refused to send it — it is likely over this tenant's message size limit. Try a OneDrive link instead."
                    : "";

                return Fail("sending", body + hint, sendResp.StatusCode, draftId);
            }

            progress?.Report((100, "sent"));
            log.LogInformation("Sent mail from {Label} with {Count} attachment(s).", connection.Label, attachments.Count);

            // The draft becomes the sent item on success, so there is nothing to clean up.
            return new SendOutcome(true, null, false, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Send failed for {Label}.", connection.Label);

            // Clean up in a finally-shaped path, not on the happy path only: every
            // abandoned draft otherwise accumulates in the folder the user actually looks at.
            if (draftId is not null) await TryDeleteDraftAsync(http, draftId, ct);
            return new SendOutcome(false, ex.Message, true, null);
        }

        static int Percent(int done, int total, int inner) =>
            total == 0 ? 90 : Math.Clamp((int)((done + inner / 100.0) / total * 90), 0, 90);
    }

    private async Task<SendOutcome> UploadChunksAsync(
        StagedFile file, string uploadUrl, Action<int, string>? progress, CancellationToken ct)
    {
        // A bare client: the session URL carries its own authorisation, and attaching our
        // bearer token to it is rejected.
        using var raw = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        long sent = 0;
        while (sent < file.SizeBytes)
        {
            var end = Math.Min(sent + ChunkSize, file.SizeBytes) - 1;
            var length = (int)(end - sent + 1);

            // Ranged read straight from object storage — the file is never held whole.
            await using var slice = await staging.OpenRangeAsync(file, sent, end, ct);
            using var buffer = new MemoryStream(length);
            await slice.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new StreamContent(buffer),
            };
            req.Content.Headers.ContentLength = length;
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(sent, end, file.SizeBytes);

            using var resp = await raw.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Created)
                return new SendOutcome(false,
                    $"Upload of {file.Name} failed at {sent / (1024 * 1024)} MB: HTTP {(int)resp.StatusCode}.",
                    true, null);

            sent = end + 1;
            progress?.Invoke((int)(sent * 100 / file.SizeBytes), $"uploading {file.Name}");
        }

        return new SendOutcome(true, null, false, null);
    }

    private async Task TryDeleteDraftAsync(HttpClient http, string draftId, CancellationToken ct)
    {
        try { await http.DeleteAsync($"me/messages/{draftId}", ct); }
        catch (Exception ex) { log.LogWarning(ex, "Could not clean up draft {DraftId}.", draftId); }
    }

    private static SendOutcome Fail(string what, string body, System.Net.HttpStatusCode status, string? draftId = null)
    {
        string message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                ? m.GetString() ?? body : body;
        }
        catch { message = body; }

        if (message.Length > 300) message = message[..300] + "…";

        // 4xx other than 429 will fail again identically, so saying "try again" would be
        // a lie that costs the user another attempt.
        var retryable = status is System.Net.HttpStatusCode.TooManyRequests
                        || (int)status >= 500;

        return new SendOutcome(false, $"Failed while {what}: {message}", retryable, draftId);
    }

    private sealed record GraphId([property: JsonPropertyName("id")] string Id);
    private sealed record UploadSession([property: JsonPropertyName("uploadUrl")] string? UploadUrl);
}
