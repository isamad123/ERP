using System.Net;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Documents;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Settings;

namespace Erp.IntegrationTests.Settings;

/// <summary>
/// Login branding (slice S1.15a, FR-ADM-SEC-08, ruling Q3): the public answer holds only the product name, the logo address, the banner
/// and the terms; changes need the settings permissions; the logo is served only when it is a clean picture of the branding owner.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class BrandingTests(TenancyFixture fx) : IAsyncLifetime
{
    private DocumentRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A 1 by 1 pixel PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public async ValueTask InitializeAsync() => _rig = await DocumentRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    private async Task<(HttpStatusCode Status, string Body, System.Net.Http.Headers.HttpResponseHeaders Headers)> PublicAsync(string path, string? code = null)
    {
        using var client = TenancyFixture.Client(_rig.App, TenancyFixture.HostOf(code ?? _rig.Code), null);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Headers);
    }

    private async Task<Guid> AdminAsync() => await _rig.UserAsync(Ct, "tenant_admin");

    private async Task<FileAnswer> SetAsync(Guid user, string key, string value) =>
        await _rig.PutJsonAsync(user, "/api/cfg/settings/" + key, JsonSerializer.Serialize(new { value, reason = "branding test" }), Ct);

    [Fact]
    public async Task FR_ADM_SEC_08_the_public_branding_has_standard_values_and_only_the_five_fields()
    {
        var (status, body, headers) = await PublicAsync("/api/kernel/branding");
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(["banner", "logoUrl", "productName", "terms"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("ERP", root.GetProperty("productName").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("logoUrl").ValueKind);
        Assert.Equal("", root.GetProperty("banner").GetProperty("en").GetString());
        Assert.Contains("max-age=60", headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("public", headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await PublicAsync("/api/kernel/branding/logo")).Status);
    }

    [Fact]
    public async Task FR_ADM_SEC_08_an_administrator_changes_name_banner_and_terms_in_both_languages_and_the_sign_in_page_shows_them()
    {
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.ProductName, "Acme People")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.BannerEn, "Welcome to Acme")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.BannerUr, "ایکمی میں خوش آمدید")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.TermsEn, "Use is monitored.")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.TermsUr, "استعمال کی نگرانی کی جاتی ہے۔")).Status);
        var (_, body, _) = await PublicAsync("/api/kernel/branding");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Acme People", root.GetProperty("productName").GetString());
        Assert.Equal("Welcome to Acme", root.GetProperty("banner").GetProperty("en").GetString());
        Assert.Equal("ایکمی میں خوش آمدید", root.GetProperty("banner").GetProperty("ur").GetString());
        Assert.Equal("Use is monitored.", root.GetProperty("terms").GetProperty("en").GetString());
        Assert.Equal("استعمال کی نگرانی کی جاتی ہے۔", root.GetProperty("terms").GetProperty("ur").GetString());

        // Over the length limit is refused with the settings error, and nothing changes.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(admin, BrandingKeys.BannerEn, new string('x', 301))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(admin, BrandingKeys.LogoFileId, "not-a-guid")).Status);
    }

    [Fact]
    public async Task FR_ADM_SEC_08_a_person_without_the_settings_permission_cannot_change_branding_or_upload_the_logo()
    {
        var user = await _rig.UserAsync(Ct, "business_user");
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(user, BrandingKeys.ProductName, "Mine")).Status);
        var upload = await _rig.UploadAsync(user, BrandingKeys.OwnerId, "logo.png", Png, Ct, BrandingKeys.OwnerType);
        Assert.True(upload.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, upload.Text);
        Assert.Equal("ERP", JsonDocument.Parse((await PublicAsync("/api/kernel/branding")).Body).RootElement.GetProperty("productName").GetString());
    }

    [Fact]
    public async Task FR_ADM_SEC_08_the_uploaded_logo_is_served_to_everyone_as_a_picture_and_only_that_logo()
    {
        var admin = await AdminAsync();
        var upload = await _rig.UploadAsync(admin, BrandingKeys.OwnerId, "logo.png", Png, Ct, BrandingKeys.OwnerType);
        Assert.Equal(HttpStatusCode.Created, upload.Status);
        var fileId = upload.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.LogoFileId, fileId.ToString())).Status);

        var (_, body, _) = await PublicAsync("/api/kernel/branding");
        var logoUrl = JsonDocument.Parse(body).RootElement.GetProperty("logoUrl").GetString();
        Assert.StartsWith("/api/kernel/branding/logo?v=", logoUrl, StringComparison.Ordinal);
        using var client = TenancyFixture.Client(_rig.App, TenancyFixture.HostOf(_rig.Code), null);
        using var response = await client.GetAsync(new Uri(logoUrl!, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("nosniff", string.Join(',', response.Headers.GetValues("X-Content-Type-Options")), StringComparison.Ordinal);

        // The setting cannot be used to read another file: a PDF of another record answers no logo at all.
        var record = _rig.Records.Add(_rig.Company, [admin], [admin]);
        var other = await _rig.UploadAsync(admin, record, "contract.pdf", DocumentRig.PdfBytes(), Ct);
        Assert.Equal(HttpStatusCode.Created, other.Status);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.LogoFileId, other.Json.GetProperty("id").GetGuid().ToString())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PublicAsync("/api/kernel/branding/logo")).Status);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse((await PublicAsync("/api/kernel/branding")).Body).RootElement.GetProperty("logoUrl").ValueKind);

        // A text file of the branding owner is not a picture either.
        var text = await _rig.UploadAsync(admin, BrandingKeys.OwnerId, "notes.txt", Encoding.UTF8.GetBytes("not a picture"), Ct, BrandingKeys.OwnerType);
        Assert.Equal(HttpStatusCode.Created, text.Status);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, BrandingKeys.LogoFileId, text.Json.GetProperty("id").GetGuid().ToString())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PublicAsync("/api/kernel/branding/logo")).Status);
    }

    [Fact]
    public async Task FR_ADM_SEC_08_one_customers_logo_and_texts_are_never_served_on_another_customers_host()
    {
        var admin = await AdminAsync();
        var upload = await _rig.UploadAsync(admin, BrandingKeys.OwnerId, "logo.png", Png, Ct, BrandingKeys.OwnerType);
        await SetAsync(admin, BrandingKeys.LogoFileId, upload.Json.GetProperty("id").GetGuid().ToString());
        await SetAsync(admin, BrandingKeys.ProductName, "Customer One Only");
        var other = "dd" + fx.Suffix;
        await fx.ProvisionAsync(other, Ct);
        var (status, body, _) = await PublicAsync("/api/kernel/branding", other);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Customer One Only", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await PublicAsync("/api/kernel/branding/logo", other)).Status);
    }
}
