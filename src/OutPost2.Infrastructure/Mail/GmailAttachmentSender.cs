using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Files;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// send_mail_with_attachments for Gmail (OP2-281), run from the job queue like the Graph
/// sender and held to the same guarantees (OP2-262):
/// <list type="bullet">
/// <item>"sending" is reported immediately before the request goes out, which is how the
/// job runner knows when "may have gone" begins;</item>
/// <item>after that, no answer or a 5xx is <see cref="SendOutcome.MaybeSent"/> — not
/// retryable, and never "nothing was sent";</item>
/// <item>before it, failures are ordinary, and nothing has left the mailbox.</item>
/// </list>
/// <para>
/// Simpler than Graph's in one respect: there is no draft to resume. Gmail takes the whole
/// message in one request of at most 35 MB, so an interrupted attempt has nothing to pick
/// up and starts again — and the resume state the job runner offers is not used.
/// </para>
/// </summary>
public sealed class GmailAttachmentSender(
    IGoogleTokenAccessor tokens,
    IStagingService staging,
    IHttpClientFactory httpFactory,
    ILogger<GmailAttachmentSender> log) : IAttachmentSenderProvider
{
    public ProviderKind Provider => ProviderKind.Google;

    public async Task<SendOutcome> SendAsync(
        Connection connection, OutgoingMail mail, IReadOnlyList<StagedFile> attachments,
        SendResumeState? resume = null,
        Func<SendResumeState, CancellationToken, Task>? persist = null,
        IProgress<(int Percent, string Detail)>? progress = null,
        CancellationToken ct = default)
    {
        var total = attachments.Sum(a => a.SizeBytes);
        if (total > GmailService.AttachmentLimitBytes)
            return new SendOutcome(false,
                $"Those attachments come to {total / (1024.0 * 1024):0.#} MB, over Gmail's 25 MB limit for one "
                + "message. Nothing was sent.", Retryable: false, DraftId: null);

        var opened = new List<Stream>();
        try
        {
            var token = await tokens.GetAccessTokenAsync(connection, ct);
            if (token is null)
                return new SendOutcome(false, $"{connection.Label} needs re-authorising before it can send. Nothing was sent.",
                    Retryable: false, DraftId: null);

            progress?.Report((5, "preparing"));

            var files = new List<MimeKit.MimeEntity>();
            foreach (var a in attachments)
            {
                var s = await staging.OpenAsync(a, ct);
                opened.Add(s);
                files.Add(GmailMime.Attachment(a.FileName, a.ContentType, s));
            }

            var html = mail.IsHtml
                ? MarkdownToHtml.Render(mail.Body)
                : "<div style=\"white-space:pre-wrap\">" + System.Net.WebUtility.HtmlEncode(mail.Body) + "</div>";

            var message = GmailMime.Compose(connection.AccountIdentifier,
                mail.To.Select(MailService.ParseRecipient), mail.Cc.Select(MailService.ParseRecipient),
                mail.Subject, html, mail.Importance, files);

            // Composed in full before anything is sent, so a staged file that cannot be read
            // fails here — before the request, where failing is safe.
            await using var raw = new MemoryStream();
            await GmailMime.WriteAsync(message, raw, ct);
            raw.Position = 0;

            if (raw.Length > 35L * 1024 * 1024)
                return new SendOutcome(false,
                    "With its attachments encoded the message is over Gmail's 35 MB limit. Nothing was sent.",
                    Retryable: false, DraftId: null);

            using var http = httpFactory.CreateClient("gmail-upload");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            progress?.Report((95, "sending"));
            await GmailService.SendRawAsync(http, connection, raw, threadId: null, log, ct);
            progress?.Report((100, "sent"));

            log.LogInformation("Sent Gmail from {Label} with {Count} attachment(s).", connection.Label, attachments.Count);
            return new SendOutcome(true, null, false, null);
        }
        catch (MailMaybeSentException)
        {
            return new SendOutcome(false, SendOutcome.MaybeSentMessage, Retryable: false, DraftId: null, MaybeSent: true);
        }
        catch (MailThrottledException ex)
        {
            // A refusal is an answer: it did not go, and later it may.
            return new SendOutcome(false, ex.Message, Retryable: true, DraftId: null);
        }
        catch (MailAuthRequiredException ex)
        {
            return new SendOutcome(false, ex.Message + " Nothing was sent.", Retryable: false, DraftId: null);
        }
        catch (MailException ex)
        {
            return new SendOutcome(false, ex.Message, Retryable: false, DraftId: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Everything that reaches here happened before the request: SendRawAsync turns
            // every failure after it into MailMaybeSentException.
            log.LogError(ex, "Gmail send for {Label} failed before anything was sent.", connection.Label);
            return new SendOutcome(false, ex.Message, Retryable: true, DraftId: null);
        }
        finally
        {
            foreach (var s in opened) await s.DisposeAsync();
        }
    }
}
