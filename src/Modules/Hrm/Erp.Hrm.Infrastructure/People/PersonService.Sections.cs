using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Domain.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.People;

/// <summary>
/// The sections of a person (FR-HRM-EMP-01): identifiers (FR-HRM-EMP-05, -16), contacts, addresses effective-dated by kind
/// (GHR-03), relations (dependents and emergency contacts, BR-03) and qualifications. Each change checks the section's permission,
/// the data scope of the row before and after, and field security.
/// </summary>
internal sealed partial class PersonService
{
    public const string IdentifierDuplicate = "errors.hrm.identifier.duplicate";
    public const string IdentifierRepeated = "errors.hrm.identifier.repeated";
    public const string IdentifierKindUnknown = "errors.hrm.identifier.unknown_kind";
    public const string IdentifierFormat = "errors.hrm.identifier.format";
    public const string IdentifierNoExpiry = "errors.hrm.identifier.no_expiry";
    public const string AddressSameStart = "errors.hrm.address.same_start";
    public const string AddressFixed = "errors.hrm.address.kind_and_start_fixed";
    public const string RevealMasked = "errors.hrm.identifier.reveal_masked";

    private static readonly string[] ContactKinds = ["mobile", "phone", "email_personal", "email_work", "other"];
    private static readonly string[] AddressKinds = ["permanent", "current", "mailing"];
    private static readonly string[] QualificationKinds = ["education", "experience", "language", "certification"];

    private const string Identifiers = "hrm.person_identifiers";
    private const string Contacts = "hrm.person_contacts";
    private const string Addresses = "hrm.person_addresses";
    private const string Relations = "hrm.person_relations";
    private const string Qualifications = "hrm.person_qualifications";

    // ================================================================== identifiers

