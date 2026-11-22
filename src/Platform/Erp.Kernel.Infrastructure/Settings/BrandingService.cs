using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.Documents;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>
/// Login branding (slice S1.15a, FR-ADM-SEC-08, ruling Q3). Reads the six <c>cfg.branding.*</c> settings of the customer the host
/// name names and serves the logo file without a login. Only a clean PNG or JPEG that belongs to the branding owner is ever
/// served, whatever id the setting holds, so the setting cannot be used to read another file.
/// </summary>
public sealed class BrandingService(ISettings settings, ITenantConnectionProvider tenant, IFileStore store, FileEncryption encryption) : IBrandingService
{
    private static readonly string[] Pictures = ["image/png", "image/jpeg"];

    public async Task<BrandingInfo> GetAsync(CancellationToken cancellationToken)
    {
        var name = (await TextAsync(BrandingKeys.ProductName, cancellationToken).ConfigureAwait(false)).Trim();
        var logo = await LogoRowAsync(cancellationToken).ConfigureAwait(false);
        return new BrandingInfo(
            name.Length == 0 ? "ERP" : name,
            logo is null ? null : "/api/kernel/branding/logo?v=" + logo.Id.ToString("N"),
            new BrandingTexts(await TextAsync(BrandingKeys.BannerEn, cancellationToken).ConfigureAwait(false), await TextAsync(BrandingKeys.BannerUr, cancellationToken).ConfigureAwait(false)),
            new BrandingTexts(await TextAsync(BrandingKeys.TermsEn, cancellationToken).ConfigureAwait(false), await TextAsync(BrandingKeys.TermsUr, cancellationToken).ConfigureAwait(false)));
    }

    public async Task<BrandingLogo?> LogoAsync(CancellationToken cancellationToken)
    {
        if (await LogoRowAsync(cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return null;
        }

        var stored = await store.GetAsync(row.StorageKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var content = await encryption.DecryptAsync(row.Id, stored, cancellationToken).ConfigureAwait(false);
        return FileValidation.Checksum(content) == row.Checksum ? new BrandingLogo(row.ContentType, content) : null;
    }

    private async Task<string> TextAsync(string key, CancellationToken ct)
    {
        var value = await settings.GetAsync(key, SettingContext.Customer, ct).ConfigureAwait(false);
        return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? string.Empty : string.Empty;
    }

    private async Task<FileRow?> LogoRowAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(await TextAsync(BrandingKeys.LogoFileId, ct).ConfigureAwait(false), out var id))
        {
            return null;
        }

        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var row = await FileRows.FindAsync(c, null, id, ct).ConfigureAwait(false);
        return row is { Status: FileStatuses.Clean, OwnerType: BrandingKeys.OwnerType } && row.OwnerId == BrandingKeys.OwnerId
            && row.StorageKey != FileStatuses.PurgedKey && Pictures.Contains(row.ContentType, StringComparer.Ordinal)
            ? row
            : null;
    }
}
