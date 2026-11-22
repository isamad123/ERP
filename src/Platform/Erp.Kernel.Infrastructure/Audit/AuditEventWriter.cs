using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Audit;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>Modules' business events in the audit log (<see cref="IAuditEvents"/>), through the same writer as the kernel's own events.</summary>
internal sealed class AuditEventWriter(IClock clock, ICurrentActor actor) : IAuditEvents
{
    public Task RecordAsync(string eventType, string aggregateType, Guid aggregateId, object payload, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        var tx = (NpgsqlTransaction)transaction;
        return AuditTrail.RecordEventAsync(tx.Connection ?? throw new InvalidOperationException("The transaction has no connection."), tx, eventType, aggregateType, aggregateId,
            actor.UserId, clock.UtcNow, payload, cancellationToken);
    }
}
