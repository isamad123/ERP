using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Statutory registrations of employments (slice S1.20 Build item 15; FR-HRM-CPL-02 data part, FR-HRM-PAY-20 inputs): per employment
/// and scheme (EOBI, provincial social security, provident fund), applicable or exempt with a reason, the registration number
/// encrypted with a blind index, effective-dated (one row per scheme on any date, the exclusion constraint). Registrations only: no
/// contribution rate, ceiling or rule is stored or coded here (S1.37, AS-4). The scheme is a typed code until the pack catalogue
/// (S1.23) lists the schemes. The same number of a scheme on another person is refused. Generated writes are closed (R-113).
/// </summary>
internal sealed class StatutoryRegistrationService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISensitiveAccessLog accessLog,
    IFieldEncryptorProvider keys,
    EmploymentApplier applier) : IStatutoryRegistrationService
{
    public const string NumberPurpose = "hrm.employment_statutory_registrations.registration_no";
    public const string SchemeFormat = "errors.hrm.registration.scheme_format";
    public const string NumberFormat = "errors.hrm.registration.number_format";
    public const string ReasonRequired = "errors.hrm.registration.exemption_reason_required";
    public const string ReasonNotAllowed = "errors.hrm.registration.exemption_reason_not_allowed";
    public const string Overlaps = "errors.hrm.registration.overlaps";
    public const string Duplicate = "errors.hrm.registration.duplicate";
    public const string RevealMasked = "errors.hrm.registration.reveal_masked";
    public const string BeforeJoining = "errors.hrm.registration.before_joining";
    public const string EffectiveFrom = "errors.hrm.registration.effective_from";

    private const int SchemeLength = 40;
    private const int NumberLength = 40;
    private const int ReasonLength = 500;

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    private static FieldCatalog Catalog => EmploymentStatutoryRegistrationAccess.Fields;

    // ================================================================== reads

    public async Task<PeopleResult> ListAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.RegistrationsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var query = (await scope.ApplyAsync(db.Set<EmploymentStatutoryRegistration>().AsNoTracking(), EmploymentStatutoryRegistrationAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            .Where(r => r.EmploymentId == employmentId);
        if (asOf is { } date)
        {
            query = query.Where(r => r.ValidFrom <= date && (r.ValidTo == null || r.ValidTo > date));
        }

        var rows = await query.OrderBy(r => r.Scheme).ThenBy(r => r.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = await fields.PlanAsync(Catalog, EmploymentStatutoryRegistrationAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var maps = await plan.ResolveAsync(rows, Source, cancellationToken).ConfigureAwait(false);
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Select((r, i) => View(r, maps[i], enc)).ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new { items });
    }

    /// <summary>A registration as lists show it: fields under the caller's rules, the number always masked (GHR-02).</summary>
    private static Dictionary<string, object?> View(EmploymentStatutoryRegistration r, FieldAccessMap access, IFieldEncryptor enc)
    {
        string? number = null;
        if (r.RegistrationNo is not null)
        {
            try
            {
                number = FieldShaping.Mask(System.Text.Json.Nodes.JsonValue.Create(Encoding.UTF8.GetString(enc.Decrypt(r.RegistrationNo, NumberPurpose))))!.GetValue<string>();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                number = null;
            }
        }

        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = r.Id,
            ["companyId"] = r.CompanyId,
            ["employmentId"] = r.EmploymentId,
            ["scheme"] = r.Scheme,
            ["isApplicable"] = r.IsApplicable,
            ["exemptionReason"] = r.ExemptionReason,
            ["registrationNo"] = number,
            ["validFrom"] = r.ValidFrom,
            ["validTo"] = r.ValidTo,
            ["rowVersion"] = r.RowVersion,
        };
        foreach (var field in Catalog.Fields.Where(f => view.ContainsKey(f.JsonName)))
        {
            var level = access.Of(field.Key);
            if (level == FieldAccessLevel.Hidden)
            {
                view.Remove(field.JsonName);
            }
            else if (level == FieldAccessLevel.Masked && field.Key != "registration_no" && view[field.JsonName] is { } value)
            {
                view[field.JsonName] = FieldShaping.Mask(JsonSerializer.SerializeToNode(value))?.GetValue<string>();
            }
        }

        return view;
    }

    public async Task<PeopleResult> RevealAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.RegistrationsReveal, cancellationToken).ConfigureAwait(false)
            || !await permissions.HasPermissionAsync(EmploymentPermissions.RegistrationsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<EmploymentStatutoryRegistration>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registrationId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, EmploymentStatutoryRegistrationAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var plan = await fields.PlanAsync(Catalog, EmploymentStatutoryRegistrationAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([row], Source, cancellationToken).ConfigureAwait(false))[0];
        if (access.Of("registration_no") < FieldAccessLevel.ReadOnly)
        {
            return PeopleResult.Forbidden(RevealMasked);
        }

        string? number = null;
        if (row.RegistrationNo is not null)
        {
            var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
            number = Encoding.UTF8.GetString(enc.Decrypt(row.RegistrationNo, NumberPurpose));
            await accessLog.RecordAsync(Catalog.Resource, row.Id, ["registration_no"], "view", 1, cancellationToken).ConfigureAwait(false); // before the answer (GHR-02)
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { id = row.Id, scheme = row.Scheme, registrationNo = number });
    }

    // ================================================================== writes

    public async Task<PeopleResult> CreateAsync(Guid employmentId, StatutoryRegistrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.RegistrationsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, problem) = await CurrentEmploymentAsync(employmentId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var scheme = Scheme(request.Scheme, errors);
        if (request.IsApplicable is null)
        {
            errors["isApplicable"] = [PersonService.Required];
        }

        var today = await applier.TodayAsync(employment!.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var from = request.ValidFrom ?? (today > employment.JoiningDate ? today : employment.JoiningDate);
        if (from < employment.JoiningDate)
        {
            errors["validFrom"] = [BeforeJoining];
        }

        if (request.ValidTo is { } to && to <= from)
        {
            errors["validTo"] = [PersonService.DateOrder];
        }

        var now = clock.UtcNow;
        var row = new EmploymentStatutoryRegistration
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            Scheme = scheme ?? string.Empty,
            IsApplicable = request.IsApplicable ?? false,
            ValidFrom = from,
            ValidTo = request.ValidTo,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        Reason(row, request.ExemptionReason, errors);
        Number(row, request.RegistrationNo, enc, errors);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        if (!await scope.AllowsAsync(row, EmploymentStatutoryRegistrationAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var map = await fields.ForAsync(Catalog, "create", row.CompanyId, cancellationToken).ConfigureAwait(false);
        var keysSet = new List<string> { "scheme", "is_applicable", "valid_from" };
        keysSet.AddRange(row.ExemptionReason is null ? [] : ["exemption_reason"]);
        keysSet.AddRange(row.RegistrationNo is null ? [] : ["registration_no"]);
        keysSet.AddRange(row.ValidTo is null ? [] : ["valid_to"]);
        if (keysSet.Where(k => Catalog.Find(k) is not null && !map.CanEdit(k)).ToList() is { Count: > 0 } denied)
        {
            return NotEditable(denied);
        }

        return await SaveAsync(employment, row, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid registrationId, StatutoryRegistrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.RegistrationsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<EmploymentStatutoryRegistration>().FirstOrDefaultAsync(r => r.Id == registrationId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, EmploymentStatutoryRegistrationAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, EmploymentStatutoryRegistrationAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        var (employment, problem) = await CurrentEmploymentAsync(row.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            db.ChangeTracker.Clear();
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Scheme is not null && Scheme(request.Scheme, errors) != row.Scheme && !errors.ContainsKey("scheme"))
        {
            errors["scheme"] = [PersonService.CannotClear]; // another scheme is another registration
        }

        if (request.ValidFrom is not null)
        {
            errors["validFrom"] = [EffectiveFrom]; // a change takes effectiveFrom; the first day of a row is not moved
        }

        var effective = request.EffectiveFrom ?? row.ValidFrom;
        if (effective < row.ValidFrom || (row.ValidTo is { } end && effective >= end))
        {
            errors["effectiveFrom"] = [EffectiveFrom];
        }

        var keysSet = new List<string>();
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        var target = effective > row.ValidFrom ? Copy(row, Guid.CreateVersion7(clock.UtcNow), effective, clock.UtcNow, actor.UserId) : row;
        if (request.IsApplicable is { } applicable)
        {
            target.IsApplicable = applicable;
            keysSet.Add("is_applicable");
        }

        if (request.ExemptionReason is not null || request.IsApplicable is not null)
        {
            Reason(target, request.ExemptionReason ?? (target.IsApplicable ? null : row.ExemptionReason), errors);
            keysSet.Add("exemption_reason");
        }

        if (request.ClearRegistrationNo == true)
        {
            target.RegistrationNo = null;
            target.RegistrationNoHmac = null;
            keysSet.Add("registration_no");
        }
        else if (request.RegistrationNo is not null)
        {
            Number(target, request.RegistrationNo, enc, errors);
            keysSet.Add("registration_no");
        }

        if (request.ClearValidTo == true || request.ValidTo is not null)
        {
            target.ValidTo = request.ClearValidTo == true ? null : request.ValidTo;
            keysSet.Add("valid_to");
            if (target.ValidTo is { } to && to <= target.ValidFrom)
            {
                errors["validTo"] = [PersonService.DateOrder];
            }
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        var plan = await fields.PlanAsync(Catalog, EmploymentStatutoryRegistrationAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false);
        var map = (await plan.ResolveAsync([row], Source, cancellationToken).ConfigureAwait(false))[0];
        if (keysSet.Distinct().Where(k => Catalog.Find(k) is not null && !map.CanEdit(k)).ToList() is { Count: > 0 } denied)
        {
            db.ChangeTracker.Clear();
            return NotEditable(denied);
        }

        if (ReferenceEquals(target, row))
        {
            EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
            return await SaveAsync(employment!, null, row, cancellationToken).ConfigureAwait(false);
        }

        // From a later day: the row ends that day and a new row with the new details starts on it (history kept, GHR-03).
        row.ValidTo = effective;
        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        return await SaveAsync(employment!, target, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> DeleteAsync(Guid registrationId, long? rowVersion, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.RegistrationsDelete, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (rowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<EmploymentStatutoryRegistration>().FirstOrDefaultAsync(r => r.Id == registrationId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, EmploymentStatutoryRegistrationAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, EmploymentStatutoryRegistrationAccess.Scope, "delete", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        db.Entry(row).Property(nameof(row.RowVersion)).OriginalValue = expected;
        db.Remove(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        return new PeopleResult(PeopleOutcome.NoContent);
    }

    // ================================================================== helpers

    private async Task<PeopleResult> SaveAsync(Employment employment, EmploymentStatutoryRegistration? added, EmploymentStatutoryRegistration? changed, CancellationToken ct)
    {
        var subject = added ?? changed!;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employment.Id} for update", ct).ConfigureAwait(false);

            // The same number of the scheme on another person is refused (a registration follows the person, so their own other
            // employments may carry it: a transfer or a rehire).
            if (subject.RegistrationNoHmac is { } hmac
                && await (from r in db.Set<EmploymentStatutoryRegistration>().AsNoTracking()
                          join e in db.Set<Employment>().AsNoTracking() on r.EmploymentId equals e.Id
                          where r.Scheme == subject.Scheme && r.RegistrationNoHmac == hmac && e.PersonId != employment.PersonId
                          select r.Id).AnyAsync(ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return PeopleResult.Conflict(Duplicate);
            }

            if (changed is not null)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false); // the old row ends first, so the new one fits the exclusion constraint
            }

            if (added is not null)
            {
                db.Add(added);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return pg.ConstraintName == "ex_employment_statutory_registrations_employment_id_scheme"
                ? PeopleResult.Fields(new Dictionary<string, string[]> { ["validFrom"] = [Overlaps] })
                : PersonService.Refusal(pg);
        }

        var body = new { id = subject.Id, scheme = subject.Scheme, validFrom = subject.ValidFrom, validTo = subject.ValidTo, rowVersion = subject.RowVersion, endedId = added is not null ? changed?.Id : null };
        return new PeopleResult(added is not null ? PeopleOutcome.Created : PeopleOutcome.Done, Body: body);
    }

    private async Task<(Employment? Employment, PeopleResult? Problem)> CurrentEmploymentAsync(Guid id, CancellationToken ct)
    {
        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return CurrentStatuses.Contains(employment.Status) ? (employment, null) : (null, PeopleResult.Conflict(EmploymentService.NotCurrent));
    }

    /// <summary>A scheme code in lower case: letters, digits and underscores, starting with a letter (typed until the pack catalogue, S1.23).</summary>
    private static string? Scheme(string? value, Dictionary<string, string[]> errors)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code))
        {
            errors["scheme"] = [PersonService.Required];
            return null;
        }

        if (code.Length > SchemeLength || !char.IsAsciiLetterLower(code[0]) || !code.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_'))
        {
            errors["scheme"] = [SchemeFormat];
            return null;
        }

        return code;
    }

    private static void Reason(EmploymentStatutoryRegistration row, string? value, Dictionary<string, string[]> errors)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }

        if (row.IsApplicable && text is not null)
        {
            errors["exemptionReason"] = [ReasonNotAllowed];
        }
        else if (!row.IsApplicable && text is null)
        {
            errors["exemptionReason"] = [ReasonRequired];
        }
        else if (text is { Length: > ReasonLength })
        {
            errors["exemptionReason"] = [PersonService.TooLong];
        }

        row.ExemptionReason = text;
    }

    private static void Number(EmploymentStatutoryRegistration row, string? value, IFieldEncryptor enc, Dictionary<string, string[]> errors)
    {
        var typed = value?.Trim();
        if (string.IsNullOrEmpty(typed))
        {
            return;
        }

        var normal = new string(typed.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normal.Length == 0 || typed.Length > NumberLength || typed.Any(ch => !char.IsLetterOrDigit(ch) && ch is not (' ' or '-' or '/')))
        {
            errors["registrationNo"] = [NumberFormat];
            return;
        }

        row.RegistrationNo = enc.Encrypt(Encoding.UTF8.GetBytes(typed), NumberPurpose);
        row.RegistrationNoHmac = enc.BlindIndex(Encoding.UTF8.GetBytes($"{row.Scheme}:{normal}"), NumberPurpose);
    }

    private static EmploymentStatutoryRegistration Copy(EmploymentStatutoryRegistration r, Guid id, DateOnly from, DateTimeOffset now, Guid by) => new()
    {
        Id = id,
        CompanyId = r.CompanyId,
        EmploymentId = r.EmploymentId,
        Scheme = r.Scheme,
        IsApplicable = r.IsApplicable,
        ExemptionReason = r.ExemptionReason,
        RegistrationNo = r.RegistrationNo,
        RegistrationNoHmac = r.RegistrationNoHmac,
        ValidFrom = from,
        ValidTo = r.ValidTo,
        CreatedAt = now,
        CreatedBy = by,
        UpdatedAt = now,
        UpdatedBy = by,
        RowVersion = 1,
    };

    private static PeopleResult NotEditable(List<string> denied) =>
        new(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));
}
