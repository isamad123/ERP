using System.Text;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// One change of a payment account as data, with its final values (slice S1.20 Build item 14): applied at once, or stored as the
/// payload of a held change (<see cref="BankAccountChangeHandler"/>) and applied when a second person approves it. The account number
/// and IBAN are carried as ciphertext with their blind indexes, never as text (as for the company's accounts, R-207).
/// </summary>
internal sealed record BankOp
{
    /// <summary><c>add</c>, <c>change</c>, <c>end</c> or <c>remove</c>.</summary>
    public required string Action { get; init; }

    /// <summary>The account (for <c>add</c>, the new row's id).</summary>
    public required Guid Id { get; init; }

    /// <summary>The row version read when the change was asked (change, end, remove): a change made meanwhile voids the request.</summary>
    public long? RowVersion { get; init; }

    /// <summary>A change from a later day than the row's first: the new row that starts then (the old row ends that day).</summary>
    public Guid? NewRowId { get; init; }

    /// <summary>Add: the first day. Change: the first day of the new details.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Add: the day the account stops being used (null: open). End: that day.</summary>
    public DateOnly? ValidTo { get; init; }

    public string? PaymentMode { get; init; }

    public Guid? BankId { get; init; }

    public string? AccountTitle { get; init; }

    public byte[]? AccountNumber { get; init; }

    public byte[]? AccountNumberHmac { get; init; }

    /// <summary>The last four characters of the account number, for the approver's description only.</summary>
    public string? AccountNumberLast4 { get; init; }

    public byte[]? Iban { get; init; }

    public byte[]? IbanHmac { get; init; }

    public string? IbanLast4 { get; init; }

    public string? SplitKind { get; init; }

    public decimal? SplitValue { get; init; }

    public int Priority { get; init; }
}

/// <summary>The payload of a held change of payment accounts (kind <c>hrm.employee_bank_accounts.change</c>).</summary>
internal sealed record BankPlan(Guid EmploymentId, Guid CompanyId, IReadOnlyList<BankOp> Ops);

/// <summary>
/// The rules of payment accounts shared by the service and the approval of held changes: the result of a set of changes on an
/// employment's rows, the split rule on every date, and the application in an order the database constraints accept (removals,
/// then changes, then new rows; the split rule is deferred to the commit and set immediate before it).
/// </summary>
internal static class BankAccountPlanner
{
    public const string Kind = "hrm.employee_bank_accounts.change";
    public const string NumberPurpose = "hrm.employee_bank_accounts.account_number";
    public const string IbanPurpose = "hrm.employee_bank_accounts.iban";
    public const string SplitConstraint = "hrm.tg_employee_bank_accounts_split";

    public const string Stale = "errors.hrm.bank_account.stale";
    public const string InUse = "errors.hrm.bank_account.in_use";
    public const string BankUnknown = "errors.hrm.bank_account.bank_unknown";
    public const string SplitRule = "errors.hrm.bank_account.split_rule";

    /// <summary>The form an account number is indexed in: letters and digits in upper case, after the bank, so the same account is found however it was typed.</summary>
    public static byte[] NumberIndex(IFieldEncryptor enc, Guid? bankId, string normalized) =>
        enc.BlindIndex(Encoding.UTF8.GetBytes($"{bankId?.ToString("N") ?? "-"}:{normalized}"), NumberPurpose);

