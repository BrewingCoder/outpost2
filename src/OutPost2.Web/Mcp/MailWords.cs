using OutPost2.Core.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// What things are called on each mail provider (OP2-282). Outlook has Deleted Items,
/// Junk Email, folders and colour categories; Gmail has Trash, Spam and labels that do the
/// work of both. A confirmation in the wrong provider's words is a confirmation the user
/// cannot check — "moved to Deleted Items" sends a Gmail user looking for a folder that does
/// not exist — so every sentence that names a place or a thing takes it from here.
/// </summary>
public sealed record MailWords(
    string Trash,
    string TrashKeeps,
    string Junk,
    string Folder,
    string OwnFolders,
    string Recoverable,
    string RecoverableLimit,
    string Category,
    string Categories,
    string Company,
    string OwnMarker)
{
    public static readonly MailWords Outlook = new(
        Trash: "Deleted Items",
        TrashKeeps: "They stay there until you empty it, so this is undoable.",
        Junk: "Junk Email",
        Folder: "folder",
        OwnFolders: "one of Outlook's own folders",
        Recoverable: "Recoverable Items",
        RecoverableLimit: "If it went missing, it may have been there longer than the retention window.",
        Category: "category",
        Categories: "categories",
        Company: "Microsoft",
        OwnMarker: "Outlook's own");

    public static readonly MailWords Gmail = new(
        Trash: "Trash",
        TrashKeeps: "Gmail keeps Trash for 30 days, so this is undoable until then.",
        Junk: "Spam",
        Folder: "label",
        OwnFolders: "one of Gmail's own labels",
        Recoverable: "Trash",
        RecoverableLimit: "Gmail empties Trash after 30 days, so anything older than that has gone.",
        Category: "label",
        Categories: "labels",
        Company: "Google",
        OwnMarker: "Gmail's own");

    /// <summary>
    /// For a mail provider this build has no words for. Plain and provider-free, so a
    /// sentence is at worst vague, never confidently in another provider's terms.
    /// </summary>
    public static readonly MailWords Neutral = new(
        Trash: "the trash",
        TrashKeeps: "Check the provider's trash if that was wrong.",
        Junk: "the junk folder",
        Folder: "folder",
        OwnFolders: "one of the mailbox's own folders",
        Recoverable: "the provider's deleted-items store",
        RecoverableLimit: "If it went missing, it may have been there longer than the provider keeps it.",
        Category: "category",
        Categories: "categories",
        Company: "The mail provider",
        OwnMarker: "built in");

    /// <summary>
    /// Every provider by name. This used to be <c>Google ? Gmail : Outlook</c>, which is the
    /// OP2-285 shape again: right on the day it was written, and the day a third provider
    /// arrived every sentence about it would have been in Outlook's words without anything
    /// going red (OP2-294). A provider missing here gets <see cref="Neutral"/>, and
    /// <c>ProviderParityMatrixTests</c> fails until it is given words of its own.
    /// </summary>
    public static MailWords For(ProviderKind provider) => provider switch
    {
        ProviderKind.Microsoft => Outlook,
        ProviderKind.Google => Gmail,
        _ => Neutral,
    };

    public bool IsGmail => ReferenceEquals(this, Gmail);
}
