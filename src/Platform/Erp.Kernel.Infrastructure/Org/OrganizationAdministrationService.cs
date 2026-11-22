using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Application.Generated.Org;
using Erp.Kernel.Application.Org;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Org;

/// <summary>
/// Company registrations and bank accounts (slice S1.14a Build item 3, FR-KRN-ORG-01, FR-ADM-ORG-01, FR-KRN-IAM-14) and the
/// holiday calendar helper (Build item 5, FR-KRN-LOC-06). The generated API never returns or writes encrypted columns, so
/// these hand-written services do: values are encrypted with the customer's field key (with a blind index where the design
/// has one), shown masked, in full or not at all according to field security, every unmasked view is in the access log, and
/// a new, changed or removed bank account waits for a second administrator while maker-checker is on. Every value is a
/// parameter. Change-log entries never carry the encrypted values.
/// </summary>
public sealed class OrganizationAdministrationService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    ICallerAccess caller,
    IFieldAccess fieldAccess,
    ISensitiveAccessLog accessLog,
    IFieldEncryptorProvider encryptors) : IOrganizationAdministration
{
    public const string RegistrationsResource = "org.company_registrations";
    public const string BankAccountsResource = "org.company_bank_accounts";
    public const string RegistrationValuePurpose = "org.company_registrations.value";
    public const string AccountNumberPurpose = "org.company_bank_accounts.account_number";
    public const string IbanPurpose = "org.company_bank_accounts.iban";

    public const string ValueRequired = "errors.org.value_required";
    public const string AuthorityInvalid = "errors.org.authority_invalid";
    public const string KindInvalid = "errors.org.kind_invalid";
    public const string DatesInvalid = "errors.org.dates_invalid";
    public const string RegistrationOverlap = "errors.org.registration_overlap";
    public const string BankUnknown = "errors.org.bank_unknown";
    public const string CurrencyUnknown = "errors.org.currency_unknown";
    public const string TitleRequired = "errors.org.account_title_required";
    public const string AccountNumberRequired = "errors.org.account_number_required";
    public const string IbanInvalid = "errors.org.iban_invalid";
    public const string YearInvalid = "errors.org.year_invalid";
    public const string CalendarIsPack = "errors.org.calendar_is_pack";
    public const string FieldNotEditable = FieldShaping.NotEditable;

    private const int MaxValueLength = 60;
    private const int MaxTitleLength = 120;
    private const int MaxAccountNumberLength = 50;
    private static readonly string[] Kinds = ["ntn", "strn", "eobi", "social_security", "provident_fund", "commercial_registration", "other"];

    // ================================================================== registrations

    public async Task<OrgRead<RegistrationView>> RegistrationsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        if (await GuardAsync(c, null, companyId, "org.company_registrations.read", cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return OrgReads.Refused<RegistrationView>(refused);
        }

        var access = await fieldAccess.ForAsync(CompanyRegistrationAccess.Fields, "read", companyId, cancellationToken).ConfigureAwait(false);
        var level = access.Of("value");
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<RegistrationView>();
        await using (var command = IdentityDb.Command(c, null, """
            select id, company_id, kind, authority, value, valid_from, valid_to, row_version
            from org.company_registrations where company_id = $1 order by kind, authority, valid_from desc, id
            """, companyId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new RegistrationView(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), Shown(encryptor, reader.GetFieldValue<byte[]>(4), RegistrationValuePurpose, level), Name(level),
                    DateOnly.FromDateTime(reader.GetDateTime(5)), reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)), reader.GetInt64(7)));
            }
        }

        if (level >= FieldAccessLevel.ReadOnly && items.Count > 0)
        {
            await accessLog.RecordAsync(RegistrationsResource, null, ["value"], "view", items.Count, cancellationToken).ConfigureAwait(false);
        }

        return OrgReads.Of<RegistrationView>(items);
    }

    public Task<AccessResult> CreateRegistrationAsync(Guid companyId, RegistrationRequest request, CancellationToken cancellationToken) =>
        SaveRegistrationAsync(null, companyId, request, cancellationToken);

    public Task<AccessResult> UpdateRegistrationAsync(Guid registrationId, RegistrationRequest request, CancellationToken cancellationToken) =>
        SaveRegistrationAsync(registrationId, null, request, cancellationToken);

    private async Task<AccessResult> SaveRegistrationAsync(Guid? registrationId, Guid? companyOfNew, RegistrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var creating = registrationId is null;
        var value = request.Value?.Trim();
        var authority = request.Authority?.Trim();
        if (creating || value is not null)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
            {
                return AccessResult.Invalid(ValueRequired);
            }
        }

        if (creating && (request.Kind is null || !Kinds.Contains(request.Kind, StringComparer.Ordinal)))
        {
            return AccessResult.Invalid(KindInvalid);
        }

        if (creating && (authority is null || authority.Length is 0 or > 40 || authority != authority.Trim()))
        {
            return AccessResult.Invalid(AuthorityInvalid);
        }

        if (!creating && request.RowVersion is null)
        {
            return AccessResult.Invalid(AccessAdministrationService.RowVersionRequired);
        }

        var from = request.ValidFrom ?? (creating ? today : null);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid companyId;
            (string Kind, string Authority, DateOnly From, DateOnly? To, long RowVersion)? old = null;
            if (creating)
            {
                companyId = companyOfNew!.Value;
            }
            else
            {
                await using var find = IdentityDb.Command(c, tx, "select company_id, kind, authority, valid_from, valid_to, row_version from org.company_registrations where id = $1 for update", registrationId);
                await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return AccessResult.NotFound;
                }

                companyId = reader.GetGuid(0);
                old = (reader.GetString(1), reader.GetString(2), DateOnly.FromDateTime(reader.GetDateTime(3)), reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)), reader.GetInt64(5));
            }

            if (await GuardAsync(c, tx, companyId, creating ? "org.company_registrations.create" : "org.company_registrations.update", cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            if (old is { } existing && existing.RowVersion != request.RowVersion)
            {
                return AccessResult.Conflict(AccessAdministrationService.ConcurrencyConflict);
            }

            var access = await fieldAccess.ForAsync(CompanyRegistrationAccess.Fields, creating ? "create" : "update", companyId, cancellationToken).ConfigureAwait(false);
            if (value is not null && !access.CanEdit("value"))
            {
                return AccessResult.Forbidden(FieldNotEditable);
            }

            var validFrom = from ?? old!.Value.From;
            var validTo = request.ValidFrom is null && request.ValidTo is null && !creating ? old!.Value.To : request.ValidTo;
            if (validTo is { } end && end < validFrom)
            {
                return AccessResult.Invalid(DatesInvalid);
            }

            var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
            var by = actor.UserId;
            var changes = new List<(string Field, object? Before, object? After, bool Redacted)>();
            Guid id;
            if (creating)
            {
                id = Guid.CreateVersion7(now);
                await IdentityDb.ExecuteAsync(c, tx, """
                    insert into org.company_registrations (id, company_id, kind, value, value_hmac, authority, valid_from, valid_to, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10)
                    """, cancellationToken, id, companyId, request.Kind, encryptor.Encrypt(Encoding.UTF8.GetBytes(value!), RegistrationValuePurpose),
                    encryptor.BlindIndex(Encoding.UTF8.GetBytes(NormalizeRegistration(value!)), RegistrationValuePurpose), authority, validFrom, validTo, now, by).ConfigureAwait(false);
                changes.AddRange([("kind", null, request.Kind, false), ("authority", null, authority, false), ("value", null, null, true), ("valid_from", null, validFrom, false), ("valid_to", null, validTo, false)]);
            }
            else
            {
                id = registrationId!.Value;
                await IdentityDb.ExecuteAsync(c, tx, """
                    update org.company_registrations set
                        value = coalesce($2, value), value_hmac = coalesce($3, value_hmac), valid_from = $4, valid_to = $5,
                        updated_at = $6, updated_by = $7, row_version = row_version + 1
                    where id = $1
                    """, cancellationToken, id, value is null ? null : encryptor.Encrypt(Encoding.UTF8.GetBytes(value), RegistrationValuePurpose),
                    value is null ? null : encryptor.BlindIndex(Encoding.UTF8.GetBytes(NormalizeRegistration(value)), RegistrationValuePurpose), validFrom, validTo, now, by).ConfigureAwait(false);
                if (value is not null)
                {
                    changes.Add(("value", null, null, true));
                }

                if (old!.Value.From != validFrom)
                {
                    changes.Add(("valid_from", old.Value.From, validFrom, false));
                }

                if (old.Value.To != validTo)
                {
                    changes.Add(("valid_to", old.Value.To, validTo, false));
                }
            }

            if (changes.Count > 0)
            {
                await WriteChangesAsync(c, tx, [Entry(RegistrationsResource, id, companyId, creating ? "create" : "update", now, by, changes)], cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(id);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            return AccessResult.Conflict(RegistrationOverlap);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            return AccessResult.Invalid(ex.SqlState == PostgresErrorCodes.CheckViolation ? "errors.data.rule_violated" : "errors.data.reference_not_found");
        }
    }

    // ================================================================== bank accounts

    public async Task<OrgRead<BankAccountView>> BankAccountsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        if (await GuardAsync(c, null, companyId, "org.company_bank_accounts.read", cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return OrgReads.Refused<BankAccountView>(refused);
        }

        var access = await fieldAccess.ForAsync(CompanyBankAccountAccess.Fields, "read", companyId, cancellationToken).ConfigureAwait(false);
        var numberLevel = access.Of("account_number");
        var ibanLevel = access.Of("iban");
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<BankAccountView>();
        await using (var command = IdentityDb.Command(c, null, """
            select id, company_id, bank_code, account_title, account_number, iban, currency_code, salary_file_format, row_version
            from org.company_bank_accounts where company_id = $1 and not is_deleted order by bank_code, account_title, id
            """, companyId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new BankAccountView(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    Shown(encryptor, reader.GetFieldValue<byte[]>(4), AccountNumberPurpose, numberLevel),
                    reader.IsDBNull(5) ? null : Shown(encryptor, reader.GetFieldValue<byte[]>(5), IbanPurpose, ibanLevel), Name(numberLevel),
                    reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8)));
            }
        }

        var shown = new List<string>();
        if (numberLevel >= FieldAccessLevel.ReadOnly)
        {
            shown.Add("account_number");
        }

        if (ibanLevel >= FieldAccessLevel.ReadOnly && items.Exists(i => i.Iban is not null))
        {
            shown.Add("iban");
        }

        if (shown.Count > 0 && items.Count > 0)
        {
            await accessLog.RecordAsync(BankAccountsResource, null, shown, "view", items.Count, cancellationToken).ConfigureAwait(false);
        }

        return OrgReads.Of<BankAccountView>(items);
    }

    public Task<AccessResult> CreateBankAccountAsync(Guid companyId, BankAccountRequest request, CancellationToken cancellationToken) =>
        SaveBankAccountAsync(null, companyId, request, cancellationToken);

    public Task<AccessResult> UpdateBankAccountAsync(Guid bankAccountId, BankAccountRequest request, CancellationToken cancellationToken) =>
        SaveBankAccountAsync(bankAccountId, null, request, cancellationToken);

    public async Task<AccessResult> DeleteBankAccountAsync(Guid bankAccountId, BankAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RowVersion is null)
        {
            return AccessResult.Invalid(AccessAdministrationService.RowVersionRequired);
        }

        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var company = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select company_id from org.company_bank_accounts where id = $1 and not is_deleted", cancellationToken, bankAccountId).ConfigureAwait(false);
        if (company is not { } companyId)
        {
            return AccessResult.NotFound;
        }

        if (await GuardAsync(c, tx, companyId, "org.company_bank_accounts.delete", cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var change = new BankAccountChange { Id = bankAccountId, CompanyId = companyId, Operation = "delete", RowVersion = request.RowVersion };
        return await HoldOrApplyAsync(c, tx, change, AccessChange.DeleteBankAccount, request.Reason, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessResult> SaveBankAccountAsync(Guid? bankAccountId, Guid? companyOfNew, BankAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var creating = bankAccountId is null;
        var number = request.AccountNumber?.Trim();
        var iban = request.Iban is null ? null : NormalizeIban(request.Iban);
        var title = request.AccountTitle?.Trim();
        if (creating && string.IsNullOrEmpty(number) || number is { Length: > MaxAccountNumberLength } || (number is not null && number.Length == 0))
        {
            return AccessResult.Invalid(AccountNumberRequired);
        }

        if (creating && string.IsNullOrEmpty(title) || title is { Length: > MaxTitleLength } || (title is not null && title.Length == 0))
        {
            return AccessResult.Invalid(TitleRequired);
        }

        if (iban is not null && !IsValidIban(iban))
        {
            return AccessResult.Invalid(IbanInvalid);
        }

        if (creating && (string.IsNullOrWhiteSpace(request.BankCode) || string.IsNullOrWhiteSpace(request.CurrencyCode)))
        {
            return AccessResult.Invalid(string.IsNullOrWhiteSpace(request.BankCode) ? BankUnknown : CurrencyUnknown);
        }

        if (!creating && request.RowVersion is null)
        {
            return AccessResult.Invalid(AccessAdministrationService.RowVersionRequired);
        }

        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid companyId;
        if (creating)
        {
            companyId = companyOfNew!.Value;
        }
        else
        {
            var found = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select company_id from org.company_bank_accounts where id = $1 and not is_deleted", cancellationToken, bankAccountId).ConfigureAwait(false);
            if (found is not { } existing)
            {
                return AccessResult.NotFound;
            }

            companyId = existing;
        }

        if (await GuardAsync(c, tx, companyId, creating ? "org.company_bank_accounts.create" : "org.company_bank_accounts.update", cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var access = await fieldAccess.ForAsync(CompanyBankAccountAccess.Fields, creating ? "create" : "update", companyId, cancellationToken).ConfigureAwait(false);
        if ((number is not null && !access.CanEdit("account_number")) || ((iban is not null || request.ClearIban == true) && !access.CanEdit("iban")))
        {
            return AccessResult.Forbidden(FieldNotEditable);
        }

        var bankCode = request.BankCode?.Trim();
        if (bankCode is not null && await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from ref.banks where lower(code) = lower($1))", cancellationToken, bankCode).ConfigureAwait(false) is false)
        {
            return AccessResult.Invalid(BankUnknown);
        }

        var currency = request.CurrencyCode?.Trim().ToUpperInvariant();
        if (currency is not null && await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from ref.currencies where code = $1)", cancellationToken, currency).ConfigureAwait(false) is false)
        {
            return AccessResult.Invalid(CurrencyUnknown);
        }

        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var change = new BankAccountChange
        {
            Id = bankAccountId ?? Guid.CreateVersion7(now),
            CompanyId = companyId,
            Operation = creating ? "create" : "update",
            BankCode = bankCode,
            AccountTitle = title,
            CurrencyCode = currency,
            SalaryFileFormat = string.IsNullOrWhiteSpace(request.SalaryFileFormat) ? null : request.SalaryFileFormat.Trim(),
            AccountNumber = number is null ? null : encryptor.Encrypt(Encoding.UTF8.GetBytes(number), AccountNumberPurpose),
            AccountNumberLast4 = number is null ? null : number.Length <= 4 ? number : number[^4..],
            Iban = iban is null ? null : encryptor.Encrypt(Encoding.UTF8.GetBytes(iban), IbanPurpose),
            IbanHmac = iban is null ? null : encryptor.BlindIndex(Encoding.UTF8.GetBytes(iban), IbanPurpose),
            ClearIban = request.ClearIban == true && iban is null,
            RowVersion = request.RowVersion,
        };
        return await HoldOrApplyAsync(c, tx, change, AccessChange.SaveBankAccount, request.Reason, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A bank account change waits for a second administrator while maker-checker is on (FR-KRN-IAM-14, the same switch as
    /// the sensitive role changes: <c>iam.maker_checker.sensitive_roles</c>); otherwise it is applied at once.
    /// </summary>
    private async Task<AccessResult> HoldOrApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, BankAccountChange change, string kind, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (await CheckHeldAsync(c, tx, change, ct).ConfigureAwait(false) is { } problem)
            {
                return problem.StartsWith("errors.data.concurrency", StringComparison.Ordinal) ? AccessResult.Conflict(problem) : AccessResult.Invalid(problem);
            }

            AccessResult result;
            if (await AccessDb.SettingOnAsync(c, tx, IdentitySettings.MakerCheckerSensitiveRoles, ct).ConfigureAwait(false))
            {
                var held = new AccessChange { Kind = kind, BankAccount = change, CompanyId = change.CompanyId, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() };
                result = await AccessAdministrationService.SubmitChangeAsync(c, tx, held, now, actor.UserId, ct).ConfigureAwait(false);
            }
            else
            {
                result = await ApplyAsync(c, tx, change, now, actor.UserId, ct).ConfigureAwait(false);
            }

            if (result.Outcome is AccessOutcome.Done or AccessOutcome.Pending)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            return AccessResult.Invalid(ex.SqlState == PostgresErrorCodes.CheckViolation ? "errors.data.rule_violated" : "errors.data.reference_not_found");
        }
    }

    /// <summary>The checks of a bank account change that depend on the stored row: it exists and was not edited since it was read. Null when it can be applied.</summary>
    internal static async Task<string?> CheckHeldAsync(NpgsqlConnection c, NpgsqlTransaction tx, BankAccountChange change, CancellationToken ct)
    {
        if (change.Operation == "create")
        {
            return await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from org.companies where id = $1 and not is_deleted)", ct, change.CompanyId).ConfigureAwait(false)
                ? null : AccessAdministrationService.InvalidRequest;
        }

        var version = await IdentityDb.ScalarAsync<long?>(c, tx, "select row_version from org.company_bank_accounts where id = $1 and company_id = $2 and not is_deleted", ct, change.Id, change.CompanyId).ConfigureAwait(false);
        return version is null ? AccessAdministrationService.InvalidRequest
            : version != change.RowVersion ? AccessAdministrationService.ConcurrencyConflict : null;
    }

    /// <summary>Applies a bank account change in the caller's transaction, with its change-log entry (no encrypted values in it).</summary>
    internal static async Task<AccessResult> ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, BankAccountChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var changes = new List<(string Field, object? Before, object? After, bool Redacted)>();
        string action;
        if (change.Operation == "create")
        {
            action = "create";
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into org.company_bank_accounts (id, company_id, bank_code, account_title, account_number, iban, iban_hmac, currency_code, salary_file_format, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $10, $11)
                """, ct, change.Id, change.CompanyId, change.BankCode, change.AccountTitle, change.AccountNumber, change.Iban, change.IbanHmac, change.CurrencyCode, change.SalaryFileFormat, now, by).ConfigureAwait(false);
            changes.AddRange([("bank_code", null, change.BankCode, false), ("account_title", null, change.AccountTitle, false), ("account_number", null, null, true),
                ("currency_code", null, change.CurrencyCode, false), ("salary_file_format", null, change.SalaryFileFormat, false)]);
            if (change.Iban is not null)
            {
                changes.Add(("iban", null, null, true));
            }
        }
        else if (change.Operation == "delete")
        {
            action = "delete";
            var title = await IdentityDb.ScalarAsync<string>(c, tx, "select account_title from org.company_bank_accounts where id = $1", ct, change.Id).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, """
                update org.company_bank_accounts set is_deleted = true, deleted_at = $2, deleted_by = $3, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
                """, ct, change.Id, now, by).ConfigureAwait(false);
            changes.Add(("is_deleted", false, true, false));
            changes.Add(("account_title", title, null, false));
        }
        else
        {
            action = "update";
            await using (var find = IdentityDb.Command(c, tx, "select bank_code, account_title, currency_code, salary_file_format from org.company_bank_accounts where id = $1 for update", change.Id))
            await using (var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    void Diff(string field, string? before, string? after)
                    {
                        if (after is not null && after != before)
                        {
                            changes.Add((field, before, after, false));
                        }
                    }

                    Diff("bank_code", reader.GetString(0), change.BankCode);
                    Diff("account_title", reader.GetString(1), change.AccountTitle);
                    Diff("currency_code", reader.GetString(2), change.CurrencyCode);
                    Diff("salary_file_format", reader.IsDBNull(3) ? null : reader.GetString(3), change.SalaryFileFormat);
                }
            }

            await IdentityDb.ExecuteAsync(c, tx, """
                update org.company_bank_accounts set
                    bank_code = coalesce($2, bank_code), account_title = coalesce($3, account_title), currency_code = coalesce($4, currency_code),
                    salary_file_format = coalesce($5, salary_file_format), account_number = coalesce($6, account_number),
                    iban = case when $9 then null else coalesce($7, iban) end, iban_hmac = case when $9 then null else coalesce($8, iban_hmac) end,
                    updated_at = $10, updated_by = $11, row_version = row_version + 1
                where id = $1
                """, ct, change.Id, change.BankCode, change.AccountTitle, change.CurrencyCode, change.SalaryFileFormat, change.AccountNumber, change.Iban, change.IbanHmac, change.ClearIban, now, by).ConfigureAwait(false);
            if (change.AccountNumber is not null)
            {
                changes.Add(("account_number", null, null, true));
            }

            if (change.Iban is not null || change.ClearIban)
            {
                changes.Add(("iban", null, null, true));
            }
        }

        if (changes.Count > 0)
        {
            await WriteChangesAsync(c, tx, [Entry(BankAccountsResource, change.Id, change.CompanyId, action, now, by, changes)], ct).ConfigureAwait(false);
        }

        return AccessResult.Done(change.Id);
    }

    // ================================================================== holiday calendars

    public async Task<(AccessResult Result, CopyYearResult? Copied)> CopyYearAsync(Guid calendarId, CopyYearRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FromYear is not { } from || request.ToYear is not { } to || from == to || from is < 2000 or > 2100 || to is < 2000 or > 2100)
        {
            return (AccessResult.Invalid(YearInvalid), null);
        }

        if (!await caller.HoldsAsync("ref.holidays.create", cancellationToken).ConfigureAwait(false))
        {
            return (AccessResult.Forbidden("errors.access.forbidden"), null);
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await IdentityDb.ScalarAsync<string>(c, tx, "select source from ref.calendars where id = $1 for share", cancellationToken, calendarId).ConfigureAwait(false);
        if (source is null)
        {
            return (AccessResult.NotFound, null);
        }

        if (source == "pack")
        {
            return (AccessResult.Forbidden(CalendarIsPack), null);
        }

        var rows = new List<(DateOnly Date, string Names, string Kind)>();
        await using (var command = IdentityDb.Command(c, tx, "select holiday_date, name_i18n::text, kind from ref.holidays where calendar_id = $1 and extract(year from holiday_date) = $2 order by holiday_date", calendarId, from))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetString(1), reader.GetString(2)));
            }
        }

        int created = 0, skipped = 0;
        var entries = new List<ChangeEntry>();
        foreach (var (date, names, kind) in rows)
        {
            if (date.Month == 2 && date.Day == 29 && !DateTime.IsLeapYear(to))
            {
                skipped++;
                continue;
            }

            // A religious holiday follows the moon: the copied date is a guess until it is confirmed (FR-ADM-LOC-04).
            var tentative = kind == "religious";
            var target = new DateOnly(to, date.Month, date.Day);
            var id = Guid.CreateVersion7(now);
            var inserted = await IdentityDb.ExecuteAsync(c, tx, """
                insert into ref.holidays (id, calendar_id, holiday_date, name_i18n, kind, is_tentative, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $7, $8)
                on conflict (calendar_id, holiday_date) do nothing
                """, cancellationToken, id, calendarId, target, new JsonPayload(names), kind, tentative, now, by).ConfigureAwait(false);
            if (inserted == 0)
            {
                skipped++;
                continue;
            }

            created++;
            entries.Add(Entry("ref.holidays", id, null, "create", now, by, [("calendar_id", null, calendarId, false), ("holiday_date", null, target, false), ("kind", null, kind, false), ("is_tentative", null, tentative, false)]));
        }

        await WriteChangesAsync(c, tx, entries, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (AccessResult.Done(calendarId), new CopyYearResult(created, skipped));
    }

    // ================================================================== helpers

    /// <summary>404 when the company does not exist here, 403 when the caller's role for the permission does not cover it.</summary>
    private async Task<AccessResult?> GuardAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid companyId, string permission, CancellationToken ct)
    {
        if (!await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from org.companies where id = $1 and not is_deleted)", ct, companyId).ConfigureAwait(false))
        {
            return AccessResult.NotFound;
        }

        return await caller.CoversCompanyAsync(permission, companyId, ct).ConfigureAwait(false) ? null : AccessResult.Forbidden("errors.access.forbidden");
    }

    private static string Name(FieldAccessLevel level) => level switch
    {
        FieldAccessLevel.Hidden => "hidden",
        FieldAccessLevel.Masked => "masked",
        _ => "full",
    };

    /// <summary>
    /// The value as the person may see it. A value that cannot be decrypted (not written by these services, or under another
    /// key) is shown as hidden instead of failing the whole list.
    /// </summary>
    private static string? Shown(IFieldEncryptor encryptor, byte[] ciphertext, string purpose, FieldAccessLevel level)
    {
        if (level == FieldAccessLevel.Hidden)
        {
            return null;
        }

        string plain;
        try
        {
            plain = Encoding.UTF8.GetString(encryptor.Decrypt(ciphertext, purpose));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        return level == FieldAccessLevel.Masked ? FieldShaping.Mask(JsonValue.Create(plain))!.GetValue<string>() : plain;
    }

    /// <summary>The form a registration number is indexed in: without spaces, dashes or letter case, so the same number is found however it was typed.</summary>
    private static string NormalizeRegistration(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray().Select(char.ToUpperInvariant).ToArray());

    private static string NormalizeIban(string value) =>
        new(value.Where(ch => !char.IsWhiteSpace(ch)).Select(char.ToUpperInvariant).ToArray());

    /// <summary>ISO 13616: 15 to 34 letters and digits, country code and check digits first, remainder 1 when read as a number modulo 97.</summary>
    internal static bool IsValidIban(string iban)
    {
        if (iban.Length is < 15 or > 34 || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3])
            || !iban.All(char.IsAsciiLetterOrDigit))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var digits = char.IsAsciiDigit(ch) ? ch - '0' : ch - 'A' + 10;
            remainder = digits >= 10 ? ((remainder * 100) + digits) % 97 : ((remainder * 10) + digits) % 97;
        }

        return remainder == 1;
    }

    private static ChangeEntry Entry(string table, Guid id, Guid? companyId, string action, DateTimeOffset now, Guid by, IEnumerable<(string Field, object? Before, object? After, bool Redacted)> fields)
    {
        var changes = new JsonArray();
        foreach (var (field, before, after, redacted) in fields)
        {
            changes.Add(redacted
                ? new JsonObject { ["field"] = field, ["redacted"] = true }
                : new JsonObject
                {
                    ["field"] = field,
                    ["before"] = before is null ? null : JsonSerializer.SerializeToNode(before, Plain),
                    ["after"] = after is null ? null : JsonSerializer.SerializeToNode(after, Plain),
                });
        }

        var request = AuditAmbient.Current;
        var system = by == BaselineSeed.SystemUserId;
        return new ChangeEntry(Guid.CreateVersion7(now), now, system ? null : by, system ? ActorKinds.System : ActorKinds.User, table, id, companyId, action, changes, null,
            request?.CorrelationId, request?.IpAddress, system && request is null ? AuditSources.System : AuditAmbient.Source, by);
    }

    private static readonly JsonSerializerOptions Plain = new(JsonSerializerDefaults.Web);

    private static Task<int> WriteChangesAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<ChangeEntry> entries, CancellationToken ct) =>
        AuditDb.AppendChangesAsync(c, tx, entries, ct);
}
