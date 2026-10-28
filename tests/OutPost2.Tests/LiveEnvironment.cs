using Microsoft.EntityFrameworkCore;
using Npgsql;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Tests;

/// <summary>
/// Environmental guards shared by the <c>Live</c> probes.
/// <para>
/// Every Live probe is supposed to skip rather than fail when the thing it needs is not
/// there, and they all guarded Vault and credentials. None guarded the case that actually
/// bit: a Postgres that is <em>reachable but unmigrated</em>.
/// </para>
/// <para>
/// That combination does not occur on a developer machine — the dev database has been
/// migrated for months — so it went unnoticed until CI, where a fresh container answers
/// on the port with an empty schema. The probe sailed past its Vault check and died on
/// <c>42P01: relation "connections" does not exist</c>, which reads like a product bug
/// and is really "this environment has no database to speak of".
/// </para>
/// </summary>
internal static class LiveEnvironment
{
    /// <summary>
    /// Skips the calling test unless the connections table exists.
    /// <para>
    /// Deliberately narrow: only <c>42P01 undefined_table</c> becomes a skip. Any other
    /// Postgres error is a real failure and is left to propagate — a guard that swallows
    /// everything turns a broken build green, which is worse than the failure it hides.
    /// </para>
    /// </summary>
    public static async Task RequireSchemaAsync(OutPostDbContext db)
    {
        try
        {
            await db.Connections.AnyAsync();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            Skip.If(true,
                "Postgres is reachable but has no OutPost2 schema — apply migrations, or " +
                "point the probe at the dev database.");
        }
    }
}
