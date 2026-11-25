using System.Globalization;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.Hrm.Application.Payroll;

/// <summary>A result line: the component, its amount rounded to the currency's minor unit, and the quantity and rate where the method has them.</summary>
public sealed record PayLine(Guid ComponentId, string Code, string Kind, int PayslipOrder, decimal Amount, decimal? Units, decimal? Rate, bool Statutory);

/// <summary>Why an employee could not be calculated: a localization key and the component (or name) it is about.</summary>
public sealed record PayProblem(string Key, string? Subject = null);

/// <summary>One step of the calculation for the trace (S1.35b stores it in <c>hrm.pay_traces</c>): the component, the kind of step and its text.</summary>
public sealed record PayTraceStep(string? Component, string Step, string Text);

/// <summary>
/// What the engine worked out for one employee: complete or not (with the problems), the lines, the totals (gross earnings, reimbursements, deductions,
/// net = gross + reimbursements - deductions, employer cost = employer contributions), worked and paid days, the named inputs used, the calculators'
/// facts and the trace steps.
/// </summary>
public sealed record PayCalculation(
    bool Complete,
    IReadOnlyList<PayProblem> Problems,
    IReadOnlyList<PayLine> Lines,
    decimal Gross,
    decimal Reimbursements,
    decimal Deductions,
    decimal Net,
    decimal EmployerCost,
    decimal WorkedDays,
    decimal PaidDays,
    IReadOnlyDictionary<string, decimal> Inputs,
    IReadOnlyDictionary<string, decimal> Facts,
    IReadOnlyList<PayTraceStep> Trace,
    string EngineVersion);

/// <summary>
/// The payroll calculation (slice S1.35 Build items 4 to 6; FR-HRM-PAY-03, -05, -31): a pure function of a <see cref="PaySnapshot"/> and the registered
/// statutory calculators. No database, no clock, no floating point; the same snapshot gives the same lines to the last paisa.
/// <list type="number">
/// <item>The period is cut into segments where the employee's compensation, structure rows or component versions change inside the days employed
/// (mid-month changes); the first segment reaches back to the period's start and the last to its end, so joiners and leavers are segments too.</item>
/// <item>In each segment the structure's components are worked out in their order of evaluation as full-period values: fixed (the line's amount, else
/// the structure's default), percent of the base components, formula (the S1.35 grammar over components and named inputs), table lookup (the bracket
/// of a non-statutory table that holds the earnings worked out before it: fixed amount plus rate times the excess over the lower bound), per day
/// (rate times paid days), per hour (rate times worked hours), attendance-linked (rate times worked days over planned working days), manual (inputs only).
/// Statutory components are left to their calculator.</item>
/// <item>Each component's own pro-rata rule (its latest version in the period) turns the segment values into its amount: <c>none</c> takes the last
/// segment's value; the other rules weigh each segment by its counted days less the missing ones (days not employed, unpaid leave) against the rule's
/// divisor: the period's calendar days, its planned working days, 30, or the custom divisor with its day count. A whole period gives the full value
/// exactly.</item>
/// <item>One-time and provider amounts are added to their components; every amount is rounded to the currency's minor unit by the run's mode.</item>
/// <item>Statutory components, in order, are asked of the one calculator that handles each; with none the employee is incomplete (never zero).</item>
/// </list>
/// </summary>
public static class PayEngine
{
    /// <summary>The engine and grammar version stored in every snapshot (ruling Q1); a change of the calculation rules is a new version.</summary>
    public const string Version = "S1.35a.1";

