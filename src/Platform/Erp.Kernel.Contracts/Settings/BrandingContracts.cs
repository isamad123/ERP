namespace Erp.Kernel.Contracts.Settings;

/// <summary>
/// What the public sign-in page may know about the customer (slice S1.15a, FR-ADM-SEC-08, ruling Q3): the product name, the
/// logo address, the login banner and the terms notice in both languages. Nothing else is ever added here.
/// </summary>
public sealed record BrandingInfo(string ProductName, string? LogoUrl, BrandingTexts Banner, BrandingTexts Terms);

/// <summary>A text in English and Urdu; an empty Urdu text means "show the English one".</summary>
public sealed record BrandingTexts(string En, string Ur);

/// <summary>The logo file as the sign-in page receives it.</summary>
public sealed record BrandingLogo(string ContentType, byte[] Content);

/// <summary>The public branding service (contract "Branding API"): answers from the customer the host name names.</summary>
public interface IBrandingService
{
    Task<BrandingInfo> GetAsync(CancellationToken cancellationToken);

    /// <summary>The logo, or null when none is set or the file is not a clean picture of the branding owner.</summary>
    Task<BrandingLogo?> LogoAsync(CancellationToken cancellationToken);
}

/// <summary>Where the logo file lives: attachments of the owner type <c>cfg.branding</c> with one fixed owner id.</summary>
public static class BrandingKeys
{
    public const string OwnerType = "cfg.branding";
    public static readonly Guid OwnerId = new("00000000-0000-7000-8000-00000000b4a1");

    public const string ProductName = "cfg.branding.product_name";
    public const string LogoFileId = "cfg.branding.logo_file_id";
    public const string BannerEn = "cfg.branding.banner_en";
    public const string BannerUr = "cfg.branding.banner_ur";
    public const string TermsEn = "cfg.branding.terms_en";
    public const string TermsUr = "cfg.branding.terms_ur";
}
