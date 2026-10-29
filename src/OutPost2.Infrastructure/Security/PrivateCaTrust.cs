using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace OutPost2.Infrastructure.Security;

/// <summary>
/// Certificate authorities this deployment trusts in addition to the operating
/// system's (OP2-45).
/// <para>
/// Deployment-wide rather than per connection, which the ticket left as a choice. A
/// GitHub Enterprise server behind a private CA belongs to the same organisation that is
/// running OutPost2 — whoever can add the connection can already reach the server, and
/// the person who decides which CA to trust is the operator, not the user. Putting a PEM
/// on a connection row would mean a user could make the server trust an authority, which
/// is a larger decision than the feature needs.
/// </para>
/// </summary>
public sealed class TrustedRootsOptions
{
    public const string SectionName = "OutPost2:TrustedRoots";

    /// <summary>
    /// Paths to PEM or DER certificate files. Configured as
    /// <c>OutPost2__TrustedRoots__CaCertPaths__0</c>, so a compose file can mount a CA
    /// bundle and name it without a rebuild.
    /// </summary>
    public string[] CaCertPaths { get; set; } = [];

    public bool Any => CaCertPaths.Length > 0;
}

/// <summary>
/// Builds the TLS validation callback for a client that has to reach a host behind a
/// private CA.
/// </summary>
public static class PrivateCaTrust
{
    /// <summary>
    /// Loads what is configured, skipping anything unreadable with a warning.
    /// <para>
    /// A missing CA file must not stop the process: it would take the whole server down
    /// over one self-hosted connection, and the failure it causes instead — that one
    /// host's TLS not validating — is local, obvious and says so.
    /// </para>
    /// </summary>
    public static X509Certificate2Collection Load(IEnumerable<string> paths, ILogger? log = null)
    {
        var roots = new X509Certificate2Collection();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                // A bundle file may hold several certificates; a single cert is the
                // one-element case of the same thing.
                var loaded = new X509Certificate2Collection();
                loaded.ImportFromPemFile(path);

                if (loaded.Count == 0) loaded.Add(X509CertificateLoader.LoadCertificateFromFile(path));

                foreach (var cert in loaded) roots.Add(cert);
                log?.LogInformation("Trusting {Count} additional root(s) from {Path}.", loaded.Count, path);
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Could not load the CA certificate at {Path}; it will not be trusted.", path);
            }
        }

        return roots;
    }

    /// <summary>
    /// The validation callback, or <b>null</b> when nothing extra is configured.
    /// <para>
    /// Null matters: returning a callback that merely defers to the default is not the
    /// same thing, because it replaces the platform's validation with our reimplementation
    /// of it. Where there are no private roots, the right amount of custom TLS code is
    /// none.
    /// </para>
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>?
        Validator(X509Certificate2Collection roots)
    {
        if (roots.Count == 0) return null;

        return (_, cert, chain, errors) =>
        {
            if (errors == SslPolicyErrors.None) return true;
            if (cert is null || chain is null) return false;

            // ONLY a chain error may be forgiven. A private CA explains why the chain
            // does not reach a trusted root; it explains nothing about a certificate
            // issued for a different hostname, and forgiving that would mean any host
            // holding any certificate from that CA could impersonate the GHE server —
            // which, inside an organisation running its own CA, is most of them.
            if (errors != SslPolicyErrors.RemoteCertificateChainErrors) return false;

            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);

            // Private CAs rarely publish a CRL or run an OCSP responder, and a revocation
            // check that cannot complete fails the build. The alternative is refusing
            // every private CA, which is the feature.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            return chain.Build(cert);
        };
    }

    /// <summary>
    /// A primary handler for a named <c>HttpClient</c>, trusting the configured roots.
    /// </summary>
    public static HttpMessageHandler Handler(X509Certificate2Collection roots)
    {
        var handler = new HttpClientHandler();
        if (Validator(roots) is { } validate) handler.ServerCertificateCustomValidationCallback = validate;
        return handler;
    }
}
