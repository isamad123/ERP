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

/// <summary>
/// The payload of a held change of payment accounts (kind <c>hrm.employee_bank_accounts.change</c>). <see cref="Matched"/> are the other
/// persons who had the same account when the change was asked and <see cref="Answer"/> the requester's answer to that warning
/// (<c>review</c> or <c>distinct</c>): the approver is shown both, the cases are written when the change is approved (review S1.20d
/// findings 1 and 7), and a person who got the same account after the request voids it.
/// </summary>
internal sealed record BankPlan(Guid EmploymentId, Guid CompanyId, IReadOnlyList<BankOp> Ops, IReadOnlyList<Guid>? Matched = null, string? Answer = null);

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
    public const string DuplicateSince = "errors.hrm.bank_account.duplicate_since";

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

            // Rows whose priority changes in place are first parked on priorities no row of the employment uses, so two accounts can
            // swap priorities in one change (the exclusion constraint is checked row by row; review S1.20d finding 5).
            var moving = changed.Distinct().Where(r => db.Entry(r).Property(x => x.Priority).IsModified).ToList();
            if (moving.Count > 1)
            {
                var final = moving.ToDictionary(r => r.Id, r => r.Priority);
                var used = tracked.Select(r => r.Priority).Concat(added.Select(r => r.Priority)).ToHashSet();
                foreach (var r in moving)
                {
                    used.Add(db.Entry(r).Property(x => x.Priority).OriginalValue);
                }

                var free = Enumerable.Range(1, 99).Where(p => !used.Contains(p)).ToList();
                if (free.Count < moving.Count)
                {
                    return PaySplit.PriorityTaken;
                }

                foreach (var (row, i) in moving.Select((r, i) => (r, i)))
                {
                    row.Priority = free[i];
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                foreach (var row in moving)
                {
                    row.Priority = final[row.Id];
                }
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

    /// <summary>The blind indexes (account number with its bank, IBAN) the ops give an account that it did not have before.</summary>
    public static List<byte[]> NewIndexes(IReadOnlyList<EmployeeBankAccount> current, IReadOnlyList<BankOp> ops)
    {
        var indexes = new List<byte[]>();
        foreach (var op in ops.Where(o => o.Action is "add" or "change"))
        {
            var row = current.FirstOrDefault(r => r.Id == op.Id);
            if (op.AccountNumberHmac is { } n && (row?.AccountNumberHmac is null || !row.AccountNumberHmac.AsSpan().SequenceEqual(n)))
            {
                indexes.Add(n);
            }

            if (op.IbanHmac is { } b && (row?.IbanHmac is null || !row.IbanHmac.AsSpan().SequenceEqual(b)))
            {
                indexes.Add(b);
            }
        }

        return indexes;
    }

    /// <summary>
    /// Transaction locks on the blind indexes, in a fixed order, so two writes or approvals giving the same account to different persons
    /// run one after the other and the second sees the first (review S1.20d findings 1 and 8). Also used for registration numbers.
    /// </summary>
    public static async Task LockIndexesAsync(DbContext db, IEnumerable<byte[]> indexes, CancellationToken ct)
    {
        foreach (var key in indexes.Select(i => BitConverter.ToInt64(i, 0)).Distinct().Order())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock({key})", ct).ConfigureAwait(false);
        }
    }

    /// <summary>The other persons with an account (any date, any employment) carrying one of the blind indexes.</summary>
    public static async Task<List<Guid>> OtherPersonsAsync(HrmDbContext db, Guid personId, IReadOnlyList<byte[]> indexes, CancellationToken ct)
    {
        var persons = new HashSet<Guid>();
        foreach (var index in indexes)
        {
            persons.UnionWith(await (from a in db.Set<EmployeeBankAccount>().AsNoTracking()
                                     join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                                     where (a.AccountNumberHmac == index || a.IbanHmac == index) && e.PersonId != personId
                                     select e.PersonId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        }

        return [.. persons.Order()];
    }

    /// <summary>
    /// The review cases of an answered warning (match kind <c>bank_account</c>): open for review, or decided as different people by
    /// <paramref name="decidedBy"/> (the approver of a held change; the requester only when no approval is configured). Existing pairs are kept.
    /// </summary>
    public static async Task<List<PersonDuplicateCase>> CasesAsync(HrmDbContext db, Guid personId, IReadOnlyList<Guid> others, string? answer, Guid decidedBy, DateTimeOffset now, CancellationToken ct)
    {
        var cases = new List<PersonDuplicateCase>();
        if (others.Count == 0)
        {
            return cases;
        }

        var existing = await db.Set<PersonDuplicateCase>().AsNoTracking()
            .Where(c => c.PersonId == personId && others.Contains(c.OtherPersonId) && c.MatchKind == "bank_account").Select(c => c.OtherPersonId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var other in others.Where(o => !existing.Contains(o)))
        {
            var distinct = answer == "distinct";
            cases.Add(new PersonDuplicateCase
            {
                Id = Guid.CreateVersion7(now),
                PersonId = personId,
                OtherPersonId = other,
                MatchKind = "bank_account",
                Source = "create",
                Status = distinct ? "distinct" : "open",
                DecidedBy = distinct ? decidedBy : null,
                DecidedAt = distinct ? now : null,
                CreatedAt = now,
                CreatedBy = decidedBy,
                UpdatedAt = now,
                UpdatedBy = decidedBy,
                RowVersion = 1,
            });
        }

        return cases;
    }

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
