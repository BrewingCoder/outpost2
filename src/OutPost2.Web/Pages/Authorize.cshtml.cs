using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Auth;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class AuthorizeModel(IOAuthService oauth, IConnectionStore store) : PageModel
{
    public string ClientName { get; private set; } = "An MCP client";
    public string ClientId { get; private set; } = "";
    public string RedirectUri { get; private set; } = "";
    public string CodeChallenge { get; private set; } = "";
    public string? State { get; private set; }
    public IReadOnlyList<Connection> Connections { get; private set; } = [];

    /// <summary>
    /// Just the host of the redirect. Shown large and in mono because it is the single
    /// field that tells a person whether this request is the one they started: a client
    /// name is whatever it registered itself as, but the code goes where the host says.
    /// </summary>
    public string RedirectHost { get; private set; } = "";

    public DateTimeOffset RequestedAt { get; } = DateTimeOffset.UtcNow;

    public string SignedInAs { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(
        string client_id, string redirect_uri, string code_challenge, string? state)
    {
        var client = await oauth.FindClientAsync(client_id);
        if (client is null) return BadRequest("Unknown client.");

        ClientName = client.ClientName;
        ClientId = client_id;
        RedirectUri = redirect_uri;
        CodeChallenge = code_challenge;
        State = state;

        RedirectHost = Uri.TryCreate(redirect_uri, UriKind.Absolute, out var u)
            ? u.Host
            : redirect_uri;

        SignedInAs = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "you";

        Connections = await store.ListAsync(User.UserId());
        return Page();
    }
}
