using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.People;

/// <summary>
/// The person services (slice S1.20a Build items 1 to 4; FR-HRM-EMP-01, -05, -15, -16, -18, FR-XM-28). Every write goes through
/// here (the generated writes are closed, ruling Q1): permission, data scope before and after, field security, validation, one
/// transaction, and the change log written by the audit interceptor with the acting user (GHR-09). Identifier values are
/// encrypted with a blind index and shown as their last four characters (GHR-02).
/// </summary>
internal sealed partial class PersonService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISensitiveAccessLog accessLog,
    IFieldEncryptorProvider keys,
    INumberAllocator numbers,
    ISettings settings,
    IHeldChanges held,
    PersonMerger merger) : IPersonService
{
    public const string ValuePurpose = "hrm.person_identifiers.value";
    public const string RelationIdentifierPurpose = "hrm.person_relations.identifier";

    // Localization keys (people.en.json).
    public const string Required = "errors.hrm.field.required";
    public const string TooLong = "errors.hrm.field.too_long";
    public const string UnknownCode = "errors.hrm.field.unknown_code";
    public const string CannotClear = "errors.hrm.field.cannot_clear";
    public const string FutureDate = "errors.hrm.date.future";
    public const string DateOrder = "errors.hrm.date.order";
    public const string ReligionNotAsked = "errors.hrm.person.religion_not_asked";
    public const string CustomNotAvailable = "errors.hrm.person.custom_not_available";
    public const string Retired = "errors.hrm.person.retired";
    public const string PossibleDuplicates = "errors.hrm.person.possible_duplicates";
    public const string DuplicatesAnswer = "errors.hrm.person.duplicates_answer";
    public const string ObjectRequired = "errors.hrm.field.object_required";
    public const string RowVersionRequired = "errors.data.row_version_required";
    public const string Concurrency = "errors.data.concurrency_conflict";

    private const int NameLength = 200;
    private const int TextLength = 500;

    private static readonly string[] Genders = ["male", "female", "other", "unspecified"];

    private static readonly string[] Clearable = ["middleName", "lastName", "nameLocal", "fatherOrSpouseName", "dateOfBirth", "maritalStatus", "nationalityCode", "photoFileId"];

    private static PeopleCatalogue Catalogue => PeopleCatalogue.Default;

    private DbContextScopeSource Source => new(db);

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    // ================================================================== persons

    public async Task<PeopleResult> CreateAsync(CreatePersonRequest request, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(PeoplePermissions.PersonsCreate, cancellationToken).ConfigureAwait(false)
            || !await SectionsAllowedAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new FieldErrors();
        var person = new Person { Gender = string.Empty, FirstName = string.Empty, PersonNo = string.Empty };
        ApplyPersonFields(person, request, errors, creating: true);
        if (request.Duplicates is not null and not ("distinct" or "review"))
        {
            errors.Add("duplicates", DuplicatesAnswer);
        }

        // Confirming "a different person" decides the review case at once, so it needs the review permission (review S1.20a finding 7).
        if (request.Duplicates == "distinct" && !await HasAsync(PeoplePermissions.DuplicatesUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        Stamp(person);
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        var identifiers = new List<PersonIdentifier>();
        foreach (var (input, i) in (request.Identifiers ?? []).Select((x, i) => (x, i)))
        {
            var row = new PersonIdentifier { PersonId = person.Id, Kind = string.Empty, CountryCode = string.Empty, Value = [], ValueHmac = [] };
            if (ApplyIdentifier(row, input, enc, errors, $"identifiers[{i}].", creating: true))
            {
                identifiers.Add(Stamp(row));
            }
        }

        var contacts = (request.Contacts ?? []).Select((x, i) => ApplyContact(new PersonContact { PersonId = person.Id, Kind = string.Empty, Value = string.Empty }, x, errors, $"contacts[{i}].", creating: true)).Select(Stamp).ToList();
        var addresses = (request.Addresses ?? []).Select((x, i) => ApplyAddress(new PersonAddress { PersonId = person.Id, Kind = string.Empty, Address = null! }, x, errors, $"addresses[{i}].", creating: true)).Select(Stamp).ToList();
        var relations = (request.Relations ?? []).Select((x, i) => ApplyRelation(new PersonRelation { PersonId = person.Id, Relation = string.Empty, FullName = string.Empty }, x, enc, errors, $"relations[{i}].", creating: true)).Select(Stamp).ToList();
        var qualifications = (request.Qualifications ?? []).Select((x, i) => ApplyQualification(new PersonQualification { PersonId = person.Id, Kind = string.Empty, Title = string.Empty, Details = null! }, x, errors, $"qualifications[{i}].", creating: true)).Select(Stamp).ToList();
        foreach (var kind in addresses.GroupBy(a => (a.Kind, a.ValidFrom)).Where(g => g.Count() > 1))
        {
            errors.Add("addresses", "errors.hrm.address.same_start");
        }

        foreach (var repeated in identifiers.GroupBy(r => (r.Kind, r.CountryCode, Convert.ToBase64String(r.ValueHmac))).Where(g => g.Count() > 1))
        {
            errors.Add("identifiers", IdentifierRepeated);
        }

        if (errors.Any)
        {
            return PeopleResult.Fields(errors.ToDictionary());
        }

        // Data scope of the new rows (a role limited to some companies sees only persons employed there: S1.20b hires them).
        if (!await AllowsAsync(person, PersonAccess.Scope, "create", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var denied = await NotEditableAsync(PersonAccess.Fields, PersonAccess.Scope, "create", person, PersonChanges(request, creating: true), cancellationToken).ConfigureAwait(false);
        if (denied.Count > 0)
        {
            return NotEditable(denied);
        }

        // Exact identifier match on the blind index blocks (FR-HRM-EMP-05, -18).
        foreach (var identifier in identifiers)
        {
            if (await IdentifierOwnerAsync(identifier, cancellationToken).ConfigureAwait(false) is { } owner)
            {
                return PeopleResult.Conflict(IdentifierDuplicate, new Dictionary<string, object?> { ["matches"] = new[] { await MatchAsync(owner, "identifier", cancellationToken).ConfigureAwait(false) } });
            }
        }

        // Possible duplicates: same normalized name and date of birth (FR-HRM-EMP-18).
        var similar = await SimilarPersonsAsync(person, cancellationToken).ConfigureAwait(false);
        if (similar.Count > 0 && request.Duplicates is null)
        {
            var matches = new List<PersonMatch>();
            foreach (var other in similar)
            {
                matches.Add(await MatchAsync(other, "name_and_birth_date", cancellationToken).ConfigureAwait(false));
            }

            return PeopleResult.Conflict(PossibleDuplicates, new Dictionary<string, object?> { ["matches"] = matches });
        }

        var visibleMatches = new HashSet<Guid>();
        foreach (var other in similar)
        {
            if (await AllowsAsync(other, PersonAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
            {
                visibleMatches.Add(other.Id);
            }
        }

        try
        {
            person.PersonNo = await numbers.AssignOnCreateAsync(new NumberRequest("hrm.person_no", null, null, null), cancellationToken).ConfigureAwait(false);
        }
        catch (NumberingException ex)
        {
            return PeopleResult.Invalid(ex.ErrorKey);
        }

        var cases = similar.Select(other => Stamp(new PersonDuplicateCase
        {
            PersonId = person.Id,
            OtherPersonId = other.Id,
            MatchKind = "name_and_birth_date",
            Source = source,
            Status = request.Duplicates == "distinct" ? "distinct" : "open",
            DecidedBy = request.Duplicates == "distinct" ? actor.UserId : null,
            DecidedAt = request.Duplicates == "distinct" ? clock.UtcNow : null,
        })).ToList();

        foreach (var (row, target) in SectionTargets(identifiers, contacts, addresses, relations, qualifications))
        {
            if (!await target(row).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }
        }

        // The person first, then the rows that reference it (EF knows no relationships here), in one transaction.
        if (await SaveAsync(cancellationToken, () => db.Add(person), () =>
            {
                db.AddRange(identifiers);
                db.AddRange(contacts);
                db.AddRange(addresses);
                db.AddRange(relations);
                db.AddRange(qualifications);
                db.AddRange(cases);
            }).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        return new PeopleResult(PeopleOutcome.Created, Body: new
        {
            id = person.Id,
            personNo = person.PersonNo,
            rowVersion = person.RowVersion,
            // The other person is named only when the caller may read them (review S1.20a finding 6, CR-076 detail h).
            duplicateCases = cases.Select(c => new { id = c.Id, otherPersonId = visibleMatches.Contains(c.OtherPersonId) ? c.OtherPersonId : (Guid?)null, matchKind = c.MatchKind, status = c.Status }).ToList(),
        });
    }

    public async Task<PeopleResult> UpdateAsync(Guid personId, UpdatePersonRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(PeoplePermissions.PersonsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        if (await PersonForChangeAsync(personId, "update", cancellationToken).ConfigureAwait(false) is not { } person)
        {
            return PeopleResult.NotFound;
        }

        if (person.MergedIntoPersonId is not null)
        {
            return PeopleResult.Conflict(Retired);
        }

        if (person.RowVersion != expected)
        {
            return PeopleResult.Conflict(Concurrency);
        }

        var errors = new FieldErrors();
        var changes = PersonChanges(request, creating: false);
        var original = Copy(person);
        ApplyPersonFields(person, request, errors, creating: false);
        if (errors.Any)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors.ToDictionary());
        }

        var denied = await NotEditableAsync(PersonAccess.Fields, PersonAccess.Scope, "update", original, changes.Where(c => Changed(original, person, c.Key)), cancellationToken).ConfigureAwait(false);
        if (denied.Count > 0)
        {
            db.ChangeTracker.Clear();
            return NotEditable(denied);
        }

        Touch(person, expected);
        return await SaveAsync(cancellationToken).ConfigureAwait(false) ?? Done(person.Id, person.RowVersion);
    }

    public Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken) =>
        RunReadAsync(PeoplePermissions.IdentifiersRead, () => Task.FromResult(new PeopleResult(PeopleOutcome.Done, Body: new
        {
            identifierKinds = Catalogue.IdentifierKinds,
            maritalStatuses = Catalogue.MaritalStatuses,
            relations = Catalogue.Relations,
        })), cancellationToken);

    /// <summary>Applies the person fields of a create or change; problems go to <paramref name="errors"/>.</summary>
    private void ApplyPersonFields(Person person, PersonFields f, FieldErrors errors, bool creating)
    {
        var clear = (f.Clear ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var name in clear.Where(n => !Clearable.Contains(n, StringComparer.Ordinal)))
        {
            errors.Add(name, CannotClear);
        }

        if (creating || f.FirstName is not null)
        {
            person.FirstName = TextOf(f.FirstName, "firstName", NameLength, errors, required: true) ?? person.FirstName;
        }

        person.MiddleName = Optional(f.MiddleName, "middleName", NameLength, clear, person.MiddleName, errors);
        person.LastName = Optional(f.LastName, "lastName", NameLength, clear, person.LastName, errors);
        person.NameLocal = Optional(f.NameLocal, "nameLocal", NameLength, clear, person.NameLocal, errors);
        person.FatherOrSpouseName = Optional(f.FatherOrSpouseName, "fatherOrSpouseName", NameLength, clear, person.FatherOrSpouseName, errors);
        if (creating || f.Gender is not null)
        {
            if (f.Gender is null)
            {
                errors.Add("gender", Required);
            }
            else if (!Genders.Contains(f.Gender, StringComparer.Ordinal))
            {
                errors.Add("gender", UnknownCode);
            }
            else
            {
                person.Gender = f.Gender;
            }
        }

        if (f.DateOfBirth is { } birth)
        {
            if (birth > Today)
            {
                errors.Add("dateOfBirth", FutureDate);
            }

            person.DateOfBirth = birth;
        }
        else if (clear.Contains("dateOfBirth"))
        {
            person.DateOfBirth = null;
        }

        if (f.MaritalStatus is { } marital)
        {
            if (!Catalogue.MaritalStatuses.Any(m => m.Code == marital))
            {
                errors.Add("maritalStatus", UnknownCode);
            }

            person.MaritalStatus = marital;
        }
        else if (clear.Contains("maritalStatus"))
        {
            person.MaritalStatus = null;
        }

        if (f.NationalityCode is { } nationality)
        {
            if (nationality.Length != 2 || !nationality.All(char.IsAsciiLetterUpper))
            {
                errors.Add("nationalityCode", UnknownCode);
            }

            person.NationalityCode = nationality;
        }
        else if (clear.Contains("nationalityCode"))
        {
            person.NationalityCode = null;
        }

        // Religion only where a pack field requirement asks for it (spec 06a data design; packs arrive with S1.23).
        if (f.Religion is not null)
        {
            errors.Add("religion", ReligionNotAsked);
        }

        if (f.PhotoFileId is { } photo)
        {
            person.PhotoFileId = photo;
        }
        else if (clear.Contains("photoFileId"))
        {
            person.PhotoFileId = null;
        }

        // Custom fields are checked against their definitions by S1.15b; until then none can be defined, so none can be set.
        if (f.Custom is { } custom && !(custom.ValueKind == JsonValueKind.Object && !custom.EnumerateObject().Any()))
        {
            errors.Add("custom", CustomNotAvailable);
        }
    }

    /// <summary>The person columns a request sets (field security keys), for the "not editable" check.</summary>
    private static List<(string Key, bool Changed)> PersonChanges(PersonFields f, bool creating)
    {
        var clear = (f.Clear ?? []).ToHashSet(StringComparer.Ordinal);
        bool Set(object? value, string json) => value is not null || clear.Contains(json);
        return
        [
            ("first_name", creating || f.FirstName is not null),
            ("middle_name", Set(f.MiddleName, "middleName")),
            ("last_name", Set(f.LastName, "lastName")),
            ("name_local", Set(f.NameLocal, "nameLocal")),
            ("father_or_spouse_name", Set(f.FatherOrSpouseName, "fatherOrSpouseName")),
            ("gender", creating || f.Gender is not null),
            ("date_of_birth", Set(f.DateOfBirth, "dateOfBirth")),
            ("marital_status", Set(f.MaritalStatus, "maritalStatus")),
            ("nationality_code", Set(f.NationalityCode, "nationalityCode")),
            ("photo_file_id", Set(f.PhotoFileId, "photoFileId")),
        ];
    }

    private static bool Changed(Person before, Person after, string key) => key switch
    {
        "first_name" => before.FirstName != after.FirstName,
        "middle_name" => before.MiddleName != after.MiddleName,
        "last_name" => before.LastName != after.LastName,
        "name_local" => before.NameLocal != after.NameLocal,
        "father_or_spouse_name" => before.FatherOrSpouseName != after.FatherOrSpouseName,
        "gender" => before.Gender != after.Gender,
        "date_of_birth" => before.DateOfBirth != after.DateOfBirth,
        "marital_status" => before.MaritalStatus != after.MaritalStatus,
        "nationality_code" => before.NationalityCode != after.NationalityCode,
        "photo_file_id" => before.PhotoFileId != after.PhotoFileId,
        _ => true,
    };

    private static Person Copy(Person p) => new()
    {
        Id = p.Id, PersonNo = p.PersonNo, FirstName = p.FirstName, MiddleName = p.MiddleName, LastName = p.LastName, NameLocal = p.NameLocal,
        FatherOrSpouseName = p.FatherOrSpouseName, Gender = p.Gender, DateOfBirth = p.DateOfBirth, MaritalStatus = p.MaritalStatus,
        NationalityCode = p.NationalityCode, PhotoFileId = p.PhotoFileId, MergedIntoPersonId = p.MergedIntoPersonId, RowVersion = p.RowVersion,
    };

    /// <summary>Persons (not retired, not deleted) with the same normalized name and date of birth.</summary>
    private async Task<List<Person>> SimilarPersonsAsync(Person person, CancellationToken ct)
    {
        if (person.DateOfBirth is not { } birth)
        {
            return [];
        }

        var name = PeopleCatalogue.NormalizeName(person.FirstName, person.MiddleName, person.LastName);
        var sameDay = await db.Set<Person>().AsNoTracking()
            .Where(p => p.DateOfBirth == birth && !p.IsDeleted && p.MergedIntoPersonId == null && p.Id != person.Id)
            .OrderBy(p => p.Id).Take(500).ToListAsync(ct).ConfigureAwait(false);
        return sameDay.Where(p => PeopleCatalogue.NormalizeName(p.FirstName, p.MiddleName, p.LastName) == name).ToList();
    }

    /// <summary>
    /// A match shown to the person creating a record: who it is when the caller may see that person, otherwise only that a match
    /// exists outside their records (no name, number or id leaves the caller's data scope).
    /// </summary>
    private async Task<PersonMatch> MatchAsync(Person other, string kind, CancellationToken ct) =>
        await AllowsAsync(other, PersonAccess.Scope, "read", ct).ConfigureAwait(false)
            ? new PersonMatch(kind, true, other.Id, other.PersonNo, DisplayName(other))
            : new PersonMatch(kind, false, null, null, null);

    /// <summary>A possible or exact duplicate as the refusal lists it.</summary>
    private sealed record PersonMatch(string MatchKind, bool InScope, Guid? PersonId, string? PersonNo, string? Name);

    internal static string DisplayName(Person p) => string.Join(' ', new[] { p.FirstName, p.MiddleName, p.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));

    // ================================================================== shared helpers

    private async Task<bool> HasAsync(string permission, CancellationToken ct) => await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false);

    private async Task<bool> SectionsAllowedAsync(CreatePersonRequest r, CancellationToken ct) =>
        ((r.Identifiers?.Count ?? 0) == 0 || await HasAsync(PeoplePermissions.IdentifiersCreate, ct).ConfigureAwait(false))
        && ((r.Contacts?.Count ?? 0) == 0 || await HasAsync("hrm.person_contacts.create", ct).ConfigureAwait(false))
        && ((r.Addresses?.Count ?? 0) == 0 || await HasAsync("hrm.person_addresses.create", ct).ConfigureAwait(false))
        && ((r.Relations?.Count ?? 0) == 0 || await HasAsync("hrm.person_relations.create", ct).ConfigureAwait(false))
        && ((r.Qualifications?.Count ?? 0) == 0 || await HasAsync("hrm.person_qualifications.create", ct).ConfigureAwait(false));

    private IEnumerable<(object Row, Func<object, Task<bool>> Allowed)> SectionTargets(
        List<PersonIdentifier> identifiers, List<PersonContact> contacts, List<PersonAddress> addresses, List<PersonRelation> relations, List<PersonQualification> qualifications)
    {
        foreach (var r in identifiers)
        {
            yield return (r, o => AllowsAsync((PersonIdentifier)o, PersonIdentifierAccess.Scope, "create", CancellationToken.None));
        }

        foreach (var r in contacts)
        {
            yield return (r, o => AllowsAsync((PersonContact)o, PersonContactAccess.Scope, "create", CancellationToken.None));
        }

        foreach (var r in addresses)
        {
            yield return (r, o => AllowsAsync((PersonAddress)o, PersonAddressAccess.Scope, "create", CancellationToken.None));
        }

        foreach (var r in relations)
        {
            yield return (r, o => AllowsAsync((PersonRelation)o, PersonRelationAccess.Scope, "create", CancellationToken.None));
        }

        foreach (var r in qualifications)
        {
            yield return (r, o => AllowsAsync((PersonQualification)o, PersonQualificationAccess.Scope, "create", CancellationToken.None));
        }
    }

    private async Task<bool> AllowsAsync<T>(T row, ScopeTarget<T> target, string action, CancellationToken ct)
        where T : class =>
        await scope.AllowsAsync(row, target, action, Source, ct).ConfigureAwait(false);

    /// <summary>The person, tracked for a change, when the caller may read it and make the action on it; null otherwise.</summary>
    private async Task<Person?> PersonForChangeAsync(Guid id, string action, CancellationToken ct)
    {
        var person = await db.Set<Person>().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct).ConfigureAwait(false);
        return person is not null && await AllowsAsync(person, PersonAccess.Scope, "read", ct).ConfigureAwait(false)
               && await AllowsAsync(person, PersonAccess.Scope, action, ct).ConfigureAwait(false)
            ? person
            : null;
    }

    /// <summary>
    /// The fields among <paramref name="changes"/> the caller may not edit (FR-KRN-IAM-13): decided by the roles whose data scope
    /// covers the row; a field the catalogue does not list (an encrypted value) is governed by the permission alone.
    /// </summary>
    private async Task<List<string>> NotEditableAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, string action, T row, IEnumerable<(string Key, bool Changed)> changes, CancellationToken ct)
        where T : class, IEntity
    {
        var plan = await fields.PlanAsync(catalog, target, action, Source, ct).ConfigureAwait(false);
        var map = (await plan.ResolveAsync([row], Source, ct).ConfigureAwait(false))[0];
        return changes.Where(c => c.Changed && catalog.Find(c.Key) is not null && !map.CanEdit(c.Key)).Select(c => c.Key).ToList();
    }

    private static PeopleResult NotEditable(List<string> denied) =>
        new(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));

    private async Task<PeopleResult> RunReadAsync(string permission, Func<Task<PeopleResult>> work, CancellationToken ct) =>
        await HasAsync(permission, ct).ConfigureAwait(false) ? await work().ConfigureAwait(false) : PeopleResult.Forbidden();

    private T Stamp<T>(T entity)
        where T : class, IEntity
    {
        var now = clock.UtcNow;
        if (entity.Id == Guid.Empty)
        {
            entity.Id = Guid.CreateVersion7(now);
        }

        if (entity is ICreationAudited created)
        {
            created.CreatedAt = now;
            created.CreatedBy = actor.UserId;
        }

        if (entity is IVersioned versioned)
        {
            versioned.UpdatedAt = now;
            versioned.UpdatedBy = actor.UserId;
            versioned.RowVersion = 1;
        }

        return entity;
    }

    /// <summary>Marks a tracked row changed: <c>where row_version = expected</c> on save (concurrency), next version, stamp.</summary>
    private void Touch<T>(T entity, long expected)
        where T : class, IVersioned => PersonMerger.Touch(db, entity, expected, clock.UtcNow, actor.UserId);

    /// <summary>Saves the pending changes in one transaction; constraint refusals become answers with their keys.</summary>
    /// <remarks>Each of <paramref name="stages"/> adds rows and is saved before the next (parents before children).</remarks>
    private async Task<PeopleResult?> SaveAsync(CancellationToken ct, params Action[] stages)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var stage in stages)
            {
                stage();
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }
    }

    internal static PeopleResult Refusal(PostgresException pg) =>
        pg.ConstraintName == "uq_person_identifiers_kind_country_code_value_hmac"
            ? PeopleResult.Conflict(IdentifierDuplicate)
            : PostgresErrors.Translate(pg, pg) switch
            {
                DataConflictException c => PeopleResult.Conflict(c.ErrorCode, new Dictionary<string, object?> { ["constraint"] = c.Constraint }),
                DataException d => new PeopleResult(PeopleOutcome.Invalid, d.ErrorCode, Extensions: new Dictionary<string, object?> { ["constraint"] = d.Constraint }),
            };

    private static PeopleResult Done(Guid id, long rowVersion) => new(PeopleOutcome.Done, Body: new { id, rowVersion });

    private static string? TextOf(string? value, string field, int max, FieldErrors errors, bool required)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (required)
            {
                errors.Add(field, Required);
            }

            return null;
        }

        if (text.Length > max)
        {
            errors.Add(field, TooLong);
        }

        return text;
    }

    private static string? Optional(string? value, string field, int max, HashSet<string> clear, string? current, FieldErrors errors) =>
        value is not null ? TextOf(value, field, max, errors, required: false) : clear.Contains(field) ? null : current;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Field errors of a request: field name (camelCase, with the section index) to localization keys.</summary>
    private sealed class FieldErrors
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

        public bool Any => _errors.Count > 0;

        public void Add(string field, string key)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                _errors[field] = list = [];
            }

            if (!list.Contains(key, StringComparer.Ordinal))
            {
                list.Add(key);
            }
        }

        public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }
}
