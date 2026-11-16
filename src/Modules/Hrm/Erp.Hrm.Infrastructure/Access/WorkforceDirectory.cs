using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Access;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Hrm.Infrastructure.Access;

/// <summary>
/// The HR module's answers to the kernel's data-scope questions (slice S1.06 Q3, CR-018), read from hrm.employments and
/// the effective-dated hrm.employment_assignments of the bound customer. Only ids leave the module.
/// </summary>
internal sealed class WorkforceDirectory(ITenantConnectionProvider tenant) : IWorkforceDirectory
{
    // An assignment is valid on a date when valid_from <= date < valid_to (open end allowed), the eff trait's '[)' range.
    private const string ValidOn = "a.valid_from <= $2 and (a.valid_to is null or a.valid_to > $2)";

    public async Task<WorkforcePosition> PositionOfUserAsync(Guid userId, DateOnly asOf, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var employments = new List<Guid>();
        var persons = new HashSet<Guid>();
        await using (var command = Command(connection, "select e.id, e.person_id from hrm.employments e where e.user_ref = $1 and e.status <> 'cancelled' order by e.id", userId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                employments.Add(reader.GetGuid(0));
                persons.Add(reader.GetGuid(1));
            }
        }

        var units = employments.Count == 0
            ? []
            : await IdsAsync(connection, $"select distinct a.org_unit_id from hrm.employment_assignments a where a.employment_id = any($1) and {ValidOn}", cancellationToken, employments.ToArray(), asOf).ConfigureAwait(false);
        return new WorkforcePosition(employments, [.. persons], units);
    }

    public Task<IReadOnlyList<Guid>> DirectReportsAsync(IReadOnlyCollection<Guid> managerEmploymentIds, DateOnly asOf, CancellationToken cancellationToken) =>
        QueryAsync($"select distinct a.employment_id from hrm.employment_assignments a where a.manager_employment_id = any($1) and {ValidOn}", managerEmploymentIds, asOf, cancellationToken);

    public Task<IReadOnlyList<Guid>> InOrgUnitsAsync(IReadOnlyCollection<Guid> orgUnitIds, DateOnly asOf, CancellationToken cancellationToken) =>
        QueryAsync($"select distinct a.employment_id from hrm.employment_assignments a where a.org_unit_id = any($1) and {ValidOn}", orgUnitIds, asOf, cancellationToken);

    public Task<IReadOnlyList<Guid>> InBranchesAsync(IReadOnlyCollection<Guid> branchIds, DateOnly asOf, CancellationToken cancellationToken) =>
        QueryAsync($"select distinct a.employment_id from hrm.employment_assignments a where a.branch_id = any($1) and {ValidOn}", branchIds, asOf, cancellationToken);

    public Task<IReadOnlyList<Guid>> InCompaniesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) =>
        QueryAsync("select e.id from hrm.employments e where e.company_id = any($1)", companyIds, null, cancellationToken);

    public Task<IReadOnlyList<Guid>> PersonsOfAsync(IReadOnlyCollection<Guid> employmentIds, CancellationToken cancellationToken) =>
        QueryAsync("select distinct e.person_id from hrm.employments e where e.id = any($1)", employmentIds, null, cancellationToken);

    private async Task<IReadOnlyList<Guid>> QueryAsync(string sql, IReadOnlyCollection<Guid> ids, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return asOf is { } date
            ? await IdsAsync(connection, sql, cancellationToken, ids.ToArray(), date).ConfigureAwait(false)
            : await IdsAsync(connection, sql, cancellationToken, ids.ToArray()).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    private static async Task<IReadOnlyList<Guid>> IdsAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = Command(connection, sql, values);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
#pragma warning disable CA2100 // constant SQL text; every value is a parameter
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(value switch
            {
                Guid[] ids => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = ids },
                DateOnly date => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = date },
                _ => new NpgsqlParameter { Value = value },
            });
        }

        return command;
    }
}

/// <summary>Registers the HR module's access-control services (slice S1.06).</summary>
public static class HrmAccessServices
{
    public static IServiceCollection AddHrmAccess(this IServiceCollection services)
    {
        services.AddScoped<IWorkforceDirectory, WorkforceDirectory>();
        return services;
    }
}
