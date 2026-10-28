using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Users;

public interface IUserStore
{
    /// <summary>
    /// Finds the user for a GitHub numeric id, creating the row on first sign-in.
    /// The caller must already have checked the allowlist — this does not authorise.
    /// </summary>
    Task<User> UpsertOnSignInAsync(long gitHubUserId, string login, string? avatarUrl, CancellationToken ct = default);

    Task<User?> FindByGitHubIdAsync(long gitHubUserId, CancellationToken ct = default);

    /// <summary>Looks up a user by our own id — what a request's principal actually carries.</summary>
    Task<User?> FindAsync(Guid id, CancellationToken ct = default);
}

public sealed class UserStore(OutPostDbContext db) : IUserStore
{
    public async Task<User> UpsertOnSignInAsync(
        long gitHubUserId, string login, string? avatarUrl, CancellationToken ct = default)
    {
        // Matched on the numeric id only. The login is display data that we refresh — if
        // someone renames themselves on GitHub we follow the rename rather than creating a
        // second row, and we never treat the new name as a different person.
        var user = await db.Users.FirstOrDefaultAsync(u => u.GitHubUserId == gitHubUserId, ct);

        if (user is null)
        {
            user = new User { GitHubUserId = gitHubUserId, GitHubLogin = login, AvatarUrl = avatarUrl };
            db.Users.Add(user);
        }
        else
        {
            user.GitHubLogin = login;
            user.AvatarUrl = avatarUrl;
        }

        user.LastSignInAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public Task<User?> FindByGitHubIdAsync(long gitHubUserId, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.GitHubUserId == gitHubUserId, ct);

    public Task<User?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
}
