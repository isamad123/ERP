using Erp.Kernel.Application.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>Service registration of the file mechanism (slice S1.11c1). Hosts pass the operator's store and scanner settings.</summary>
public static class DocumentRegistration
{
    /// <summary>The file store (<c>Files:Store</c>), the virus scanner (<c>Files:Scan:*</c>), encryption, scanning and the attachment service; both hosts call it.</summary>
    public static IServiceCollection AddKernelDocuments(this IServiceCollection services, FileStoreOptions store, VirusScanOptions? scan = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        services.AddSingleton(store);
        services.AddSingleton<IFileStore>(sp => sp.GetRequiredService<FileStoreOptions>() is { Store: "local", Root: { Length: > 0 } root }
            ? new LocalFolderFileStore(root)
            : throw new InvalidOperationException("Files:Store must be 'local' with Files:Root set (or 's3', see docs/runbooks/file-store.md)."));
        services.AddSingleton(scan ?? new VirusScanOptions(null, 3310));
        services.AddSingleton<IVirusScanner, ClamAvScanner>();
        services.AddScoped<FileEncryption>();
        services.AddScoped<FileScanning>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IAttachmentOwner, WorkflowTaskOwner>();
        return services;
    }
}
