using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Hrm.Infrastructure.People;

/// <summary>
/// Merging a duplicate person into the surviving one (FR-HRM-EMP-18; slice S1.20 rulings Q4 and Q5). Used at once when the
/// approval setting is off, and by the approval of a held merge otherwise (<see cref="PersonMergeHandler"/>), with the same
/// checks both times:
/// <list type="bullet">
/// <item>the two persons differ, exist, are inside the caller's data scope for update and neither is retired;</item>
/// <item>at most one of them has an active primary employment (pre-joining, active or on notice);</item>
/// <item>the person merged away has no nominations (their shares are per person and scheme; nothing writes them before the
/// nominations slice, so this refusal is a guard).</item>
/// </list>
/// The merge moves employments (and with them their documents), identifiers, relations, qualifications and contacts to the survivor;
/// addresses move for the kinds the survivor has none of, the others stay on the retired person as history. The merged person keeps
/// its id and is marked retired with a pointer to the survivor (<c>merged_into_person_id</c>), so its audit history stays readable.
/// Every moved row is an audited change made by the acting user (GHR-09).
/// </summary>
internal sealed class PersonMerger(IClock clock, ICurrentActor actor, IDataScopeFilter scope)
{
    public const string Kind = "hrm.person.merge";
    public const string NotFound = "errors.hrm.merge.person_not_found";
    public const string SamePerson = "errors.hrm.merge.same_person";
    public const string BothEmployed = "errors.hrm.merge.both_employed";
    public const string HasNominations = "errors.hrm.merge.nominations";

    private static readonly string[] ActiveStatuses = ["pre_joining", "active", "on_notice"];

    /// <summary>The refusal the merge would get now, or null. With <paramref name="apply"/> the rows are changed (the caller saves).</summary>
    public async Task<string?> RunAsync(HrmDbContext db, Guid personId, Guid intoPersonId, bool apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (personId == intoPersonId)
        {
            return SamePerson;
        }

        if (apply)
        {
            // Both rows locked, in id order, so two merges of the same persons wait for each other instead of deadlocking.
            var first = personId.CompareTo(intoPersonId) < 0 ? personId : intoPersonId;
            var second = first == personId ? intoPersonId : personId;
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.persons where id in ({first}, {second}) order by id for update", ct).ConfigureAwait(false);
        }

        var source = new DbContextScopeSource(db);
        var persons = await db.Set<Person>().Where(p => (p.Id == personId || p.Id == intoPersonId) && !p.IsDeleted).ToListAsync(ct).ConfigureAwait(false);
        var merged = persons.FirstOrDefault(p => p.Id == personId);
        var survivor = persons.FirstOrDefault(p => p.Id == intoPersonId);
        if (merged is null || survivor is null)
        {
            return NotFound;
        }

        foreach (var p in persons)
        {
            if (!await scope.AllowsAsync(p, PersonAccess.Scope, "read", source, ct).ConfigureAwait(false)
                || !await scope.AllowsAsync(p, PersonAccess.Scope, "update", source, ct).ConfigureAwait(false))
            {
                return NotFound; // outside the caller's records: answered as if it did not exist
            }
        }

        if (merged.MergedIntoPersonId is not null || survivor.MergedIntoPersonId is not null)
        {
            return PersonService.Retired;
        }

        var employed = await db.Set<Employment>().AsNoTracking()
            .Where(e => (e.PersonId == personId || e.PersonId == intoPersonId) && e.IsPrimary && ActiveStatuses.Contains(e.Status))
            .Select(e => e.PersonId).Distinct().CountAsync(ct).ConfigureAwait(false);
        if (employed > 1)
        {
            return BothEmployed;
        }

        if (await db.Set<Nomination>().AnyAsync(n => n.PersonId == personId, ct).ConfigureAwait(false))
        {
            return HasNominations;
        }

        if (!apply)
        {
            return null;
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        void Move<T>(T row, Action<T> set)
            where T : class, IVersioned
        {
            set(row);
            Touch(db, row, row.RowVersion, now, by);
        }

        foreach (var e in await db.Set<Employment>().Where(e => e.PersonId == personId).ToListAsync(ct).ConfigureAwait(false))
        {
            Move(e, x => x.PersonId = intoPersonId);
        }

        foreach (var i in await db.Set<PersonIdentifier>().Where(i => i.PersonId == personId).ToListAsync(ct).ConfigureAwait(false))
        {
            Move(i, x => x.PersonId = intoPersonId);
        }

        foreach (var r in await db.Set<PersonRelation>().Where(r => r.PersonId == personId && !r.IsDeleted).ToListAsync(ct).ConfigureAwait(false))
        {
            Move(r, x => x.PersonId = intoPersonId);
        }

        foreach (var q in await db.Set<PersonQualification>().Where(q => q.PersonId == personId && !q.IsDeleted).ToListAsync(ct).ConfigureAwait(false))
        {
            Move(q, x => x.PersonId = intoPersonId);
        }

        var survivorPrimary = await db.Set<PersonContact>().Where(c => c.PersonId == intoPersonId && c.IsPrimary).Select(c => c.Kind).ToListAsync(ct).ConfigureAwait(false);
        foreach (var c in await db.Set<PersonContact>().Where(c => c.PersonId == personId).ToListAsync(ct).ConfigureAwait(false))
        {
            Move(c, x =>
            {
                x.PersonId = intoPersonId;
                x.IsPrimary = x.IsPrimary && !survivorPrimary.Contains(x.Kind);
            });
        }

        var survivorKinds = await db.Set<PersonAddress>().Where(a => a.PersonId == intoPersonId).Select(a => a.Kind).Distinct().ToListAsync(ct).ConfigureAwait(false);
        foreach (var a in await db.Set<PersonAddress>().Where(a => a.PersonId == personId && !survivorKinds.Contains(a.Kind)).ToListAsync(ct).ConfigureAwait(false))
        {
            Move(a, x => x.PersonId = intoPersonId);
        }

        // The review cases between the two are closed as merged; cases with third persons stay open for HR to decide.
        foreach (var c in await db.Set<PersonDuplicateCase>()
                     .Where(c => c.Status == "open" && ((c.PersonId == personId && c.OtherPersonId == intoPersonId) || (c.PersonId == intoPersonId && c.OtherPersonId == personId)))
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            Move(c, x =>
            {
                x.Status = "merged";
                x.DecidedBy = by;
                x.DecidedAt = now;
            });
        }

        Move(merged, x => x.MergedIntoPersonId = intoPersonId);
        return null;
    }

    /// <summary>Marks a tracked row changed with optimistic concurrency on <paramref name="expected"/>.</summary>
    internal static void Touch<T>(DbContext db, T entity, long expected, DateTimeOffset now, Guid by)
        where T : class, IVersioned
    {
        var entry = db.Entry(entity);
        entry.Property(nameof(IVersioned.RowVersion)).OriginalValue = expected;
        entity.RowVersion = expected + 1;
        entity.UpdatedAt = now;
        entity.UpdatedBy = by;
    }
}

/// <summary>The payload of a held merge (maker-checker request of kind <c>hrm.person.merge</c>).</summary>
internal sealed record MergePayload(Guid PersonId, Guid IntoPersonId, string Reason);

/// <summary>
/// Applies a held merge when a second person approves it (FR-KRN-IAM-14, FR-XM-32: the kernel refuses the requester). The approver
/// needs <c>hrm.persons.merge</c> and both persons inside their data scope; the checks run again on the approval's transaction.
/// </summary>
internal sealed class PersonMergeHandler(IServiceProvider services, PersonMerger merger) : IHeldChangeHandler
{
    public string Kind => PersonMerger.Kind;

