using System.Text.Json;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Puts a transfer between companies into force (slice S1.20 Build item 11, FR-XM-30, ruling Q9), for the transfer service (no
/// approval needed) and the approval of a held transfer alike, inside the caller's transaction and with both employments locked by
/// the caller: the separation event of the first employment and the starting <c>hire</c> event (reason <c>transfer_in</c>) of the
/// linked one become approved (dated in the future) or effective; the first employment's job details end on the transfer date and
/// the linked one's start on it; on the date the first becomes <c>separated</c> and the linked one the person's main employment
/// (<see cref="EmploymentApplier"/>, which also moves the probation then: review S1.20c finding 2); <c>hrm.employee_hired</c> and, once
/// effective, <c>hrm.employee_separated</c> are raised.
/// </summary>
internal static class TransferApplier
{
    public const string SeparationReason = "transfer";
    public const string TransferInReason = "transfer_in";

    public static async Task<string?> ApplyAsync(HrmDbContext db, EmploymentApplier applier, Employment from, EmploymentEvent separation, Employment to, EmploymentEvent start,
        DateOnly today, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(separation);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(start);
        var fromEvents = await applier.EventsAsync(from.Id, ct).ConfigureAwait(false);
        EmploymentApplier.SetApplied(separation, fromEvents, today, now, actor);
        EmploymentApplier.SetApplied(start, [], today, now, actor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The linked employment first (not yet the main one), then the first employment's end, which makes it the main one.
        if (await applier.SyncRowsAsync(to, [start], now, actor, ct).ConfigureAwait(false) is { } toProblem)
        {
            return toProblem;
        }

        await applier.ApplyEmploymentFieldsAsync(to, start, today, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        List<EmploymentEvent> all = [.. fromEvents.Where(e => e.Id != separation.Id), separation];
        if (await applier.SyncRowsAsync(from, all, now, actor, ct).ConfigureAwait(false) is { } fromProblem)
        {
            return fromProblem;
        }

        await applier.ApplyEmploymentFieldsAsync(from, separation, today, ct).ConfigureAwait(false);
        applier.Raise(EmploymentEvents.EmployeeHired, to, now, actor, new
        {
            employmentId = to.Id,
            personId = to.PersonId,
            companyId = to.CompanyId,
            eventId = start.Id,
            joiningDate = to.JoiningDate,
            status = to.Status,
            previousEmploymentId = from.Id,
        });
        if (separation.Status == "effective")
        {
            applier.RaiseChanged(from, separation, all, now, actor);
        }

        return null;
    }

    /// <summary>The three choices recorded on both events of a transfer (<c>carry_over</c>, FR-XM-30).</summary>
    public static TransferCarryOver? CarryOver(EmploymentEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.CarryOver is not { RootElement.ValueKind: JsonValueKind.Object } doc)
        {
            return null;
        }

        string? Text(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return Text("service") is { } s && Text("leave") is { } l && Text("loans") is { } n ? new TransferCarryOver(s, l, n) : null;
    }
}
