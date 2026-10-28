using Microsoft.AspNetCore.Html;

namespace OutPost2.Web.Pages;

/// <summary>
/// Inline SVG icons.
/// <para>
/// Written as C# rather than a partial because a partial taking ViewData turns every use
/// site into four lines of dictionary construction, and the view is already dense enough.
/// </para>
/// <para>
/// Every icon here sits next to a word that carries the same meaning, and all are
/// <c>aria-hidden</c>. Nothing in this app may use an icon as the only signal for a state
/// — that rule is in the brand book and it is also just correct.
/// </para>
/// </summary>
public static class Icons
{
    private static IHtmlContent Svg(string body, string? extraClass = null) =>
        new HtmlString(
            $"""<svg class="op-ico{(extraClass is null ? "" : " " + extraClass)}" viewBox="0 0 16 16" aria-hidden="true">{body}</svg>""");

    public static IHtmlContent Mic() => Svg(
        """<rect x="6" y="2" width="4" height="8" rx="2"/><path d="M3.5 8a4.5 4.5 0 0 0 9 0M8 12.5V14"/>""");

    public static IHtmlContent Lock() => Svg(
        """<rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/>""");

    public static IHtmlContent Unlock() => Svg(
        """<rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5.5 7V5a2.5 2.5 0 0 1 4.8-1"/>""");

    public static IHtmlContent Arrow() => Svg("""<path d="M3 8h10M9 4l4 4-4 4"/>""");

    public static IHtmlContent Ask() => Svg(
        """<circle cx="8" cy="8" r="6"/><path d="M6.2 6.3a1.9 1.9 0 1 1 2.6 1.8c-.5.2-.8.6-.8 1.1V10"/><path d="M8 12h.01"/>""");

    public static IHtmlContent Plus() => Svg("""<path d="M8 3v10M3 8h10"/>""");

    public static IHtmlContent Clock() => Svg(
        """<circle cx="8" cy="8" r="6"/><path d="M8 4.5V8l2.5 1.5"/>""");

    public static IHtmlContent Warn() => Svg(
        """<path d="M8 2.8 14 13H2z"/><path d="M8 6.6v3M8 11.3h.01"/>""");

    public static IHtmlContent Check() => Svg("""<path d="M3 8.5 6.5 12 13 4.5"/>""");

    public static IHtmlContent Close() => Svg("""<path d="M4 4l8 8M12 4l-8 8"/>""");

    public static IHtmlContent Copy() => Svg(
        """<rect x="6" y="6" width="8" height="8" rx="1.5"/><path d="M10 6V3.5A1.5 1.5 0 0 0 8.5 2h-5A1.5 1.5 0 0 0 2 3.5v5A1.5 1.5 0 0 0 3.5 10H6"/>""");

    /// <summary>The GitHub mark, filled rather than stroked like the rest.</summary>
    public static IHtmlContent GitHub() => Svg(
        """<path d="M8 .5a7.5 7.5 0 0 0-2.37 14.62c.37.07.51-.16.51-.36v-1.3c-2.09.45-2.53-1-2.53-1-.34-.87-.83-1.1-.83-1.1-.68-.47.05-.46.05-.46.75.05 1.15.77 1.15.77.67 1.15 1.76.82 2.19.63.07-.49.26-.82.48-1.01-1.67-.19-3.43-.84-3.43-3.72 0-.82.3-1.49.77-2.02-.08-.19-.33-.96.07-1.99 0 0 .63-.2 2.06.77a7.2 7.2 0 0 1 3.75 0c1.43-.97 2.06-.77 2.06-.77.4 1.03.15 1.8.07 1.99.48.53.77 1.2.77 2.02 0 2.89-1.76 3.53-3.44 3.71.27.23.51.69.51 1.39v2.06c0 .2.14.44.51.36A7.5 7.5 0 0 0 8 .5z" fill="currentColor" stroke="none"/>""",
        "op-ico--fill");
}
