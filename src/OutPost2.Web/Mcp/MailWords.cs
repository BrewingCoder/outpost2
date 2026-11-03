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
    string Categories)
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
        Categories: "categories");

    public static readonly MailWords Gmail = new(
        Trash: "Trash",
        TrashKeeps: "Gmail keeps Trash for 30 days, so this is undoable until then.",
        Junk: "Spam",
        Folder: "label",
        OwnFolders: "one of Gmail's own labels",
        Recoverable: "Trash",
        RecoverableLimit: "Gmail empties Trash after 30 days, so anything older than that has gone.",
        Category: "label",
        Categories: "labels");

    public static MailWords For(ProviderKind provider) =>
        provider == ProviderKind.Google ? Gmail : Outlook;

    public bool IsGmail => ReferenceEquals(this, Gmail);
}
