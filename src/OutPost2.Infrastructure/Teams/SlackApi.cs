using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OutPost2.Infrastructure.Teams;

/// <summary>
/// The parts of the Slack Web API every Slack call shares (OP2-86): the client, the scopes
/// the chat tools need, and the response envelope.
/// <para>
/// <b>Slack fails with HTTP 200.</b> Almost every refusal — a revoked token, a missing
/// scope, a channel the user left — arrives as <c>200 OK</c> with
/// <c>{"ok":false,"error":"missing_scope","needed":"im:history"}</c>. Checking the status
/// code alone reads every one of those as success and then fails to find the payload, so
/// the envelope is checked on every response and turned into a sentence naming Slack.
/// </para>
/// <para>
/// <b>Rate limits are the exception</b>, and come back as a real HTTP 429 with a
/// <c>Retry-After</c> in seconds. Slack's limits are per method per workspace, and a
/// listing makes several calls, so this is reachable in normal use. It is reported with
/// the wait rather than retried here: a voice turn that silently sleeps for thirty
/// seconds reads as a hang.
/// </para>
/// </summary>
public static class SlackApi
{
    public const string Base = "https://slack.com/api/";

    /// <summary>
    /// Every scope reading needs, one pair per kind of conversation. Slack scopes are per
    /// method AND per conversation type: <c>conversations.history</c> on a DM needs
    /// <c>im:history</c>, on a channel <c>channels:history</c>, and a token with one but not
    /// the other fails only on the conversations of the missing kind. Checked up front so a
    /// connection cannot be made that half-works.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadScopes =
    [
        "channels:read", "channels:history",
        "groups:read", "groups:history",
        "im:read", "im:history",
        "mpim:read", "mpim:history",
        "users:read",
    ];

    /// <summary>
    /// Posting as the user. Optional at connect time: without it the connection reads and
    /// its chat.send capability is recorded as not granted, which is the truth.
    /// </summary>
    public const string SendScope = "chat:write";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static HttpClient Configure(HttpClient http, string token)
    {
        http.BaseAddress = new Uri(Base);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        return http;
    }

    /// <summary>The scopes the token carries, from the <c>x-oauth-scopes</c> header. Null if Slack did not say.</summary>
    public static IReadOnlySet<string>? Scopes(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("x-oauth-scopes", out var v)
            ? v.SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// Checks the status and the envelope, then reads the payload. Throws
    /// <see cref="SlackApiException"/> with a sentence for any refusal.
    /// </summary>
    /// <param name="label">The connection's name, for the sentence.</param>
    /// <param name="what">A verb phrase for the sentence: "list your conversations".</param>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage resp, string label, string what, CancellationToken ct)
    {
        if (resp.StatusCode is HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfter(resp);
            throw new SlackApiException("ratelimited", null, Sentence("ratelimited", null, label, what, wait), wait);
        }

        if (!resp.IsSuccessStatusCode)
            throw new SlackApiException($"http_{(int)resp.StatusCode}", null,
                $"Slack returned HTTP {(int)resp.StatusCode} when {label} tried to {what}.");

        var body = await resp.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new SlackApiException("not_json", null,
                $"Slack sent back something that isn't an API answer when {label} tried to {what}.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind is not JsonValueKind.True)
            {
                var error = Str(root, "error") ?? "unknown_error";
                var needed = Str(root, "needed");
                throw new SlackApiException(error, needed, Sentence(error, needed, label, what, null));
            }

            return root.Deserialize<T>(Json)
                ?? throw new SlackApiException("empty", null, $"Slack returned nothing when {label} tried to {what}.");
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object
        && root.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.String
            ? v.GetString()
            : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d) return d;
        if (resp.Headers.RetryAfter?.Date is { } at) return at - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>
    /// Slack's error codes in the user's terms. The common ones are spelled out; anything
    /// else is reported with Slack's own code, which is at least searchable.
    /// </summary>
    public static string Sentence(string error, string? needed, string label, string what, TimeSpan? wait) => error switch
    {
        "invalid_auth" or "not_authed" or "token_revoked" or "token_expired" or "account_inactive" =>
            $"Slack rejected the token for {label}. Replace it on the connections page.",

        // The scope by name, as Gitea's probe names its own. Slack says which one it
        // wanted in "needed", and the fix is in the Slack app's settings, not here.
        "missing_scope" =>
            $"Slack refused to let {label} {what}: the token lacks the {needed ?? "required"} scope. "
            + "Add it under OAuth & Permissions in the Slack app, reinstall the app, and replace the token.",

        "channel_not_found" =>
            "Slack can't find that conversation, or you're no longer in it.",
        "not_in_channel" =>
            $"You're not a member of that Slack channel, so {label} can't {what}.",
        "is_archived" =>
            "That Slack channel has been archived, so nothing can be posted to it.",
        "msg_too_long" =>
            "That message is too long for Slack.",
        "restricted_action" or "restricted_action_read_only_channel" or "restricted_action_thread_only_channel"
            or "ekm_access_denied" =>
            $"Slack's workspace settings don't allow {label} to {what} there.",
        "ratelimited" =>
            $"Slack is rate-limiting {label}"
            + (wait is { } w && w > TimeSpan.Zero ? $"; try again in {Math.Ceiling(w.TotalSeconds)} seconds." : "; try again in a minute."),

        _ => $"Slack refused to let {label} {what} ({error}).",
    };
}

/// <summary>
/// A Slack refusal, already in a sentence. A <see cref="TeamsException"/> because that is
/// the one type the chat tools report, the way Gitea's failures are
/// <c>GitHubException</c>s.
/// </summary>
public sealed class SlackApiException(string error, string? needed, string message, TimeSpan? retryAfter = null)
    : TeamsException(message)
{
    /// <summary>Slack's own code: <c>missing_scope</c>, <c>invalid_auth</c>, <c>ratelimited</c>…</summary>
    public string Error { get; } = error;

    /// <summary>The scope Slack said it wanted, on a <c>missing_scope</c>.</summary>
    public string? Needed { get; } = needed;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
