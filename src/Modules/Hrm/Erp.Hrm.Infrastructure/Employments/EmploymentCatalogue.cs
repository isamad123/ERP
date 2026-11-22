using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>One event kind with its reason codes.</summary>
public sealed record EventKindEntry(string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyList<CatalogueCode> Reasons);

/// <summary>
/// The provisional employment catalogue (slice S1.20b, CR-076 S1.20b detail): employee types and the reason codes of each event
/// kind, read from the embedded <c>employment-catalogue.json</c>. Data, not code: the customer option lists of S1.15b replace it.
/// </summary>
public sealed class EmploymentCatalogue
{
    private EmploymentCatalogue(string status, IReadOnlyList<CatalogueCode> employeeTypes, IReadOnlyList<EventKindEntry> kinds)
    {
        Status = status;
        EmployeeTypes = employeeTypes;
        Kinds = kinds;
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static EmploymentCatalogue Default { get; } = Load();

    /// <summary>
    /// The kinds the change service records (<c>POST /api/hrm/employments/{id}/events</c>); hire, rehire, transfer between companies,
    /// probation, pay and separation have their own services.
    /// </summary>
    public static IReadOnlyList<string> ChangeKinds { get; } = ["promotion", "demotion", "transfer", "grade_change", "acting", "secondment", "confirmation", "data_correction"];

    public string Status { get; }

    public IReadOnlyList<CatalogueCode> EmployeeTypes { get; }

    public IReadOnlyList<EventKindEntry> Kinds { get; }

    public bool IsEmployeeType(string? code) => code is not null && EmployeeTypes.Any(t => t.Code == code);

    public bool IsReason(string kind, string? reason) =>
        reason is not null && Kinds.FirstOrDefault(k => k.Code == kind) is { } entry && entry.Reasons.Any(r => r.Code == reason);

    private static EmploymentCatalogue Load()
    {
        using var stream = typeof(EmploymentCatalogue).Assembly.GetManifestResourceStream("Erp.Hrm.Infrastructure.Employments.employment-catalogue.json")
            ?? throw new InvalidOperationException("employment-catalogue.json is not embedded.");
        var file = JsonSerializer.Deserialize<CatalogueFile>(stream, Options) ?? throw new InvalidDataException("employment-catalogue.json is empty.");
        return new EmploymentCatalogue(file.Status ?? "provisional", file.EmployeeTypes, file.Kinds);
    }

    private sealed class CatalogueFile
    {
        public string? Status { get; init; }

        public List<CatalogueCode> EmployeeTypes { get; init; } = [];

        public List<EventKindEntry> Kinds { get; init; } = [];
    }
}
