using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Itg;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// The administrator's user list as an exportable list (slice S1.16b, FR-ADM-USR-09, S1.13 bulk export): the same search and filters
/// (status, role, company, second factor) and the same rows as the user console, read page by page. Passwords and secrets are not in the list.
/// </summary>
public sealed class UserExportReader(IServiceProvider services) : IExportRowReader
{
    public const string Source = "iam.user_overview";

    private static readonly ExportColumn[] Columns =
    [
        new("username", "username", null, false), new("display_name", "displayName", null, false), new("email", "email", null, false), new("status", "status", null, false),
        new("last_login_at", "lastLoginAt", null, false), new("default_company_id", "defaultCompanyId", null, false), new("access_valid_to", "accessValidTo", null, false),
        new("has_mfa", "hasMfa", null, false), new("roles", "roles", null, false), new("company_ids", "companyIds", null, false), new("all_companies", "allCompanies", null, false),
    ];

    public string Code => Source;

    public string ReadPermission => "iam.users.read";

    public Task<IReadOnlyList<(ExportColumn Column, string Access)>> ColumnsAsync(Guid? companyId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(ExportColumn, string)>>([.. Columns.Select(c => (c, "visible"))]);

    public async Task<ExportPage> ReadPageAsync(ExportQuery query, string? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var f = query.Filter;
        string? Text(string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var g) ? g : null;
        bool? Flag(string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
        var console = services.GetRequiredService<IUserConsoleService>();
        var page = await console.OverviewAsync(new UserOverviewQuery(query.Search, Text("status"), Id("roleId"), Id("companyId"), Flag("mfa"), after, Math.Clamp(limit, 1, 200)), cancellationToken).ConfigureAwait(false);
        var rows = page.Items.Select(u => new JsonObject
        {
            ["username"] = u.Username,
            ["displayName"] = u.DisplayName,
            ["email"] = u.Email,
            ["status"] = u.Status,
            ["lastLoginAt"] = u.LastLoginAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["defaultCompanyId"] = u.DefaultCompanyId?.ToString(),
            ["accessValidTo"] = u.AccessValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["hasMfa"] = u.HasMfa,
            ["roles"] = string.Join("; ", u.Roles.Select(r => r.Code)),
            ["companyIds"] = string.Join("; ", u.CompanyIds),
            ["allCompanies"] = u.AllCompanies,
        }).ToList<JsonObject>();
        return new ExportPage(rows, page.Next, []);
    }
}