    public async Task<PeopleResult> AddIdentifierAsync(Guid personId, IdentifierInput input, CancellationToken cancellationToken)
    {
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        return await AddSectionAsync(personId, Identifiers, PersonIdentifierAccess.Scope, PersonIdentifierAccess.Fields,
            errors =>
            {
                var row = new PersonIdentifier { PersonId = personId, Kind = string.Empty, CountryCode = string.Empty, Value = [], ValueHmac = [] };
                return ApplyIdentifier(row, input, enc, errors, string.Empty, creating: true) ? row : null;
            },
            _ => IdentifierChanges(input),
            row => DuplicateIdentifierAsync(row, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateIdentifierAsync(Guid personId, Guid identifierId, IdentifierInput input, CancellationToken cancellationToken)
    {
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateSectionAsync<PersonIdentifier>(personId, identifierId, Identifiers, PersonIdentifierAccess.Scope, PersonIdentifierAccess.Fields, r => r.PersonId, input.RowVersion,
            (row, errors) => ApplyIdentifier(row, input, enc, errors, string.Empty, creating: false),
            IdentifierChanges(input), row => DuplicateIdentifierAsync(row, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<PeopleResult> DeleteIdentifierAsync(Guid personId, Guid identifierId, long? rowVersion, CancellationToken cancellationToken) =>
        DeleteSectionAsync<PersonIdentifier>(personId, identifierId, Identifiers, PersonIdentifierAccess.Scope, r => r.PersonId, rowVersion, cancellationToken);

    public async Task<PeopleResult> RevealIdentifierAsync(Guid personId, Guid identifierId, CancellationToken cancellationToken)
    {
        if (!await HasAsync(PeoplePermissions.IdentifiersReveal, cancellationToken).ConfigureAwait(false)
            || !await HasAsync(PeoplePermissions.IdentifiersRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<PersonIdentifier>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == identifierId && i.PersonId == personId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await AllowsAsync(row, PersonIdentifierAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        // A role whose field rules hide or mask the identifier never sees it in full, even with the permission (FR-KRN-IAM-13).
        var plan = await fields.PlanAsync(PersonIdentifierAccess.Fields, PersonIdentifierAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([row], Source, cancellationToken).ConfigureAwait(false))[0];
        if (access.Of("value_last4") < FieldAccessLevel.ReadOnly)
        {
            return PeopleResult.Forbidden(RevealMasked);
        }

        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        var value = System.Text.Encoding.UTF8.GetString(enc.Decrypt(row.Value, ValuePurpose));
        await accessLog.RecordAsync(Identifiers, row.Id, ["value"], "view", 1, cancellationToken).ConfigureAwait(false); // before the answer (GHR-02)
        return new PeopleResult(PeopleOutcome.Done, Body: new { id = row.Id, kind = row.Kind, countryCode = row.CountryCode, value });
    }

    /// <summary>Sets kind, country, value and dates; validates against the identifier catalogue (ruling Q3).</summary>
    private bool ApplyIdentifier(PersonIdentifier row, IdentifierInput input, BuildingBlocks.Application.Security.IFieldEncryptor enc, FieldErrors errors, string prefix, bool creating)
    {
        var failed = false;
        void Fail(string field, string key)
        {
            errors.Add(prefix + field, key);
            failed = true;
        }

        var kindCode = creating ? input.Kind?.Trim() : input.Kind?.Trim() ?? row.Kind;
        var country = creating ? input.CountryCode?.Trim().ToUpperInvariant() : input.CountryCode?.Trim().ToUpperInvariant() ?? row.CountryCode;
        if (string.IsNullOrEmpty(kindCode))
        {
            Fail("kind", Required);
        }

        if (string.IsNullOrEmpty(country) || country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            Fail("countryCode", string.IsNullOrEmpty(country) ? Required : UnknownCode);
        }

        var kind = failed ? null : Catalogue.Find(kindCode!, country!);
        if (!failed && kind is null)
        {
            Fail("kind", IdentifierKindUnknown);
        }

        if (kind is not null && !creating && input.Value is null && (kindCode != row.Kind || country != row.CountryCode))
        {
            Fail("value", Required); // a different kind or country is a different identifier: its value must be given again
        }

        if (creating || input.Value is not null)
        {
            var value = input.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                Fail("value", Required);
            }
            else if (kind is not null)
            {
                if (!Catalogue.Matches(kind, value))
                {
                    Fail("value", IdentifierFormat);
                }
                else
                {
                    var normal = PeopleCatalogue.Normalize(value);
                    row.Value = enc.Encrypt(Utf8(PeopleCatalogue.Display(kind, value)), ValuePurpose);
                    row.ValueHmac = enc.BlindIndex(Utf8(normal), ValuePurpose);
                    row.ValueLast4 = normal.Length <= 4 ? normal : normal[^4..];
                }
            }
        }

        if (input.IssueDate is { } issue)
        {
            row.IssueDate = issue;
        }

        if (input.ExpiryDate is { } expiry)
        {
            row.ExpiryDate = expiry;
        }

        if (input.FileId is { } file)
        {
            row.FileId = file;
        }

        if (kind is not null)
        {
            if (!kind.HasExpiry && row.ExpiryDate is not null)
            {
                Fail("expiryDate", IdentifierNoExpiry);
            }

            if (row.IssueDate is { } i && row.ExpiryDate is { } e && e < i)
            {
                Fail("expiryDate", DateOrder);
            }

            if (row.IssueDate > Today)
            {
                Fail("issueDate", FutureDate);
            }

            row.Kind = kind.Code;
            row.CountryCode = country!;
        }

        return !failed;
    }

    private static List<(string Key, bool Changed)> IdentifierChanges(IdentifierInput input) =>
    [
        ("kind", input.Kind is not null), ("country_code", input.CountryCode is not null), ("value_last4", input.Value is not null),
        ("issue_date", input.IssueDate is not null), ("expiry_date", input.ExpiryDate is not null), ("file_id", input.FileId is not null),
    ];

    /// <summary>The person already holding the identifier (same kind, country and blind index), other than this row's own record.</summary>
    private async Task<Person?> IdentifierOwnerAsync(PersonIdentifier row, CancellationToken ct)
    {
        var owner = await db.Set<PersonIdentifier>().AsNoTracking()
            .Where(i => i.Kind == row.Kind && i.CountryCode == row.CountryCode && i.ValueHmac == row.ValueHmac && i.Id != row.Id)
            .Select(i => (Guid?)i.PersonId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return owner is { } id ? await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false) : null;
    }

    private async Task<PeopleResult?> DuplicateIdentifierAsync(PersonIdentifier row, CancellationToken ct) =>
        await IdentifierOwnerAsync(row, ct).ConfigureAwait(false) is { } owner
            ? PeopleResult.Conflict(IdentifierDuplicate, new Dictionary<string, object?> { ["matches"] = new[] { await MatchAsync(owner, "identifier", ct).ConfigureAwait(false) } })
            : null;

    // ================================================================== contacts

    public Task<PeopleResult> AddContactAsync(Guid personId, ContactInput input, CancellationToken cancellationToken) =>
        AddSectionAsync(personId, Contacts, PersonContactAccess.Scope, PersonContactAccess.Fields,
            errors => ApplyContact(new PersonContact { PersonId = personId, Kind = string.Empty, Value = string.Empty }, input, errors, string.Empty, creating: true),
            _ => ContactChanges(input), row => OnePrimaryAsync(row, cancellationToken), cancellationToken);

    public Task<PeopleResult> UpdateContactAsync(Guid personId, Guid contactId, ContactInput input, CancellationToken cancellationToken) =>
        UpdateSectionAsync<PersonContact>(personId, contactId, Contacts, PersonContactAccess.Scope, PersonContactAccess.Fields, r => r.PersonId, input.RowVersion,
            (row, errors) => ApplyContact(row, input, errors, string.Empty, creating: false) is not null,
            ContactChanges(input), row => OnePrimaryAsync(row, cancellationToken), cancellationToken);

    public Task<PeopleResult> DeleteContactAsync(Guid personId, Guid contactId, long? rowVersion, CancellationToken cancellationToken) =>
        DeleteSectionAsync<PersonContact>(personId, contactId, Contacts, PersonContactAccess.Scope, r => r.PersonId, rowVersion, cancellationToken);

    private static PersonContact ApplyContact(PersonContact row, ContactInput input, FieldErrors errors, string prefix, bool creating)
    {
        if (creating || input.Kind is not null)
        {
            if (input.Kind is null || !ContactKinds.Contains(input.Kind, StringComparer.Ordinal))
            {
                errors.Add(prefix + "kind", input.Kind is null ? Required : UnknownCode);
            }
            else
            {
                row.Kind = input.Kind;
            }
        }

        if (creating || input.Value is not null)
        {
            row.Value = TextOf(input.Value, prefix + "value", NameLength, errors, required: true) ?? row.Value;
        }

        if (input.IsPrimary is { } primary)
        {
            row.IsPrimary = primary;
        }

        return row;
    }

    private static List<(string Key, bool Changed)> ContactChanges(ContactInput input) =>
        [("kind", input.Kind is not null), ("value", input.Value is not null), ("is_primary", input.IsPrimary is not null)];

    /// <summary>One primary contact per kind: a new primary takes the flag from the person's other contacts of that kind.</summary>
    private async Task<PeopleResult?> OnePrimaryAsync(PersonContact row, CancellationToken ct)
    {
        if (!row.IsPrimary)
        {
            return null;
        }

        var others = await db.Set<PersonContact>().Where(c => c.PersonId == row.PersonId && c.Kind == row.Kind && c.IsPrimary && c.Id != row.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var other in others)
        {
            if (!await AllowsAsync(other, PersonContactAccess.Scope, "update", ct).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            other.IsPrimary = false;
            Touch(other, other.RowVersion);
        }

        return null;
    }

    // ================================================================== addresses (effective-dated by kind)

    public Task<PeopleResult> AddAddressAsync(Guid personId, AddressInput input, CancellationToken cancellationToken) =>
        AddSectionAsync(personId, Addresses, PersonAddressAccess.Scope, PersonAddressAccess.Fields,
            errors => ApplyAddress(new PersonAddress { PersonId = personId, Kind = string.Empty, Address = null! }, input, errors, string.Empty, creating: true),
            _ => [("kind", true), ("address", true), ("valid_from", true)], row => FitAddressAsync(row, cancellationToken), cancellationToken);

    /// <summary>Corrects an address's text; its kind and start date stay (a move is a new address from its date).</summary>
    public Task<PeopleResult> UpdateAddressAsync(Guid personId, Guid addressId, AddressInput input, CancellationToken cancellationToken) =>
        UpdateSectionAsync<PersonAddress>(personId, addressId, Addresses, PersonAddressAccess.Scope, PersonAddressAccess.Fields, r => r.PersonId, input.RowVersion,
            (row, errors) =>
            {
                if ((input.Kind is not null && input.Kind != row.Kind) || (input.ValidFrom is not null && input.ValidFrom != row.ValidFrom))
                {
                    errors.Add("validFrom", AddressFixed);
                    return false;
                }

                ApplyAddress(row, input, errors, string.Empty, creating: false);
                return true;
            },
            [("address", input.Address is not null)], null, cancellationToken);

    /// <summary>
    /// Removes an address; the address of the same kind before it then runs on to the removed one's end, so the history keeps no
    /// gap (GHR-03; review S1.20a finding 10).
    /// </summary>
    public Task<PeopleResult> DeleteAddressAsync(Guid personId, Guid addressId, long? rowVersion, CancellationToken cancellationToken) =>
        DeleteSectionAsync<PersonAddress>(personId, addressId, Addresses, PersonAddressAccess.Scope, r => r.PersonId, rowVersion, cancellationToken, async row =>
        {
            var before = await db.Set<PersonAddress>().FirstOrDefaultAsync(a => a.PersonId == row.PersonId && a.Kind == row.Kind && a.ValidTo == row.ValidFrom && a.Id != row.Id, cancellationToken).ConfigureAwait(false);
            if (before is null)
            {
                return null;
            }

            if (!await AllowsAsync(before, PersonAddressAccess.Scope, "update", cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            before.ValidTo = row.ValidTo;
            Touch(before, before.RowVersion);
            return null;
        });

    private static PersonAddress ApplyAddress(PersonAddress row, AddressInput input, FieldErrors errors, string prefix, bool creating)
    {
        if (creating)
        {
            if (input.Kind is null || !AddressKinds.Contains(input.Kind, StringComparer.Ordinal))
            {
                errors.Add(prefix + "kind", input.Kind is null ? Required : UnknownCode);
            }
            else
            {
                row.Kind = input.Kind;
            }

            if (input.ValidFrom is { } from)
            {
                row.ValidFrom = from;
            }
            else
            {
                errors.Add(prefix + "validFrom", Required);
            }
        }

        if (creating || input.Address is not null)
        {
            if (input.Address is not { ValueKind: JsonValueKind.Object } address || !address.EnumerateObject().Any())
            {
                errors.Add(prefix + "address", ObjectRequired);
            }
            else
            {
                row.Address = JsonDocument.Parse(address.GetRawText());
            }
        }

        return row;
    }

    /// <summary>
    /// Fits a new address into the history of its kind (GHR-03): the address valid on the new start date ends the day before; the
    /// new one runs until the next address of the kind starts (or open-ended). Two addresses of a kind never overlap (exclusion
    /// constraint); one starting on the same day is refused (correct that one instead).
    /// </summary>
    private async Task<PeopleResult?> FitAddressAsync(PersonAddress row, CancellationToken ct)
    {
        var history = await db.Set<PersonAddress>().Where(a => a.PersonId == row.PersonId && a.Kind == row.Kind && a.Id != row.Id).ToListAsync(ct).ConfigureAwait(false);
        if (history.Any(a => a.ValidFrom == row.ValidFrom))
        {
            return PeopleResult.Conflict(AddressSameStart);
        }

        var covering = history.FirstOrDefault(a => a.ValidFrom < row.ValidFrom && (a.ValidTo is null || a.ValidTo > row.ValidFrom));
        var next = history.Where(a => a.ValidFrom > row.ValidFrom).OrderBy(a => a.ValidFrom).FirstOrDefault();
        row.ValidTo = covering?.ValidTo is { } coveringEnd && (next is null || coveringEnd < next.ValidFrom) ? coveringEnd : next?.ValidFrom;
        if (covering is not null)
        {
            if (!await AllowsAsync(covering, PersonAddressAccess.Scope, "update", ct).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            covering.ValidTo = row.ValidFrom;
            Touch(covering, covering.RowVersion);
        }

        return null;
    }

    // ================================================================== relations (dependents and emergency contacts, BR-03)

    public async Task<PeopleResult> AddRelationAsync(Guid personId, RelationInput input, CancellationToken cancellationToken)
    {
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        return await AddSectionAsync(personId, Relations, PersonRelationAccess.Scope, PersonRelationAccess.Fields,
            errors => ApplyRelation(new PersonRelation { PersonId = personId, Relation = string.Empty, FullName = string.Empty }, input, enc, errors, string.Empty, creating: true),
            _ => RelationChanges(input), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateRelationAsync(Guid personId, Guid relationId, RelationInput input, CancellationToken cancellationToken)
    {
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateSectionAsync<PersonRelation>(personId, relationId, Relations, PersonRelationAccess.Scope, PersonRelationAccess.Fields, r => r.PersonId, input.RowVersion,
            (row, errors) => ApplyRelation(row, input, enc, errors, string.Empty, creating: false) is not null,
            RelationChanges(input), null, cancellationToken).ConfigureAwait(false);
    }

    public Task<PeopleResult> DeleteRelationAsync(Guid personId, Guid relationId, long? rowVersion, CancellationToken cancellationToken) =>
        DeleteSectionAsync<PersonRelation>(personId, relationId, Relations, PersonRelationAccess.Scope, r => r.PersonId, rowVersion, cancellationToken);

    private PersonRelation ApplyRelation(PersonRelation row, RelationInput input, BuildingBlocks.Application.Security.IFieldEncryptor enc, FieldErrors errors, string prefix, bool creating)
    {
        if (creating || input.Relation is not null)
        {
            if (input.Relation is null || !Catalogue.Relations.Any(r => r.Code == input.Relation))
            {
                errors.Add(prefix + "relation", input.Relation is null ? Required : UnknownCode);
            }
            else
            {
                row.Relation = input.Relation;
            }
        }

        if (creating || input.FullName is not null)
        {
            row.FullName = TextOf(input.FullName, prefix + "fullName", NameLength, errors, required: true) ?? row.FullName;
        }

        if (input.DateOfBirth is { } birth)
        {
            if (birth > Today)
            {
                errors.Add(prefix + "dateOfBirth", FutureDate);
            }

            row.DateOfBirth = birth;
        }

        if (input.Gender is { } gender)
        {
            if (!Genders.Contains(gender, StringComparer.Ordinal))
            {
                errors.Add(prefix + "gender", UnknownCode);
            }

            row.Gender = gender;
        }

        if (input.Identifier is { } identifier)
        {
            var text = TextOf(identifier, prefix + "identifier", NameLength, errors, required: false);
            row.Identifier = text is null ? null : enc.Encrypt(Utf8(text), RelationIdentifierPurpose);
        }

        if (input.Phone is { } phone)
        {
            row.Phone = TextOf(phone, prefix + "phone", NameLength, errors, required: false);
        }

        if (input.IsDependent is { } dependent)
        {
            row.IsDependent = dependent;
        }

        if (input.IsEmergencyContact is { } emergency)
        {
            row.IsEmergencyContact = emergency;
        }

        return row;
    }

    private static List<(string Key, bool Changed)> RelationChanges(RelationInput input) =>
    [
        ("relation", input.Relation is not null), ("full_name", input.FullName is not null), ("date_of_birth", input.DateOfBirth is not null),
        ("gender", input.Gender is not null), ("phone", input.Phone is not null), ("is_dependent", input.IsDependent is not null),
        ("is_emergency_contact", input.IsEmergencyContact is not null),
    ];

    // ================================================================== qualifications

    public Task<PeopleResult> AddQualificationAsync(Guid personId, QualificationInput input, CancellationToken cancellationToken) =>
        AddSectionAsync(personId, Qualifications, PersonQualificationAccess.Scope, PersonQualificationAccess.Fields,
            errors => ApplyQualification(new PersonQualification { PersonId = personId, Kind = string.Empty, Title = string.Empty, Details = null! }, input, errors, string.Empty, creating: true),
            _ => QualificationChanges(input), null, cancellationToken);

    public Task<PeopleResult> UpdateQualificationAsync(Guid personId, Guid qualificationId, QualificationInput input, CancellationToken cancellationToken) =>
        UpdateSectionAsync<PersonQualification>(personId, qualificationId, Qualifications, PersonQualificationAccess.Scope, PersonQualificationAccess.Fields, r => r.PersonId, input.RowVersion,
            (row, errors) => ApplyQualification(row, input, errors, string.Empty, creating: false) is not null,
            QualificationChanges(input), null, cancellationToken);

    public Task<PeopleResult> DeleteQualificationAsync(Guid personId, Guid qualificationId, long? rowVersion, CancellationToken cancellationToken) =>
        DeleteSectionAsync<PersonQualification>(personId, qualificationId, Qualifications, PersonQualificationAccess.Scope, r => r.PersonId, rowVersion, cancellationToken);

    private static PersonQualification ApplyQualification(PersonQualification row, QualificationInput input, FieldErrors errors, string prefix, bool creating)
    {
        if (creating || input.Kind is not null)
        {
            if (input.Kind is null || !QualificationKinds.Contains(input.Kind, StringComparer.Ordinal))
            {
                errors.Add(prefix + "kind", input.Kind is null ? Required : UnknownCode);
            }
            else
            {
                row.Kind = input.Kind;
            }
        }

        if (creating || input.Title is not null)
        {
            row.Title = TextOf(input.Title, prefix + "title", TextLength, errors, required: true) ?? row.Title;
        }

        if (input.Institution is not null)
        {
            row.Institution = TextOf(input.Institution, prefix + "institution", TextLength, errors, required: false);
        }

        if (input.GradeOrLevel is not null)
        {
            row.GradeOrLevel = TextOf(input.GradeOrLevel, prefix + "gradeOrLevel", NameLength, errors, required: false);
        }

        row.StartDate = input.StartDate ?? row.StartDate;
        row.EndDate = input.EndDate ?? row.EndDate;
        row.ExpiryDate = input.ExpiryDate ?? row.ExpiryDate;
        row.FileId = input.FileId ?? row.FileId;
        if (row.StartDate is { } s && row.EndDate is { } e && e < s)
        {
            errors.Add(prefix + "endDate", DateOrder);
        }

        if (input.Details is { } details)
        {
            if (details.ValueKind != JsonValueKind.Object)
            {
                errors.Add(prefix + "details", ObjectRequired);
            }
            else
            {
                row.Details = JsonDocument.Parse(details.GetRawText());
            }
        }
        else if (creating)
        {
            row.Details = JsonDocument.Parse("{}");
        }

        return row;
    }

    private static List<(string Key, bool Changed)> QualificationChanges(QualificationInput input) =>
    [
        ("kind", input.Kind is not null), ("title", input.Title is not null), ("institution", input.Institution is not null),
        ("start_date", input.StartDate is not null), ("end_date", input.EndDate is not null), ("expiry_date", input.ExpiryDate is not null),
        ("grade_or_level", input.GradeOrLevel is not null), ("details", input.Details is not null), ("file_id", input.FileId is not null),
    ];

    // ================================================================== section plumbing

    /// <summary>The person a section change belongs to: readable by the caller and not retired (null answer: go on).</summary>
    private async Task<(Person? Person, PeopleResult? Refusal)> OwnerForSectionAsync(Guid personId, CancellationToken ct)
    {
        var person = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId && !p.IsDeleted, ct).ConfigureAwait(false);
        if (person is null || !await AllowsAsync(person, PersonAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return person.MergedIntoPersonId is not null ? (null, PeopleResult.Conflict(Retired)) : (person, null);
    }

    private async Task<PeopleResult> AddSectionAsync<T>(
        Guid personId, string table, ScopeTarget<T> target, FieldCatalog catalog, Func<FieldErrors, T?> build,
        Func<T, IEnumerable<(string Key, bool Changed)>> changes, Func<T, Task<PeopleResult?>>? beforeSave, CancellationToken ct)
        where T : class, IEntity, IVersioned
    {
        if (!await HasAsync(PeoplePermissions.Of(table, "create"), ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (_, refusal) = await OwnerForSectionAsync(personId, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var errors = new FieldErrors();
        var row = build(errors);
        if (errors.Any || row is null)
        {
            return PeopleResult.Fields(errors.ToDictionary());
        }

        Stamp(row);
        if (!await AllowsAsync(row, target, "create", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var denied = await NotEditableAsync(catalog, target, "create", row, changes(row), ct).ConfigureAwait(false);
        if (denied.Count > 0)
        {
            return NotEditable(denied);
        }

        if (beforeSave is not null && await beforeSave(row).ConfigureAwait(false) is { } problem)
        {
            db.ChangeTracker.Clear();
            return problem;
        }

        db.Add(row);
        return await SaveAsync(ct).ConfigureAwait(false) ?? new PeopleResult(PeopleOutcome.Created, Body: new { id = row.Id, rowVersion = row.RowVersion });
    }

    private async Task<PeopleResult> UpdateSectionAsync<T>(
        Guid personId, Guid rowId, string table, ScopeTarget<T> target, FieldCatalog catalog, Func<T, Guid> personOf, long? rowVersion,
        Func<T, FieldErrors, bool> apply, IEnumerable<(string Key, bool Changed)> changes, Func<T, Task<PeopleResult?>>? beforeSave, CancellationToken ct)
        where T : class, IEntity, IVersioned
    {
        if (!await HasAsync(PeoplePermissions.Of(table, "update"), ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (rowVersion is not { } expected)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        var (_, refusal) = await OwnerForSectionAsync(personId, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var row = await db.Set<T>().FirstOrDefaultAsync(e => e.Id == rowId, ct).ConfigureAwait(false);
        if (row is null || personOf(row) != personId || row is ISoftDeletable { IsDeleted: true }
            || !await AllowsAsync(row, target, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await AllowsAsync(row, target, "update", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            return PeopleResult.Conflict(Concurrency);
        }

        var denied = await NotEditableAsync(catalog, target, "update", row, changes, ct).ConfigureAwait(false);
        if (denied.Count > 0)
        {
            return NotEditable(denied);
        }

        var errors = new FieldErrors();
        if (!apply(row, errors) || errors.Any)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors.ToDictionary());
        }

        if (beforeSave is not null && await beforeSave(row).ConfigureAwait(false) is { } problem)
        {
            db.ChangeTracker.Clear();
            return problem;
        }

        Touch(row, expected);
        return await SaveAsync(ct).ConfigureAwait(false) ?? Done(row.Id, row.RowVersion);
    }

    private async Task<PeopleResult> DeleteSectionAsync<T>(Guid personId, Guid rowId, string table, ScopeTarget<T> target, Func<T, Guid> personOf, long? rowVersion, CancellationToken ct,
        Func<T, Task<PeopleResult?>>? beforeDelete = null)
        where T : class, IEntity, IVersioned
    {
        if (!await HasAsync(PeoplePermissions.Of(table, "delete"), ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (rowVersion is not { } expected)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        var (_, refusal) = await OwnerForSectionAsync(personId, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var row = await db.Set<T>().FirstOrDefaultAsync(e => e.Id == rowId, ct).ConfigureAwait(false);
        if (row is null || personOf(row) != personId || row is ISoftDeletable { IsDeleted: true }
            || !await AllowsAsync(row, target, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await AllowsAsync(row, target, "delete", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            return PeopleResult.Conflict(Concurrency);
        }

        if (beforeDelete is not null && await beforeDelete(row).ConfigureAwait(false) is { } problem)
        {
            db.ChangeTracker.Clear();
            return problem;
        }

        if (row is ISoftDeletable soft)
        {
            soft.IsDeleted = true;
            soft.DeletedAt = clock.UtcNow;
            soft.DeletedBy = actor.UserId;
            Touch(row, expected);
        }
        else
        {
            db.Remove(row); // the change log keeps the removed row (S1.07)
        }

        return await SaveAsync(ct).ConfigureAwait(false) ?? new PeopleResult(PeopleOutcome.NoContent);
    }
}
