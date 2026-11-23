using System.Globalization;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// Import of users (slice S1.16a, FR-ADM-USR-02, S1.13 ruling Q3): the person is created through the same rules as the user form
/// (the clash rule, the unique e-mail and username, field security) and starts invited; the invitation e-mail is sent after the
/// chunk is saved (import type <c>postCommit: invite</c>), never a password.
/// </summary>
public sealed class UserImportWriter(IServiceProvider services) : IImportRowWriter
{
    public const string DefaultLanguage = "en";
    public const string DefaultTimeZone = "Asia/Karachi";

    public string Code => "users";

    public Task<ImportRowOutcome> WriteAsync(ImportRow row, Guid? existingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var values = (JsonObject)row.Values.DeepClone();
        values["language"] ??= DefaultLanguage;
        values["timeZone"] ??= DefaultTimeZone;
        return ImportWriters.Generated(services, "iam.users").WriteAsync(new ImportRow(row.RowNumber, values, row.DryRun), existingId, cancellationToken);
    }
}

/// <summary>
/// Import of organization units (slice S1.16a, FR-ADM-ORG-03): the unit through the generated rules of <c>org.org_units</c>, then its
/// first place in a hierarchy (<c>parent</c> by code, empty for the top) as a new dated link. The database refuses a cycle and a parent of
/// another company. Moving an existing unit to another parent is the move screen's job (it asks for a date and a reason), so a row
/// that would do it is refused.
/// </summary>
public sealed class OrgUnitImportWriter(IServiceProvider services, KernelDbContext db, IClock clock) : IImportRowWriter
{
    public string Code => "org_units";

    public async Task<ImportRowOutcome> WriteAsync(ImportRow row, Guid? existingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parentCode = row.Values["parent"]?.GetValue<string>();
        Guid? hierarchy = row.Values["hierarchy"]?.GetValue<string>() is { } h ? Guid.Parse(h) : null;
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var from = row.Values["validFrom"]?.GetValue<string>() is { } v ? DateOnly.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture) : today;
        if (hierarchy is null && !string.IsNullOrWhiteSpace(parentCode))
        {
            return ImportRowOutcome.Failed(new ImportRowError("hierarchy", "errors.import.required"));
        }

        var company = Guid.Parse(row.Values["companyId"]!.GetValue<string>());
        Guid? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentCode))
        {
            parentId = await ScalarAsync("select id from org.org_units where company_id = $1 and lower(code) = lower($2) and not is_deleted", [company, parentCode.Trim()], cancellationToken).ConfigureAwait(false);
            if (parentId is null)
            {
                return ImportRowOutcome.Failed(new ImportRowError("parent", "errors.import.parent_unknown"));
            }
        }

        var unit = await ImportWriters.Generated(services, "org.org_units").WriteAsync(row, existingId, cancellationToken).ConfigureAwait(false);
        if (!unit.Ok || hierarchy is null)
        {
            return unit;
        }

        var unitId = unit.Records[0].Id;
        if (existingId is not null)
        {
            var current = await ScalarAsync("select coalesce(parent_unit_id, '00000000-0000-0000-0000-000000000000') from org.org_unit_links where hierarchy_id = $1 and unit_id = $2 and valid_from <= $3 and (valid_to is null or valid_to > $3)",
                [hierarchy.Value, unitId, today], cancellationToken).ConfigureAwait(false);
            var wanted = parentId ?? Guid.Empty;
            return current is null || current.Value == wanted
                ? unit
                : ImportRowOutcome.Failed(new ImportRowError("parent", "errors.import.parent_change_not_supported"));
        }

        var link = new JsonObject { ["hierarchyId"] = hierarchy.Value.ToString(), ["unitId"] = unitId.ToString(), ["validFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (parentId is { } p)
        {
            link["parentUnitId"] = p.ToString();
        }

        var placed = await ImportWriters.Generated(services, "org.org_unit_links").WriteAsync(new ImportRow(row.RowNumber, link, row.DryRun), null, cancellationToken).ConfigureAwait(false);
        return placed.Ok ? new ImportRowOutcome([], "created", [.. unit.Records, .. placed.Records]) : placed;
    }

    private async Task<Guid?> ScalarAsync(string sql, object[] parameters, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        foreach (var p in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = p });
        }

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid g ? g : null;
    }
}

internal static class ImportWriters
{
    /// <summary>The generated writer of a table (registered by the API project's generated code).</summary>
    public static IImportRowWriter Generated(IServiceProvider services, string table) =>
        services.GetServices<IImportRowWriter>().FirstOrDefault(w => string.Equals(w.Code, table, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("No import writer is registered for " + table + ".");
}
