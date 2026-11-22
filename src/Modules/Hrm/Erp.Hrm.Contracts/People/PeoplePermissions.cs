namespace Erp.Hrm.Contracts.People;

/// <summary>
/// Permissions of the person services (slice S1.20a, CR-076). The generated writes of the person tables are closed (ruling Q1),
/// so their create, update and delete actions are declared by hand in <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>.
/// Read stays the generated <c>&lt;table&gt;.read</c>.
/// </summary>
public static class PeoplePermissions
{
    public const string PersonsRead = "hrm.persons.read";
    public const string PersonsCreate = "hrm.persons.create";
    public const string PersonsUpdate = "hrm.persons.update";

    /// <summary>Merge a duplicate person into another (FR-HRM-EMP-18); also needed to approve or reject a held merge. Sensitive.</summary>
    public const string PersonsMerge = "hrm.persons.merge";

    public const string IdentifiersRead = "hrm.person_identifiers.read";
    public const string IdentifiersCreate = "hrm.person_identifiers.create";
    public const string IdentifiersUpdate = "hrm.person_identifiers.update";
    public const string IdentifiersDelete = "hrm.person_identifiers.delete";

    /// <summary>See an identifier's full value (GHR-02: masked by default); every use is in the access log. Sensitive.</summary>
    public const string IdentifiersReveal = "hrm.person_identifiers.reveal";

    public const string DuplicatesRead = "hrm.person_duplicate_cases.read";
    public const string DuplicatesUpdate = "hrm.person_duplicate_cases.update";

    /// <summary>The create, update and delete permission of a person section table (<c>hrm.person_contacts</c> and so on).</summary>
    public static string Of(string table, string action) => table + "." + action;

    /// <summary>Every code declared in permissions.json (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } =
    [
        PersonsCreate, PersonsUpdate, PersonsMerge, IdentifiersCreate, IdentifiersUpdate, IdentifiersDelete, IdentifiersReveal,
        "hrm.person_contacts.create", "hrm.person_contacts.update", "hrm.person_contacts.delete",
        "hrm.person_addresses.create", "hrm.person_addresses.update", "hrm.person_addresses.delete",
        "hrm.person_relations.create", "hrm.person_relations.update", "hrm.person_relations.delete",
        "hrm.person_qualifications.create", "hrm.person_qualifications.update", "hrm.person_qualifications.delete",
        DuplicatesUpdate,
    ];
}
