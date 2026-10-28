using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OutPost2.Infrastructure.Data;

/// <summary>
/// Builds an <see cref="OutPostDbContext"/> for <c>dotnet ef</c> without starting the web
/// application.
/// <para>
/// Without this, EF's design-time tooling falls back to building the Web host to find a
/// registered context — which means every piece of startup validation runs before you can
/// so much as list migrations. That coupling bit immediately: a deliberate fail-fast on an
/// unset <c>PublicBaseUrl</c> (correct for a running server, meaningless for generating
/// SQL) stopped <c>dotnet ef</c> dead with an error about OAuth metadata.
/// </para>
/// <para>
/// Design time never needs to reach the database for <c>migrations add</c>,
/// <c>migrations script</c> or <c>has-pending-model-changes</c> — those compare the model
/// against the snapshot. The connection string only has to be well-formed, so it reads the
/// real one when present and otherwise uses the local dev database.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OutPostDbContext>
{
    private const string LocalDev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    public OutPostDbContext CreateDbContext(string[] args)
    {
        var connection =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? Environment.GetEnvironmentVariable("OUTPOST2_POSTGRES")
            ?? LocalDev;

        var options = new DbContextOptionsBuilder<OutPostDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new OutPostDbContext(options);
    }
}
