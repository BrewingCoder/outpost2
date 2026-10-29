using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OutPost2.Web.Pages;

public class PrivacyModel : PageModel
{
    public void OnGet()
    {
        // Nothing to do. The page is static text and exists because Entra's publisher
        // verification and the MCP client both want a privacy URL to point at; the
        // handler is here only because Razor Pages wants one.
    }
}

