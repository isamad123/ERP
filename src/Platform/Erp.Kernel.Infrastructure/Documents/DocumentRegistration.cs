using Erp.Kernel.Application.Documents;
using Erp.Kernel.Infrastructure.Documents.Templates;
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
        services.AddScoped<IAttachmentOwner, CompanyAttachmentOwner>(); // S1.14a: the company logo
        return services;
    }

    /// <summary>
    /// Both hosts' file settings from configuration (runbook <c>docs/runbooks/file-store.md</c>): <c>Files:Store</c>, <c>Files:Root</c>,
    /// <c>Files:S3:*</c> and <c>Files:Scan:*</c>. One reader for both hosts, so the API and the worker always agree (S1.11c1 review finding 1).
    /// </summary>
    public static IServiceCollection AddKernelDocuments(this IServiceCollection services, Func<string, string?> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var (store, scan, s3) = HostSettings(setting);
        services.AddKernelDocuments(store, scan, s3);
        return services.AddKernelDocumentTemplates(PdfRendererOptions.From(setting));
    }

    /// <summary>Templates and PDF (slice S1.11c2): the template service and the Chromium renderer (<c>Pdf:*</c> settings, runbook <c>docs/runbooks/pdf-rendering.md</c>). Both hosts need Chromium (ruling Q27).</summary>
    public static IServiceCollection AddKernelDocumentTemplates(this IServiceCollection services, PdfRendererOptions pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        services.AddSingleton(pdf);
        services.AddSingleton<IDocumentRenderer, ChromiumPdfRenderer>();
        services.AddScoped<ITemplateService, TemplateService>();
        return services;
    }

    /// <summary>The store, scanner and S3 options as the hosts read them; S3 options are null when no bucket is set.</summary>
    public static (FileStoreOptions Store, VirusScanOptions Scan, S3StoreOptions? S3) HostSettings(Func<string, string?> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        static string? Value(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var port = int.TryParse(setting("Files:Scan:Port"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 3310;
        var bucket = Value(setting("Files:S3:Bucket"));
        var s3 = bucket is null ? null : new S3StoreOptions(bucket, Value(setting("Files:S3:ServiceUrl")), Value(setting("Files:S3:Region")), Value(setting("Files:S3:AccessKeyId")), Value(setting("Files:S3:SecretKeyRef")));
        return (new FileStoreOptions(Value(setting("Files:Store")) ?? "local", Value(setting("Files:Root"))), new VirusScanOptions(Value(setting("Files:Scan:Host")), port), s3);
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
