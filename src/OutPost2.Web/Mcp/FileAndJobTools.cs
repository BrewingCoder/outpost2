using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Connections;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

[McpServerToolType]
public sealed class FileAndJobTools(
    IHttpContextAccessor http,
    IStagingService staging,
    IUploadTicketService tickets,
    IConnectionStore connections,
    IJobService jobs,
    PublicUrls urls)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    [McpServerTool(Name = "stage_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Get a one-use link for putting a file into the user's staging area, so it can be attached to mail. Use this when YOU have a file — one you generated, or one on the machine you are running on. Returns a URL to PUT the bytes to, and the name to pass to send_mail_with_attachments. This server cannot read your filesystem, so you must send the bytes yourself.")]
    public async Task<string> StageFile(
        [Description("What to call it, e.g. 'quarterly report'. This is the name you will pass to send_mail_with_attachments.")] string name,
        [Description("How big the file is, in bytes. The link will refuse anything larger.")] long sizeBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Give it a name — that's what you'll use to attach it later.";

        if (name.Trim().Length > 120)
            return "That name is too long. Something short enough to say out loud.";

        if (sizeBytes <= 0)
            return "Tell me how many bytes it is, so the link can refuse anything bigger. "
                 + "Check the file's size first — if you cannot read it, say so rather than "
                 + "staging an empty one.";

        if (sizeBytes > MaxStageBytes)
            return $"That's {sizeBytes / (1024 * 1024)} MB, over the "
                 + $"{MaxStageBytes / (1024 * 1024)} MB limit for one upload.";

        // A name already in use would make "attach the report" ambiguous, which is the one
        // thing the name exists to avoid.
        var clash = await staging.FindByNameAsync(UserId, name.Trim(), ct);
        if (clash is not null)
            return $"There is already a staged file called {clash.Name} ({clash.HumanSize}). "
                 + "Either attach that one, or pick a different name for this.";

        try
        {
            var (ticket, token) = await tickets.MintAsync(UserId, name.Trim(), sizeBytes, ct);

            // The URL, the method, and the name back — explicitly. Deriving the name from a
            // filename later would make the caller guess at what to pass.
            return $"PUT the bytes to {urls.Base}/files/stage/{token}\n"
                 + $"Then attach it as \"{ticket.Name}\".\n"
                 + $"The link is good for one upload of up to {sizeBytes} bytes and expires in "
                 + $"{(int)UploadTicket.Lifetime.TotalMinutes} minutes. For example:\n"
                 + $"  curl -X PUT --data-binary @yourfile {urls.Base}/files/stage/{token}\n"
                 + "If the PUT does not return 200, do not say the file was attached.";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The largest single upload a ticket will admit.
    /// <para>
    /// Well under the staging quota, which is per user and cumulative: this is a bound on
    /// what one unauthenticated request can push, which is a different question.
    /// </para>
    /// </summary>
    public const long MaxStageBytes = 150L * 1024 * 1024;

    [McpServerTool(Name = "list_files", ReadOnly = true, OpenWorld = true)]
    [Description("List files the user has staged for attaching. Refer to them by name.")]
    public async Task<string> ListFiles(CancellationToken ct = default)
    {
        var files = await staging.ListAsync(UserId, ct);
        if (files.Count == 0)
            return $"No files staged. The user can upload one at {urls.Files} and name it — "
                 + "or, if you have the file yourself, call stage_file and push the bytes.";

        return "Staged files:\n" + string.Join("\n",
            files.Select(f => $"- {f.Name} ({f.FileName}, {f.HumanSize})"));
    }

    [McpServerTool(Name = "send_mail_with_attachments", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Send an email with staged files attached, naming each file. Returns immediately; large uploads continue in the background.")]
    public async Task<string> SendWithAttachments(
        [Description("Recipient addresses, comma separated.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Body. Markdown is supported.")] string body,
        [Description("Names of staged files to attach, comma separated, e.g. 'signed contract'.")] string attachments,
        [Description("Which connection to send from. Required when more than one could send.")] string? connection = null,
        [Description("CC addresses, comma separated.")] string? cc = null,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailSend, connection, isWrite: true);

        if (!resolution.IsActionable) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        var recipients = Split(to);
        if (recipients.Count == 0) return "I need at least one recipient.";

        // Resolve every named attachment up front. Sending a message that is silently
        // missing a file is worse than refusing: the user would believe it went.
        var wanted = Split(attachments);
        var found = new List<Core.Files.StagedFile>();
        var missing = new List<string>();

        foreach (var name in wanted)
        {
            var f = await staging.FindByNameAsync(UserId, name, ct);
            if (f is null) missing.Add(name); else found.Add(f);
        }

        if (missing.Count > 0)
        {
            var available = await staging.ListAsync(UserId, ct);
            return $"I couldn't find {string.Join(" or ", missing.Select(m => $"\"{m}\""))}. " +
                   (available.Count == 0
                       ? "You have no files staged."
                       : $"You have: {string.Join(", ", available.Select(f => f.Name))}.");
        }

        var totalBytes = found.Sum(f => f.SizeBytes);

        // Idempotency key derived from the request itself, so a retried tool call after a
        // timeout joins the existing job instead of sending a second copy.
        var key = Fingerprint(c.Id, recipients, subject, body, found.Select(f => f.Id));

        var (job, existed) = await jobs.EnqueueAsync(
            UserId, JobKind.SendMail,
            $"{subject} to {string.Join(", ", recipients)}",
            new SendMailPayload(c.Id, recipients, Split(cc), subject, body, found.Select(f => f.Id).ToList()),
            key, c.Label, requiresApproval: false, ct);

        if (existed && job.IsTerminal) return job.Spoken();
        if (existed) return $"Already underway. {job.Spoken()}";

        // Never block on the upload. A 150 MB attachment takes minutes and would blow
        // through any tool timeout long before it finished.
        var size = totalBytes < 1024 * 1024
            ? $"{totalBytes / 1024} KB"
            : $"{totalBytes / (1024.0 * 1024):0.#} MB";

        return $"Sending from {c.Label} with {found.Count} attachment{(found.Count == 1 ? "" : "s")} ({size}). " +
               (totalBytes > 8 * 1024 * 1024
                   ? "That'll take a minute — ask me for the queue to check on it."
                   : "I'll have it in your queue.");
    }

    [McpServerTool(Name = "check_queue", ReadOnly = true, OpenWorld = true)]
    [Description("Check what is in flight, waiting for approval, or recently finished.")]
    public async Task<string> CheckQueue(
        [Description("Only show things still in progress.")] bool activeOnly = false,
        CancellationToken ct = default)
    {
        var items = await jobs.ListAsync(UserId, activeOnly, ct);
        if (items.Count == 0) return activeOnly ? "Nothing in progress." : "Queue is empty.";

        var sb = new StringBuilder();
        foreach (var j in items.Take(10)) sb.AppendLine("- " + j.Spoken());

        var waiting = items.Count(j => j.State == JobState.AwaitingApproval);
        if (waiting > 0)
            sb.AppendLine($"{waiting} item{(waiting == 1 ? "" : "s")} need your approval at " +
                          urls.Jobs);

        return sb.ToString().TrimEnd();
    }

    private static List<string> Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Fingerprint(
        Guid connectionId, IEnumerable<string> to, string subject, string body, IEnumerable<Guid> files)
    {
        var material = $"{connectionId}|{string.Join(",", to)}|{subject}|{body}|{string.Join(",", files)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }
}
