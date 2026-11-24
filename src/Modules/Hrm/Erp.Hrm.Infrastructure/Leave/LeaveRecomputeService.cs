using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// The recompute endpoint (slice S1.32 Build item 6, FR-HRM-LVE-14): an employment's balances worked out again from the ledger and the
/// policies, as of the company's today, with every difference and what explains it. A report only; nothing is written.
/// </summary>
internal sealed class LeaveRecomputeService(
    HrmDbContext db, IClock clock, IPermissionChecker permissions, IDataScopeFilter scope, EmploymentApplier applier, LeaveRecompute recompute) : ILeaveRecomputeService
{
    public async Task<PeopleResult> RecomputeAsync(Guid employmentId, Guid? leaveTypeId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.Recompute, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (e is null || !await scope.AllowsAsync(new LeaveLedger { CompanyId = e.CompanyId, EmploymentId = e.Id }, LeaveLedgerAccess.Scope, "recompute", new DbContextScopeSource(db), cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == e.CompanyId && !t.IsDeleted && (leaveTypeId == null || t.Id == leaveTypeId))
            .OrderBy(t => t.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (leaveTypeId is not null && types.Count == 0)
        {
            return PeopleResult.NotFound;
        }

        var today = await applier.TodayAsync(e.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var report = await recompute.RunAsync(e, types, today, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: View(e, report));
    }

    /// <summary>The report as the API shows it: each difference with the text key that explains it, the expected posting's policy and inputs, and the entries.</summary>
    public static object View(Employment e, RecomputeReport report)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(report);
        return new
        {
            employmentId = e.Id,
            employeeNo = e.EmployeeNo,
            asOf = report.Today,
            horizonFrom = report.HorizonFrom,
            @checked = report.Checked,
            changed = false,
            differences = report.Differences.Select(d => new
            {
                leaveTypeId = d.LeaveTypeId,
                leaveTypeCode = d.LeaveTypeCode,
                leaveYear = d.LeaveYear,
                period = d.Key.Length == 0 ? null : d.Key,
                kind = d.Kind,
                expected = d.Expected,
                posted = d.Posted,
                date = d.Date,
                entryId = d.EntryId,
                explanation = "hrm.leave.recompute." + d.Kind,
                policy = d.Plan is { } p ? new { policyId = p.PolicyId, versionId = p.PolicyVersionId, version = p.PolicyVersion, model = p.Model, inputs = p.Inputs } : null,
                entries = d.Entries.Select(x => new { id = x.Id, entryDate = x.EntryDate, kind = x.Kind, quantity = x.Quantity, policyVersionId = x.PolicyVersionId, reversedById = x.ReversedById }),
            }),
            years = report.Years,
        };
    }

    /// <summary>One difference as a line of the job's log.</summary>
    public static string Line(Employment e, RecomputeDifference d)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(d);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{e.EmployeeNo} {d.LeaveTypeCode} {d.LeaveYear} {d.Key}: {d.Kind}; expected {d.Expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}{(d.Plan is { } p ? $" (policy version {p.PolicyVersion})" : string.Empty)}, posted {d.Posted?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}{(d.EntryId is { } id ? $" (entry {id})" : string.Empty)}");
    }
}
