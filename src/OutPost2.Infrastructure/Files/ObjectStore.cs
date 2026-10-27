using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OutPost2.Infrastructure.Files;

public sealed class StorageOptions
{
    public const string SectionName = "OutPost2:Storage";

    /// <summary>Garage in dev and in the cluster; any S3 API works, so R2 stays a swap.</summary>
    public string ServiceUrl { get; set; } = "http://garage:3900";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "garage";
    public string StagingBucket { get; set; } = "op2-staging";

    /// <summary>Required for anything that is not real AWS.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>How long an uploaded file survives if unused.</summary>
    public TimeSpan StagingLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Per-user ceiling, so one person cannot fill the lab's disk.</summary>
    public long PerUserQuotaBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

public interface IObjectStore
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Reads a byte range. This is the capability the whole attachment pipeline rests on
    /// (OP2-10): a 150 MB attachment is streamed to Graph in 320 KiB-multiple chunks and
    /// must never be held whole in memory.
    /// </summary>
    Task<Stream> GetRangeAsync(string key, long from, long toInclusive, CancellationToken ct = default);

    Task<Stream> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _o;
    private readonly ILogger<S3ObjectStore> _log;

    public S3ObjectStore(IOptions<StorageOptions> options, ILogger<S3ObjectStore> log)
    {
        _o = options.Value;
        _log = log;

        _s3 = new AmazonS3Client(_o.AccessKey, _o.SecretKey, new AmazonS3Config
        {
            ServiceURL = _o.ServiceUrl,
            // Path style, not virtual-host style: a self-hosted store has no wildcard DNS,
            // so bucket.host would simply not resolve.
            ForcePathStyle = _o.ForcePathStyle,
            AuthenticationRegion = _o.Region,

            // AWS SDK v4 turns on request checksums and response validation by default.
            // Garage (and most S3-compatible servers) reject the resulting requests with
            // "Invalid payload signature", because the extra trailer is not part of the
            // signature they compute. WHEN_REQUIRED restores interoperability without
            // weakening anything we depend on - the content hash is still signed.
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _o.StagingBucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,

            // aws-chunked encoding is an AWS extension; Garage wants the plain body.
            UseChunkEncoding = false,
        }, ct);

        _log.LogInformation("Stored object {Key}.", key);
    }

    public async Task<Stream> GetRangeAsync(string key, long from, long toInclusive, CancellationToken ct = default)
    {
        var resp = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _o.StagingBucket,
            Key = key,
            ByteRange = new ByteRange(from, toInclusive),
        }, ct);

        return resp.ResponseStream;
    }

    public async Task<Stream> GetAsync(string key, CancellationToken ct = default)
    {
        var resp = await _s3.GetObjectAsync(_o.StagingBucket, key, ct);
        return resp.ResponseStream;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        _s3.DeleteObjectAsync(_o.StagingBucket, key, ct);

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_o.StagingBucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public void Dispose() => _s3.Dispose();
}
