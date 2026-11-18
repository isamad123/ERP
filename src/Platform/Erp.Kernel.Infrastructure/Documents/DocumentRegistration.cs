using Erp.Kernel.Application.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>Service registration of the file mechanism (slice S1.11c1). Hosts pass the operator's store and scanner settings.</summary>
public static class DocumentRegistration
{
    /// <summary>The file store (<c>Files:Store</c>), the virus scanner (<c>Files:Scan:*</c>), encryption, scanning and the attachment service; both hosts call it.</summary>
    public static IServiceCollection AddKernelDocuments(this IServiceCollection services, FileStoreOptions store, VirusScanOptions? scan = null, S3StoreOptions? s3 = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        services.AddSingleton(store);
        services.AddSingleton<IFileStore>(sp => store switch
        {
            { Store: "local", Root: { Length: > 0 } root } => new LocalFolderFileStore(root),
            { Store: "s3" } when s3 is { Bucket.Length: > 0 } => CreateS3(s3, sp.GetRequiredService<BuildingBlocks.Application.Tenancy.ISecretStore>()),
            _ => throw new InvalidOperationException("Files:Store must be 'local' with Files:Root set, or 's3' with Files:S3:Bucket set (docs/runbooks/file-store.md)."),
        });
        services.AddSingleton(scan ?? new VirusScanOptions(null, 3310));
        services.AddSingleton<IVirusScanner, ClamAvScanner>();
        services.AddScoped<FileEncryption>();
        services.AddScoped<FileScanning>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IAttachmentOwner, WorkflowTaskOwner>();
        return services;
    }

    private static S3FileStore CreateS3(S3StoreOptions o, BuildingBlocks.Application.Tenancy.ISecretStore secrets)
    {
        var config = new Amazon.S3.AmazonS3Config { ForcePathStyle = o.ServiceUrl is not null };
        if (o.ServiceUrl is not null)
        {
            config.ServiceURL = o.ServiceUrl;
        }

        if (o.Region is not null)
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);
        }

        var secret = o.SecretKeyRef is null ? null : secrets.GetAsync(o.SecretKeyRef, CancellationToken.None).GetAwaiter().GetResult();
        Amazon.S3.IAmazonS3 client = o.AccessKeyId is not null && secret is not null ? new Amazon.S3.AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials(o.AccessKeyId, secret), config) : new Amazon.S3.AmazonS3Client(config);
        return new S3FileStore(client, o.Bucket);
    }
}
