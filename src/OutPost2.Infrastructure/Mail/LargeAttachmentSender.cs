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
        SendResumeState? resume = null,
        Func<SendResumeState, CancellationToken, Task>? persist = null,
        IProgress<(int Percent, string Detail)>? progress = null,
        CancellationToken ct = default);
}

public sealed record SendOutcome(bool Ok, string? Error, bool Retryable, string? DraftId);

/// <summary>
/// Everything a resumed send needs to pick up where the last attempt stopped (OP2-101).
/// <para>
/// Persisted on the job after every step that costs something to redo: the draft, each
/// upload session, and each attachment finishing. The expensive part of a large send is
/// bytes already on the wire, and a restart that throws those away is a resume in name
/// only.
/// </para>
/// </summary>
public sealed record SendResumeState
{
    /// <summary>The draft already created in the mailbox, with its inline attachments.</summary>
    public string? DraftId { get; init; }

    /// <summary>Keyed by staged-file id, because the user may rename a file mid-flight.</summary>
    public Dictionary<string, AttachmentUpload> Attachments { get; init; } = [];
}

/// <summary>One large attachment's place in the queue.</summary>
/// <param name="UploadUrl">
/// The session URL. It carries its own authorisation and its own expiry, so it is the one
/// piece of state that can go stale while everything around it is still valid.
/// </param>
/// <param name="Done">
/// True once Graph has acknowledged the final chunk. Bytes sent is deliberately NOT
/// recorded: Graph is the authority on what it received, and the two disagree exactly
/// when it matters, which is after a failure.
/// </param>
public sealed record AttachmentUpload(string? UploadUrl, bool Done);

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

    /// <summary>
    /// Retries of one chunk before giving up and letting the job be requeued. Small on
    /// purpose: the job-level resume is the real recovery path, and a worker that spends
    /// ten minutes retrying in place is a worker not draining anybody else's queue.
    /// </summary>
    private const int MaxChunkAttempts = 3;

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
        SendResumeState? resume = null,
        Func<SendResumeState, CancellationToken, Task>? persist = null,
        IProgress<(int Percent, string Detail)>? progress = null,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var state = resume ?? new SendResumeState();
        string? draftId = null;
        var retryable = true;

        async Task Save(CancellationToken c)
        {
            if (persist is not null) await persist(state, c);
        }

        try
        {
            // A draft from a previous attempt is worth a great deal: it carries every
            // inline attachment and every large one already uploaded against it. Checked
            // rather than assumed, because the user can delete a draft from Outlook and
            // then nothing downstream makes sense.
            if (state.DraftId is { Length: > 0 } previous)
            {
                using var check = await http.GetAsync($"me/messages/{previous}?$select=id", ct);
                if (check.IsSuccessStatusCode)
                {
                    draftId = previous;
                    progress?.Report((0, "resuming"));
                    log.LogInformation("Resuming send for {Label} on draft {DraftId}.", connection.Label, draftId);
                }
                else
                {
                    // Gone. Everything keyed to it is meaningless, including the upload
                    // sessions, which belong to that draft's attachments.
                    log.LogInformation("Draft {DraftId} is gone; starting the send again.", previous);
                    state = new SendResumeState();
                    await Save(ct);
                }
            }

            // Small attachments ride inline on the draft; only the big ones pay for a
            // session. Chosen by size automatically — the caller should never have to know.
            var small = attachments.Where(a => a.SizeBytes <= InlineLimit).ToList();
            var large = attachments.Where(a => a.SizeBytes > InlineLimit).ToList();

            if (draftId is null)
            {
                progress?.Report((0, "creating draft"));

                var (contentType, content) = mail.IsHtml
                    ? ("HTML", MarkdownToHtml.Render(mail.Body))
                    : ("Text", mail.Body);

                var inline = new List<object>();
                foreach (var a in small)
                {
                    await using var s = await staging.OpenAsync(a, ct);
                    await using var ms = new MemoryStream();
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
                        await using var ms = new MemoryStream();
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

                state = state with { DraftId = draftId };
                await Save(ct);
            }

            // ── large attachments, one upload session each ──────────────────
            var done = 0;
            foreach (var a in large)
            {
                var key = a.Id.ToString();
                state.Attachments.TryGetValue(key, out var tracked);

                // Already acknowledged by Graph on a previous attempt. Re-uploading would
                // attach it twice, which is worse than slow.
                if (tracked is { Done: true }) { done++; continue; }

                progress?.Report((Percent(done, large.Count, 0), $"uploading {a.Name}"));

                var url = tracked?.UploadUrl;

                if (url is null)
                {
                    url = await StartSessionAsync(http, draftId!, a, ct);
                    if (url is null)
                        return new SendOutcome(false,
                            $"Could not start the upload for {a.Name}.", true, draftId);

                    state.Attachments[key] = new AttachmentUpload(url, false);
                    await Save(ct);
                }

                var outcome = await UploadChunksAsync(a, url, (pct, detail) =>
                    progress?.Report((Percent(done, large.Count, pct), detail)), ct);

                // A session that has expired or been cleaned up is a restart of THIS
                // attachment, not of the whole job — the draft and every other
                // attachment on it are still good. Exactly one fresh attempt, so a
                // server that keeps dropping sessions cannot loop here forever.
                if (outcome.SessionGone)
                {
                    log.LogInformation("Upload session for {Name} had gone; starting a new one.", a.Name);

                    url = await StartSessionAsync(http, draftId!, a, ct);
                    if (url is null)
                        return new SendOutcome(false,
                            $"The upload session for {a.Name} expired and a new one could not be started.",
                            true, draftId);

                    state.Attachments[key] = new AttachmentUpload(url, false);
                    await Save(ct);

                    outcome = await UploadChunksAsync(a, url, (pct, detail) =>
                        progress?.Report((Percent(done, large.Count, pct), detail)), ct);
                }

                if (!outcome.Ok)
                {
                    retryable = outcome.Retryable;
                    return new SendOutcome(false, outcome.Error, outcome.Retryable, draftId);
                }

                state.Attachments[key] = new AttachmentUpload(null, true);
                await Save(ct);
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

                var failure = Fail("sending", body + hint, sendResp.StatusCode, draftId);
                retryable = failure.Retryable;
                return failure;
            }

            progress?.Report((100, "sent"));
            log.LogInformation("Sent mail from {Label} with {Count} attachment(s).", connection.Label, attachments.Count);

            // The draft becomes the sent item on success, so there is nothing to clean up.
            return new SendOutcome(true, null, false, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Send failed for {Label}.", connection.Label);
            return new SendOutcome(false, ex.Message, true, draftId);
        }
        finally
        {
            // The draft is deleted only when nothing will come back for it. It used to go
            // on EVERY failure, which threw away every byte already uploaded and made the
            // resume this ticket asks for impossible — the next attempt would start from
            // an empty mailbox. A retryable failure keeps it; a permanent one does not,
            // because an abandoned draft otherwise sits in the folder the user looks at.
            //
            // CancellationToken.None: the usual reason for being here is that ct was
            // cancelled, and a cleanup that cancels itself never cleans anything up.
            if (draftId is not null && !retryable)
                await TryDeleteDraftAsync(http, draftId, CancellationToken.None);
        }

        static int Percent(int done, int total, int inner) =>
            total == 0 ? 90 : Math.Clamp((int)((done + (inner / 100.0)) / total * 90), 0, 90);
    }

    /// <summary>Opens an upload session for one attachment. Null if Graph refuses.</summary>
    private async Task<string?> StartSessionAsync(
        HttpClient http, string draftId, StagedFile a, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync(
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

        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("createUploadSession for {Name} returned {Status}: {Body}",
                a.Name, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            return null;
        }

        return (await resp.Content.ReadFromJsonAsync<UploadSession>(ct))?.UploadUrl;
    }

    private sealed record ChunkOutcome(bool Ok, string? Error, bool Retryable, bool SessionGone);

    /// <summary>
    /// Pushes a file up an existing session, starting wherever GRAPH says it is (OP2-101).
    /// <para>
    /// The rule from OP2-10 is "<c>GET {uploadUrl}</c> returns <c>nextExpectedRanges</c> —
    /// use it, do not assume". This used to assume: it started at zero and advanced by
    /// the chunk length it had just sent. Both are wrong on a resume, and wrong in the
    /// expensive direction — re-sending bytes Graph already holds, or skipping bytes it
    /// does not. Graph is the authority on what it received, so every starting point and
    /// every advance comes from it.
    /// </para>
    /// </summary>
    private async Task<ChunkOutcome> UploadChunksAsync(
        StagedFile file, string uploadUrl, Action<int, string>? progress, CancellationToken ct)
    {
        // A SEPARATE named client, not the "graph" one: the session URL carries its own
        // authorisation, and attaching our bearer token to it is rejected outright. From
        // the factory rather than `new HttpClient` so the handler is pooled like every
        // other outbound call here, and so the upload path can be driven in a test.
        using var raw = httpFactory.CreateClient("graph-upload");
        raw.Timeout = TimeSpan.FromMinutes(10);

        var (known, gone) = await AskGraphAsync(raw, uploadUrl, ct);
        if (gone) return new ChunkOutcome(false, null, true, true);

        // No ranges outstanding on a session that exists means Graph already has the
        // whole file — the previous attempt died between the last chunk and recording it.
        if (known is null) return new ChunkOutcome(true, null, false, false);

        var sent = known.Value;
        var attempts = 0;

        while (sent < file.SizeBytes)
        {
            var end = Math.Min(sent + ChunkSize, file.SizeBytes) - 1;
            var length = (int)(end - sent + 1);

            // Ranged read straight from object storage — the file is never held whole.
            await using var slice = await staging.OpenRangeAsync(file, sent, end, ct);
            await using var buffer = new MemoryStream(length);
            await slice.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new StreamContent(buffer),
            };
            req.Content.Headers.ContentLength = length;
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(sent, end, file.SizeBytes);

            using var resp = await raw.SendAsync(req, ct);

            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                return new ChunkOutcome(false, null, true, true);

            if (!resp.IsSuccessStatusCode)
            {
                // 4xx other than the throttle will fail again identically; no amount of
                // asking Graph where it is will change a rejected chunk into an accepted
                // one, and retrying wastes the whole file's worth of bandwidth to learn it.
                var worthRetrying = resp.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                                    || (int)resp.StatusCode >= 500;

                if (!worthRetrying || ++attempts > MaxChunkAttempts)
                    return new ChunkOutcome(false,
                        $"Upload of {file.Name} failed at {sent / (1024 * 1024)} MB: HTTP {(int)resp.StatusCode}.",
                        worthRetrying, false);

                // Do not retry the same range blind. Graph may well have taken the chunk
                // and failed to answer, in which case resending it is the one thing that
                // cannot work — ask where it actually is and carry on from there.
                var (after, vanished) = await AskGraphAsync(raw, uploadUrl, ct);
                if (vanished) return new ChunkOutcome(false, null, true, true);
                if (after is null) return new ChunkOutcome(true, null, false, false);

                sent = after.Value;
                continue;
            }

            attempts = 0;

            // 201/200 carries the finished attachment rather than a range, so a missing
            // nextExpectedRanges on a success means the upload is complete.
            var next = await NextExpectedAsync(resp, ct);
            sent = next ?? file.SizeBytes;

            progress?.Invoke((int)(Math.Min(sent, file.SizeBytes) * 100 / file.SizeBytes), $"uploading {file.Name}");
        }

        return new ChunkOutcome(true, null, false, false);
    }

    /// <summary>
    /// Asks the session where it is.
    /// </summary>
    /// <returns>
    /// The first byte Graph still wants and false; (null, false) when it wants nothing
    /// more; or (null, true) when the session itself has gone.
    /// </returns>
    private async Task<(long? NextByte, bool Gone)> AskGraphAsync(
        HttpClient raw, string uploadUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await raw.GetAsync(uploadUrl, ct);

            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                return (null, true);

            if (!resp.IsSuccessStatusCode)
            {
                // Could not ask. Starting from zero is the only safe answer — Graph
                // ignores a range it already holds, so the cost is bandwidth, where
                // guessing a higher offset would leave a hole in the file.
                log.LogWarning("Could not read the upload session ({Status}); starting from the beginning.",
                    (int)resp.StatusCode);
                return (0, false);
            }

            return (await NextExpectedAsync(resp, ct), false);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Could not read the upload session; starting from the beginning.");
            return (0, false);
        }
    }

    /// <summary>
    /// The first byte of the first outstanding range, or null when none are outstanding.
    /// Graph writes them as "start-end" or an open-ended "start-".
    /// </summary>
    private static async Task<long?> NextExpectedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        UploadStatus? status;
        try
        {
            status = await resp.Content.ReadFromJsonAsync<UploadStatus>(ct);
        }
        catch (JsonException)
        {
            return null;   // 201 with an attachment body, or an empty 200.
        }

        var first = status?.NextExpectedRanges?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return null;

        var start = first.Split('-')[0];
        return long.TryParse(start, out var at) ? at : null;
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

    private sealed record UploadStatus(
        [property: JsonPropertyName("nextExpectedRanges")] List<string>? NextExpectedRanges);
}
