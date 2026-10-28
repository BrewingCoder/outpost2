namespace OutPost2.Infrastructure.Feedback;

/// <summary>
/// Where friction reports go (OP2-74).
/// <para>
/// Deliberately NOT a user connection. A report is about OutPost2 itself, so it belongs in
/// OutPost2's own tracker whoever hit the problem — and the coworkers this is meant to
/// serve have no connection to that tracker and no business being given one. The server
/// files on their behalf, under its own credential, and records who reported it.
/// </para>
/// </summary>
public sealed class FeedbackOptions
{
    public const string SectionName = "OutPost2:Feedback";

    /// <summary>Base URL of the YouTrack instance, e.g. <c>https://youtrack.example.com</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>A permanent token with permission to create issues in <see cref="Project"/>, and nothing wider.</summary>
    public string Token { get; set; } = "";

    /// <summary>Project short name, e.g. <c>OP2</c>.</summary>
    public string Project { get; set; } = "OP2";

    /// <summary>Applied to every filed report so they can be found as a set.</summary>
    public string Tag { get; set; } = "friction";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(Project);
}
