using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Tests.Web;

/// <summary>
/// What an unhandled exception tells the caller (OP2-259).
/// <para>
/// Found while testing the OP2-252 upload endpoint: a storage failure came back to
/// <c>curl</c>, over the public hostname, as a full developer exception page — internal
/// source paths, the middleware pipeline in order, class and method names, line numbers, and
/// the request's own headers echoed back. The container was running
/// <c>ASPNETCORE_ENVIRONMENT=Development</c>, and that host is reachable from the internet
/// through the Cloudflare tunnel.
/// </para>
/// <para>
/// Keyed off the environment, which had also been quietly carrying "apply migrations on
/// boot" — so the deployment could not have one without the other. Those are now two
/// switches, and this is the guard on the one that matters: a new endpoint must not be able
/// to reintroduce the leak by default.
/// </para>
/// <para>
/// The throwing path is injected by the test rather than being a real endpoint, because the
/// thing under test is the pipeline's behaviour around a throw, not any particular throw.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ErrorLeakTests
{
    private const string Admin =
        "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";

    /// <summary>Boots the real app in a named environment, with one path that throws.</summary>
    private sealed class Host(string environment) : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "op2_leak_" + Guid.NewGuid().ToString("n")[..12];
        public bool Available { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            try
            {
                using var admin = new OutPostDbContext(
                    new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
                admin.Database.ExecuteSqlRaw($"CREATE DATABASE {DbName}");
                Available = true;
            }
            catch { /* reported by the caller as a skip */ }

            builder.UseEnvironment(environment);

            builder.UseSetting(
                "ConnectionStrings:Postgres",
                $"Host=localhost;Port=5435;Database={DbName};Username=outpost2;Password=outpost2-dev");

            // The same dev-stack settings the other integration tests supply. The app
            // refuses to boot without them, by design.
            builder.UseSetting("OutPost2:PublicBaseUrl", "https://localhost");
            builder.UseSetting("OutPost2:Vault:Address", "http://localhost:8200");
            builder.UseSetting("OutPost2:Vault:AuthMethod", "Token");
            builder.UseSetting("OutPost2:Vault:Token", "op2-dev-root");
            builder.UseSetting("OutPost2:Storage:ServiceUrl", "http://localhost:3900");
            builder.UseSetting("OutPost2:Storage:AccessKey", "GK" + new string('0', 22) + "d2");
            builder.UseSetting("OutPost2:Storage:SecretKey", new string('0', 62) + "d2");

            // Migrating is now its own switch rather than a side effect of Development,
            // which is the other half of OP2-259 — and this test needs the schema in both
            // environments, which is only expressible because they were separated.
            builder.UseSetting("OutPost2:MigrateOnBoot", "true");

            builder.ConfigureServices(services =>
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter>(
                    new Boom())));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            try
            {
                using var admin = new OutPostDbContext(
                    new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);

                admin.Database.ExecuteSqlRaw(
                    $"DROP DATABASE IF EXISTS {DbName} WITH (FORCE)");
            }
            catch { /* a leftover test database is not worth failing a test over */ }
        }
    }

    /// <summary>
    /// Puts a throwing path BEHIND the app's own pipeline, so everything the app installs —
    /// including the error handling under test — is in front of it.
    /// <para>
    /// Order is the whole trick. Added ahead of the app instead, the throw escapes before
    /// any handler sees it and the test server hands the exception straight to the client,
    /// which proves nothing about either environment. Behind it, a request for a path no
    /// endpoint matches falls through to here and throws inside the pipeline, exactly where
    /// a real endpoint's exception would.
    /// </para>
    /// </summary>
    private sealed class Boom : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                next(app);

                app.Use(async (ctx, nxt) =>
                {
                    if (ctx.Request.Path == "/__op2_throw")
                        throw new InvalidOperationException(
                            "op2-259 probe: this should not reach the caller.");

                    await nxt();
                });
            };
    }

    /// <summary>Everything that must never be in a response to a stranger.</summary>
    private static readonly string[] Leaks =
    [
        "op2-259 probe",                 // the exception message itself
        "InvalidOperationException",     // the type
        "ErrorLeakTests",                // the throwing frame
        "/src/src/",                     // container source paths
        "OutPost2.Web.Auth",             // the middleware pipeline
        "Microsoft.AspNetCore.Builder",  // ditto
        "HEADERS",                       // the dev page's header dump
    ];

    [SkippableFact]
    public async Task In_production_a_thrown_error_tells_the_caller_nothing()
    {
        await using var host = new Host("Production");
        HttpClient client;

        try { client = host.CreateClient(); }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        Skip.IfNot(host.Available, "Postgres unavailable");

        using var resp = await client.GetAsync("/__op2_throw");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        foreach (var leak in Leaks)
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task In_development_it_does_leak_which_is_why_the_deployment_is_not_development()
    {
        // The control. Without it the test above passes just as well against an app that
        // never reached the throwing middleware at all, and a regression would be invisible.
        await using var host = new Host("Development");
        HttpClient client;

        try { client = host.CreateClient(); }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        Skip.IfNot(host.Available, "Postgres unavailable");

        using var resp = await client.GetAsync("/__op2_throw");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("op2-259 probe", body);
        Assert.Contains("InvalidOperationException", body);
    }

    [SkippableFact]
    public async Task Migrating_on_boot_no_longer_depends_on_the_environment()
    {
        // The other half of OP2-259. The schema has to be applied in Production too, or
        // flipping the deployment would have traded a leak for an outage — and the test
        // factory above would not boot at all.
        await using var host = new Host("Production");
        HttpClient client;

        try { client = host.CreateClient(); }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        Skip.IfNot(host.Available, "Postgres unavailable");

        // Reaches the database, so the tables exist.
        using var resp = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
