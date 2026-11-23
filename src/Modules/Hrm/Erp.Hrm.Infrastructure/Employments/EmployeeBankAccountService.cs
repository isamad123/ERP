using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Payment accounts of employments (slice S1.20 Build item 14; FR-HRM-EMP-06, FR-HRM-PAY-24, GHR-02). The generated writes of
/// <c>hrm.employee_bank_accounts</c> are closed (R-113): every change comes here, is checked as a whole (bank fields by payment mode,
/// IBAN, one remainder account and at most 100 percent on every date, priorities) and is held for a second person while
/// <c>hrm.bank_accounts.requires_approval</c> is on (FR-XM-32: the kernel refuses the requester as approver). The same account on
/// another person is a warning answered by HR (the "ghost employee" check, FR-HRM-EMP-18). Numbers are encrypted with blind indexes,
/// always shown masked in lists, and in full only through the reveal action with its permission, in the access log.
/// </summary>
internal sealed class EmployeeBankAccountService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISensitiveAccessLog accessLog,
    IFieldEncryptorProvider keys,
    ISettings settings,
    IHeldChanges held,
    EmploymentApplier applier) : IEmployeeBankAccountService
{
    public const string ChangesRequired = "errors.hrm.bank_account.changes_required";
    public const string TooMany = "errors.hrm.bank_account.too_many_changes";
    public const string UnknownAction = "errors.hrm.bank_account.unknown_action";
    public const string Repeated = "errors.hrm.bank_account.repeated";
    public const string NumberFormat = "errors.hrm.bank_account.number_format";
    public const string NotForMode = "errors.hrm.bank_account.not_for_mode";
    public const string NumberOrIban = "errors.hrm.bank_account.number_or_iban";
    public const string SplitValue = "errors.hrm.bank_account.split_value";
    public const string PriorityRange = "errors.hrm.bank_account.priority_range";
    public const string BeforeJoining = "errors.hrm.bank_account.before_joining";
    public const string EffectiveFrom = "errors.hrm.bank_account.effective_from";
    public const string RevealMasked = "errors.hrm.bank_account.reveal_masked";
    public const string PossibleDuplicates = "errors.hrm.bank_account.possible_duplicates";
    public const string DatesOnChange = "errors.hrm.bank_account.dates_on_change";
    public const string Unreadable = "errors.hrm.bank_account.unreadable";

    public const int MaxChanges = 10;
    private const int TitleLength = 200;
    private const int NumberLength = 34;
    private const int ReasonLength = 500;

    private static readonly string[] Modes = ["bank_transfer", "cheque", "cash", "mobile_wallet"];
    private static readonly string[] BankModes = ["bank_transfer", "mobile_wallet"];
    private static readonly string[] Kinds = ["remainder", "percent", "amount"];
    private static readonly string[] Actions = ["add", "change", "end", "remove"];
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    // ================================================================== reads

    public async Task<PeopleResult> ListAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.BankAccountsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var query = (await scope.ApplyAsync(db.Set<EmployeeBankAccount>().AsNoTracking(), EmployeeBankAccountAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            .Where(a => a.EmploymentId == employmentId);
        if (asOf is { } date)
        {
            query = query.Where(a => a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date));
        }

        var rows = await query.OrderBy(a => a.ValidFrom).ThenBy(a => a.Priority).ThenBy(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = await fields.PlanAsync(EmployeeBankAccountAccess.Fields, EmployeeBankAccountAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var maps = await plan.ResolveAsync(rows, Source, cancellationToken).ConfigureAwait(false);
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Select((r, i) => View(r, maps[i], enc)).ToList();
        // Changes still waiting for their second person (R-233): said, not shown: no content, so no new number or IBAN is given away.
        var waiting = (await held.PendingAsync(BankAccountPlanner.Kind, employmentId, cancellationToken).ConfigureAwait(false))
            .Select(p => new { requestId = p.RequestId, requestedAt = p.RequestedAt, mine = p.Mine }).ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new { items, pending = waiting });
    }

    /// <summary>
    /// One account as a list shows it: every field under the caller's field rules; the account number and IBAN masked even for a
    /// role that may read them in full (masked by default, GHR-02; the full value only through <see cref="RevealAsync"/>).
    /// </summary>
    private static Dictionary<string, object?> View(EmployeeBankAccount r, FieldAccessMap access, IFieldEncryptor enc)
    {
        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = r.Id,
            ["companyId"] = r.CompanyId,
            ["employmentId"] = r.EmploymentId,
            ["paymentMode"] = r.PaymentMode,
            ["bankId"] = r.BankId,
            ["accountTitle"] = r.AccountTitle,
            ["accountNumber"] = Masked(r.AccountNumber, BankAccountPlanner.NumberPurpose, enc),
            ["iban"] = Masked(r.Iban, BankAccountPlanner.IbanPurpose, enc),
            ["splitKind"] = r.SplitKind,
            ["splitValue"] = r.SplitValue,
            ["priority"] = r.Priority,
            ["validFrom"] = r.ValidFrom,
            ["validTo"] = r.ValidTo,
            ["rowVersion"] = r.RowVersion,
        };
        foreach (var field in EmployeeBankAccountAccess.Fields.Fields)
        {
            if (!view.ContainsKey(field.JsonName))
            {
                continue;
            }

            var level = access.Of(field.Key);
            if (level == FieldAccessLevel.Hidden)
            {
                view.Remove(field.JsonName);
            }
            else if (level == FieldAccessLevel.Masked && field.Key is not ("account_number" or "iban") && view[field.JsonName] is { } value)
            {
                view[field.JsonName] = FieldShaping.Mask(JsonSerializer.SerializeToNode(value))?.GetValue<string>();
            }
        }

        return view;
    }

    private static string? Masked(byte[]? ciphertext, string purpose, IFieldEncryptor enc)
    {
        if (ciphertext is null)
        {
            return null;
        }

        try
        {
            return FieldShaping.Mask(System.Text.Json.Nodes.JsonValue.Create(Encoding.UTF8.GetString(enc.Decrypt(ciphertext, purpose))))!.GetValue<string>();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // not written by this service or under another key: shown as absent, the list still answers
        }
    }

    public async Task<PeopleResult> RevealAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.BankAccountsReveal, cancellationToken).ConfigureAwait(false)
            || !await permissions.HasPermissionAsync(EmploymentPermissions.BankAccountsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<EmployeeBankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, EmployeeBankAccountAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        // A role whose field rules hide or mask the numbers never sees them in full, even with the permission (FR-KRN-IAM-13).
        var plan = await fields.PlanAsync(EmployeeBankAccountAccess.Fields, EmployeeBankAccountAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([row], Source, cancellationToken).ConfigureAwait(false))[0];
        var shown = new List<string>();
        if (row.AccountNumber is not null)
        {
            shown.Add("account_number");
        }

        if (row.Iban is not null)
        {
            shown.Add("iban");
        }

        if (shown.Exists(f => access.Of(f) < FieldAccessLevel.ReadOnly))
        {
            return PeopleResult.Forbidden(RevealMasked);
        }

        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        string? Plain(byte[]? value, string purpose) => value is null ? null : Encoding.UTF8.GetString(enc.Decrypt(value, purpose));
        string? number, iban;
        try
        {
            number = Plain(row.AccountNumber, BankAccountPlanner.NumberPurpose);
            iban = Plain(row.Iban, BankAccountPlanner.IbanPurpose);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return PeopleResult.Conflict(Unreadable); // not written by this service or under another key (review S1.20d finding 10)
        }

        if (shown.Count > 0)
        {
            await accessLog.RecordAsync(EmployeeBankAccountAccess.Fields.Resource, row.Id, shown, "view", 1, cancellationToken).ConfigureAwait(false); // before the answer (GHR-02)
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { id = row.Id, accountNumber = number, iban });
    }

    // ================================================================== changes

    public async Task<PeopleResult> ChangeAsync(Guid employmentId, BankAccountsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputs = request.Changes ?? [];
        if (inputs.Count == 0)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["changes"] = [ChangesRequired] });
        }

        if (inputs.Count > MaxChanges)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["changes"] = [TooMany] });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (input, i) in inputs.Select((x, i) => (x, i)))
        {
            if (input is null || !Actions.Contains(input.Action ?? string.Empty, StringComparer.Ordinal))
            {
                errors[$"changes[{i}].action"] = [UnknownAction];
            }
            else if (input.Action != "add" && (input.Id is null || input.RowVersion is null))
            {
                errors[$"changes[{i}].{(input.Id is null ? "id" : "rowVersion")}"] = [input.Id is null ? PersonService.Required : PersonService.RowVersionRequired];
            }
        }

        if (inputs.Where(x => x?.Id is not null).GroupBy(x => x.Id).Any(g => g.Count() > 1))
        {
            errors["changes"] = [Repeated];
        }

        var reason = request.Reason?.Trim();
        if (reason is { Length: > ReasonLength })
        {
            errors["reason"] = [PersonService.TooLong];
        }

        if (request.Duplicates is not null and not ("distinct" or "review"))
        {
            errors["duplicates"] = [PersonService.DuplicatesAnswer];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        foreach (var action in inputs.Select(x => x.Action).Distinct())
        {
            var permission = action switch
            {
                "add" => EmploymentPermissions.BankAccountsCreate,
                "remove" => EmploymentPermissions.BankAccountsDelete,
                _ => EmploymentPermissions.BankAccountsUpdate,
            };
            if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden();
            }
        }

        // Confirming "a different person" decides the review case at once, so it needs the review permission (as for persons).
        if (request.Duplicates == "distinct" && !await permissions.HasPermissionAsync(PeoplePermissions.DuplicatesUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!CurrentStatuses.Contains(employment.Status))
        {
            return PeopleResult.Conflict(EmploymentService.NotCurrent);
        }

        var today = await applier.TodayAsync(employment.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var approval = await settings.GetAsync(HrmSettingKeys.HrmBankAccountsRequiresApproval, (Guid?)employment.CompanyId, cancellationToken).ConfigureAwait(false);
        var enc = await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employmentId} for update", cancellationToken).ConfigureAwait(false);
            var current = await db.Set<EmployeeBankAccount>().Where(a => a.EmploymentId == employmentId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var (ops, problem) = await PlanAsync(employment, current, inputs, today, enc, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return await RollbackAsync(tx, problem, cancellationToken).ConfigureAwait(false);
            }

            // The whole result: every op fits its row and the split holds on every date (also checked by the database at commit).
            var (after, stale) = BankAccountPlanner.Simulate(current, ops!);
            if (stale is not null)
            {
                return await RollbackAsync(tx, PeopleResult.Conflict(stale), cancellationToken).ConfigureAwait(false);
            }

            if (PaySplit.Check(BankAccountPlanner.Split(after!)) is { } split)
            {
                return await RollbackAsync(tx, new PeopleResult(PeopleOutcome.Invalid, split.ErrorKey, Extensions: new Dictionary<string, object?> { ["date"] = split.Date }), cancellationToken).ConfigureAwait(false);
            }

            // The same account on another person: a warning HR answers (FR-HRM-EMP-18, the "ghost employee" check). The blind indexes are
            // locked first, so two writes giving one account to two persons run one after the other (review S1.20d findings 1 and 8).
            var indexes = BankAccountPlanner.NewIndexes(current, ops!);
            await BankAccountPlanner.LockIndexesAsync(db, indexes, cancellationToken).ConfigureAwait(false);
            var others = await BankAccountPlanner.OtherPersonsAsync(db, employment.PersonId, indexes, cancellationToken).ConfigureAwait(false);
            if (others.Count > 0 && request.Duplicates is null)
            {
                return await RollbackAsync(tx, await WarningAsync(others, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }

            var answer = others.Count > 0 ? request.Duplicates : null;
            if (approval.Value.ValueKind != JsonValueKind.False)
            {
                // The change is tried against the database rules (bank, bank fields, priorities, split) and rolled back, so a request that
                // could never be approved is refused now (review S1.20d finding 3).
                await tx.CreateSavepointAsync("bank_trial", cancellationToken).ConfigureAwait(false);
                var trial = await BankAccountPlanner.ApplyAsync(db, employment.CompanyId, employment.Id, current, ops!, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false);
                await tx.RollbackToSavepointAsync("bank_trial", cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                if (trial is not null)
                {
                    return await RollbackAsync(tx, Answer(trial), cancellationToken).ConfigureAwait(false);
                }

                // Held for a second person; the kernel refuses the requester as approver (FR-XM-32). The warning and its answer travel
                // with the change: the approver sees them, and the review cases are written only when the change is approved.
                var payload = JsonSerializer.Serialize(new BankPlan(employment.Id, employment.CompanyId, ops!, others, answer), JsonSerializerOptions.Web);
                var requestId = await held.SubmitAsync(new HeldChangeRequest(BankAccountPlanner.Kind, EmploymentAccess.Scope.Resource, employment.Id, payload, string.IsNullOrEmpty(reason) ? null : reason),
                    tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId, sameAccountPersons = others.Count });
            }

            if (await BankAccountPlanner.ApplyAsync(db, employment.CompanyId, employment.Id, current, ops!, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return await RollbackAsync(tx, Answer(refused), cancellationToken).ConfigureAwait(false);
            }

            // No second person is configured: the requester's own answer decides the cases.
            var cases = await BankAccountPlanner.CasesAsync(db, employment.PersonId, others, answer, actor.UserId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            db.AddRange(cases);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            var saved = await db.Set<EmployeeBankAccount>().AsNoTracking().Where(a => a.EmploymentId == employmentId)
                .Select(a => new { id = a.Id, rowVersion = a.RowVersion, validFrom = a.ValidFrom, validTo = a.ValidTo }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { status = "applied", accounts = saved, duplicateCases = CaseViews(cases) });
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Answer(BankAccountPlanner.Refusal(pg));
        }
    }

    internal static PeopleResult Answer(string key) => key switch
    {
        BankAccountPlanner.Stale => PeopleResult.Conflict(PersonService.Concurrency),
        BankAccountPlanner.InUse => PeopleResult.Conflict(key),
        BankAccountPlanner.BankUnknown => PeopleResult.Fields(new Dictionary<string, string[]> { ["bankId"] = [key] }),
        _ => PeopleResult.Invalid(key),
    };

    private async Task<PeopleResult> RollbackAsync(IDbContextTransaction tx, PeopleResult result, CancellationToken ct)
    {
        await tx.RollbackAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return result;
    }

    /// <summary>
    /// The ops with the final values of each row, after the caller's permission per action, data scope of each row before and after,
    /// and field security of the fields set; else the refusal.
    /// </summary>
    private async Task<(List<BankOp>? Ops, PeopleResult? Problem)> PlanAsync(Employment employment, List<EmployeeBankAccount> current, IReadOnlyList<BankAccountChangeInput> inputs, DateOnly today, IFieldEncryptor enc, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(int i, string field, string key) => errors.TryAdd($"changes[{i}].{field}", [key]);
        var ops = new List<BankOp>();
        var now = clock.UtcNow;
        var createMap = await fields.ForAsync(EmployeeBankAccountAccess.Fields, "create", employment.CompanyId, ct).ConfigureAwait(false);
        var nextPriority = Math.Min(99, (current.Count == 0 ? 0 : current.Max(r => r.Priority)) + 1);
        foreach (var (input, i) in inputs.Select((x, i) => (x, i)))
        {
            EmployeeBankAccount? row = null;
            if (input.Action != "add")
            {
                row = current.FirstOrDefault(r => r.Id == input.Id);
                if (row is null || !await scope.AllowsAsync(row, EmployeeBankAccountAccess.Scope, "read", Source, ct).ConfigureAwait(false))
                {
                    return (null, PeopleResult.NotFound);
                }

                if (row.RowVersion != input.RowVersion)
                {
                    return (null, PeopleResult.Conflict(PersonService.Concurrency));
                }

                var action = input.Action == "remove" ? "delete" : "update";
                if (!await scope.AllowsAsync(row, EmployeeBankAccountAccess.Scope, action, Source, ct).ConfigureAwait(false))
                {
                    return (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
                }
            }

            if (input.Action == "remove")
            {
                ops.Add(new BankOp { Action = "remove", Id = row!.Id, RowVersion = row.RowVersion, Priority = row.Priority });
                continue;
            }

            if (input.Action == "end")
            {
                if (input.ValidTo is not { } to)
                {
                    Fail(i, "validTo", PersonService.Required);
                }
                else if (to <= row!.ValidFrom)
                {
                    Fail(i, "validTo", PersonService.DateOrder);
                }

                if (await NotEditableAsync(row!, ["valid_to"], ct).ConfigureAwait(false) is { Count: > 0 } denied)
                {
                    return (null, NotEditable(denied));
                }

                ops.Add(new BankOp { Action = "end", Id = row!.Id, RowVersion = row.RowVersion, ValidTo = input.ValidTo, Priority = row.Priority });
                continue;
            }

            // add or change: the final values of the row.
            var mode = input.PaymentMode?.Trim() ?? row?.PaymentMode;
            if (mode is null || !Modes.Contains(mode, StringComparer.Ordinal))
            {
                Fail(i, "paymentMode", mode is null ? PersonService.Required : PersonService.UnknownCode);
                continue;
            }

            var bankMode = BankModes.Contains(mode, StringComparer.Ordinal);
            var keysSet = new List<string>();
            void Set(string key, bool given)
            {
                if (given)
                {
                    keysSet.Add(key);
                }
            }

            Set("payment_mode", input.PaymentMode is not null);
            Guid? bankId = input.BankId ?? row?.BankId;
            var title = input.AccountTitle is null ? row?.AccountTitle : input.AccountTitle.Trim();
            Set("bank_id", input.BankId is not null);
            Set("account_title", input.AccountTitle is not null);
            if (title is { Length: 0 })
            {
                title = null;
            }

            if (title is { Length: > TitleLength })
            {
                Fail(i, "accountTitle", PersonService.TooLong);
            }

            // The account number: typed (normalized for the index), kept, or cleared.
            string? number = null;
            if (input.AccountNumber is not null)
            {
                var typed = input.AccountNumber.Trim();
                number = BankAccountPlanner.NormalizeNumber(typed);
                if (number.Length == 0 || number.Length > NumberLength || typed.Any(ch => !char.IsLetterOrDigit(ch) && ch is not (' ' or '-')))
                {
                    Fail(i, "accountNumber", NumberFormat);
                    number = null;
                }

                Set("account_number", true);
            }

            string? iban = null;
            if (input.Iban is not null)
            {
                iban = Iban.Normalize(input.Iban);
                if (Iban.Check(iban) is { } bad)
                {
                    Fail(i, "iban", bad);
                    iban = null;
                }

                Set("iban", true);
            }

            if (!bankMode && (input.BankId is not null || input.AccountNumber is not null || input.Iban is not null))
            {
                Fail(i, input.BankId is not null ? "bankId" : input.AccountNumber is not null ? "accountNumber" : "iban", NotForMode);
            }

            // Stored values carried over on a change; a cash or cheque account keeps no bank details.
            var keepNumber = row?.AccountNumber is not null && input.AccountNumber is null && input.ClearAccountNumber != true;
            var keepIban = row?.Iban is not null && input.Iban is null && input.ClearIban != true;
            Set("account_number", input.ClearAccountNumber == true);
            Set("iban", input.ClearIban == true);
            byte[]? numberCipher = null, numberHmac = null, ibanCipher = null, ibanHmac = null;
            string? numberLast4 = null, ibanLast4 = null;
            if (bankMode)
            {
                if (number is not null)
                {
                    numberCipher = enc.Encrypt(Encoding.UTF8.GetBytes(input.AccountNumber!.Trim()), BankAccountPlanner.NumberPurpose);
                    numberHmac = BankAccountPlanner.NumberIndex(enc, bankId, number);
                    numberLast4 = BankAccountPlanner.Last4(number);
                }
                else if (keepNumber)
                {
                    numberCipher = row!.AccountNumber;
                    var plain = Encoding.UTF8.GetString(enc.Decrypt(row.AccountNumber!, BankAccountPlanner.NumberPurpose));
                    numberHmac = BankAccountPlanner.NumberIndex(enc, bankId, BankAccountPlanner.NormalizeNumber(plain)); // a new bank indexes it again
                    numberLast4 = BankAccountPlanner.Last4(BankAccountPlanner.NormalizeNumber(plain));
                }

                if (iban is not null)
                {
                    ibanCipher = enc.Encrypt(Encoding.UTF8.GetBytes(iban), BankAccountPlanner.IbanPurpose);
                    ibanHmac = enc.BlindIndex(Encoding.UTF8.GetBytes(iban), BankAccountPlanner.IbanPurpose);
                    ibanLast4 = BankAccountPlanner.Last4(iban);
                }
                else if (keepIban)
                {
                    ibanCipher = row!.Iban;
                    ibanHmac = row.IbanHmac;
                    ibanLast4 = BankAccountPlanner.Last4(Encoding.UTF8.GetString(enc.Decrypt(row.Iban!, BankAccountPlanner.IbanPurpose)));
                }

                if (bankId is null)
                {
                    Fail(i, "bankId", PersonService.Required);
                }

                if (title is null)
                {
                    Fail(i, "accountTitle", PersonService.Required);
                }

                if (numberCipher is null && ibanCipher is null && !errors.ContainsKey($"changes[{i}].accountNumber") && !errors.ContainsKey($"changes[{i}].iban"))
                {
                    Fail(i, "accountNumber", NumberOrIban);
                }
            }
            else if (row is not null && row.PaymentMode != mode)
            {
                bankId = null; // a change to cash or cheque drops the stored bank details
                Set("bank_id", row.BankId is not null);
                Set("account_number", row.AccountNumber is not null);
                Set("iban", row.Iban is not null);
            }
            else
            {
                bankId = null;
            }

            var kind = input.SplitKind?.Trim() ?? row?.SplitKind ?? (input.Action == "add" && current.Count == 0 ? "remainder" : null);
            Set("split_kind", input.SplitKind is not null);
            decimal? value = null;
            if (kind is null || !Kinds.Contains(kind, StringComparer.Ordinal))
            {
                Fail(i, "splitKind", kind is null ? PersonService.Required : PersonService.UnknownCode);
            }
            else
            {
                value = kind == "remainder" ? null : input.SplitValue ?? (input.SplitKind is null ? row?.SplitValue : null);
                Set("split_value", input.SplitValue is not null || (row is not null && row.SplitValue != value));
                if (kind == "remainder" ? input.SplitValue is not null : value is not { } v || v <= 0 || (kind == "percent" && v > 100) || decimal.Round(v, 4) != v)
                {
                    Fail(i, "splitValue", SplitValue);
                }
            }

            var priority = input.Priority ?? row?.Priority ?? nextPriority;
            Set("priority", input.Priority is not null);
            if (priority is < 1 or > 99)
            {
                Fail(i, "priority", PriorityRange);
            }

            if (input.Action == "add")
            {
                var from = input.ValidFrom ?? (today > employment.JoiningDate ? today : employment.JoiningDate);
                Set("valid_from", input.ValidFrom is not null);
                Set("valid_to", input.ValidTo is not null);
                if (from < employment.JoiningDate)
                {
                    Fail(i, "validFrom", BeforeJoining);
                }

                if (input.ValidTo is { } to && to <= from)
                {
                    Fail(i, "validTo", PersonService.DateOrder);
                }

                var op = new BankOp
                {
                    Action = "add",
                    Id = Guid.CreateVersion7(now),
                    From = from,
                    ValidTo = input.ValidTo,
                    PaymentMode = mode,
                    BankId = bankId,
                    AccountTitle = title,
                    AccountNumber = numberCipher,
                    AccountNumberHmac = numberHmac,
                    AccountNumberLast4 = numberLast4,
                    Iban = ibanCipher,
                    IbanHmac = ibanHmac,
                    IbanLast4 = ibanLast4,
                    SplitKind = kind,
                    SplitValue = value,
                    Priority = priority,
                };
                var probe = new EmployeeBankAccount { CompanyId = employment.CompanyId, EmploymentId = employment.Id, PaymentMode = mode, SplitKind = kind ?? string.Empty, ValidFrom = from };
                if (!await scope.AllowsAsync(probe, EmployeeBankAccountAccess.Scope, "create", Source, ct).ConfigureAwait(false))
                {
                    return (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
                }

                keysSet.AddRange(["payment_mode", "split_kind", "priority"]);
                if (keysSet.Distinct().Where(k => EmployeeBankAccountAccess.Fields.Find(k) is not null && !createMap.CanEdit(k)).ToList() is { Count: > 0 } denied)
                {
                    return (null, NotEditable(denied));
                }

                ops.Add(op);
                nextPriority = Math.Min(99, Math.Max(nextPriority, priority + 1));
                continue;
            }

            // change: from the row's first day (a correction of the row) or from a later day inside its period. Its dates move only
            // through effectiveFrom and the end action (review S1.20d finding 6: never silently ignored).
            if (input.ValidFrom is not null || input.ValidTo is not null)
            {
                Fail(i, input.ValidFrom is not null ? "validFrom" : "validTo", DatesOnChange);
            }

            var effective = input.EffectiveFrom ?? row!.ValidFrom;
            if (effective < row!.ValidFrom || (row.ValidTo is { } end && effective >= end))
            {
                Fail(i, "effectiveFrom", EffectiveFrom);
            }

            if (await NotEditableAsync(row, keysSet.Distinct().ToList(), ct).ConfigureAwait(false) is { Count: > 0 } notEditable)
            {
                return (null, NotEditable(notEditable));
            }

            ops.Add(new BankOp
            {
                Action = "change",
                Id = row.Id,
                RowVersion = row.RowVersion,
                NewRowId = effective > row.ValidFrom ? Guid.CreateVersion7(now) : null,
                From = effective,
                PaymentMode = mode,
                BankId = bankId,
                AccountTitle = title,
                AccountNumber = numberCipher,
                AccountNumberHmac = numberHmac,
                AccountNumberLast4 = numberLast4,
                Iban = ibanCipher,
                IbanHmac = ibanHmac,
                IbanLast4 = ibanLast4,
                SplitKind = kind,
                SplitValue = value,
                Priority = priority,
            });
        }

        return errors.Count > 0 ? (null, PeopleResult.Fields(errors)) : (ops, null);
    }

    private async Task<List<string>> NotEditableAsync(EmployeeBankAccount row, List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var plan = await fields.PlanAsync(EmployeeBankAccountAccess.Fields, EmployeeBankAccountAccess.Scope, "update", Source, ct).ConfigureAwait(false);
        var map = (await plan.ResolveAsync([row], Source, ct).ConfigureAwait(false))[0];
        return keys.Where(k => EmployeeBankAccountAccess.Fields.Find(k) is not null && !map.CanEdit(k)).ToList();
    }

    private static PeopleResult NotEditable(List<string> denied) =>
        new(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));

    /// <summary>The warning listing the other persons with the same account (named only when the caller may read them).</summary>
    private async Task<PeopleResult> WarningAsync(List<Guid> others, CancellationToken ct)
    {
        var persons = await db.Set<Person>().AsNoTracking().Where(p => others.Contains(p.Id)).OrderBy(p => p.PersonNo).ToListAsync(ct).ConfigureAwait(false);
        var matches = new List<AccountMatch>();
        foreach (var other in persons)
        {
            matches.Add(await scope.AllowsAsync(other, PersonAccess.Scope, "read", Source, ct).ConfigureAwait(false)
                ? new AccountMatch("bank_account", true, other.Id, other.PersonNo, PersonService.DisplayName(other))
                : new AccountMatch("bank_account", false, null, null, null));
        }

        return PeopleResult.Conflict(PossibleDuplicates, new Dictionary<string, object?> { ["matches"] = matches });
    }

    private static List<object> CaseViews(List<PersonDuplicateCase> cases) => cases.Select(c => (object)new { id = c.Id, matchKind = c.MatchKind, status = c.Status }).ToList();

    /// <summary>Another person with the same account, as the warning lists them (named only when the caller may read them).</summary>
    private sealed record AccountMatch(string MatchKind, bool InScope, Guid? PersonId, string? PersonNo, string? Name);
}
