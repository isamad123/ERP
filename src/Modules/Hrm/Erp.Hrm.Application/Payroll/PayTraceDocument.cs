using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// The calculation trace of one result (slice S1.35 Build item 8, FR-HRM-PAY-32): everything a payroll officer or an auditor needs to explain the result
/// without the database, stored in <c>hrm.pay_traces</c> (not audited, CR-034; read only with <c>hrm.pay.trace.read</c>). It holds the engine version and
/// the snapshot's SHA-256, the rounding applied (mode, currency, decimals), the versions used (component versions with method and pro-rata rule,
/// structure rows, compensation rows, tables, policy versions, statutory calculators), the named inputs and every input item used, every evaluated step
/// (inputs, methods with formula text and evaluated values, pro-rata, inputs added, rounding, statutory), the lines, totals and facts, and the
/// result's hash. Built only from the snapshot and the calculation, in canonical JSON (sorted keys, no spaces), so the same snapshot gives the same
/// bytes (FR-HRM-PAY-31).
/// </summary>
public static class PayTraceDocument
{
    public const string Format = "pay-trace-v1";

    /// <summary>The trace's canonical JSON.</summary>
    public static byte[] Build(PaySnapshot snapshot, PayCalculation calc, byte[] snapshotHash)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(calc);
        ArgumentNullException.ThrowIfNull(snapshotHash);
        var run = snapshot.Run;
        var trace = new JsonObject
        {
            ["format"] = Format,
            ["engineVersion"] = calc.EngineVersion,
            ["snapshotHash"] = Convert.ToHexStringLower(snapshotHash),
            ["employmentId"] = snapshot.Employment.EmploymentId.ToString(),
            ["payRunId"] = run.RunId.ToString(),
            ["period"] = new JsonObject { ["start"] = Iso(run.PeriodStart), ["end"] = Iso(run.PeriodEnd), ["taxYear"] = run.TaxYear, ["periodNo"] = run.PeriodNo },
            ["rounding"] = new JsonObject
            {
                ["mode"] = run.RoundingMode,
                ["currency"] = run.CurrencyCode,
                ["decimals"] = run.CurrencyDecimals,
                ["internal"] = "half_away_from_zero, 4 places at every step",
            },
            ["versions"] = Versions(snapshot),
            ["inputs"] = Node(calc.Inputs),
            ["items"] = Node(snapshot.Inputs),
            ["steps"] = new JsonArray([.. calc.Trace.Select(t => (JsonNode)new JsonObject { ["component"] = t.Component, ["step"] = t.Step, ["text"] = t.Text })]),
            ["complete"] = calc.Complete,
            ["problems"] = new JsonArray([.. calc.Problems.Select(p => (JsonNode)new JsonObject { ["key"] = p.Key, ["subject"] = p.Subject })]),
            ["result"] = ResultNode(calc),
            ["resultHash"] = Convert.ToHexStringLower(ResultHash(calc)),
        };
        return Canonical(trace);
    }

    /// <summary>
    /// The SHA-256 of the result's content (lines with component, amount, units and rate; totals; worked and paid days; facts), without row ids or
    /// times, so two calculations of the same snapshot give the same hash.
    /// </summary>
    public static byte[] ResultHash(PayCalculation calc)
    {
        ArgumentNullException.ThrowIfNull(calc);
        return PaySnapshotCodec.Hash(Canonical(ResultNode(calc)));
    }

    /// <summary>The SHA-256 of a stored trace, after putting it in canonical form again (the stored text is already canonical).</summary>
    public static byte[] Hash(JsonElement stored) => PaySnapshotCodec.Hash(PaySnapshotCodec.Canonical(stored));

    private static JsonObject ResultNode(PayCalculation calc) => new()
    {
        ["lines"] = new JsonArray([.. calc.Lines.Select(l => (JsonNode)new JsonObject
        {
            ["componentId"] = l.ComponentId.ToString(),
            ["code"] = l.Code,
            ["kind"] = l.Kind,
            ["amount"] = Dec(l.Amount),
            ["units"] = l.Units is { } u ? Dec(u) : null,
            ["rate"] = l.Rate is { } r ? Dec(r) : null,
            ["statutory"] = l.Statutory,
        })]),
        ["gross"] = Dec(calc.Gross),
        ["reimbursements"] = Dec(calc.Reimbursements),
        ["deductions"] = Dec(calc.Deductions),
        ["net"] = Dec(calc.Net),
        ["employerCost"] = Dec(calc.EmployerCost),
        ["workedDays"] = Dec(calc.WorkedDays),
        ["paidDays"] = Dec(calc.PaidDays),
        ["facts"] = Node(calc.Facts),
    };

    private static JsonObject Versions(PaySnapshot s) => new()
    {
        ["components"] = new JsonArray([.. s.Components.Select(c => (JsonNode)new JsonObject
        {
            ["componentId"] = c.ComponentId.ToString(),
            ["code"] = c.Code,
            ["versions"] = new JsonArray([.. c.Versions.Select(v => (JsonNode)new JsonObject
            {
                ["versionId"] = v.VersionId.ToString(),
                ["validFrom"] = Iso(v.ValidFrom),
                ["validTo"] = v.ValidTo is { } to ? Iso(to) : null,
                ["method"] = v.Method,
                ["formula"] = v.Formula,
                ["proRataRule"] = v.ProRataRule,
                ["statutory"] = v.IsStatutory,
            })]),
        })]),
        ["structureRows"] = new JsonArray([.. s.StructureRows.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id.ToString(), ["structureId"] = r.StructureId.ToString(), ["componentId"] = r.ComponentId.ToString(), ["order"] = r.Order, ["validFrom"] = Iso(r.ValidFrom),
        })]),
        ["compensation"] = new JsonArray([.. s.Compensation.Select(c => (JsonNode)new JsonObject
        {
            ["assignmentId"] = c.AssignmentId.ToString(), ["structureId"] = c.StructureId.ToString(), ["validFrom"] = Iso(c.ValidFrom), ["validTo"] = c.ValidTo is { } to ? Iso(to) : null,
        })]),
        ["tables"] = new JsonArray([.. s.Tables.Select(t => (JsonNode)new JsonObject { ["code"] = t.Code, ["tableId"] = t.TableId.ToString(), ["validFrom"] = Iso(t.ValidFrom) })]),
        ["policies"] = new JsonArray([.. s.Policies.Select(p => (JsonNode)new JsonObject { ["type"] = p.Type, ["versionId"] = p.VersionId.ToString() })]),
        ["statutoryCalculators"] = new JsonArray([.. s.Statutory.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode)JsonValue.Create(k))]),
    };

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, PaySnapshotCodec.Options);

    private static byte[] Canonical(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return PaySnapshotCodec.Canonical(doc.RootElement);
    }

    /// <summary>A decimal as its invariant text, so 12.50 stays 12.50 and nothing goes through floating point.</summary>
    private static JsonNode Dec(decimal value) => JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture))!;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