    private const string P = "errors.hrm.pay.calc.";
    public const string EngineVersionDiffers = P + "engine_version";
    public const string NotEmployed = P + "not_employed";
    public const string NoCompensation = P + "no_compensation";
    public const string CurrencyRateMissing = P + "currency_rate_missing";
    public const string ComponentMissing = P + "component_missing";
    public const string ComponentIncomplete = P + "component_incomplete";
    public const string FormulaInvalid = P + "formula_invalid";
    public const string UsesStatutory = P + "uses_statutory";
    public const string InputUnknown = P + "input_unknown";
    public const string TableMissing = P + "table_missing";
    public const string TableNeedsCalculator = P + "table_needs_calculator";
    public const string TableRowMissing = P + "table_row_missing";
    public const string PlanMissing = P + "plan_missing";
    public const string NoCountedDays = P + "no_counted_days";
    public const string InputRateMissing = P + "input_rate_missing";
    public const string InputOnStatutory = P + "input_on_statutory";
    public const string StatutoryCalculatorMissing = P + "statutory_calculator_missing";
    public const string StatutoryCalculatorAmbiguous = P + "statutory_calculator_ambiguous";
    public const string StatutoryNegative = P + "statutory_negative";
    public const string ProRataNotApplicable = P + "pro_rata_not_applicable";

    /// <summary>Every problem key (kept in step with the localization files by a unit test).</summary>
    public static IReadOnlyList<string> ProblemKeys { get; } =
    [
        EngineVersionDiffers, NotEmployed, NoCompensation, CurrencyRateMissing, ComponentMissing, ComponentIncomplete, FormulaInvalid, UsesStatutory, InputUnknown,
        TableMissing, TableNeedsCalculator, TableRowMissing, PlanMissing, NoCountedDays, InputRateMissing, InputOnStatutory, StatutoryCalculatorMissing,
        StatutoryCalculatorAmbiguous, StatutoryNegative, ProRataNotApplicable,
    ];

    /// <summary>Whether any of the versions counts working days (so the snapshot must hold the plan).</summary>
    public static bool NeedsPlan(IEnumerable<SnapshotVersion> versions) => versions.Any(v =>
        v.ProRataRule == "working_days" || (v.ProRataRule == "custom" && v.ProRataCount == "working_days") || v.Method == "attendance_linked"
        || (v.Formula is { } f && f.Contains("$" + PayInputNames.WorkingDays, StringComparison.Ordinal)));

    private sealed record Segment(DateOnly Start, DateOnly End, DateOnly Ref, SnapshotCompensation? Compensation, Dictionary<Guid, decimal> Full, Dictionary<Guid, SnapshotVersion> Versions);

