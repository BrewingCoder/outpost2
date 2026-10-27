using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;

namespace OutPost2.Infrastructure.Mail;

public interface IMailService
{
    Task<IReadOnlyList<MailSummary>> ListAsync(
        Connection connection, string folder = "inbox", int top = 25, bool unreadOnly = false,
        CancellationToken ct = default);

    Task<MailMessage?> ReadAsync(Connection connection, string messageId, CancellationToken ct = default);

    Task<IReadOnlyList<MailSummary>> SearchAsync(
        Connection connection, string query, int top = 25, CancellationToken ct = default);

    Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default);
}

public sealed class MailService(
    IMicrosoftConnectionServiceAccessor tokens,
    IHttpClientFactory httpFactory,
    ILogger<MailService> log) : IMailService
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    private async Task<HttpClient> ClientAsync(Connection connection, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(connection, ct)
            ?? throw new MailAuthRequiredException(connection.Label);

        var http = httpFactory.CreateClient("graph");
        http.BaseAddress = new Uri(Graph + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<IReadOnlyList<MailSummary>> ListAsync(
        Connection connection, string folder = "inbox", int top = 25, bool unreadOnly = false,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var q = $"me/mailFolders/{Uri.EscapeDataString(folder)}/messages" +
                $"?$top={Math.Clamp(top, 1, 100)}" +
                "&$orderby=receivedDateTime desc" +
                "&$select=id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";

        if (unreadOnly) q += "&$filter=isRead eq false";

        var page = await http.GetFromJsonAsync<GraphList<GraphMessage>>(q, ct);
        return (page?.Value ?? []).Select(m => m.ToSummary(connection.Label)).ToList();
    }

    public async Task<MailMessage?> ReadAsync(Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // uniqueBody, not body. It returns ONLY the new content, stripping the quoted reply
        // history — the single highest-value change on the read path. Without it a
        // ten-reply thread dumps ten copies of itself into context.
        var q = $"me/messages/{Uri.EscapeDataString(messageId)}" +
                "?$select=id,subject,from,toRecipients,ccRecipients,receivedDateTime,isRead," +
                "hasAttachments,uniqueBody,body,conversationId";

        using var resp = await http.GetAsync(q, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var m = await resp.Content.ReadFromJsonAsync<GraphMessage>(ct);
        if (m is null) return null;

        // Prefer uniqueBody; fall back to body when Graph omits it (it is absent on some
        // single-message conversations).
        var source = m.UniqueBody ?? m.Body;
        var text = string.Equals(source?.ContentType, "html", StringComparison.OrdinalIgnoreCase)
            ? HtmlToText.Convert(source?.Content)
            : (source?.Content ?? "");

        text = HtmlToText.TrimSignature(text);

        return new MailMessage
        {
            Id = m.Id,
            ConnectionLabel = connection.Label,
            Subject = m.Subject ?? "(no subject)",
            From = m.From?.EmailAddress?.Describe() ?? "(unknown)",
            To = (m.ToRecipients ?? []).Select(r => r.EmailAddress?.Describe() ?? "").Where(x => x != "").ToList(),
            Cc = (m.CcRecipients ?? []).Select(r => r.EmailAddress?.Describe() ?? "").Where(x => x != "").ToList(),
            ReceivedAt = m.ReceivedDateTime,
            IsRead = m.IsRead,
            HasAttachments = m.HasAttachments,
            Body = text,
            QuotedHistoryStripped = m.UniqueBody is not null,
        };
    }

    public async Task<IReadOnlyList<MailSummary>> SearchAsync(
        Connection connection, string query, int top = 25, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // $search cannot be combined with $orderby in Graph; attempting it returns a
        // confusing 400 rather than ignoring the sort.
        var q = $"me/messages?$search={Uri.EscapeDataString($"\"{query}\"")}" +
                $"&$top={Math.Clamp(top, 1, 100)}" +
                "&$select=id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";

        using var req = new HttpRequestMessage(HttpMethod.Get, q);
        req.Headers.Add("ConsistencyLevel", "eventual");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphMessage>>(ct);
        return (page?.Value ?? []).Select(m => m.ToSummary(connection.Label)).ToList();
    }

    public async Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // HTML by default. The model writes Markdown, which we convert and sanitise
        // server-side rather than asking it to emit HTML — more reliable, and it means we
        // control exactly which tags go out under the user's name.
        var (contentType, content) = mail.IsHtml
            ? ("HTML", MarkdownToHtml.Render(mail.Body))
            : ("Text", mail.Body);

        var payload = new
        {
            message = new
            {
                subject = mail.Subject,
                body = new { contentType, content },
                toRecipients = mail.To.Select(Recipient).ToArray(),
                ccRecipients = mail.Cc.Select(Recipient).ToArray(),
            },
            saveToSentItems = true,
        };

        using var resp = await http.PostAsJsonAsync("me/sendMail", payload, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            log.LogError("sendMail failed for {Label}: HTTP {Status} {Body}",
                connection.Label, (int)resp.StatusCode, body);
            throw new MailException($"Could not send from {connection.Label}: {Describe(resp.StatusCode, body)}");
        }

        log.LogInformation("Sent mail from {Label} to {Count} recipient(s).", connection.Label, mail.To.Count);

        static object Recipient(string address) => new { emailAddress = new { address } };
    }

    private static string Describe(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) &&
                e.TryGetProperty("message", out var m))
                return m.GetString() ?? status.ToString();
        }
        catch { /* not JSON */ }

        return status.ToString();
    }

    // ── Graph wire types ─────────────────────────────────────────────────────

    private sealed record GraphList<T>([property: JsonPropertyName("value")] List<T>? Value);

    private sealed record GraphMessage(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("from")] GraphRecipient? From,
        [property: JsonPropertyName("toRecipients")] List<GraphRecipient>? ToRecipients,
        [property: JsonPropertyName("ccRecipients")] List<GraphRecipient>? CcRecipients,
        [property: JsonPropertyName("receivedDateTime")] DateTimeOffset ReceivedDateTime,
        [property: JsonPropertyName("isRead")] bool IsRead,
        [property: JsonPropertyName("hasAttachments")] bool HasAttachments,
        [property: JsonPropertyName("bodyPreview")] string? BodyPreview,
        [property: JsonPropertyName("uniqueBody")] GraphBody? UniqueBody,
        [property: JsonPropertyName("body")] GraphBody? Body)
    {
        public MailSummary ToSummary(string label) => new()
        {
            Id = Id,
            ConnectionLabel = label,
            Subject = Subject ?? "(no subject)",
            From = From?.EmailAddress?.Describe() ?? "(unknown)",
            ReceivedAt = ReceivedDateTime,
            IsRead = IsRead,
            HasAttachments = HasAttachments,
            Preview = (BodyPreview ?? "").Trim(),
        };
    }

    private sealed record GraphRecipient(
        [property: JsonPropertyName("emailAddress")] GraphAddress? EmailAddress);

    private sealed record GraphAddress(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("address")] string? Address)
    {
        /// <summary>A name alone is ambiguous and an address alone is unfriendly; prefer "Name &lt;addr&gt;".</summary>
        public string Describe() =>
            string.IsNullOrWhiteSpace(Name) ? Address ?? ""
            : string.IsNullOrWhiteSpace(Address) ? Name
            : $"{Name} <{Address}>";
    }

    private sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("content")] string? Content);
}

/// <summary>Indirection so MailService does not depend on the whole connection service.</summary>
public interface IMicrosoftConnectionServiceAccessor
{
    Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default);
}

public sealed class MailException(string message) : Exception(message);

/// <summary>
/// The connection needs an interactive sign-in. Distinct from a general failure because
/// it cannot be retried and cannot be fixed inside a voice turn — it needs a browser.
/// </summary>
public sealed class MailAuthRequiredException(string label)
    : Exception($"{label} needs re-authorising before it can be used.")
{
    public string Label { get; } = label;
}
