using System.Text.Json;
using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Domain.Generated;
using Erp.Kernel.Contracts.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Hrm.Infrastructure.People;

/// <summary>The duplicate review queue and the merge (FR-HRM-EMP-18; slice S1.20 rulings Q4 and Q5).</summary>
internal sealed partial class PersonService
{
    public const string CaseNotOpen = "errors.hrm.duplicates.not_open";

    private static readonly string[] CaseStatuses = ["open", "distinct", "merged"];

    public async Task<PeopleResult> DuplicatesAsync(string? status, Guid? after, int? limit, CancellationToken cancellationToken)
    {
        if (!await HasAsync(PeoplePermissions.DuplicatesRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var wanted = status ?? "open";
        if (!CaseStatuses.Contains(wanted, StringComparer.Ordinal))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["status"] = [UnknownCode] });
        }

        var page = PageRequest.Create(after, limit);
        var query = await scope.ApplyAsync(db.Set<PersonDuplicateCase>().AsNoTracking(), PersonDuplicateCaseAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        query = query.Where(c => c.Status == wanted);
        if (page.After is { } from)
        {
            query = query.Where(c => c.Id.CompareTo(from) > 0);
        }

        var cases = await query.OrderBy(c => c.Id).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = cases.SelectMany(c => new[] { c.PersonId, c.OtherPersonId }).Distinct().ToList();
        var visible = await (await scope.ApplyAsync(db.Set<Person>().AsNoTracking(), PersonAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            .Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        object? Who(Guid id) => visible.TryGetValue(id, out var p)
            ? new { id = p.Id, personNo = p.PersonNo, name = DisplayName(p), mergedIntoPersonId = p.MergedIntoPersonId }
            : null; // outside the caller's records: the case is listed, the person is not named
        var items = cases.Select(c => new
        {
            id = c.Id,
            matchKind = c.MatchKind,
            source = c.Source,
            status = c.Status,
            createdAt = c.CreatedAt,
            decidedBy = c.DecidedBy,
            decidedAt = c.DecidedAt,
            decisionNote = c.DecisionNote,
            rowVersion = c.RowVersion,
            person = Who(c.PersonId),
            other = Who(c.OtherPersonId),
        }).ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new PagedResult<object>(items, cases.Count == page.Limit ? cases[^1].Id : null));
    }

    public async Task<PeopleResult> MarkDistinctAsync(Guid caseId, DecideCaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(PeoplePermissions.DuplicatesUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        var row = await db.Set<PersonDuplicateCase>().FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await AllowsAsync(row, PersonDuplicateCaseAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        // Both persons of the pair must be inside the reviewer's records to decide it (review S1.20a finding 8).
        var other = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == row.OtherPersonId, cancellationToken).ConfigureAwait(false);
        if (!await AllowsAsync(row, PersonDuplicateCaseAccess.Scope, "update", cancellationToken).ConfigureAwait(false)
            || other is null || !await AllowsAsync(other, PersonAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(BuildingBlocks.Application.Security.AccessDeniedException.OutOfScope);
        }

        if (row.Status != "open")
        {
            return PeopleResult.Conflict(CaseNotOpen);
        }

        if (row.RowVersion != expected)
        {
            return PeopleResult.Conflict(Concurrency);
        }

        var errors = new FieldErrors();
        row.DecisionNote = TextOf(request.Note, "note", TextLength, errors, required: false);
        if (errors.Any)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors.ToDictionary());
        }

        row.Status = "distinct";
        row.DecidedBy = actor.UserId;
        row.DecidedAt = clock.UtcNow;
        Touch(row, expected);
        return await SaveAsync(cancellationToken).ConfigureAwait(false) ?? Done(row.Id, row.RowVersion);
    }

    public async Task<PeopleResult> MergeAsync(Guid personId, MergeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(PeoplePermissions.PersonsMerge, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new FieldErrors();
        var reason = TextOf(request.Reason, "reason", TextLength, errors, required: true);
        if (request.IntoPersonId is null)
        {
            errors.Add("intoPersonId", Required);
        }

        if (errors.Any)
        {
            return PeopleResult.Fields(errors.ToDictionary());
        }

        var into = request.IntoPersonId!.Value;
        if (await merger.RunAsync(db, personId, into, apply: false, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return MergeRefusal(problem);
        }

        db.ChangeTracker.Clear();
        var approval = await settings.GetAsync(HrmSettingKeys.HrmPersonMergeRequiresApproval, (Guid?)null, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (approval.Value.ValueKind != JsonValueKind.False)
        {
            // Held for a second person (ruling Q4); the kernel refuses the requester as approver (FR-XM-32).
            var payload = JsonSerializer.Serialize(new MergePayload(personId, into, reason!), JsonSerializerOptions.Web);
            var requestId = await held.SubmitAsync(new HeldChangeRequest(PersonMerger.Kind, PersonAccess.Scope.Resource, personId, payload, reason), tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId });
        }

        if (await merger.RunAsync(db, personId, into, apply: true, cancellationToken).ConfigureAwait(false) is { } late)
        {
            return MergeRefusal(late);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await merger.RecordAsync(personId, into, reason!, null, actor.UserId, tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { status = "merged", id = personId, intoPersonId = into });
    }

    private static PeopleResult MergeRefusal(string key) => key switch
    {
        PersonMerger.NotFound => PeopleResult.NotFound,
        PersonMerger.EmploymentOutOfScope => PeopleResult.Forbidden(key),
        _ => PeopleResult.Conflict(key),
    };
}
