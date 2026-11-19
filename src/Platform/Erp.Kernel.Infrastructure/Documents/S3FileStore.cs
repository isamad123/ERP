using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Host settings of the S3-compatible store (<c>Files:S3:*</c>): bucket, optional <c>ServiceUrl</c> (a compatible store such as
/// MinIO or a company's own object store), region, the access key id and the secret-store reference of the secret key.
/// </summary>
public sealed record S3StoreOptions(string Bucket, string? ServiceUrl, string? Region, string? AccessKeyId, string? SecretKeyRef);

/// <summary>
/// The S3-compatible file store (slice S1.11c1; KRN-DOC-02, ruling Q20), for the SaaS deployment. Contents arrive encrypted with
/// the customer's file key, so the bucket never holds readable content; keys carry the customer's prefix.
/// </summary>
public sealed class S3FileStore(IAmazonS3 client, string bucket) : IFileStore
{
    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream(content, writable: false);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream, AutoCloseStream = false }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => client.DeleteObjectAsync(bucket, key, cancellationToken);

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
