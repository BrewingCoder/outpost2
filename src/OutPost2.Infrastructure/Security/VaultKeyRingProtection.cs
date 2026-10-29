using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OutPost2.Infrastructure.Security;

/// <summary>
/// Encrypts the ASP.NET Core data protection key ring at rest, under the same Vault KEK
/// that already guards the mailbox credentials (OP2-56).
///
/// <para>
/// The key ring is stored in Postgres so it survives a deploy. That solves the original
/// problem — a new container no longer invalidates everyone's cookies — but it also moves
/// key material into the database, where it would otherwise sit in the clear next to the
/// sealed credentials it can decrypt. Without this, anyone holding a database dump could
/// mint a valid auth cookie for any user.
/// </para>
///
/// <para>
/// Envelope encryption, reusing <see cref="ICredentialProtector"/> rather than a second
/// implementation of the same idea: Vault mints a data key, we seal the key ring XML with
/// AES-GCM, and only the wrapped key is persisted. Rotating the KEK rotates this with
/// everything else, and the plaintext KEK never leaves Vault.
/// </para>
///
/// <para>
/// <b>The consequence to understand before deploying:</b> the key ring is now unreadable
/// without Vault. If Vault is sealed or unreachable, the app cannot decrypt cookies or
/// antiforgery tokens and the web UI is down — where previously it would have come up and
/// silently signed everybody out. That is the better failure: loud, and it does not
/// quietly discard the keys protecting other people's sessions. Mail credentials already
/// had exactly this dependency, so it adds no new single point of failure.
/// </para>
/// </summary>
public sealed class VaultXmlEncryptor(ICredentialProtector protector) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        // Synchronous by interface contract, and there is no async overload to reach for.
        // Blocking is acceptable here and nowhere else in this codebase: the key ring is
        // written when a key is created and read when the ring is loaded or refreshed —
        // a handful of calls per day, never on a request path. ASP.NET Core has no
        // synchronisation context, so this cannot deadlock the way it would in a UI app.
        var sealedXml = protector
            .ProtectAsync(plaintextElement.ToString(SaveOptions.DisableFormatting))
            .GetAwaiter().GetResult();

        return new EncryptedXmlInfo(
            new XElement("vaultSealedKey", sealedXml.ToJson()),
            typeof(VaultXmlDecryptor));
    }
}

/// <summary>
/// The other half. The data protection system records this type's assembly-qualified name
/// inside every sealed key and instantiates it from the container to read one back.
/// <para>
/// <b>So renaming, moving or re-namespacing this class orphans every key already stored</b>
/// — which presents as every user being signed out on deploy, the exact symptom OP2-56
/// exists to stop. Keep the name, or write a migration that rewrites the stored elements.
/// </para>
/// </summary>
public sealed class VaultXmlDecryptor : IXmlDecryptor
{
    private readonly ICredentialProtector _protector;

    /// <summary>
    /// The shape the framework's activator actually supports. It instantiates a decryptor
    /// by type name through <c>SimpleActivator</c>, which only ever looks for a
    /// parameterless constructor or this one — ordinary constructor injection compiles,
    /// registers and then fails at runtime with "no parameterless constructor defined",
    /// at the moment a key is read back rather than when it is written.
    /// </summary>
    public VaultXmlDecryptor(IServiceProvider services)
        : this(services.GetRequiredService<ICredentialProtector>()) { }

    /// <summary>Direct construction, for tests and for the encryptor's own round trip.</summary>
    public VaultXmlDecryptor(ICredentialProtector protector) => _protector = protector;

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var plaintext = _protector
            .UnprotectAsync(ProtectedCredential.FromJson(encryptedElement.Value))
            .GetAwaiter().GetResult();

        // PreserveWhitespace: the element is reparsed into the key ring and compared by
        // the framework, so it has to come back byte-identical to what went in.
        return XElement.Parse(plaintext, LoadOptions.PreserveWhitespace);
    }
}

public static class VaultDataProtectionExtensions
{
    /// <summary>
    /// Seals the key ring with the Vault KEK. The counterpart to
    /// <c>PersistKeysToDbContext</c>: one decides where the ring lives, this decides
    /// whether holding that storage is enough to read it.
    /// </summary>
    public static IDataProtectionBuilder ProtectKeysWithVault(this IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<VaultXmlEncryptor>();

        // Configured through the options pipeline rather than by constructing an encryptor
        // here, so it resolves ICredentialProtector from the real container — the same
        // instance, and the same Vault token lease, as everything else.
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<VaultXmlEncryptor>((options, encryptor) => options.XmlEncryptor = encryptor);

        return builder;
    }
}
