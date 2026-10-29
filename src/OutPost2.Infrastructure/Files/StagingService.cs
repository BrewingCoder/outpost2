using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Files;

public interface IStagingService
{
    Task<StagedFile> UploadAsync(
        Guid userId, string name, string fileName, string contentType, Stream content,
        CancellationToken ct = default);

    Task<IReadOnlyList<StagedFile>> ListAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Finds a staged file by what the user called it. Loose matching on purpose: speech
    /// recognition will not reproduce a name exactly, and "attach the contract" should
    /// find "Signed contract v3.pdf".
    /// </summary>
    Task<StagedFile?> FindByNameAsync(Guid userId, string name, CancellationToken ct = default);

    Task<Stream> OpenAsync(StagedFile file, CancellationToken ct = default);
    Task<Stream> OpenRangeAsync(StagedFile file, long from, long toInclusive, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid fileId, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

public sealed class StagingService(
    OutPostDbContext db,
    IObjectStore store,
    IOptions<StorageOptions> options,
    ILogger<StagingService> log) : IStagingService
{
    private readonly StorageOptions _o = options.Value;

    /// <summary>
    /// A directory this process created, under an unpredictable name and — on Unix — mode
    /// 0700. Lazy so that merely constructing the service on a host with no writable temp
    /// directory fails at the upload rather than at startup.
    /// </summary>
    private static readonly Lazy<string> TempRoot = new(() =>
    {
        var dir = Directory.CreateTempSubdirectory("outpost2-staging-");

        // Best effort, and only ever removes a directory this process made. The buffers
        // inside it are already DeleteOnClose, so this is just not leaving an empty
        // directory behind on every restart.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { dir.Delete(recursive: true); } catch { /* exiting anyway */ }
        };

        return dir.FullName;
    });

    public async Task<StagedFile> UploadAsync(
        Guid userId, string name, string fileName, string contentType, Stream content,
        CancellationToken ct = default)
    {
        var used = await db.StagedFiles
            .Where(f => f.UserId == userId && f.ExpiresAt > DateTimeOffset.UtcNow)
            .SumAsync(f => f.SizeBytes, ct);

        // Buffer to a temp file rather than memory: this path exists precisely to carry
        // files too large to hold, so holding them here would defeat it.
        //
        // One handle, opened CreateNew under TempRoot, used for both the write and the
        // read back (OP2-145). This replaced Path.GetTempFileName, which has two problems
        // that matter on a path carrying other people's attachments: it draws names from
        // a 16-bit space and is documented to throw once ~65,000 of them exist, which in
        // a long-lived container is a denial of service somebody else can cause; and it
        // creates the file and then hands back a path for the caller to open, so on a
        // host with a shared temp directory there is a window in between. Never reopening
        // by name closes the second one outright — the upload reads the same handle it
        // wrote, so there is nothing to swap underneath it.
        //
        // DeleteOnClose, rather than a finally: the old code deleted on every exit path
        // and that was correct, but the kernel enforcing it beats us remembering to.
        var temp = Path.Combine(TempRoot.Value, Path.GetRandomFileName());

        await using var fs = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        });

        long size;
        string hash;

        using (var sha = SHA256.Create())
        {
            await using var tee = new CryptoStream(fs, sha, CryptoStreamMode.Write, leaveOpen: true);
            await content.CopyToAsync(tee, ct);
            await tee.FlushFinalBlockAsync(ct);

            size = fs.Length;
            hash = Convert.ToHexString(sha.Hash!);
        }

        if (used + size > _o.PerUserQuotaBytes)
            throw new InvalidOperationException(
                $"That would exceed your {_o.PerUserQuotaBytes / (1024 * 1024 * 1024)} GB staging quota. " +
                "Remove something first.");

        // Random key, not derived from the id or filename: a leaked key must not let
        // anyone enumerate or guess at other objects.
        var key = $"{userId:N}/{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";

        fs.Position = 0;
        await store.PutAsync(key, fs, contentType, ct);

        var staged = new StagedFile
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name.Trim(),
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = size,
            StorageKey = key,
            Sha256 = hash,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_o.StagingLifetime),
        };

        db.StagedFiles.Add(staged);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Staged {Name} ({Size} bytes) for user {User}.", staged.Name, size, userId);
        return staged;
    }

    public async Task<IReadOnlyList<StagedFile>> ListAsync(Guid userId, CancellationToken ct = default) =>
        await db.StagedFiles
            .Where(f => f.UserId == userId && f.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(f => f.UploadedAt)
            .ToListAsync(ct);

    public async Task<StagedFile?> FindByNameAsync(Guid userId, string name, CancellationToken ct = default)
    {
        var candidates = await ListAsync(userId, ct);
        if (candidates.Count == 0) return null;

        var needle = name.Trim();

        // Exact, then prefix, then substring, then filename. Degrading deliberately:
        // a voice transcript rarely matches a stored name character for character.
        return candidates.FirstOrDefault(f => f.Name.Equals(needle, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public Task<Stream> OpenAsync(StagedFile file, CancellationToken ct = default) =>
        store.GetAsync(file.StorageKey, ct);

    public Task<Stream> OpenRangeAsync(StagedFile file, long from, long toInclusive, CancellationToken ct = default) =>
        store.GetRangeAsync(file.StorageKey, from, toInclusive, ct);

    public async Task DeleteAsync(Guid userId, Guid fileId, CancellationToken ct = default)
    {
        // Scoped by userId: a known id must not be enough to delete someone else's file.
        var f = await db.StagedFiles.FirstOrDefaultAsync(x => x.UserId == userId && x.Id == fileId, ct);
        if (f is null) return;

        await store.DeleteAsync(f.StorageKey, ct);
        db.StagedFiles.Remove(f);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var dead = await db.StagedFiles.Where(f => f.ExpiresAt < DateTimeOffset.UtcNow).ToListAsync(ct);

        foreach (var f in dead)
        {
            // Remove the object first. A row without its object is a confusing ghost;
            // an object without its row is merely reclaimable garbage.
            try { await store.DeleteAsync(f.StorageKey, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Could not delete object {Key} during purge.", f.StorageKey); }
        }

        db.StagedFiles.RemoveRange(dead);
        await db.SaveChangesAsync(ct);
        return dead.Count;
    }
}