    public static PayCalculation Calculate(PaySnapshot s, IReadOnlyList<IStatutoryCalculator> calculators)
    {
        ArgumentNullException.ThrowIfNull(s);
        try
        {
            return CalculateEmployee(s, calculators);
        }
        catch (OverflowException)
        {
            // An amount too large for decimal arithmetic (a typing error of many digits): this employee is incomplete, the batch goes on (review S1.35a finding 12).
            return new PayCalculation(false, [new PayProblem(FormulaLanguage.Overflow)], [], 0m, 0m, 0m, 0m, 0m, 0m, 0m,
                new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), [], s.EngineVersion);
        }
    }

    private static PayCalculation CalculateEmployee(PaySnapshot s, IReadOnlyList<IStatutoryCalculator> calculators)
    {
        ArgumentNullException.ThrowIfNull(calculators);
        var problems = new List<PayProblem>();
        var trace = new List<PayTraceStep>();
        var lines = new List<PayLine>();
        var facts = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var mode = PayRounding.Mode(s.Run.RoundingMode);
        var run = s.Run;
        if (s.EngineVersion != Version)
        {
            problems.Add(new PayProblem(EngineVersionDiffers, s.EngineVersion));
        }

        var periodDays = Days(run.PeriodStart, run.PeriodEnd).ToList();
        var employed = new HashSet<DateOnly>(s.Employment.Employed.SelectMany(r => Days(r.From, r.To)).Where(d => d >= run.PeriodStart && d <= run.PeriodEnd));
        var nonWorking = new HashSet<DateOnly>(s.Days.NonWorking);
        var components = s.Components.ToDictionary(c => c.ComponentId);

        // Unpaid leave by date (a day counts at most once), and the named inputs: each provider's quantities summed per name, plus the engine's own.
        var unpaid = new Dictionary<DateOnly, decimal>();
        var inputs = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var name in PayInputNames.BuiltIn)
        {
            inputs[name] = 0m;
        }

        // A dated quantity counts only on a day the employee is employed in the run's pay group: after a move to another group in the period
        // each run counts its own days (review S1.35a finding 3).
        foreach (var item in s.Inputs.Where(i => i.Name is not null && i.Value is not null && (i.Date is not { } on || employed.Contains(on))))
        {
            inputs[item.Name!] = inputs.GetValueOrDefault(item.Name!) + item.Value!.Value;
            if (item.Name == PayInputNames.UnpaidLeaveDays && item.Date is { } d)
            {
                unpaid[d] = Math.Min(1m, unpaid.GetValueOrDefault(d) + item.Value.Value);
            }
        }

        inputs[PayInputNames.PeriodDays] = periodDays.Count;
        inputs[PayInputNames.WorkingDays] = periodDays.Count(d => !nonWorking.Contains(d));
        inputs[PayInputNames.EmployedDays] = employed.Count;
        inputs[PayInputNames.UnpaidLeaveDays] = unpaid.Values.Sum();
        inputs[PayInputNames.PaidDays] = employed.Count - unpaid.Values.Sum();
        trace.Add(new PayTraceStep(null, "inputs", string.Join(", ", inputs.Select(kv => Invariant($"${kv.Key} = {kv.Value}")))));

        if (employed.Count == 0)
        {
            problems.Add(new PayProblem(NotEmployed));
            return Finish();
        }

        var first = employed.Min();
        var last = employed.Max();
        if (!s.Days.PlanRead && NeedsPlan(s.Components.SelectMany(c => c.Versions)))
        {
            problems.Add(new PayProblem(PlanMissing));
        }

        // 1. Segments: a change of compensation, structure row or version inside the employed days starts a new one.
        var changes = new SortedSet<DateOnly>();
        void Change(DateOnly? d)
        {
            if (d is { } x && x > first && x <= last)
            {
                changes.Add(x);
            }
        }

        foreach (var c in s.Compensation)
        {
            Change(c.ValidFrom);
            Change(c.ValidTo);
        }

        foreach (var r in s.StructureRows)
        {
            Change(r.ValidFrom);
            Change(r.ValidTo);
        }

        foreach (var v in s.Components.SelectMany(c => c.Versions))
        {
            Change(v.ValidFrom);
            Change(v.ValidTo);
        }

        var starts = new List<DateOnly> { run.PeriodStart };
        starts.AddRange(changes);
        var segments = new List<Segment>();
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1].AddDays(-1) : run.PeriodEnd;
            var refDay = start < first ? first : start;
            var comp = s.Compensation.FirstOrDefault(c => InForce(c.ValidFrom, c.ValidTo, refDay));
            segments.Add(new Segment(start, end, refDay, comp, [], []));
        }

        if (segments.All(g => g.Compensation is null))
        {
            problems.Add(new PayProblem(NoCompensation));
            return Finish();
        }

        // 2. Full-period values per segment, in the structure's order of evaluation; statutory rows are kept for step 5.
        var lastWithPay = segments.Last(g => g.Compensation is not null);
        var statutoryRows = new List<(SnapshotStructureRow Row, SnapshotComponent Component, SnapshotVersion Version)>();
        var rateOf = new Dictionary<Guid, (decimal? Rate, decimal? Units)>();
        foreach (var seg in segments.Where(g => g.Compensation is not null))
        {
            var comp = seg.Compensation!;
            if (comp.CurrencyCode != run.CurrencyCode)
            {
                Add(new PayProblem(CurrencyRateMissing, comp.CurrencyCode));
            }

            var rows = s.StructureRows.Where(r => r.StructureId == comp.StructureId && InForce(r.ValidFrom, r.ValidTo, seg.Ref))
                .OrderBy(r => r.Order).ThenBy(r => components.GetValueOrDefault(r.ComponentId)?.Code, StringComparer.Ordinal).ToList();
            foreach (var row in rows)
            {
                if (!components.TryGetValue(row.ComponentId, out var c))
                {
                    Add(new PayProblem(ComponentMissing, row.ComponentId.ToString()));
                    continue;
                }

                if (c.Versions.FirstOrDefault(v => InForce(v.ValidFrom, v.ValidTo, seg.Ref)) is not { } version)
                {
                    Add(new PayProblem(ComponentIncomplete, c.Code));
                    continue;
                }

                seg.Versions[c.ComponentId] = version;
                if (version.IsStatutory)
                {
                    if (seg == lastWithPay)
                    {
                        statutoryRows.Add((row, c, version));
                    }

                    continue;
                }

                var line = comp.Lines.FirstOrDefault(l => l.ComponentId == c.ComponentId);
                var value = Method(seg, c, version, row, line);
                if (value is { } v)
                {
                    seg.Full[c.ComponentId] = v;
                }
            }
        }

        // 3. Each component's pro-rata rule over the segments (its version in force on the last day it has one).
        var amounts = new Dictionary<Guid, decimal>();
        foreach (var id in segments.SelectMany(g => g.Full.Keys).Distinct().OrderBy(i => components[i].Code, StringComparer.Ordinal))
        {
            var c = components[id];
            var version = segments.Last(g => g.Versions.ContainsKey(id)).Versions[id];
            amounts[id] = ProRata(c, version, id);
        }

        // 4. Amounts from inputs (one-time inputs, provider amounts), then rounding to the currency's minor unit.
        foreach (var item in s.Inputs.Where(i => i.ComponentId is not null).OrderBy(i => i.Provider, StringComparer.Ordinal).ThenBy(i => i.SourceId))
        {
            var id = item.ComponentId!.Value;
            if (!components.TryGetValue(id, out var c) || c.Versions.LastOrDefault(v => v.ValidFrom <= last) is not { } version || version.ValidTo is { } to && to <= first)
            {
                Add(new PayProblem(ComponentIncomplete, components.GetValueOrDefault(id)?.Code ?? id.ToString()));
                continue;
            }

            if (version.IsStatutory)
            {
                Add(new PayProblem(InputOnStatutory, c.Code));
                continue;
            }

            decimal amount;
            if (item.Amount is { } a)
            {
                amount = a;
            }
            else if (item.Units is { } u && RateFor(id) is { } rate)
            {
                amount = PayRounding.Internal(u * rate);
            }
            else
            {
                Add(new PayProblem(InputRateMissing, c.Code));
                continue;
            }

            amounts[id] = amounts.GetValueOrDefault(id) + amount;
            trace.Add(new PayTraceStep(c.Code, "input", Invariant($"{item.Provider}/{item.Source}: {amount}")));
        }

        foreach (var (id, amount) in amounts)
        {
            var c = components[id];
            var rounded = PayRounding.Line(amount, run.CurrencyDecimals, mode);
            if (rounded != amount)
            {
                trace.Add(new PayTraceStep(c.Code, "rounding", Invariant($"{amount} to {rounded} ({run.RoundingMode}, {run.CurrencyDecimals} places)")));
            }

            if (rounded != 0m)
            {
                var (rate, units) = rateOf.GetValueOrDefault(id);
                lines.Add(new PayLine(id, c.Code, c.Kind, c.PayslipOrder, rounded, units, rate, false));
            }
        }

        // 5. Statutory components, in order, by their calculator only.
        foreach (var (row, c, version) in statutoryRows)
        {
            var sc = new StatutoryComponent(c.ComponentId, c.Code, c.Kind, version.VersionId, version.Flags, version.TaxTreatment);
            var handlers = calculators.Where(k => k.AppliesTo(sc)).ToList();
            if (handlers.Count != 1)
            {
                Add(new PayProblem(handlers.Count == 0 ? StatutoryCalculatorMissing : StatutoryCalculatorAmbiguous, c.Code));
                continue;
            }

            var calculator = handlers[0];
            var context = new StatutoryContext(
                sc,
                s.Statutory.TryGetValue(calculator.Code, out var frozen) ? frozen : null,
                [.. lines.OrderBy(l => l.PayslipOrder).ThenBy(l => l.Code, StringComparer.Ordinal).Select(l => new StatutoryLine(l.ComponentId, l.Code, l.Kind, l.Amount, components[l.ComponentId].Versions.LastOrDefault(v => v.ValidFrom <= last)?.Flags ?? new Dictionary<string, bool>()))],
                inputs,
                s.Employment.EmploymentId,
                run.TaxYear,
                run.PeriodNo,
                run.PeriodStart,
                run.PeriodEnd,
                run.CurrencyCode,
                run.CurrencyDecimals);
            var outcome = calculator.Calculate(context);
            foreach (var step in outcome.Steps)
            {
                trace.Add(new PayTraceStep(c.Code, "statutory:" + calculator.Code, step));
            }

            if (outcome.ProblemKey is { } key || outcome.Amount is not { } statutoryAmount)
            {
                Add(new PayProblem(outcome.ProblemKey ?? StatutoryCalculatorMissing, c.Code));
                continue;
            }

            if (statutoryAmount < 0)
            {
                Add(new PayProblem(StatutoryNegative, c.Code));
                continue;
            }

            lines.Add(new PayLine(c.ComponentId, c.Code, c.Kind, c.PayslipOrder, PayRounding.Line(statutoryAmount, run.CurrencyDecimals, mode), null, null, true));
            foreach (var (name, value) in outcome.Facts ?? new Dictionary<string, decimal>())
            {
                facts[name] = value;
            }
        }

        return Finish();

        PayCalculation Finish()
        {
            var ordered = lines.OrderBy(l => l.PayslipOrder).ThenBy(l => l.Code, StringComparer.Ordinal).ToList();
            decimal Sum(string kind) => ordered.Where(l => l.Kind == kind).Sum(l => l.Amount);
            var gross = Sum("earning");
            var reimbursements = Sum("reimbursement");
            var deductions = Sum("deduction");
            var distinct = problems.Distinct().ToList();
            return new PayCalculation(
                distinct.Count == 0, distinct, ordered, gross, reimbursements, deductions, gross + reimbursements - deductions, Sum("employer_contribution"),
                inputs.GetValueOrDefault(PayInputNames.WorkedDays), inputs.GetValueOrDefault(PayInputNames.PaidDays), inputs, facts, trace, s.EngineVersion);
        }

        void Add(PayProblem p) => problems.Add(p);

        decimal? RateFor(Guid id)
        {
            var comp = lastWithPay.Compensation!;
            var line = comp.Lines.FirstOrDefault(l => l.ComponentId == id);
            var row = s.StructureRows.FirstOrDefault(r => r.StructureId == comp.StructureId && r.ComponentId == id && InForce(r.ValidFrom, r.ValidTo, lastWithPay.Ref));
            return line?.Amount ?? row?.DefaultAmount;
        }

        decimal? Method(Segment seg, SnapshotComponent c, SnapshotVersion v, SnapshotStructureRow row, SnapshotCompensationLine? line)
        {
            var amount = line?.Amount ?? row.DefaultAmount;
            var percent = line?.Percent ?? row.DefaultPercent;
            decimal? value;
            string text;
            switch (v.Method)
            {
                case "fixed":
                    value = amount ?? 0m;
                    text = Invariant($"fixed {value}");
                    break;
                case "percent_of":
                    decimal bases = 0m;
                    foreach (var b in v.Bases)
                    {
                        if (seg.Versions.TryGetValue(b, out var bv) && bv.IsStatutory)
                        {
                            Add(new PayProblem(UsesStatutory, c.Code));
                        }

                        bases += seg.Full.GetValueOrDefault(b);
                    }

                    value = PayRounding.Internal(PayRounding.Internal((percent ?? 0m) * bases) / 100m);
                    text = Invariant($"{percent ?? 0m}% of {bases} = {value}");
                    break;
                case "formula":
                    var (tree, formulaProblems) = FormulaLanguage.Parse(v.Formula);
                    if (tree is null)
                    {
                        Add(new PayProblem(FormulaInvalid, c.Code));
                        trace.Add(new PayTraceStep(c.Code, "formula", string.Join(", ", formulaProblems.Select(p => p.ErrorKey))));
                        return null;
                    }

                    var steps = new List<string>();
                    try
                    {
                        value = FormulaLanguage.Evaluate(
                            tree,
                            code =>
                            {
                                var other = s.Components.FirstOrDefault(x => x.Code == code);
                                if (other is not null && seg.Versions.TryGetValue(other.ComponentId, out var ov) && ov.IsStatutory)
                                {
                                    Add(new PayProblem(UsesStatutory, c.Code));
                                }

                                return other is null ? 0m : seg.Full.GetValueOrDefault(other.ComponentId);
                            },
                            name => inputs.TryGetValue(name, out var iv) ? iv : throw new FormulaEvaluationException(InputUnknown),
                            mode,
                            steps);
                    }
                    catch (FormulaEvaluationException e)
                    {
                        Add(new PayProblem(e.ErrorKey == InputUnknown ? InputUnknown : FormulaInvalid, c.Code));
                        trace.Add(new PayTraceStep(c.Code, "formula", e.ErrorKey));
                        return null;
                    }

                    text = v.Formula + ": " + string.Join("; ", steps) + Invariant($" = {value}");
                    break;
                case "table_lookup":
                    var table = s.Tables.FirstOrDefault(t => t.Code == v.TableCode);
                    if (table is null)
                    {
                        Add(new PayProblem(TableMissing, c.Code));
                        return null;
                    }

                    if (table.Rows.Any(r => r.Key is not null))
                    {
                        Add(new PayProblem(TableNeedsCalculator, c.Code));
                        return null;
                    }

                    var earnings = seg.Full.Where(kv => components[kv.Key].Kind == "earning").Sum(kv => kv.Value);
                    var bracket = table.Rows.OrderBy(r => r.Seq).FirstOrDefault(r => (r.Lower is null || earnings >= r.Lower) && (r.Upper is null || earnings < r.Upper));
                    if (bracket is null)
                    {
                        Add(new PayProblem(TableRowMissing, c.Code));
                        return null;
                    }

                    value = PayRounding.Internal((bracket.FixedAmount ?? 0m) + PayRounding.Internal((bracket.Rate ?? 0m) * (earnings - (bracket.Lower ?? 0m))));
                    text = Invariant($"table {table.Code} row {bracket.Seq} for {earnings}: {bracket.FixedAmount ?? 0m} + {bracket.Rate ?? 0m} x ({earnings} - {bracket.Lower ?? 0m}) = {value}");
                    break;
                case "per_day":
                    // The paid days of this segment at this segment's rate, so a rate change inside the period pays each day at its own rate
                    // (review S1.35a finding 1); the segments' values are added, never pro-rated again.
                    var segmentPaid = Days(seg.Start, seg.End).Where(employed.Contains).Sum(d => 1m - unpaid.GetValueOrDefault(d));
                    value = PayRounding.Internal((amount ?? 0m) * segmentPaid);
                    rateOf[c.ComponentId] = (amount, inputs[PayInputNames.PaidDays]);
                    text = Invariant($"{amount ?? 0m} x paid days of the segment {segmentPaid} = {value}");
                    break;
                case "per_hour":
                    value = PayRounding.Internal((amount ?? 0m) * inputs[PayInputNames.WorkedHours]);
                    rateOf[c.ComponentId] = (amount, inputs[PayInputNames.WorkedHours]);
                    text = Invariant($"{amount ?? 0m} x $worked_hours {inputs[PayInputNames.WorkedHours]} = {value}");
                    break;
                case "attendance_linked":
                    var working = inputs[PayInputNames.WorkingDays];
                    value = working == 0m ? 0m : PayRounding.Internal(PayRounding.Internal((amount ?? 0m) * inputs[PayInputNames.WorkedDays]) / working);
                    text = Invariant($"{amount ?? 0m} x $worked_days {inputs[PayInputNames.WorkedDays]} / $working_days {working} = {value}");
                    break;
                case "manual":
                    value = 0m;
                    text = "manual: inputs only";
                    break;
                default:
                    Add(new PayProblem(ComponentIncomplete, c.Code));
                    return null;
            }

            trace.Add(new PayTraceStep(c.Code, "method", Invariant($"{seg.Start:yyyy-MM-dd}..{seg.End:yyyy-MM-dd} {v.Method} (version {v.VersionId}): {text}")));
            return value;
        }

        decimal ProRata(SnapshotComponent c, SnapshotVersion version, Guid id)
        {
            // A value already counted in days or hours is never cut again by a pro-rata rule (review S1.35a finding 1); a version saved before
            // the component service refused the combination makes the employee incomplete rather than paid twice short.
            if (ComponentRules.ProRataProblem(version.Method, version.ProRataRule, version.Formula) is not null)
            {
                Add(new PayProblem(ProRataNotApplicable, c.Code));
                return 0m;
            }

            if (version.Method == "per_day")
            {
                var paid = segments.Sum(g => g.Full.GetValueOrDefault(id));
                trace.Add(new PayTraceStep(c.Code, "pro_rata", Invariant($"per day, the segments added: {paid}")));
                return paid;
            }

            if (version.ProRataRule is "none" or null)
            {
                var full = lastWithPay.Full.GetValueOrDefault(id);
                trace.Add(new PayTraceStep(c.Code, "pro_rata", Invariant($"none: {full}")));
                return full;
            }

            var (divisorOf, count) = version.ProRataRule switch
            {
                "calendar_days" => ((int?)null, "calendar_days"),
                "working_days" => (null, "working_days"),
                "fixed_30" => (30, "calendar_days"),
                _ => (version.ProRataDivisor, version.ProRataCount ?? "calendar_days"),
            };
            bool Counted(DateOnly d) => count == "calendar_days" || !nonWorking.Contains(d);
            var kp = periodDays.Count(Counted);
            var divisor = divisorOf ?? kp;
            if (kp == 0 || divisor <= 0)
            {
                Add(new PayProblem(NoCountedDays, c.Code));
                return 0m;
            }

            decimal total = 0m;
            decimal fullTotal = 0m;
            var parts = new List<string>();
            foreach (var seg in segments)
            {
                var full = seg.Full.GetValueOrDefault(id);
                var days = Days(seg.Start, seg.End).Where(Counted).ToList();
                var ks = days.Count;
                var missing = days.Sum(d => employed.Contains(d) ? unpaid.GetValueOrDefault(d) : 1m);
                var numerator = (ks * (decimal)divisor) - (missing * kp);
                var part = PayRounding.Internal(full * numerator / (kp * (decimal)divisor));
                total += part;
                fullTotal += full;
                parts.Add(Invariant($"{seg.Start:yyyy-MM-dd}..{seg.End:yyyy-MM-dd}: {full} x ({ks} x {divisor} - {missing} x {kp}) / ({kp} x {divisor}) = {part}"));
            }

            if (total < 0m && fullTotal >= 0m)
            {
                parts.Add("below zero: 0");
                total = 0m;
            }

            trace.Add(new PayTraceStep(c.Code, "pro_rata", version.ProRataRule + ": " + string.Join("; ", parts)));
            return total;
        }
    }

    private static bool InForce(DateOnly from, DateOnly? to, DateOnly day) => from <= day && (to is null || to > day);

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
