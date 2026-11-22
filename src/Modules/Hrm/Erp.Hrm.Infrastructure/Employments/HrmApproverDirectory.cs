using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Workflow;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The HR module's answers to the approval engine's organisation questions (slice S1.20 Build item 9, ruling Q7; S1.10 ruling Q4):
/// the line manager and the managers above, from <c>manager_employment_id</c> of the assignment valid on the date, through the
/// employments' user links. Department heads and position holders come with positions (S1.22): until then the directory says it
/// cannot answer them, so the engine refuses to activate routes that use them. Only user ids leave the module.
/// </summary>
public sealed class HrmApproverDirectory(ITenantConnectionProvider tenant) : IApproverDirectory
{
    // An employment is current on the date from its joining date until its separation date (inclusive), unless cancelled.
    private const string Current = "e.status <> 'cancelled' and e.joining_date <= $2 and (e.separation_date is null or e.separation_date >= $2)";

    public bool Supports(string kind) => kind == DirectoryKinds.Manager;

    public async Task<IReadOnlyList<Guid>> ManagersAsync(Guid userId, int levels, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (levels < 1)
        {
            return [];
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var current = await IdsAsync(connection, $"select e.id from hrm.employments e where e.user_ref = $1 and {Current}", cancellationToken, userId, asOf).ConfigureAwait(false);
        for (var level = 0; level < levels && current.Count > 0; level++)
        {
            current = await IdsAsync(connection, $"""
                select distinct a.manager_employment_id from hrm.employment_assignments a join hrm.employments e on e.id = a.manager_employment_id
                where a.employment_id = any($1) and a.manager_employment_id is not null
                  and a.valid_from <= $2 and (a.valid_to is null or a.valid_to > $2) and {Current}
                """, cancellationToken, current.ToArray(), asOf).ConfigureAwait(false);
        }

        // Managers without a user link cannot approve; an employee with no manager gets no answer ("cannot resolve").
        return current.Count == 0
            ? []
            : await IdsAsync(connection, "select distinct e.user_ref from hrm.employments e where e.id = any($1) and e.user_ref is not null", cancellationToken, current.ToArray()).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Guid>> DepartmentHeadsAsync(Guid userId, Guid companyId, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> PositionHoldersAsync(string position, Guid companyId, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
#pragma warning disable CA2100 // constant SQL text; every value is a parameter
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(value switch
            {
                Guid[] list => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = list },
                DateOnly date => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = date },
                _ => new NpgsqlParameter { Value = value },
            });
        }

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
