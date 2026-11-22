using System.Text.Json;

namespace Erp.Hrm.Application.People;

/// <summary>The person fields of a create or a change (FR-HRM-EMP-01). Null leaves a field as it is on a change.</summary>
public class PersonFields
{
    public string? FirstName { get; init; }

    public string? MiddleName { get; init; }

    public string? LastName { get; init; }

    /// <summary>The name in Urdu script.</summary>
    public string? NameLocal { get; init; }

    public string? FatherOrSpouseName { get; init; }

    /// <summary><c>male</c>, <c>female</c>, <c>other</c> or <c>unspecified</c>.</summary>
    public string? Gender { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    /// <summary>A code of the marital status list of the person catalogue.</summary>
    public string? MaritalStatus { get; init; }

    public string? NationalityCode { get; init; }

    /// <summary>Accepted only where a country pack asks for it (none before S1.23): refused otherwise.</summary>
    public string? Religion { get; init; }

    public Guid? PhotoFileId { get; init; }

    /// <summary>Custom field values (S1.15b checks them); only an empty object is accepted until then.</summary>
    public JsonElement? Custom { get; init; }

    /// <summary>Fields to clear on a change (camelCase names of the optional fields above).</summary>
    public IReadOnlyList<string>? Clear { get; init; }
}

/// <summary>A new person with its first identifiers and sections, created in one transaction.</summary>
public sealed class CreatePersonRequest : PersonFields
{
    public IReadOnlyList<IdentifierInput>? Identifiers { get; init; }

    public IReadOnlyList<ContactInput>? Contacts { get; init; }

    public IReadOnlyList<AddressInput>? Addresses { get; init; }

    public IReadOnlyList<RelationInput>? Relations { get; init; }

    public IReadOnlyList<QualificationInput>? Qualifications { get; init; }

    /// <summary>
    /// The answer to possible duplicates (same name and date of birth, FR-HRM-EMP-18): null asks (the create is refused and the
    /// matches listed), <c>distinct</c> confirms a different person (recorded), <c>review</c> creates and sends the matches to the
    /// review queue. An identical identifier is always refused.
    /// </summary>
    public string? Duplicates { get; init; }
}

public sealed class UpdatePersonRequest : PersonFields
{
    public long? RowVersion { get; init; }
}

public sealed class IdentifierInput
{
    public string? Kind { get; init; }

    public string? CountryCode { get; init; }

    /// <summary>The identifier as typed; stored encrypted, looked up by its blind index, shown as its last four characters.</summary>
    public string? Value { get; init; }

    public DateOnly? IssueDate { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public Guid? FileId { get; init; }

    public long? RowVersion { get; init; }
}

public sealed class ContactInput
{
    public string? Kind { get; init; }

    public string? Value { get; init; }

    public bool? IsPrimary { get; init; }

    public long? RowVersion { get; init; }
}

public sealed class AddressInput
{
    public string? Kind { get; init; }

    public JsonElement? Address { get; init; }

