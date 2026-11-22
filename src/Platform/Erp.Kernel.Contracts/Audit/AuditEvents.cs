using System.Data.Common;

namespace Erp.Kernel.Contracts.Audit;

/// <summary>
/// Records a business event in the audit log (slice S1.20a review finding 5, CR-076 S1.20a detail n): one security event and one
/// change-log entry on the record, in the caller's open transaction, by the acting user. For decisions whose why must stay
/// readable, such as the reason of a person merge. The payload must not carry personal, salary or restricted values.
/// </summary>
public interface IAuditEvents
{
    /// <param name="eventType">For example <c>hrm.person_merged</c>.</param>
    /// <param name="aggregateType">The record's singular type, for example <c>hrm.person</c> (the change log names the table).</param>
    /// <param name="aggregateId">The record.</param>
    /// <param name="payload">The event's details (its fields become the change-log fields).</param>
    /// <param name="transaction">The caller's open transaction; the audit rows commit with it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RecordAsync(string eventType, string aggregateType, Guid aggregateId, object payload, DbTransaction transaction, CancellationToken cancellationToken);
}