    public string ApprovePermission => PeoplePermissions.PersonsMerge;

    public async Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } payload)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        return await merger.RunAsync(db, payload.PersonId, payload.IntoPersonId, apply: false, cancellationToken).ConfigureAwait(false) is { } key ? new HeldChangeProblem(key) : null;
    }

    public async Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } payload)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (await merger.RunAsync(db, payload.PersonId, payload.IntoPersonId, apply: true, cancellationToken).ConfigureAwait(false) is { } key)
        {
            return new HeldChangeProblem(key);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new HeldChangeProblem(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return new HeldChangeProblem(PersonService.Refusal(pg).Error ?? "errors.data.rejected");
        }

        return null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } payload)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        var persons = await db.Set<Person>().AsNoTracking().Where(p => p.Id == payload.PersonId || p.Id == payload.IntoPersonId).ToListAsync(cancellationToken).ConfigureAwait(false);
        static IReadOnlyDictionary<string, string>? Names(Person? p) =>
            p is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = $"{p.PersonNo} {PersonService.DisplayName(p)}", ["ur"] = $"{p.PersonNo} {(p.NameLocal ?? PersonService.DisplayName(p))}" };
        var merged = persons.FirstOrDefault(p => p.Id == payload.PersonId);
        var survivor = persons.FirstOrDefault(p => p.Id == payload.IntoPersonId);
        return new HeldChangeDescription(Names(merged), [new HeldChangeDetail("changes.detail.mergeInto", "names", Names: Names(survivor))]);
    }

    /// <summary>An HR context on the approval's connection and transaction, so the merge and its audit rows commit with the decision.</summary>
    private async Task<HrmDbContext> OnAsync(DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }

    private static MergePayload? Read(HeldChange change)
    {
        try
        {
            return JsonSerializer.Deserialize<MergePayload>(change.Payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
