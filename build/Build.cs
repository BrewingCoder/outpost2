using System.Net.Http;
using Tamp;
using Tamp.Docker.V27;
using Tamp.DotNetCoverage.V18;
using Tamp.EFCore.V10;
using Tamp.NetCli.V10;
using Tamp.ReportGenerator.V5;
using Tamp.Security.Pipeline;
using Tamp.Telegram;
using Tamp.Testcontainers.V4;
using Tamp.TruffleHog.V3;

/// <summary>
/// OutPost2's build, driven by Tamp.
/// <para>
/// Inherits <see cref="SecurityPipelineBuild"/>, which supplies the whole SBOM → SAST →
/// SCA → secrets/misconfig chain plus the Dependency-Track and DefectDojo push half. That
/// base class earns its place here more than in most adopters: this server custodies other
/// people's mailbox refresh tokens, so "what is in our dependency graph" is an operational
/// question rather than a compliance checkbox.
/// </para>
/// <para>
/// Run <c>tamp Ci</c> for the gate, <c>tamp Verify</c> for the gate plus the full security
/// chain, or <c>tamp</c> on its own for Compile. Targets are discoverable with the list
/// flag, and in JSON for an agent.
/// </para>
/// </summary>
class Build : SecurityPipelineBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Parameter("Build configuration")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution = null!;
    [GitRepository] readonly GitRepository Git = null!;

    // Fires only when a target fails, and only when the token and chat id are both in the
    // environment — so a developer without them sees nothing and CI pages the right place.
    // Telegram rather than email because the person on call is usually driving.
    [BuildReporter] readonly IBuildReporter Telegram = TelegramBuildReporter.FromEnvironment();

    // ---- tools -------------------------------------------------------------

    [NuGetPackage("dotnet-ef", Version = "10.0.14")]
    readonly Tool Ef = null!;

    [NuGetPackage("dotnet-coverage", Version = "18.6.2")]
    readonly Tool CoverageTool = null!;

    // ExecutableName because the binary is `reportgenerator`, not the package id — and
    // [NuGetPackage] binds eagerly at startup, so getting this wrong fails every target,
    // not just the one that uses the tool.
    [NuGetPackage("dotnet-reportgenerator-globaltool", Version = "5.4.17",
                  ExecutableName = "reportgenerator")]
    readonly Tool ReportGeneratorTool = null!;

    // Optional on purpose. [FromPath] binds eagerly at startup, so a required attribute
    // would fail EVERY target — Info, Compile, Test — on a machine without the binary.
    // Null instead, and the target that needs it self-skips with a reason.
    [FromPath("trufflehog", Optional = true)] readonly Tool? TruffleHogTool = null;
    [FromPath("docker", Optional = true)] readonly Tool? DockerTool = null;

    // ---- layout ------------------------------------------------------------

    AbsolutePath Artifacts => RootDirectory / "artifacts";
    AbsolutePath CoverageDir => Artifacts / "coverage";

    // Deliberately NOT inside CoverageDir. The merge globs that directory for inputs, so
    // writing the result there makes every run merge the previous merge into itself —
    // silently, and with plausible-looking output.
    AbsolutePath MergedCoverage => Artifacts / "coverage.cobertura.xml";
    AbsolutePath MigrationsDir => Artifacts / "migrations";
    AbsolutePath SecretsDir => Artifacts / "secrets";

    // trufflehog's git source takes a URI and rejects a bare Windows path outright,
    // with "unsupported Git URI". A file:// prefix plus forward slashes is the form it
    // accepts on every platform.
    string LocalRepoUri => "file://" + RootDirectory.Value.Replace('\\', '/');

    AbsolutePath InfrastructureProject =>
        RootDirectory / "src" / "OutPost2.Infrastructure" / "OutPost2.Infrastructure.csproj";

    AbsolutePath WebProject =>
        RootDirectory / "src" / "OutPost2.Web" / "OutPost2.Web.csproj";

    [Parameter("Base URL to smoke-test after a deploy.", EnvironmentVariable = "OUTPOST2_SMOKE_URL")]
    readonly string SmokeUrl = "http://localhost:5080/";

    [Parameter("Compose service to build and deploy.")]
    readonly string Service = "web";

    // ---- SecurityPipelineBuild ---------------------------------------------

    protected override string SecurityProductName => "OutPost2";
    protected override string SecuritySolutionPath => Solution.Path;

    // ---- information -------------------------------------------------------

    Target Info => _ => _
        .Description("Print build context — first thing worth having at the top of a CI log.")
        .Executes(() =>
        {
            Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
            Console.WriteLine($"  Commit:        {Git.Commit[..7]}");
            Console.WriteLine($"  Configuration: {Configuration}");
            Console.WriteLine($"  Solution:      {Solution.Name} ({Solution.Projects.Count} projects)");
            Console.WriteLine($"  Local build:   {IsLocalBuild}");
            Console.WriteLine($"  Docker:        {DescribeDocker()}");
        });

    static string DescribeDocker()
    {
        var docker = Testcontainers.Probe();
        return docker.IsAvailable
            ? $"available ({docker.DockerHostEndpoint})"
            : $"UNAVAILABLE — {docker.UnavailableReason}";
    }

    // ---- lifecycle ---------------------------------------------------------

    Target Clean => _ => _
        .Description("Delete bin/obj across the tree and the artifacts directory.")
        .Executes(() =>
        {
            foreach (var d in RootDirectory.GlobDirectories("src/**/bin", "src/**/obj",
                                                            "tests/**/bin", "tests/**/obj"))
                d.Delete();
            CleanArtifacts();
        });

    Target Restore => _ => _
        .Internal()
        .Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));

    Target Compile => _ => _
        .DependsOn(nameof(Restore))
        .Description("Build the solution.")
        .Executes(() => DotNet.Build(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoRestore(true)));

    Target Test => _ => _
        .DependsOn(nameof(Compile))
        .Description("Run the test suite with coverage collected.")
        .Produces("artifacts/coverage/**/*.trx")
        .Produces("artifacts/coverage/**/*.cobertura.xml")
        // The integration suites need Postgres, Vault and Garage, and skip themselves when
        // those are absent — so this stays green without Docker, it just proves less. The
        // Info target prints which of the two you got.
        .Executes(() => DotNet.Test(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoBuild(true)
            .AddLogger("trx;LogFileName=test-results.trx")
            .AddDataCollector("XPlat Code Coverage")
            .SetResultsDirectory(CoverageDir)));

    Target Coverage => _ => _
        .DependsOn(nameof(Test))
        .Description("Merge per-run coverage into one Cobertura file.")
        .Produces("artifacts/coverage.cobertura.xml")
        .Executes(() => DotNetCoverage.Merge(CoverageTool, m => m
            .AddInputs(CoverageDir.GlobFiles("**/*.cobertura.xml"))
            .SetOutput(MergedCoverage)
            .SetOutputFormat(CoverageFormat.Cobertura)));

    Target CoverageReport => _ => _
        .DependsOn(nameof(Coverage))
        .Description("Render the merged coverage as browsable HTML.")
        .Produces("artifacts/coverage-report/**")
        .Executes(() => ReportGenerator.Run(ReportGeneratorTool, s => s
            .AddReport(MergedCoverage)
            .SetTargetDir(Artifacts / "coverage-report")
            .AddReportType(ReportGeneratorReportType.HtmlInline)
            .AddReportType(ReportGeneratorReportType.TextSummary)
            .SetTitle("OutPost2")));

    // ---- database ----------------------------------------------------------

    // The failure this prevents: change an entity, forget the migration, and the app
    // starts fine locally against a database that already has the column. It breaks on a
    // fresh deploy, which is the worst place to find out. `dotnet ef migrations has-pending-
    // model-changes` exits non-zero when the snapshot and the model have drifted apart.
    Target MigrationCheck => _ => _
        .DependsOn(nameof(Compile))
        .Description("Fail if the EF model has drifted from the migration snapshot.")
        // SetConfiguration matters with NoBuild: `dotnet ef` otherwise looks for the
        // assembly in the default (Debug) output, and CI builds Release. Locally the
        // configuration happens to match, so the mismatch is invisible until CI.
        .Executes(() => EFCore.MigrationsHasPendingModelChanges(Ef, s => s
            .SetProject(InfrastructureProject)
            .SetStartupProject(WebProject)
            .SetConfiguration(Configuration.ToString())
            .SetNoBuild(true)));

    // Idempotent so it can be replayed against a database at any migration, which is what
    // you want when the alternative is `database update` against production.
    Target MigrationScript => _ => _
        .DependsOn(nameof(Compile))
        .Description("Emit an idempotent SQL script for every migration.")
        .Produces("artifacts/migrations/outpost2.sql")
        .Executes(() =>
        {
            MigrationsDir.CreateDirectory();
            return EFCore.MigrationsScript(Ef, s => s
                .SetProject(InfrastructureProject)
                .SetStartupProject(WebProject)
                .SetOutput(MigrationsDir / "outpost2.sql")
                .SetIdempotent(true)
                .SetConfiguration(Configuration.ToString())
                .SetNoBuild(true));
        });

    // ---- secrets -----------------------------------------------------------

    // This repo shipped a live GitHub PAT into a commit on 2026-10-28, inside a redaction
    // test's fixture — the one place in a codebase where a realistic credential looks like
    // it belongs, and where the test passes whether the string is real or not. It was found
    // by reading, which does not scale.
    //
    // Scans HISTORY, not just the working tree: by the time a secret is committed, deleting
    // it from HEAD changes nothing. Verified-only, so the gate fires on credentials that
    // actually work — a revoked token in an old commit is noise, a live one is an incident.
    Target SecretScan => _ => _
        .Description("Scan the full git history for live credentials; fail the build on a verified hit.")
        .OnlyWhen(() => TruffleHogTool is not null, "trufflehog not on PATH — secret scan skipped.")
        .Executes(() =>
        {
            SecretsDir.CreateDirectory();
            return TruffleHog.Git(TruffleHogTool!, s => s
                .SetUri(LocalRepoUri)
                .SetOnlyVerified(true)
                .SetJson(true)
                .SetFail(true)
                .SetNoUpdate(true)
                .SetResults("verified")
                .SetWorkingDirectory(RootDirectory));
        });

    // The working tree is a separate question from history: a secret written into a file
    // that is about to be committed has not reached git yet, so the history scan is blind
    // to it until the commit exists.
    //
    // Needs an exclusion list to be usable at all. Unfiltered it reports the real Entra
    // and GitHub secrets in .env — correct local state, gitignored, not going anywhere —
    // and fails every run. A gate that is always red teaches people to skip it, which is
    // worse than not having one. See build/trufflehog-exclude.txt for what is skipped
    // and why; none of it applies to the history scan.
    Target SecretScanWorkingTree => _ => _
        .Description("Scan the working tree for live credentials, excluding local config.")
        .OnlyWhen(() => TruffleHogTool is not null, "trufflehog not on PATH — secret scan skipped.")
        .Executes(() => TruffleHog.Filesystem(TruffleHogTool!, s => s
            .AddPath(RootDirectory)
            .SetExcludePathsFile(RootDirectory / "build" / "trufflehog-exclude.txt")
            .SetOnlyVerified(true)
            .SetJson(true)
            .SetFail(true)
            .SetNoUpdate(true)
            .SetWorkingDirectory(RootDirectory)));

    // ---- delivery ----------------------------------------------------------

    Target Image => _ => _
        .DependsOn(nameof(Test))
        .Description("Build the web container image through compose.")
        .OnlyWhen(() => DockerTool is not null, "docker not on PATH — image build skipped.")
        .Executes(() => Docker.Compose.BuildImages(s => s
            .AddService(Service)
            .SetProjectDirectory(RootDirectory)));

    Target Deploy => _ => _
        .DependsOn(nameof(Image))
        .Description("Bring the web service up on the built image.")
        .Executes(() => Docker.Compose.Up(s => s
            .AddService(Service)
            .SetDetach(true)
            .SetProjectDirectory(RootDirectory)));

    // A deploy that "succeeded" because the container started is not a deploy that worked.
    // 302 is the expected answer from the root: unauthenticated callers are redirected to
    // sign-in, so anything 2xx or 3xx means the pipeline is serving.
    Target Smoke => _ => _
        .DependsOn(nameof(Deploy))
        .Description("Check the deployed app actually answers.")
        .Executes(async () =>
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            var answered = await Polling.Until(
                async ct =>
                {
                    try
                    {
                        var r = await http.GetAsync(SmokeUrl, ct);
                        return (int)r.StatusCode < 400;
                    }
                    catch (HttpRequestException) { return false; }
                },
                timeout: TimeSpan.FromSeconds(60));

            if (!answered)
                throw new Exception($"{SmokeUrl} did not answer within 60s of the deploy.");

            Console.WriteLine($"  {SmokeUrl} is serving.");
        });

    // ---- aggregates --------------------------------------------------------

    // Runs on every push, so it is deliberately only the things that need no tool beyond
    // the .NET SDK. MigrationCheck is in here rather than nightly because it costs about a
    // second, needs nothing installed, and the bug it catches — model changed, migration
    // not added — is invisible until a deploy against a fresh database, by which time the
    // commit that caused it is hours of history back.
    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Compile), nameof(Test), nameof(Coverage),
                   nameof(MigrationCheck))
        .Description("Per-push gate: build, test with coverage, migration-drift check.");

    // Everything that needs a scanner binary downloaded. Minutes rather than seconds,
    // almost all of it tool installation, which is why it is nightly and not per-push.
    Target Nightly => _ => _
        .DependsOn(nameof(Ci), nameof(SecretScan), nameof(SecretScanWorkingTree),
                   nameof(Security))
        .Description("Nightly: Ci plus secret scanning and the full SBOM/SAST/SCA chain.");

    Target Default => _ => _
        .Default()
        .DependsOn(nameof(Compile));
}
