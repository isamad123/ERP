using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Org;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>Events of the HR side of organization changes (slice S1.22b, FR-HRM-POS-08, D-43: outbox only, no Finance module yet).</summary>
public static class OrgStructureEvents
{
    /// <summary>A unit moved: the positions and people of the unit moved with it; cost-centre mapping reads this (payload: unitId, companyId, hierarchyId, oldParentId, newParentId, effectiveFrom, positions, people).</summary>
    public const string Changed = "hrm.org_structure_changed";

    public const string AggregateType = "hrm.org_structure";

    public const string UnitHasPositions = "errors.hrm.org_unit.has_positions";

    public const string UnitHasPeople = "errors.hrm.org_unit.has_people";
}

/// <summary>
/// A unit is deactivated or deleted only when no active position and no current assignment references it (slice S1.22b Build item 9,
/// ruling Q6): the people and posts are moved to another unit first. Only the unit's id comes in (D-10).
/// </summary>
public sealed class HrmOrgUnitUseGuard(ITenantConnectionProvider tenant, IClock clock) : IOrgUnitUseGuard
{
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    public async Task<string?> DeactivationProblemAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var counts = await CountsAsync(connection, null, unitId, today, cancellationToken).ConfigureAwait(false);
        return counts.Positions > 0 ? OrgStructureEvents.UnitHasPositions : counts.People > 0 ? OrgStructureEvents.UnitHasPeople : null;
    }

    /// <summary>Active positions (the latest row of a code is not closed and in force or still to start) and people (current employments with an assignment in the unit today or later).</summary>
    internal static async Task<(int Positions, int People)> CountsAsync(DbConnection connection, DbTransaction? transaction, Guid unitId, DateOnly today, CancellationToken ct)
    {
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction?)transaction;
        command.CommandText = """
            select (select count(distinct p.position_code_lower) from hrm.positions p
                    where p.org_unit_id = $1 and p.status <> 'closed' and (p.valid_to is null or p.valid_to > $2)),
                   (select count(distinct a.employment_id) from hrm.employment_assignments a join hrm.employments e on e.id = a.employment_id
                    where a.org_unit_id = $1 and (a.valid_to is null or a.valid_to > $2)
                      and e.status = any($3) and (e.separation_date is null or e.separation_date >= $2))
            """;
        command.Parameters.AddWithValue(unitId);
        command.Parameters.AddWithValue(today);
        command.Parameters.AddWithValue(CurrentStatuses);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }
}

/// <summary>
/// Turns the kernel's <c>org.unit_moved</c> into <c>hrm.org_structure_changed</c> in the HR outbox (slice S1.22b Build item 9, FR-HRM-POS-08):
/// positions and holders reference the unit, so nothing is copied; the event says how many posts and people moved with the unit, for
/// cost-centre mapping. Runs once per event in the dispatcher's transaction.
/// </summary>
public sealed class OrgStructureConsumer(IClock clock) : IEventConsumer
{
    public string Name => "hrm.org_structure";

    public bool Accepts(string eventType) => eventType == OrgEvents.UnitMoved;

    public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connection);
        var payload = message.Payload;
        var unitId = payload.GetProperty("unitId").GetGuid();
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var (positions, people) = await HrmOrgUnitUseGuard.CountsAsync(connection, transaction, unitId, today, cancellationToken).ConfigureAwait(false);
        var body = new Dictionary<string, object?>
        {
            ["unitId"] = unitId,
            ["companyId"] = payload.GetProperty("companyId").GetGuid(),
            ["hierarchyId"] = payload.GetProperty("hierarchyId").GetGuid(),
            ["oldParentId"] = Nullable(payload, "oldParentId"),
            ["newParentId"] = Nullable(payload, "newParentId"),
            ["effectiveFrom"] = payload.GetProperty("effectiveFrom").GetString(),
            ["positions"] = positions,
            ["people"] = people,
        };
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction)transaction;
        command.CommandText = """
            insert into hrm.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers)
            values ($1, $2, $3, $4, $5, $6, '{}'::jsonb)
            """;
        command.Parameters.AddWithValue(Guid.CreateVersion7(clock.UtcNow));
        command.Parameters.AddWithValue(clock.UtcNow);
        command.Parameters.AddWithValue(OrgStructureEvents.Changed);
        command.Parameters.AddWithValue(OrgStructureEvents.AggregateType);
        command.Parameters.AddWithValue(unitId);
        command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(body), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Guid? Nullable(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetGuid() : null;
}
