using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Workflow;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The HR module's answers to the approval engine's organisation questions (slice S1.20 Build item 9, ruling Q7; S1.10 ruling Q4):
/// the line manager and the managers above, from <c>manager_employment_id</c> of the assignment valid on the date, through the
/// employments' user links. Department heads and position holders (slice S1.22a Build item 6, ruling Q3, FR-HRM-POS-08): the head of
/// the department is the holder, on the date, of the head position of the unit the person works in (<c>org.org_units.head_position_ref</c>);
/// a unit without a head, or whose head is the person asking, hands the question to the unit above it in the reporting hierarchy; with
/// none at the top the question has no answer and the engine's escalation applies. A position is named by its code. Only user ids leave
/// the module.
/// </summary>
public sealed class HrmApproverDirectory(ITenantConnectionProvider tenant) : IApproverDirectory
{
    // An employment is current on the date from its joining date until its separation date (inclusive), unless cancelled.
    private const string Current = "e.status <> 'cancelled' and e.joining_date <= $2 and (e.separation_date is null or e.separation_date >= $2)";

    public bool Supports(string kind) => kind is DirectoryKinds.Manager or DirectoryKinds.DepartmentHead or DirectoryKinds.Position;

    public async Task<IReadOnlyList<Guid>> ManagersAsync(Guid userId, int levels, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (levels < 1)
        {
            return [];
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var start = await IdsAsync(connection, $"select e.id from hrm.employments e where e.user_ref = $1 and {Current}", cancellationToken, userId, asOf).ConfigureAwait(false);
        return await ManagerUsersAsync(connection, start, levels, asOf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The managers of the employment the document is about (R-305, CR-116): the subject is the employment id, so a subject with no user
    /// still has its own manager. A subject that is not an employment id, an unknown one or one without a manager answers nothing.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ManagersOfSubjectAsync(string subject, int levels, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (levels < 1 || !Guid.TryParse(subject, out var employmentId))
        {
            return [];
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ManagerUsersAsync(connection, [employmentId], levels, asOf, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Guid>> ManagerUsersAsync(NpgsqlConnection connection, List<Guid> current, int levels, DateOnly asOf, CancellationToken cancellationToken)
    {
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

    public async Task<IReadOnlyList<Guid>> DepartmentHeadsAsync(Guid userId, Guid companyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // The unit the person works in on the date: the assignment valid then of their current employment in the company.
        var units = await IdsAsync(connection, $"""
            select a.org_unit_id from hrm.employments e join hrm.employment_assignments a on a.employment_id = e.id
            where e.user_ref = $1 and e.company_id = $2 and a.valid_from <= $3 and (a.valid_to is null or a.valid_to > $3) and {CurrentOn(3)}
            order by e.is_primary desc, e.joining_date, e.id
            """, cancellationToken, userId, companyId, asOf).ConfigureAwait(false);
        if (units.Count == 0)
        {
            return [];
        }

        var unit = (Guid?)units[0];
        var seen = new HashSet<Guid>();
        while (unit is { } current && seen.Add(current))
        {
            var heads = await IdsAsync(connection, $"""
                select distinct e.user_ref from org.org_units u
                join hrm.positions head on head.id = u.head_position_ref and head.company_id = u.company_id
                join hrm.positions p on p.company_id = head.company_id and p.position_code_lower = head.position_code_lower
                join hrm.employment_assignments a on a.position_id = p.id
                join hrm.employments e on e.id = a.employment_id
                where u.id = $1 and u.company_id = $2 and not u.is_deleted and e.user_ref is not null and e.user_ref <> $4
                  and a.valid_from <= $3 and (a.valid_to is null or a.valid_to > $3) and {CurrentOn(3)}
                """, cancellationToken, current, companyId, asOf, userId).ConfigureAwait(false);
            if (heads.Count > 0)
            {
                return heads;
            }

            // No head (or only the person themselves): the unit above, in the reporting hierarchy on the date.
            var parents = await IdsAsync(connection, """
                select l.parent_unit_id from org.org_unit_links l join org.org_hierarchies h on h.id = l.hierarchy_id and h.kind = 'reporting'
                where l.unit_id = $1 and l.parent_unit_id is not null and l.valid_from <= $2 and (l.valid_to is null or l.valid_to > $2)
                order by h.code, h.id limit 1
                """, cancellationToken, current, asOf).ConfigureAwait(false);
            unit = parents.Count == 0 ? null : parents[0];
        }

        return [];
    }

    public async Task<IReadOnlyList<Guid>> PositionHoldersAsync(string position, Guid companyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await IdsAsync(connection, $"""
            select distinct e.user_ref from hrm.positions p
            join hrm.employment_assignments a on a.position_id = p.id
            join hrm.employments e on e.id = a.employment_id
            where p.company_id = $2 and p.position_code_lower = lower($1) and e.user_ref is not null
              and a.valid_from <= $3 and (a.valid_to is null or a.valid_to > $3) and {CurrentOn(3)}
            """, cancellationToken, position.Trim(), companyId, asOf).ConfigureAwait(false);
    }

    private static string CurrentOn(int date) =>
        $"e.status <> 'cancelled' and e.joining_date <= ${date} and (e.separation_date is null or e.separation_date >= ${date})";

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
