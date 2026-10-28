namespace OutPost2.Core.Code;

/// <summary>
/// A name you say out loud, mapped to the <c>owner/name</c> GitHub actually uses.
/// <para>
/// Most repositories need no entry here: asking GitHub what this token can see resolves
/// a bare "RosettaCore" on its own. An alias exists for the cases that cannot resolve
/// themselves — a name you use that GitHub has never heard ("the core", "the bot"), or a
/// name that genuinely matches two repositories in different organisations and has to be
/// settled once rather than asked about every time.
/// </para>
/// </summary>
public sealed class RepoAlias
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Owning product user. Aliases are personal: my "the core" is not yours.</summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// The GitHub connection this applies to. Scoped rather than global because the same
    /// spoken name can mean different repositories on github.com and on an enterprise
    /// host, and a token that cannot see a repository should not be told its name.
    /// </summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>What gets said. Stored as typed; matched case- and separator-insensitively.</summary>
    public required string Alias { get; set; }

    /// <summary>The <c>owner/name</c> it resolves to.</summary>
    public required string FullName { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Normalised for matching: lower-cased with separators removed, so "rosetta-core",
    /// "Rosetta Core" and "rosettacore" are one key. Speech-to-text does not reliably
    /// reproduce a hyphen, and a lookup that requires one fails on every spoken request.
    /// </summary>
    public static string Normalise(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var ch in s)
        {
            if (ch is '-' or '_' or '.' or ' ') continue;
            buf[n++] = char.ToLowerInvariant(ch);
        }
        return new string(buf[..n]);
    }
}
