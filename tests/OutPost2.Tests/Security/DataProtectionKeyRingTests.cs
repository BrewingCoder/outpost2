using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Security;

/// <summary>
/// OP2-56. The bug was not that encryption was wrong — it was that the key ring did not
/// survive a deploy, so every cookie and every antiforgery token minted before a rebuild
/// was rejected after it, and an OAuth consent in flight at the wrong moment simply died.
/// <para>
/// The test that matters is therefore about <em>persistence across a restart</em>, not
/// about the cryptography. Protecting and unprotecting inside one provider would have
/// passed the whole time the bug was live.
/// </para>
/// </summary>
public class DataProtectionKeyRingTests
{
    /// <summary>
    /// Transit, in process. Enough for the envelope: a fresh key per call, and unwrapping
    /// that only works for keys this instance actually minted.
    /// </summary>
    private sealed class InMemoryVault : IVaultTransitClient
    {
        private readonly Dictionary<string, byte[]> _wrapped = [];
        private int _n;

        public Task<DataKey> GenerateDataKeyAsync(CancellationToken ct = default)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var handle = $"vault:v1:ring{_n++}";
            _wrapped[handle] = key;
            return Task.FromResult(new DataKey((byte[])key.Clone(), handle));
        }

        public Task<byte[]> DecryptDataKeyAsync(string wrappedKey, CancellationToken ct = default) =>
            _wrapped.TryGetValue(wrappedKey, out var k)
                ? Task.FromResult((byte[])k.Clone())
                : throw new VaultException($"unknown wrapped key {wrappedKey}");

        public Task<string> RewrapAsync(string wrappedKey, CancellationToken ct = default)
        {
            var handle = $"vault:v1:ring{_n++}";
            _wrapped[handle] = _wrapped[wrappedKey];
            return Task.FromResult(handle);
        }

        public Task<bool> HealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    // ── the envelope itself ──────────────────────────────────────────────────

    [Fact]
    public void A_key_ring_element_round_trips_through_vault()
    {
        var protector = new CredentialProtector(new InMemoryVault());
        var original = new XElement("key", new XElement("masterKey", "bWFzdGVyLWtleS1ieXRlcw=="));

        var sealedInfo = new VaultXmlEncryptor(protector).Encrypt(original);
        var recovered = new VaultXmlDecryptor(protector).Decrypt(sealedInfo.EncryptedElement);

        Assert.Equal(original.ToString(SaveOptions.DisableFormatting),
                     recovered.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void The_sealed_element_carries_none_of_the_key_material()
    {
        var protector = new CredentialProtector(new InMemoryVault());
        const string material = "bWFzdGVyLWtleS1ieXRlcw==";

        var sealedInfo = new VaultXmlEncryptor(protector)
            .Encrypt(new XElement("key", new XElement("masterKey", material)));

        var xml = sealedInfo.EncryptedElement.ToString();

        // This is what makes a stolen database inert: the row names no key and no element
        // of the plaintext, only a Vault handle and ciphertext.
        Assert.DoesNotContain(material, xml, StringComparison.Ordinal);
        Assert.DoesNotContain("masterKey", xml, StringComparison.Ordinal);
        Assert.Contains("vault:v", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_decryptor_named_in_the_sealed_element_is_the_one_that_can_read_it()
    {
        // The framework stores this type name inside every key and instantiates it by
        // name to read the key back. Renaming the class orphans every key already in the
        // database, which presents as OP2-56 all over again — hence an explicit assert.
        var sealedInfo = new VaultXmlEncryptor(new CredentialProtector(new InMemoryVault()))
            .Encrypt(new XElement("key", "x"));

        Assert.Equal(typeof(VaultXmlDecryptor), sealedInfo.DecryptorType);
    }

    [Fact]
    public void The_decryptor_offers_the_only_constructor_shape_the_framework_can_call()
    {
        // SimpleActivator builds a decryptor from its type name and looks for exactly two
        // shapes: parameterless, or this one. Ordinary constructor injection compiles and
        // registers cleanly and then throws "No parameterless constructor defined" the
        // first time a key is READ — i.e. after a restart, which is the one moment OP2-56
        // is supposed to have made safe. Asserted here because it cost a cycle to find.
        Assert.NotNull(typeof(VaultXmlDecryptor).GetConstructor([typeof(IServiceProvider)]));
    }

    [Fact]
    public void A_key_sealed_under_one_kek_cannot_be_read_without_it()
    {
        var sealedInfo = new VaultXmlEncryptor(new CredentialProtector(new InMemoryVault()))
            .Encrypt(new XElement("key", new XElement("masterKey", "secret-bytes")));

        // A different Vault is a different KEK. Possession of the database row is not
        // possession of the key ring.
        var elsewhere = new VaultXmlDecryptor(new CredentialProtector(new InMemoryVault()));

        Assert.Throws<VaultException>(() => elsewhere.Decrypt(sealedInfo.EncryptedElement));
    }

    // ── the regression: surviving a restart ──────────────────────────────────

    private const string Admin =
        "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";

    /// <summary>
    /// Builds a provider the way Program.cs does, against one named database. Each call
    /// stands in for a fresh container: new service provider, new key ring cache, same
    /// storage underneath.
    /// </summary>
    private static ServiceProvider BuildHost(string connectionString, IVaultTransitClient vault)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OutPostDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton(vault);
        services.AddSingleton<ICredentialProtector, CredentialProtector>();
        services.AddDataProtection()
            .SetApplicationName("outpost2")
            .PersistKeysToDbContext<OutPostDbContext>()
            .ProtectKeysWithVault();

        return services.BuildServiceProvider();
    }

    [SkippableFact]
    public async Task A_cookie_protected_before_a_restart_still_opens_after_one()
    {
        var db = "op2_dpk_" + Guid.NewGuid().ToString("n")[..12];
        var target = Admin.Replace("Database=postgres", $"Database={db}");

        try
        {
            await using (var admin = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
            {
                await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {db}");
            }
        }
        catch (Exception e)
        {
            Skip.If(true, $"Postgres not reachable on localhost:5435: {e.Message}");
        }

        try
        {
            await using (var ctx = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(target).Options))
            {
                await ctx.Database.MigrateAsync();
            }

            // One Vault across both hosts, which is the real arrangement: the container
            // is replaced, the KEK is not.
            var vault = new InMemoryVault();
            const string payload = "op2:uid=7f3c…|issued=2026-10-29";
            string token;

            await using (var before = BuildHost(target, vault))
            {
                token = before.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("op2.auth").Protect(payload);
            }

            // Everything in-process is gone. Only Postgres and Vault carried over —
            // exactly what a deploy leaves standing.
            await using (var after = BuildHost(target, vault))
            {
                var opened = after.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("op2.auth").Unprotect(token);

                Assert.Equal(payload, opened);
            }

            // And the ring really is in the database, not in some fallback the test
            // happened to share. Before OP2-56 this table did not exist at all.
            await using (var ctx = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(target).Options))
            {
                var rows = await ctx.DataProtectionKeys.ToListAsync();
                Assert.NotEmpty(rows);

                // Sealed at rest: the stored XML names the Vault envelope, and carries no
                // readable master key for anyone who gets a dump of this table.
                Assert.All(rows, r =>
                {
                    Assert.Contains("vaultSealedKey", r.Xml!, StringComparison.Ordinal);
                    Assert.DoesNotContain("<masterKey", r.Xml!, StringComparison.Ordinal);
                });
            }
        }
        finally
        {
            try
            {
                await using var admin = new OutPostDbContext(
                    new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
                await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            }
            catch { /* best effort */ }
        }
    }
}
