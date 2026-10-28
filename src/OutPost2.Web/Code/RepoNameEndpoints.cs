using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Code;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Data;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Code;

public static class RepoNameEndpoints
{
    public static void MapRepoNames(this WebApplication app)
    {
        // [FromForm], not the query string. The same binding mistake has now been made
        // twice in this codebase; both times the value arrived null and the handler
        // behaved as though the field had been left blank.
        app.MapPost("/repo-names/add", async (
            HttpContext ctx, OutPostDbContext db,
            [FromForm] Guid connectionId, [FromForm] string alias, [FromForm] string fullName) =>
        {
            var userId = ctx.User.UserId();

            alias = (alias ?? "").Trim();
            fullName = (fullName ?? "").Trim().Trim('/');

            if (alias.Length == 0 || fullName.Length == 0)
                return Results.Redirect("/repo-names?error=" + Uri.EscapeDataString("Both fields are required."));

            // owner/name, and nothing else. A bare name here would make the alias resolve
            // to something that itself needs resolving, which is a loop with no answer.
            if (fullName.Count(ch => ch == '/') != 1 || fullName.StartsWith('/') || fullName.EndsWith('/'))
                return Results.Redirect("/repo-names?error=" +
                    Uri.EscapeDataString("The repository must be owner/name, e.g. BrewingCoder/RosettaCore."));

            // Scoped by user in the WHERE clause, so a guessed connection id belonging to
            // someone else finds nothing rather than attaching an alias to their account.
            var owns = await db.Connections.AnyAsync(c =>
                c.Id == connectionId && c.UserId == userId && c.Provider == ProviderKind.GitHub);

            if (!owns) return Results.Redirect("/repo-names?error=" + Uri.EscapeDataString("Unknown connection."));

            var key = RepoAlias.Normalise(alias);
            var existing = await db.RepoAliases
                .Where(a => a.ConnectionId == connectionId)
                .ToListAsync();

            // Matched on the normalised form, because that is what resolution matches on.
            // Letting "the-core" and "The Core" both exist would make the winner depend on
            // row order, and the unique index on the raw text would not have stopped it.
            if (existing.Any(a => RepoAlias.Normalise(a.Alias) == key))
                return Results.Redirect("/repo-names?error=" +
                    Uri.EscapeDataString($"'{alias}' is already in use on that connection."));

            db.RepoAliases.Add(new RepoAlias
            {
                UserId = userId,
                ConnectionId = connectionId,
                Alias = alias,
                FullName = fullName,
            });

            await db.SaveChangesAsync();

            return Results.Redirect("/repo-names?added=" + Uri.EscapeDataString(alias));
        });

        app.MapPost("/repo-names/{id:guid}/delete", async (
            HttpContext ctx, OutPostDbContext db, Guid id) =>
        {
            var userId = ctx.User.UserId();

            await db.RepoAliases
                .Where(a => a.Id == id && a.UserId == userId)
                .ExecuteDeleteAsync();

            return Results.Redirect("/repo-names");
        });
    }
}
