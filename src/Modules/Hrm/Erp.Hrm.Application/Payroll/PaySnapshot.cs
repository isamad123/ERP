using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// Everything the engine uses to calculate one employee in one run, frozen when the snapshot is taken (slice S1.35 Build item 3, FR-HRM-PAY-31, ruling
/// Q4): the run and its period, the employment and the days it is employed in the run's pay group, its assignments, the planned non-working days, the
/// compensation rows with their lines, the salary structure rows, the components with their versions, the tables a lookup reads, the inputs of every
/// provider, the attendance policy versions behind the attendance results and each statutory calculator's own frozen data. Ids, dates, codes and
/// amounts only: never an identifier, a name or a bank number. A calculation reads nothing else, so the same snapshot gives the same result.
/// </summary>
public sealed record PaySnapshot(
    string EngineVersion,
    SnapshotRun Run,
    SnapshotEmployment Employment,
    IReadOnlyList<SnapshotAssignment> Assignments,
    SnapshotDays Days,
    IReadOnlyList<SnapshotCompensation> Compensation,
    IReadOnlyList<SnapshotStructureRow> StructureRows,
    IReadOnlyList<SnapshotComponent> Components,
    IReadOnlyList<SnapshotTable> Tables,
    IReadOnlyList<PayInputItem> Inputs,
    IReadOnlyList<SnapshotPolicy> Policies,
    IReadOnlyDictionary<string, JsonElement> Statutory);

/// <summary>The run: its kind, company, pay group, period (dates, tax year, number), currency with its minor unit, and the rounding mode.</summary>
public sealed record SnapshotRun(
    Guid RunId, string Kind, Guid CompanyId, Guid PayGroupId, Guid PayPeriodId, int TaxYear, int PeriodNo, DateOnly PeriodStart, DateOnly PeriodEnd,
    string CurrencyCode, int CurrencyDecimals, string RoundingMode);

/// <summary>An inclusive range of dates.</summary>
public sealed record DateRange(DateOnly From, DateOnly To);

/// <summary>The employment and the ranges of the period on which it is employed in the run's pay group.</summary>
public sealed record SnapshotEmployment(Guid EmploymentId, string EmployeeType, DateOnly JoiningDate, DateOnly? SeparationDate, IReadOnlyList<DateRange> Employed);

/// <summary>An assignment in force during the period (ids only; the cost allocation as stored, for the journal).</summary>
public sealed record SnapshotAssignment(
    Guid Id, DateOnly ValidFrom, DateOnly? ValidTo, Guid OrgUnitId, Guid BranchId, Guid? PositionId, Guid? GradeId, Guid? LocationId, Guid? PayGroupId, JsonElement CostAllocation);

/// <summary>
/// The plan of the period: whether it was read (only when a component counts working days) and the planned weekly-off and holiday dates
/// (from the locked attendance days, else the effective shift; a day nothing plans counts as working, R-301).
/// </summary>
public sealed record SnapshotDays(bool PlanRead, IReadOnlyList<DateOnly> NonWorking);

/// <summary>A compensation row in force during the period (its end exclusive) with the amount, percent and units of each component.</summary>
public sealed record SnapshotCompensation(Guid AssignmentId, Guid StructureId, DateOnly ValidFrom, DateOnly? ValidTo, string CurrencyCode, IReadOnlyList<SnapshotCompensationLine> Lines);

public sealed record SnapshotCompensationLine(Guid ComponentId, decimal? Amount, decimal? Percent, decimal? Units);

/// <summary>A salary structure row (component, order of evaluation, defaults) in force during the period (its end exclusive).</summary>
public sealed record SnapshotStructureRow(
    Guid Id, Guid StructureId, Guid ComponentId, int Order, decimal? DefaultAmount, decimal? DefaultPercent, bool Mandatory, string CurrencyCode, DateOnly ValidFrom, DateOnly? ValidTo);

/// <summary>A component with the versions in force during the period.</summary>
public sealed record SnapshotComponent(Guid ComponentId, string Code, string Kind, int PayslipOrder, IReadOnlyList<SnapshotVersion> Versions);

/// <summary>A component version (its end exclusive): method and what it needs, the pro-rata rule and its parameters, the flags and the tax label.</summary>
public sealed record SnapshotVersion(
    Guid VersionId, DateOnly ValidFrom, DateOnly? ValidTo, string Method, string? Formula, IReadOnlyList<Guid> Bases, string? TableCode,
    string ProRataRule, int? ProRataDivisor, string? ProRataCount, IReadOnlyDictionary<string, bool> Flags, string? TaxTreatment)
{
    /// <summary>The flag that marks a component as worked out by a statutory calculator (ruling Q2; the pack sets it).</summary>
    public const string StatutoryFlag = "statutory";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStatutory => Flags.TryGetValue(StatutoryFlag, out var on) && on;
}

/// <summary>The version of a table a non-statutory lookup reads, with its rows.</summary>
public sealed record SnapshotTable(string Code, Guid TableId, DateOnly ValidFrom, string Kind, IReadOnlyList<SnapshotTableRow> Rows);

public sealed record SnapshotTableRow(int Seq, decimal? Lower, decimal? Upper, decimal? FixedAmount, decimal? Rate, string? Key);

/// <summary>A policy version behind the frozen data (the attendance policy versions of the attendance results).</summary>
public sealed record SnapshotPolicy(string Type, Guid VersionId);

/// <summary>
/// Canonical form, hash and storage of a snapshot (ruling Q4): canonical JSON is the serialized snapshot with every object's keys in ordinal order
/// and no spaces; <c>snapshot_hash</c> is its SHA-256; the stored value is the canonical JSON compressed with gzip, in base64, in a small envelope.
/// </summary>
public static class PaySnapshotCodec
{
    public const string Format = "gzip-base64-v1";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static byte[] Canonical(PaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var doc = JsonSerializer.SerializeToDocument(snapshot, Options);
        return Canonical(doc.RootElement);
    }

    /// <summary>Any JSON value written with sorted keys and no spaces.</summary>
    public static byte[] Canonical(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(writer, element);
        }

        return stream.ToArray();
    }

    public static byte[] Hash(byte[] canonical) => SHA256.HashData(canonical);

    /// <summary>The stored envelope of the canonical JSON.</summary>
    public static JsonDocument Pack(byte[] canonical)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(canonical);
        }

        return JsonSerializer.SerializeToDocument(new { format = Format, data = Convert.ToBase64String(compressed.ToArray()) });
    }

    /// <summary>The canonical JSON in a stored envelope.</summary>
    public static byte[] Unpack(JsonDocument stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.RootElement.GetProperty("format").GetString() != Format)
        {
            throw new InvalidDataException("Unknown snapshot format.");
        }

        using var input = new MemoryStream(Convert.FromBase64String(stored.RootElement.GetProperty("data").GetString()!));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static PaySnapshot Read(byte[] canonical) => JsonSerializer.Deserialize<PaySnapshot>(canonical, Options) ?? throw new InvalidDataException("Empty snapshot.");

    private static void Write(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value);
                }

                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                {
                    Write(w, item);
                }

                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