    /// <summary>The day the address applies from (effective-dated by kind, GHR-03).</summary>
    public DateOnly? ValidFrom { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>A family member, dependent or emergency contact (BR-03: each a separate record).</summary>
public sealed class RelationInput
{
    public string? Relation { get; init; }

    public string? FullName { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public string? Gender { get; init; }

    /// <summary>Their identity number; stored encrypted and never shown back.</summary>
    public string? Identifier { get; init; }

    public string? Phone { get; init; }

    public bool? IsDependent { get; init; }

    public bool? IsEmergencyContact { get; init; }

    public long? RowVersion { get; init; }
}

public sealed class QualificationInput
{
    public string? Kind { get; init; }

    public string? Title { get; init; }

    public string? Institution { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public string? GradeOrLevel { get; init; }

    public JsonElement? Details { get; init; }

    public Guid? FileId { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>Merge the person of the route into <see cref="IntoPersonId"/> (FR-HRM-EMP-18, ruling Q4).</summary>
public sealed record MergeRequest(Guid? IntoPersonId, string? Reason);

/// <summary>Close a review case as two different persons.</summary>
public sealed record DecideCaseRequest(string? Note, long? RowVersion);

public enum PeopleOutcome
{
    Done,
    Created,
    Pending,
    NoContent,
    Invalid,
    Forbidden,
    NotFound,
    Conflict,
}

/// <summary>
/// The answer of a person service: the outcome, a localization key for refusals, the body of a success, field errors (field name
/// to keys) and extra problem members (the matches of a duplicate refusal).
/// </summary>
public sealed record PeopleResult(
    PeopleOutcome Outcome,
    string? Error = null,
    object? Body = null,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    public static PeopleResult NotFound { get; } = new(PeopleOutcome.NotFound, "errors.data.not_found");

    public static PeopleResult Forbidden(string error = "errors.access.forbidden") => new(PeopleOutcome.Forbidden, error);

    public static PeopleResult Invalid(string error) => new(PeopleOutcome.Invalid, error);

    public static PeopleResult Fields(IReadOnlyDictionary<string, string[]> errors) => new(PeopleOutcome.Invalid, "errors.data.invalid_request", FieldErrors: errors);

    public static PeopleResult Conflict(string error, IReadOnlyDictionary<string, object?>? extensions = null) => new(PeopleOutcome.Conflict, error, Extensions: extensions);
}

/// <summary>One entry of the person catalogue (identifier kinds, marital statuses, relations): a code and its names.</summary>
public sealed record CatalogueCode(string Code, IReadOnlyDictionary<string, string> Name);

/// <summary>
/// One identifier kind (FR-HRM-EMP-05, -16; ruling Q3): format rule and expiry as pack data. <see cref="Country"/> is the
/// country whose pack defines it, or <c>*</c> for a kind issued by any country (a passport).
/// </summary>
public sealed record IdentifierKind(
    string Code,
    string Country,
    IReadOnlyDictionary<string, string> Name,
    string Pattern,
    string Example,
    bool HasExpiry,
    string Status);

/// <summary>The person services (slice S1.20a Build items 1 to 4).</summary>
public interface IPersonService
{
    Task<PeopleResult> CreateAsync(CreatePersonRequest request, string source, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid personId, UpdatePersonRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> AddIdentifierAsync(Guid personId, IdentifierInput input, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateIdentifierAsync(Guid personId, Guid identifierId, IdentifierInput input, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteIdentifierAsync(Guid personId, Guid identifierId, long? rowVersion, CancellationToken cancellationToken);

    /// <summary>The full value of an identifier, for holders of the reveal permission; written to the access log first.</summary>
    Task<PeopleResult> RevealIdentifierAsync(Guid personId, Guid identifierId, CancellationToken cancellationToken);

    Task<PeopleResult> AddContactAsync(Guid personId, ContactInput input, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateContactAsync(Guid personId, Guid contactId, ContactInput input, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteContactAsync(Guid personId, Guid contactId, long? rowVersion, CancellationToken cancellationToken);

    Task<PeopleResult> AddAddressAsync(Guid personId, AddressInput input, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAddressAsync(Guid personId, Guid addressId, AddressInput input, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteAddressAsync(Guid personId, Guid addressId, long? rowVersion, CancellationToken cancellationToken);

    Task<PeopleResult> AddRelationAsync(Guid personId, RelationInput input, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateRelationAsync(Guid personId, Guid relationId, RelationInput input, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteRelationAsync(Guid personId, Guid relationId, long? rowVersion, CancellationToken cancellationToken);

    Task<PeopleResult> AddQualificationAsync(Guid personId, QualificationInput input, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateQualificationAsync(Guid personId, Guid qualificationId, QualificationInput input, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteQualificationAsync(Guid personId, Guid qualificationId, long? rowVersion, CancellationToken cancellationToken);

    /// <summary>Review cases of possible duplicates the caller may see (status <c>open</c> by default), oldest first.</summary>
    Task<PeopleResult> DuplicatesAsync(string? status, Guid? after, int? limit, CancellationToken cancellationToken);

    Task<PeopleResult> MarkDistinctAsync(Guid caseId, DecideCaseRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> MergeAsync(Guid personId, MergeRequest request, CancellationToken cancellationToken);

    /// <summary>The identifier kinds, marital statuses and relations (provisional Pakistan catalogue until S1.23).</summary>
    Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken);
}