    public static string NormalizeNumber(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Last4(string value) => value.Length <= 4 ? value : value[^4..];

    /// <summary>
    /// The rows as they would be after the changes, on copies (the tracked rows are untouched). Null and the refusal when an op names
    /// a row that is not the employment's, is stale, or does not fit the row's dates.
    /// </summary>
    public static (List<EmployeeBankAccount>? Rows, string? Problem) Simulate(IReadOnlyList<EmployeeBankAccount> current, IReadOnlyList<BankOp> ops)
    {
        var rows = current.Select(Copy).ToList();
        foreach (var op in ops)
        {
            if (op.Action == "add")
            {
                rows.Add(NewRow(op, op.Id, op.From!.Value, op.ValidTo, Guid.Empty, Guid.Empty, default));
                continue;
            }

            var row = rows.FirstOrDefault(r => r.Id == op.Id);
            if (row is null || row.RowVersion != op.RowVersion)
            {
                return (null, Stale);
            }

            switch (op.Action)
            {
                case "remove":
                    rows.Remove(row);
                    break;
                case "end":
                    if (op.ValidTo is not { } to || to <= row.ValidFrom)
                    {
                        return (null, Stale);
                    }

                    row.ValidTo = to;
                    break;
                default:
                    var from = op.From ?? row.ValidFrom;
                    if (from < row.ValidFrom || (row.ValidTo is { } end && from >= end))
                    {
                        return (null, Stale);
                    }

                    if (from == row.ValidFrom)
                    {
                        SetValues(row, op);
                    }
                    else
                    {
                        rows.Add(NewRow(op, op.NewRowId!.Value, from, row.ValidTo, row.CompanyId, row.EmploymentId, default));
                        row.ValidTo = from;
                    }

                    break;
            }
        }

        return (rows, null);
    }

    public static IReadOnlyList<SplitRow> Split(IEnumerable<EmployeeBankAccount> rows) =>
        rows.Select(r => new SplitRow(r.Id, r.ValidFrom, r.ValidTo, r.SplitKind, r.SplitValue, r.Priority)).ToList();

    /// <summary>
    /// Applies the changes to the tracked rows of the employment and saves them in three steps (removals, changes, new rows), then
    /// checks the deferred split rule at once. Null when saved; else the refusal (the caller rolls back).
    /// </summary>
    public static async Task<string?> ApplyAsync(HrmDbContext db, Guid companyId, Guid employmentId, List<EmployeeBankAccount> tracked, IReadOnlyList<BankOp> ops, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var removed = new List<EmployeeBankAccount>();
        var changed = new List<EmployeeBankAccount>();
        var added = new List<EmployeeBankAccount>();
        foreach (var op in ops)
        {
            if (op.Action == "add")
            {
                added.Add(NewRow(op, op.Id, op.From!.Value, op.ValidTo, companyId, employmentId, now, by));
                continue;
            }

            var row = tracked.FirstOrDefault(r => r.Id == op.Id);
            if (row is null || row.RowVersion != op.RowVersion)
            {
                return Stale;
            }

            switch (op.Action)
            {
                case "remove":
                    removed.Add(row);
                    break;
                case "end":
                    row.ValidTo = op.ValidTo;
                    changed.Add(row);
                    break;
                default:
                    var from = op.From ?? row.ValidFrom;
                    if (from == row.ValidFrom)
                    {
                        SetValues(row, op);
                    }
                    else
                    {
                        added.Add(NewRow(op, op.NewRowId!.Value, from, row.ValidTo, companyId, employmentId, now, by));
                        row.ValidTo = from;
                    }

                    changed.Add(row);
                    break;
            }
        }

        try
        {
            if (removed.Count > 0)
            {
                db.RemoveRange(removed);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            foreach (var row in changed.Distinct())
            {
                EmploymentApplier.Bump(row, now, by);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (added.Count > 0)
            {
                db.AddRange(added);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await db.Database.ExecuteSqlRawAsync($"set constraints {SplitConstraint} immediate", ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync($"set constraints {SplitConstraint} deferred", ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return Refusal(pg);
        }
        catch (PostgresException pg)
        {
            return Refusal(pg);
        }

        return null;
    }

    /// <summary>The answer to a database refusal of a payment account write.</summary>
    public static string Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "fk_employee_bank_accounts_bank_id" => BankUnknown,
        "fk_payment_lines_bank_account_id" => InUse,
        "ex_employee_bank_accounts_employment_id_priority" => PaySplit.PriorityTaken,
        "ck_employee_bank_accounts_split" => SplitRule,
        _ => "errors.data.rule_violated",
    };

    private static EmployeeBankAccount NewRow(BankOp op, Guid id, DateOnly from, DateOnly? to, Guid companyId, Guid employmentId, DateTimeOffset now, Guid by = default)
    {
        var row = new EmployeeBankAccount
        {
            Id = id,
            CompanyId = companyId,
            EmploymentId = employmentId,
            PaymentMode = string.Empty,
            SplitKind = string.Empty,
            ValidFrom = from,
            ValidTo = to,
            CreatedAt = now,
            CreatedBy = by,
            UpdatedAt = now,
            UpdatedBy = by,
            RowVersion = 1,
        };
        SetValues(row, op);
        return row;
    }

    private static void SetValues(EmployeeBankAccount row, BankOp op)
    {
        row.PaymentMode = op.PaymentMode!;
        row.BankId = op.BankId;
        row.AccountTitle = op.AccountTitle;
        row.AccountNumber = op.AccountNumber;
        row.AccountNumberHmac = op.AccountNumberHmac;
        row.Iban = op.Iban;
        row.IbanHmac = op.IbanHmac;
        row.SplitKind = op.SplitKind!;
        row.SplitValue = op.SplitValue;
        row.Priority = op.Priority;
    }

    private static EmployeeBankAccount Copy(EmployeeBankAccount r) => new()
    {
        Id = r.Id,
        CompanyId = r.CompanyId,
        EmploymentId = r.EmploymentId,
        PaymentMode = r.PaymentMode,
        BankId = r.BankId,
        AccountTitle = r.AccountTitle,
        AccountNumber = r.AccountNumber,
        AccountNumberHmac = r.AccountNumberHmac,
        Iban = r.Iban,
        IbanHmac = r.IbanHmac,
        SplitKind = r.SplitKind,
        SplitValue = r.SplitValue,
        Priority = r.Priority,
        ValidFrom = r.ValidFrom,
        ValidTo = r.ValidTo,
        CreatedAt = r.CreatedAt,
        CreatedBy = r.CreatedBy,
        UpdatedAt = r.UpdatedAt,
        UpdatedBy = r.UpdatedBy,
        RowVersion = r.RowVersion,
    };
}
