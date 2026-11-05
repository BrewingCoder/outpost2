namespace OutPost2.Core.Connections;

/// <summary>
/// What a connection's provider is called in a sentence the user hears (OP2-294).
/// <para>
/// Separate from <see cref="CapabilityCatalog.ProviderHeading"/>, which is a plural group
/// heading for the connections page ("YouTrack issue trackers") and reads wrongly mid-sentence.
/// A refusal has to name the provider, because "can't do that" on a Gmail connection and
/// "can't do that" on a work mailbox need different next moves from the user — and a
/// refusal that names neither is the silence OP2-294 was opened to stop.
/// </para>
/// </summary>
public static class ProviderNames
{
    /// <summary>
    /// The provider as the user knows it. A personal Microsoft account is named as such,
    /// because what it cannot do is exactly what separates it from a work one (OP2-81).
    /// An unknown kind falls back to its enum name rather than to "Microsoft", which is the
    /// mistake this whole sweep is about.
    /// </summary>
    public static string Of(Connection c) =>
        PersonalMicrosoftAccount.Is(c) ? "personal Microsoft account" : Of(c.Provider);

    public static string Of(ProviderKind provider) => provider switch
    {
        ProviderKind.Microsoft => "Microsoft",
        ProviderKind.Google => "Gmail",
        _ => provider.ToString(),
    };
}
