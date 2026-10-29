using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Security;

/// <summary>
/// Trusting a private certificate authority, for a GitHub Enterprise or on-premises
/// YouTrack server behind one (OP2-45).
/// <para>
/// Worth real certificates rather than a mocked callback. The whole value of this code
/// is what it REFUSES, and a test that asserts the happy path against a stub proves only
/// that a lambda was called. These build an actual CA, sign actual leaves with it, and
/// run the actual chain builder.
/// </para>
/// </summary>
public class PrivateCaTrustTests : IDisposable
{
    private readonly List<X509Certificate2> _certs = [];
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var c in _certs) c.Dispose();
        foreach (var f in _files) { try { File.Delete(f); } catch (IOException) { } }
        GC.SuppressFinalize(this);
    }

    private X509Certificate2 Ca(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _certs.Add(ca);
        return ca;
    }

    private X509Certificate2 LeafSignedBy(X509Certificate2 ca, string host)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());

        var leaf = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(20),
            Guid.NewGuid().ToByteArray());

        _certs.Add(leaf);
        return leaf;
    }

    private string PemFile(params X509Certificate2[] certs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"op2-ca-{Guid.NewGuid():n}.pem");
        File.WriteAllText(path, string.Join('\n', certs.Select(c => c.ExportCertificatePem())));
        _files.Add(path);
        return path;
    }

    private static bool Check(
        Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> validate,
        X509Certificate2 presented, SslPolicyErrors errors)
    {
        using var chain = new X509Chain();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://ghe.internal.example.com");
        return validate(req, presented, chain, errors);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_PEM_bundle_loads_every_certificate_in_it()
    {
        var path = PemFile(Ca("Example Root"), Ca("Example Issuing"));

        Assert.Equal(2, PrivateCaTrust.Load([path]).Count);
    }

    [Fact]
    public void A_missing_or_unreadable_CA_file_is_skipped_rather_than_fatal()
    {
        // Taking the whole server down over one self-hosted connection would be a much
        // larger failure than the one it is reporting.
        var good = PemFile(Ca("Example Root"));

        var roots = PrivateCaTrust.Load([good, Path.Combine(Path.GetTempPath(), "no-such-ca.pem"), ""]);

        Assert.Single(roots);
    }

    // ── the default must stay the default ────────────────────────────────────

    [Fact]
    public void With_nothing_configured_there_is_no_custom_validation_at_all()
    {
        // Null, not a callback that defers to the platform: a reimplementation of the
        // default is not the default, and the right amount of custom TLS code where
        // there are no private roots is none.
        Assert.Null(PrivateCaTrust.Validator([]));

        using var handler = (HttpClientHandler)PrivateCaTrust.Handler([]);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    // ── what it accepts ──────────────────────────────────────────────────────

    [Fact]
    public void A_certificate_chaining_to_the_configured_root_is_accepted()
    {
        var ca = Ca("Example Root");
        var leaf = LeafSignedBy(ca, "ghe.internal.example.com");

        var validate = PrivateCaTrust.Validator(PrivateCaTrust.Load([PemFile(ca)]))!;

        Assert.True(Check(validate, leaf, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_clean_handshake_is_accepted_without_consulting_the_chain()
    {
        var validate = PrivateCaTrust.Validator(PrivateCaTrust.Load([PemFile(Ca("Example Root"))]))!;

        Assert.True(Check(validate, LeafSignedBy(Ca("Other Root"), "x"), SslPolicyErrors.None));
    }

    // ── what it refuses, which is the point ──────────────────────────────────

    [Fact]
    public void A_certificate_from_some_other_authority_is_refused()
    {
        var mine = Ca("Example Root");
        var theirs = Ca("Somebody Else");
        var leaf = LeafSignedBy(theirs, "ghe.internal.example.com");

        var validate = PrivateCaTrust.Validator(PrivateCaTrust.Load([PemFile(mine)]))!;

        Assert.False(Check(validate, leaf, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_certificate_for_the_WRONG_HOSTNAME_is_refused_even_from_the_trusted_CA()
    {
        // The hole in the hand-rolled version this replaced. It forgave every error the
        // same way, so a name mismatch passed as long as the chain built — meaning any
        // host holding any certificate from that CA could stand in for the real one,
        // which inside an organisation running its own CA is most of them.
        var ca = Ca("Example Root");
        var leaf = LeafSignedBy(ca, "somewhere-else.internal.example.com");

        var validate = PrivateCaTrust.Validator(PrivateCaTrust.Load([PemFile(ca)]))!;

        Assert.False(Check(validate, leaf, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(Check(validate, leaf,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void No_certificate_at_all_is_refused()
    {
        var validate = PrivateCaTrust.Validator(PrivateCaTrust.Load([PemFile(Ca("Example Root"))]))!;

        Assert.False(Check(validate, null!, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void An_expired_certificate_from_the_trusted_CA_is_still_refused()
    {
        var ca = Ca("Example Root");

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=ghe.internal.example.com", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        // Inside the CA's own validity window, or Create refuses to issue it at all —
        // the certificate has to be expired, not impossible.
        var expired = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-12), DateTimeOffset.UtcNow.AddHours(-1),
            Guid.NewGuid().ToByteArray());
        _certs.Add(expired);

        var validate = PrivateCaTrust.Validator(PrivateCaTrust.Load([PemFile(ca)]))!;

        // Trusting the issuer says nothing about how long it meant the certificate to
        // live, and the chain builder still checks that.
        Assert.False(Check(validate, expired, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void The_handler_carries_the_callback_when_roots_are_configured()
    {
        using var handler = (HttpClientHandler)PrivateCaTrust.Handler(
            PrivateCaTrust.Load([PemFile(Ca("Example Root"))]));

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }
}
