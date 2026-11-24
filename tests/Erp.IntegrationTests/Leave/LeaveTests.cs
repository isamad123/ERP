#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Leave;

/// <summary>
/// The leave engine of slice S1.32a (Build items 1 to 6) through the real API pipeline, the real access engine, the real job worker and a real
/// PostgreSQL: FR-HRM-LVE-01 (leave types), FR-HRM-LVE-02 (entitlement models on a timeline: mid-year joiner, tier change, leaver pro-rata,
/// accrual by days worked, manual grant, opening cut-in, run-once), FR-HRM-LVE-03 (negative balance only within the policy's limit),
/// FR-HRM-LVE-10 with FR-XM-05 and -06 (an append-only ledger corrected by reversal; no login can change an entry), FR-HRM-LVE-14 (recompute
/// reports a planted difference and its cause and changes nothing) and FR-XM-26 (no entry dated in a locked attendance period). Dates are
/// relative to the company's today, so the tests hold on any day; "previous year" is the leave year before the current one (January start).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class LeaveTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Types = "/api/hrm/leave-types";
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The company's business date (default time zone Asia/Karachi), as the leave services use it.</summary>
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private int Prev => Today.Year - 1;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Admin);

    /// <summary>A company of its own and an HR administrator (the baseline role, which S1.32a gives the leave permissions).</summary>
    private async Task<Org> OrgAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        return new Org(company, await _h.PlaceAsync(company, Ct), await _h.UserAsync(Ct, null, "hr_admin"));
    }

    private async Task<Guid> HireAsync(Org o, DateOnly joining, Guid? userRef = null)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Admin, Employments, EmploymentTestSupport.HireBody(o.Company, o.Place, person: person, joining: Iso(joining)), Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var id = answer.Json.GetProperty("id").GetGuid();
        if (userRef is { } user)
        {
            await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, user);
        }

        return id;
    }

    private async Task<(Guid Id, long Version)> TypeAsync(Org o, string code, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["companyId"] = o.Company, ["code"] = code, ["nameI18n"] = new { en = "Leave " + code, ur = "رخصت " + code }, ["isPaid"] = true, ["counting"] = "working_days",
            ["unit"] = "day", ["requiresDocument"] = false, ["isCompOff"] = false, ["isUnpaidDeduction"] = false,
        };
        foreach (var p in extra is null ? [] : JsonSerializer.SerializeToElement(extra, JsonSerializerOptions.Web).EnumerateObject())
        {
            body[p.Name] = p.Value;
        }

        var answer = await _h.PostAsync(o.Admin, Types, body, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Json.GetProperty("rowVersion").GetInt64());
    }

    /// <summary>A leave policy for every permanent employee of the company from long before the horizon; returns the policy id.</summary>
    private async Task<Guid> PolicyAsync(Org o, object types)
    {
        var from = new DateOnly(Prev - 3, 1, 1);
        var (policy, _) = await _h.ActivePolicyAsync(o.Admin, o.Company, "leave", new { types }, from, Ct);
        var set = await _h.ActivateSetAsync(o.Admin, o.Company, "leave", from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return policy;
    }

    private Task<Answer> PostEntryAsync(Guid user, Guid employment, Guid type, string kind, decimal quantity, DateOnly date, string reason = "Taken over from the old system") =>
        _h.PostAsync(user, $"{Employments}/{employment}/leave-ledger", new { leaveTypeId = type, kind, quantity, entryDate = Iso(date), reason }, Ct);

    private async Task<JsonElement> BalanceAsync(Guid user, Guid employment, Guid type, DateOnly? asOf = null)
    {
        var answer = await _h.GetAsync(user, $"{Employments}/{employment}/leave-balances?leaveTypeId={type}" + (asOf is { } d ? "&asOf=" + Iso(d) : string.Empty), Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.GetProperty("balances")[0];
    }

    private Task<long> CountAsync(Guid employment) => _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where employment_id = $1", Ct, employment);

    private Task<decimal> SumAsync(Guid employment, Guid type, int year) =>
        _h.ScalarAsync<decimal>("select coalesce(sum(quantity), 0) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2 and leave_year = $3", Ct, employment, type, year);

    /// <summary>Runs a leave job for one employment in a real worker; <paramref name="locked"/> puts the test double of the period lock in (S1.31 brings the real lock).</summary>
    private async Task<List<string>> RunJobAsync(string job, Guid employment, bool locked = false)
    {
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker(extra: locked ? s => { s.RemoveAll<IAttendancePeriodLock>(); s.AddScoped<IAttendancePeriodLock, EverythingLocked>(); } : null);
        var run = await rig.QueueAsync(job, new { employment_id = employment }, ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        var logs = await rig.LogsAsync(run, Ct);
        Assert.True(await rig.StatusAsync(run, Ct) == "succeeded", string.Join("\n", logs));
        return logs;
    }

    /// <summary>The test double of the attendance lock (slice S1.32 ruling Q9): every date is locked.</summary>
    private sealed class EverythingLocked : IAttendancePeriodLock
    {
        public Task<bool> IsLockedAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>A new version of a leave policy with these types, activated from a date (after the version in force started).</summary>
    private async Task NewVersionAsync(Org o, Guid policy, object types, DateOnly from)
    {
        var draft = await _h.PostAsync(o.Admin, $"{PolicyTestSupport.Policies}/{policy}/versions", new { parameters = new { types } }, Ct);
        Assert.True(draft.Status is HttpStatusCode.OK or HttpStatusCode.Created, draft.Body);
        var draftId = await _h.ScalarAsync<Guid>("select id from hrm.policy_versions where policy_id = $1 and status = 'draft'", Ct, policy);
        var draftVersion = await _h.ScalarAsync<long>("select row_version from hrm.policy_versions where id = $1", Ct, draftId);
        var activated = await _h.PostAsync(o.Admin, $"/api/hrm/policy-versions/{draftId}/activate", new { effectiveFrom = Iso(from), rowVersion = draftVersion }, Ct);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
    }

    private async Task<List<JsonElement>> RecomputeAsync(Org o, Guid e)
    {
        var report = await _h.GetAsync(o.Admin, $"{Employments}/{e}/leave-recompute", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        return [.. report.Json.GetProperty("differences").EnumerateArray()];
    }

    /// <summary>Month ends from the first month to today (the accruals due).</summary>
    private int MonthEndsFrom(DateOnly firstMonth)
    {
        var n = 0;
        for (var m = new DateOnly(firstMonth.Year, firstMonth.Month, 1); m.AddMonths(1).AddDays(-1) <= Today; m = m.AddMonths(1))
        {
            n++;
        }

        return n;
    }

    // ------------------------------------------------------------------ leave types (FR-HRM-LVE-01)

    [Fact]
    public async Task FR_HRM_LVE_01_leave_types_are_created_changed_and_retired_with_their_rules_checked()
    {
        var o = await OrgAsync();
        var (annual, version) = await TypeAsync(o, "ANNUAL");
        var hourly = await TypeAsync(o, "SHORT", new { counting = "hours", unit = "hour" });
        var unpaid = await TypeAsync(o, "LWP", new { isPaid = false, isUnpaidDeduction = true, requiresDocument = true });
        var read = await _h.GetAsync(o.Admin, $"{Types}/{unpaid.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.False(read.Json.GetProperty("isPaid").GetBoolean());
        Assert.True(read.Json.GetProperty("isUnpaidDeduction").GetBoolean());
        Assert.Equal("tenant", read.Json.GetProperty("source").GetString());
        Assert.Equal("hour", (await _h.GetAsync(o.Admin, $"{Types}/{hourly.Id}", Ct)).Json.GetProperty("unit").GetString());

        // The rules between the fields: hours go with the unit hour; a paid type is never a deduction; names in both languages; codes unique.
        var refused = await _h.PostAsync(o.Admin, Types, new { companyId = o.Company, code = "BAD", nameI18n = new { en = "Bad", ur = "خراب" }, isPaid = true, counting = "hours", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.hrm.leave.hours_unit", refused.Body, StringComparison.Ordinal);
        refused = await _h.PostAsync(o.Admin, Types, new { companyId = o.Company, code = "BAD", nameI18n = new { en = "Bad", ur = "خراب" }, isPaid = true, counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = true }, Ct);
        Assert.Contains("errors.hrm.leave.paid_deduction", refused.Body, StringComparison.Ordinal);
        refused = await _h.PostAsync(o.Admin, Types, new { companyId = o.Company, code = "BAD", nameI18n = new { en = "Bad" }, isPaid = true, counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false }, Ct);
        Assert.Contains("errors.hrm.leave.name_required", refused.Body, StringComparison.Ordinal);
        refused = await _h.PostAsync(o.Admin, Types, new { companyId = o.Company, code = "annual", nameI18n = new { en = "Again", ur = "دوبارہ" }, isPaid = true, counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false }, Ct);
        Assert.Contains("errors.hrm.leave.code_taken", refused.Body, StringComparison.Ordinal);

        // A change, then retirement (never deletion) and back; the generated delete is closed.
        var changed = await _h.PutAsync(o.Admin, $"{Types}/{annual}", new { requiresDocument = true, nameI18n = new { en = "Annual leave", ur = "سالانہ رخصت" }, rowVersion = version }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.True(changed.Json.GetProperty("requiresDocument").GetBoolean());
        var retired = await _h.PutAsync(o.Admin, $"{Types}/{annual}", new { isActive = false, rowVersion = changed.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.False(retired.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(o.Admin, $"{Types}/{annual}", new { isActive = true, rowVersion = version }, Ct)).Status); // stale row version
        Assert.True((await _h.PutAsync(o.Admin, $"{Types}/{annual}", new { isActive = true, rowVersion = retired.Json.GetProperty("rowVersion").GetInt64() }, Ct)).Json.GetProperty("isActive").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, (await _h.DeleteAsync(o.Admin, $"{Types}/{annual}", Ct)).Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_types where id = $1 and not is_deleted", Ct, annual));

        // Without the permission: refused.
        var nobody = await _h.UserAsync(Ct);
        await _h.AssignAsync(nobody, await _h.RoleAsync(Ct, ["hrm.leave_types.read"], [("*", "all")]), null, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Types, new { companyId = o.Company, code = "X" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(nobody, $"{Types}/{annual}", new { isActive = false, rowVersion = 1 }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_LVE_01_code_counting_and_unit_are_fixed_once_a_type_has_ledger_entries()
    {
        var o = await OrgAsync();
        var (type, version) = await TypeAsync(o, "SICK");
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        var moved = await _h.PutAsync(o.Admin, $"{Types}/{type}", new { code = "SICK2", rowVersion = version }, Ct); // no entries yet: allowed
        Assert.True(moved.Status == HttpStatusCode.OK, moved.Body);
        version = moved.Json.GetProperty("rowVersion").GetInt64();
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, type, "opening", 4m, new DateOnly(Prev, 1, 1))).Status);

        var refused = await _h.PutAsync(o.Admin, $"{Types}/{type}", new { unit = "half_day", rowVersion = version }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.hrm.leave.type_fixed", refused.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.type_fixed", (await _h.PutAsync(o.Admin, $"{Types}/{type}", new { code = "OTHER", rowVersion = version }, Ct)).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(o.Admin, $"{Types}/{type}", new { requiresDocument = true, rowVersion = version }, Ct)).Status); // other fields still change

        // The database repeats the rule for any writer.
        var db = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.leave_types set counting = 'calendar_days' where id = $1", Ct, type));
        Assert.Equal("ck_leave_types_rules", db.ConstraintName);
        db = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.leave_types set counting = 'hours', unit = 'hour' where id = $1", Ct, type));
        Assert.Equal("ck_leave_types_rules", db.ConstraintName);
    }

    // ------------------------------------------------------------------ ledger (FR-HRM-LVE-10, FR-XM-05, FR-XM-06)

    [Fact]
    public async Task FR_HRM_LVE_10_FR_XM_06_entries_are_posted_with_their_source_and_corrected_only_by_reversal()
    {
        var o = await OrgAsync();
        var (type, _) = await TypeAsync(o, "ANNUAL");
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        var policy = await PolicyAsync(o, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "manual" } } });
        var start = new DateOnly(Today.Year, 1, 1);

        var opening = await PostEntryAsync(o.Admin, e, type, "opening", 6.5m, start);
        Assert.True(opening.Status == HttpStatusCode.Created, opening.Body);
        Assert.Equal("hrm.leave_manual", opening.Json.GetProperty("sourceType").GetString());
        Assert.Equal(Today.Year, opening.Json.GetProperty("leaveYear").GetInt32());
        var policyVersion = await _h.ScalarAsync<Guid>("select id from hrm.policy_versions where policy_id = $1", Ct, policy);
        Assert.Equal(policyVersion, opening.Json.GetProperty("policyVersionId").GetGuid()); // names the policy version in force
        var grant = await PostEntryAsync(o.Admin, e, type, "grant", 2m, start, "Long service award");
        Assert.Equal(HttpStatusCode.Created, grant.Status);
        var adjustment = await PostEntryAsync(o.Admin, e, type, "adjustment", -1.25m, start, "Correction of the old system's count");
        Assert.Equal(HttpStatusCode.Created, adjustment.Status);

        var balance = await BalanceAsync(o.Admin, e, type);
        Assert.Equal(7.25m, balance.GetProperty("balance").GetDecimal());
        Assert.Equal(3, balance.GetProperty("entries").GetArrayLength());
        Assert.Equal(-1.25m, balance.GetProperty("totals").GetProperty("adjustment").GetDecimal());
        Assert.Equal("ANNUAL", balance.GetProperty("code").GetString());

        // A second opening is refused; a manual usage or accrual is not something HR posts.
        Assert.Contains("errors.hrm.leave.opening_exists", (await PostEntryAsync(o.Admin, e, type, "opening", 1m, start)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.kind_manual", (await PostEntryAsync(o.Admin, e, type, "usage", -1m, start)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.before_joining", (await PostEntryAsync(o.Admin, e, type, "grant", 1m, new DateOnly(Prev - 1, 6, 1))).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.quantity", (await PostEntryAsync(o.Admin, e, type, "grant", 0.12345m, start)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.reason_required", (await PostEntryAsync(o.Admin, e, type, "grant", 1m, start, " ")).Body, StringComparison.Ordinal);

        // Correction by reversal: a new entry of the opposite quantity naming the one it reverses; never twice; a reversal is not reversed.
        var grantId = grant.Json.GetProperty("id").GetGuid();
        var reversal = await _h.PostAsync(o.Admin, $"/api/hrm/leave-ledger/{grantId}/reverse", new { reason = "Granted to the wrong person" }, Ct);
        Assert.True(reversal.Status == HttpStatusCode.Created, reversal.Body);
        Assert.Equal(-2m, reversal.Json.GetProperty("quantity").GetDecimal());
        Assert.Equal(grantId, reversal.Json.GetProperty("reversesEntryId").GetGuid());
        Assert.Equal("grant", reversal.Json.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(o.Admin, $"/api/hrm/leave-ledger/{grantId}/reverse", new { reason = "again" }, Ct)).Status);
        Assert.Contains("errors.hrm.leave.not_reversible", (await _h.PostAsync(o.Admin, $"/api/hrm/leave-ledger/{reversal.Json.GetProperty("id").GetGuid()}/reverse", new { reason = "undo" }, Ct)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.reversal_date", (await _h.PostAsync(o.Admin, $"/api/hrm/leave-ledger/{opening.Json.GetProperty("id").GetGuid()}/reverse", new { reason = "x", entryDate = Iso(start.AddDays(-1)) }, Ct)).Body, StringComparison.Ordinal);

        balance = await BalanceAsync(o.Admin, e, type);
        Assert.Equal(5.25m, balance.GetProperty("balance").GetDecimal());
        var reversed = balance.GetProperty("entries").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == grantId);
        Assert.Equal(reversal.Json.GetProperty("id").GetGuid(), reversed.GetProperty("reversedById").GetGuid());
        Assert.Equal(4L, await CountAsync(e)); // nothing was edited: four entries
        Assert.Equal(6.5m, await _h.ScalarAsync<decimal>("select quantity from hrm.leave_ledger where id = $1", Ct, opening.Json.GetProperty("id").GetGuid()));

        // As of the day before the year started, the year's entries do not count (balances are per leave year and date).
        Assert.Equal(0m, (await BalanceAsync(o.Admin, e, type, start.AddDays(-1))).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task FR_HRM_LVE_10_FR_XM_05_no_login_can_update_or_delete_a_ledger_entry_and_a_reversal_must_be_exact()
    {
        var o = await OrgAsync();
        var (type, _) = await TypeAsync(o, "ANNUAL");
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        var entry = (await PostEntryAsync(o.Admin, e, type, "grant", 3m, new DateOnly(Prev, 2, 1))).Json.GetProperty("id").GetGuid();

        foreach (var login in new[] { Erp.Kernel.Infrastructure.ControlPlane.TenantLogins.Owner, Erp.Kernel.Infrastructure.ControlPlane.TenantLogins.ReadWrite })
        {
            await using var c = await fx.OpenTenantAsync(_h.Code, login, Ct);
            foreach (var sql in new[] { "update hrm.leave_ledger set quantity = 30 where id = $1", "delete from hrm.leave_ledger where id = $1" })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, sql, Ct, entry));
                Assert.True(refused.SqlState is "ER001" or PostgresErrorCodes.InsufficientPrivilege, login + ": " + refused.SqlState);
            }
        }

        Assert.Equal(3m, await _h.ScalarAsync<decimal>("select quantity from hrm.leave_ledger where id = $1", Ct, entry));
        const string insert = """
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, source_type, reverses_entry_id, created_at, created_by)
            select $2, company_id, employment_id, leave_type_id, leave_year, entry_date, $3, $4, 'test', id, now(), created_by from hrm.leave_ledger where id = $1
            """;
        foreach (var (kind, quantity) in new[] { ("grant", -2m), ("adjustment", -3m), ("grant", 3m) })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(insert, Ct, entry, Guid.CreateVersion7(), kind, quantity));
            Assert.Equal("ck_leave_ledger_reversal", refused.ConstraintName);
        }

        var zero = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(
            "insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, created_at, created_by) select $2, company_id, employment_id, leave_type_id, leave_year, entry_date, 'adjustment', 0, now(), created_by from hrm.leave_ledger where id = $1",
            Ct, entry, Guid.CreateVersion7()));
        Assert.Equal("ck_leave_ledger_reversal", zero.ConstraintName);
    }

    // ------------------------------------------------------------------ negative balance (FR-HRM-LVE-03) and the lock (FR-XM-26)

    [Fact]
    public async Task FR_HRM_LVE_03_a_negative_balance_is_refused_unless_the_policy_allows_it_and_then_only_to_its_limit()
    {
        var o = await OrgAsync();
        var (strict, _) = await TypeAsync(o, "STRICT");
        var (loose, _) = await TypeAsync(o, "LOOSE");
        var (none, _) = await TypeAsync(o, "NONE");
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        await PolicyAsync(o, new object[]
        {
            new { leaveType = "STRICT", entitlement = new { model = "manual" } },
            new { leaveType = "LOOSE", entitlement = new { model = "manual" }, negativeLimit = 2 },
        });
        var day = new DateOnly(Today.Year, 1, 1);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, strict, "grant", 1m, day)).Status);
        var refused = await PostEntryAsync(o.Admin, e, strict, "adjustment", -1.5m, day);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("errors.hrm.leave.below_allowed", refused.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, strict, "adjustment", -1m, day)).Status); // down to zero is fine

        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, loose, "adjustment", -2m, day)).Status); // within the limit
        Assert.Equal(HttpStatusCode.Conflict, (await PostEntryAsync(o.Admin, e, loose, "adjustment", -0.5m, day)).Status);
        var balance = await BalanceAsync(o.Admin, e, loose);
        Assert.Equal(-2m, balance.GetProperty("balance").GetDecimal());
        Assert.Equal(2m, balance.GetProperty("negativeLimit").GetDecimal());

        // A type the policy does not list: no leave policy applies (the answer requests will give, S1.23 ruling), and never below zero.
        var noPolicy = await BalanceAsync(o.Admin, e, none);
        Assert.Equal("type_not_in_policy", noPolicy.GetProperty("noPolicy").GetProperty("reason").GetString());
        Assert.Equal("errors.hrm.leave.no_policy", noPolicy.GetProperty("noPolicy").GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PostEntryAsync(o.Admin, e, none, "adjustment", -1m, day)).Status);

        // A later negative entry may not dig into a balance a later usage already relies on: the lowest point after it counts.
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, strict, "grant", 2m, day.AddDays(1))).Status);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, strict, "adjustment", -2m, day.AddDays(2))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostEntryAsync(o.Admin, e, strict, "adjustment", -1m, day.AddDays(1))).Status);
    }

    [Fact]
    public async Task FR_XM_26_an_entry_dated_in_a_locked_attendance_period_is_refused()
    {
        var o = await OrgAsync();
        var (type, _) = await TypeAsync(o, "ANNUAL");
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        var start = new DateOnly(Prev, 3, 1);
        var end = new DateOnly(Prev, 3, 31);
        var march = await PostEntryAsync(o.Admin, e, type, "grant", 1m, new DateOnly(Prev, 3, 10)); // before payroll locked March
        Assert.Equal(HttpStatusCode.Created, march.Status);
        var group = (Guid)(await _h.RowAsync("hrm.pay_groups", o.Company, null, Ct))["id"]!;
        var period = (Guid)(await _h.RowAsync("hrm.pay_periods", o.Company, new Dictionary<string, object?> { ["pay_group_id"] = group, ["start_date"] = start, ["end_date"] = end }, Ct))["id"]!;
        await _h.RowAsync("hrm.attendance_periods", o.Company, new Dictionary<string, object?>
        {
            ["pay_group_id"] = group, ["pay_period_id"] = period, ["period_start"] = start, ["period_end"] = end, ["status"] = "locked",
        }, Ct);
        await _h.ExecAsync("update hrm.employment_assignments set pay_group_id = $2 where employment_id = $1", Ct, e, group);

        var refused = await PostEntryAsync(o.Admin, e, type, "grant", 1m, new DateOnly(Prev, 3, 15));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.hrm.leave.period_locked", refused.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, type, "grant", 1m, new DateOnly(Prev, 4, 1))).Status); // the next open period

        // The March entry is corrected in the next open period, never inside the lock.
        var reverse = $"/api/hrm/leave-ledger/{march.Json.GetProperty("id").GetGuid()}/reverse";
        Assert.Contains("errors.hrm.leave.period_locked", (await _h.PostAsync(o.Admin, reverse, new { reason = "Wrong month" }, Ct)).Body, StringComparison.Ordinal);
        var reversed = await _h.PostAsync(o.Admin, reverse, new { reason = "Wrong month", entryDate = Iso(new DateOnly(Prev, 4, 2)) }, Ct);
        Assert.True(reversed.Status == HttpStatusCode.Created, reversed.Body);
        Assert.Equal(3L, await CountAsync(e));
    }

    // ------------------------------------------------------------------ entitlement models and the accrual job (FR-HRM-LVE-02)

    [Fact]
    public async Task FR_HRM_LVE_02_a_yearly_grant_and_a_monthly_accrual_follow_the_policy_for_a_mid_year_joiner_and_post_once()
    {
        var o = await OrgAsync();
        var (annual, _) = await TypeAsync(o, "ANNUAL");
        var (casual, _) = await TypeAsync(o, "CASUAL");
        var (retired, retiredVersion) = await TypeAsync(o, "OLD");
        await _h.PutAsync(o.Admin, $"{Types}/{retired}", new { isActive = false, rowVersion = retiredVersion }, Ct);
        await PolicyAsync(o, new object[]
        {
            new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "months_started" } },
            new { leaveType = "CASUAL", entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "days" } },
            new { leaveType = "OLD", entitlement = new { model = "fixed_yearly", quantity = 5, proRata = "none" } },
        });
        var joiner = await HireAsync(o, new DateOnly(Prev, 7, 1));
        var midMonth = await HireAsync(o, new DateOnly(Prev, 10, 16));

        await RunJobAsync("hrm.leave_accrual", joiner);
        await RunJobAsync("hrm.leave_accrual", midMonth);
        var count = await CountAsync(joiner);
        Assert.Equal(6m, await SumAsync(joiner, annual, Prev)); // joined in July: six months started of twelve
        Assert.Equal(12m, await SumAsync(joiner, annual, Today.Year));
        Assert.Equal(6m, await SumAsync(joiner, casual, Prev));
        Assert.Equal(MonthEndsFrom(new DateOnly(Today.Year, 1, 1)), await SumAsync(joiner, casual, Today.Year));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2", Ct, joiner, retired)); // retired: nothing new
        Assert.Equal(decimal.Round(16m / 31m, 4), await _h.ScalarAsync<decimal>(
            "select quantity from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2 and entry_date = $3", Ct, midMonth, casual, new DateOnly(Prev, 10, 31))); // 16 of October's 31 days

        // Each entry names the job as its source, the period and the policy version; the yearly grant is dated on joining.
        Assert.Equal(1L, await _h.ScalarAsync<long>(
            "select count(*) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2 and kind = 'grant' and entry_date = $3 and source_type = 'hrm.leave_accrual' and reason = $4 and policy_version_id is not null",
            Ct, joiner, annual, new DateOnly(Prev, 7, 1), "Y" + Prev));

        // Run once: a second run posts nothing.
        await RunJobAsync("hrm.leave_accrual", joiner);
        Assert.Equal(count, await CountAsync(joiner));

        var balance = await BalanceAsync(o.Admin, joiner, casual);
        Assert.All(balance.GetProperty("entries").EnumerateArray(), x => Assert.Equal("hrm.leave.entry.automatic_accrual", x.GetProperty("explanation").GetString()));
        Assert.Equal(1, balance.GetProperty("policy").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task FR_HRM_LVE_02_tenure_tiers_change_with_service_grade_tiers_follow_the_grade_and_a_manual_type_waits_for_HR()
    {
        var o = await OrgAsync();
        var (tenure, _) = await TypeAsync(o, "TENURE");
        var (grade, _) = await TypeAsync(o, "GRADE");
        var (manual, _) = await TypeAsync(o, "SPECIAL");
        await PolicyAsync(o, new object[]
        {
            new { leaveType = "TENURE", entitlement = new { model = "tenure_tiers", frequency = "yearly", proRata = "none", tiers = new object[] { new { serviceMonthsFrom = 0, quantity = 10 }, new { serviceMonthsFrom = 12, quantity = 15 } } } },
            new { leaveType = "GRADE", entitlement = new { model = "grade_tiers", frequency = "monthly", proRata = "none", tiers = new object[] { new { grade = "G1", quantity = 1 }, new { grade = "G2", quantity = 2 } } } },
            new { leaveType = "SPECIAL", entitlement = new { model = "manual" } },
        });
        // Joined in February two years before: 11 months of service when the previous year starts, 23 when this one does.
        var e = await HireAsync(o, new DateOnly(Prev - 1, 2, 1));
        var g2 = (Guid)(await _h.RowAsync("hrm.grades", o.Company, new Dictionary<string, object?> { ["code"] = "G2" }, Ct))["id"]!;
        await _h.ExecAsync("update hrm.employment_assignments set grade_id = $2 where employment_id = $1", Ct, e, g2);

        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(10m, await SumAsync(e, tenure, Prev));
        Assert.Equal(15m, await SumAsync(e, tenure, Today.Year)); // the tier changed with service
        Assert.Equal(24m, await SumAsync(e, grade, Prev)); // two a month for G2
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2", Ct, e, manual));
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, manual, "grant", 3m, new DateOnly(Today.Year, 1, 1), "Marriage leave")).Status); // manual grant
        Assert.Equal(3m, (await BalanceAsync(o.Admin, e, manual)).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task FR_HRM_LVE_02_a_leaver_gets_one_pro_rata_correction_of_the_yearly_grant()
    {
        var o = await OrgAsync();
        var (annual, _) = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "months_completed" } } });
        var e = await HireAsync(o, new DateOnly(Prev - 2, 1, 1));
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(12m, await SumAsync(e, annual, Prev));

        // The employment ended on 30 June of the previous year, after the grant was given: one correction for the six months not worked.
        await _h.ExecAsync("update hrm.employments set separation_date = $2, status = 'separated' where id = $1", Ct, e, new DateOnly(Prev, 6, 30));
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(6m, await SumAsync(e, annual, Prev));
        Assert.Equal(-6m, await _h.ScalarAsync<decimal>("select quantity from hrm.leave_ledger where employment_id = $1 and reason = $2", Ct, e, $"Y{Prev}/leaver"));
        Assert.Equal(new DateOnly(Prev, 6, 30), await _h.ScalarAsync<DateOnly>("select entry_date from hrm.leave_ledger where employment_id = $1 and reason = $2", Ct, e, $"Y{Prev}/leaver"));
        var count = await CountAsync(e);
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(count, await CountAsync(e));

        // Nothing is reversed after the employee's last day (review S1.32a finding 11).
        var grantId = await _h.ScalarAsync<Guid>("select id from hrm.leave_ledger where employment_id = $1 and reason = $2", Ct, e, "Y" + Today.Year);
        Assert.Contains("errors.hrm.leave.after_leaving", (await _h.PostAsync(o.Admin, $"/api/hrm/leave-ledger/{grantId}/reverse", new { reason = "left last year" }, Ct)).Body, StringComparison.Ordinal);

        // This year's grant was given before the end was recorded: the job never takes it back by itself; the recompute shows it for HR.
        Assert.Equal(12m, await SumAsync(e, annual, Today.Year));
        var report = await _h.GetAsync(o.Admin, $"{Employments}/{e}/leave-recompute", Ct);
        Assert.Contains(report.Json.GetProperty("differences").EnumerateArray(), d => d.GetProperty("kind").GetString() == "unexpected" && d.GetProperty("period").GetString() == "Y" + Today.Year);
        Assert.DoesNotContain(report.Json.GetProperty("differences").EnumerateArray(), d => d.GetProperty("period").GetString() == "Y" + Prev);
    }

    [Fact]
    public async Task FR_HRM_LVE_02_accrual_by_days_worked_counts_only_months_whose_attendance_is_locked()
    {
        var o = await OrgAsync();
        var (worked, _) = await TypeAsync(o, "WORKED");
        await PolicyAsync(o, new[] { new { leaveType = "WORKED", entitlement = new { model = "per_days_worked", quantity = 0.5, per = 1 } } });
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        var period = (Guid)(await _h.RowAsync("hrm.attendance_periods", o.Company, null, Ct))["id"]!;
        foreach (var (day, result) in new[] { (1, "present"), (2, "late"), (3, "on_duty"), (4, "half_day"), (5, "absent") })
        {
            await _h.RowAsync("hrm.attendance_days", o.Company, new Dictionary<string, object?>
            {
                ["employment_id"] = e, ["work_date"] = new DateOnly(Prev, 12, day), ["result"] = result, ["day_type"] = "working", ["worked_minutes"] = 480, ["period_id"] = period,
            }, Ct);
        }

        await RunJobAsync("hrm.leave_accrual", e); // nothing is locked before S1.31: nothing is final, nothing is accrued
        Assert.Equal(0L, await CountAsync(e));
        await RunJobAsync("hrm.leave_accrual", e, locked: true); // the test double locks every period
        Assert.Equal(1.75m, await _h.ScalarAsync<decimal>("select quantity from hrm.leave_ledger where employment_id = $1 and reason = $2", Ct, e, $"M{Prev}-12")); // 3.5 days × 0.5
        Assert.Equal(1L, await CountAsync(e)); // months without worked days earn nothing
    }

    [Fact]
    public async Task FR_HRM_LVE_02_an_opening_balance_is_a_cut_in_and_earlier_accruals_are_not_posted_again()
    {
        var o = await OrgAsync();
        var (casual, _) = await TypeAsync(o, "CASUAL");
        var (annual, _) = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, new object[]
        {
            new { leaveType = "CASUAL", entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "none" } },
            new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "none" } },
        });
        var e = await HireAsync(o, new DateOnly(Prev - 3, 1, 1));
        var cutIn = new DateOnly(Today.Year, 1, 1);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, casual, "opening", 9m, cutIn)).Status);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, e, annual, "opening", 4m, cutIn)).Status);
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(0m, await SumAsync(e, casual, Prev)); // the previous year is inside the opening
        Assert.Equal(9m + MonthEndsFrom(cutIn), await SumAsync(e, casual, Today.Year));
        // An opening is the balance before its own day's entitlements: the yearly grant due that day is added (review S1.32a finding 6).
        Assert.Equal(0m, await SumAsync(e, annual, Prev));
        Assert.Equal(16m, await SumAsync(e, annual, Today.Year));
    }

    [Fact]
    public async Task FR_HRM_LVE_02_a_leave_policy_that_cannot_be_applied_is_refused_when_saved()
    {
        var o = await OrgAsync();
        var refused = await _h.PostAsync(o.Admin, PolicyTestSupport.Policies, new
        {
            companyId = o.Company, policyType = "leave", code = "BADLEAVE", name = new { en = "Bad", ur = "خراب" },
            parameters = new { types = new[] { new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", proRata = "none" } } } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("parameters.types[0].entitlement.quantity", refused.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ recompute (FR-HRM-LVE-14)

    [Fact]
    public async Task FR_HRM_LVE_14_recompute_reports_planted_differences_with_their_cause_and_changes_nothing()
    {
        var o = await OrgAsync();
        var (casual, _) = await TypeAsync(o, "CASUAL");
        var policy = await PolicyAsync(o, new[] { new { leaveType = "CASUAL", entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "none" } } });
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        await RunJobAsync("hrm.leave_accrual", e);
        var clean = await _h.GetAsync(o.Admin, $"{Employments}/{e}/leave-recompute", Ct);
        Assert.True(clean.Status == HttpStatusCode.OK, clean.Body);
        Assert.Equal(0, clean.Json.GetProperty("differences").GetArrayLength());
        Assert.True(clean.Json.GetProperty("checked").GetInt32() >= 12);

        // Planted: version 2 of the policy from July of the previous year doubles the accrual (a backdated change the ledger does not follow);
        // an automatic entry the policy never called for; a balance below zero.
        var draft = await _h.PostAsync(o.Admin, $"{PolicyTestSupport.Policies}/{policy}/versions",
            new { parameters = new { types = new[] { new { leaveType = "CASUAL", entitlement = new { model = "monthly_accrual", quantity = 2, proRata = "none" } } } } }, Ct);
        Assert.True(draft.Status is HttpStatusCode.OK or HttpStatusCode.Created, draft.Body);
        var draftId = draft.Json.TryGetProperty("versions", out var vs) ? vs.EnumerateArray().Single(v => v.GetProperty("status").GetString() == "draft").GetProperty("id").GetGuid() : draft.Json.GetProperty("id").GetGuid();
        var draftVersion = await _h.ScalarAsync<long>("select row_version from hrm.policy_versions where id = $1", Ct, draftId);
        var activated = await _h.PostAsync(o.Admin, $"/api/hrm/policy-versions/{draftId}/activate", new { effectiveFrom = Iso(new DateOnly(Prev, 7, 1)), rowVersion = draftVersion }, Ct);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
        var created = await _h.ScalarAsync<Guid>("select created_by from hrm.leave_ledger where employment_id = $1 limit 1", Ct, e);
        await _h.ExecAsync("""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, source_type, source_id, reason, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, 'grant', 3, 'hrm.leave_accrual', $7, $8, now(), $9)
            """, Ct, Guid.CreateVersion7(), o.Company, e, casual, Prev, new DateOnly(Prev, 1, 1), Guid.CreateVersion7(), "Y" + Prev, created);
        await _h.ExecAsync("""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, source_type, reason, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, 'adjustment', -100, 'test', 'planted', now(), $7)
            """, Ct, Guid.CreateVersion7(), o.Company, e, casual, Prev, new DateOnly(Prev, 12, 31), created);
        var before = await CountAsync(e);

        var report = await _h.GetAsync(o.Admin, $"{Employments}/{e}/leave-recompute", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        var differences = report.Json.GetProperty("differences").EnumerateArray().ToList();
        var july = differences.Single(d => d.GetProperty("period").GetString() == $"M{Prev}-07");
        Assert.Equal("differs", july.GetProperty("kind").GetString());
        Assert.Equal(2m, july.GetProperty("expected").GetDecimal());
        Assert.Equal(1m, july.GetProperty("posted").GetDecimal());
        Assert.Equal(2, july.GetProperty("policy").GetProperty("version").GetInt32()); // the cause: version 2 now calls for 2
        var entry = july.GetProperty("entries")[0];
        Assert.Equal(await _h.ScalarAsync<Guid>("select id from hrm.policy_versions where policy_id = $1 and version = 1", Ct, policy), entry.GetProperty("policyVersionId").GetGuid()); // posted under version 1
        Assert.Equal("hrm.leave.recompute.differs", july.GetProperty("explanation").GetString());
        Assert.DoesNotContain(differences, d => d.GetProperty("period").GetString() == $"M{Prev}-06"); // June is still right
        Assert.Contains(differences, d => d.GetProperty("kind").GetString() == "unexpected" && d.GetProperty("period").GetString() == "Y" + Prev);
        Assert.Contains(differences, d => d.GetProperty("kind").GetString() == "below_allowed" && d.GetProperty("leaveYear").GetInt32() == Prev);
        Assert.False(report.Json.GetProperty("changed").GetBoolean());
        Assert.Equal(before, await CountAsync(e)); // nothing changed

        // The job does the same for the company and logs each difference; it changes nothing either.
        var logs = await RunJobAsync("hrm.leave_recompute", e);
        Assert.Contains(logs, l => l.Contains($"M{Prev}-07: differs", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("nothing was changed", StringComparison.Ordinal));
        Assert.Equal(before, await CountAsync(e));

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(await _h.UserAsync(Ct), $"{Employments}/{e}/leave-recompute", Ct)).Status);

        // HR settles July by an adjustment naming the period: the difference is gone, the others stay (review S1.32a finding 1).
        var settle = await _h.PostAsync(o.Admin, $"{Employments}/{e}/leave-ledger", new { leaveTypeId = casual, kind = "adjustment", quantity = 1, entryDate = Iso(new DateOnly(Today.Year, 1, 1)), reason = "July at the new rate", settlesPeriod = $"M{Prev}-07" }, Ct);
        Assert.True(settle.Status == HttpStatusCode.Created, settle.Body);
        Assert.Equal($"M{Prev}-07", settle.Json.GetProperty("period").GetString());
        Assert.Equal("hrm.leave.entry.settlement", settle.Json.GetProperty("explanation").GetString());
        var after = await RecomputeAsync(o, e);
        Assert.DoesNotContain(after, d => d.GetProperty("period").GetString() == $"M{Prev}-07");
        Assert.Contains(after, d => d.GetProperty("period").GetString() == $"M{Prev}-08" && d.GetProperty("kind").GetString() == "differs");
        Assert.Contains("errors.hrm.leave.settles_period", (await _h.PostAsync(o.Admin, $"{Employments}/{e}/leave-ledger", new { leaveTypeId = casual, kind = "grant", quantity = 1, entryDate = Iso(new DateOnly(Today.Year, 1, 1)), reason = "x", settlesPeriod = $"M{Prev}-08" }, Ct)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.settles_period", (await _h.PostAsync(o.Admin, $"{Employments}/{e}/leave-ledger", new { leaveTypeId = casual, kind = "adjustment", quantity = 1, entryDate = Iso(new DateOnly(Today.Year, 1, 1)), reason = "x", settlesPeriod = "July" }, Ct)).Body, StringComparison.Ordinal);

        // The job leaves a settled period to HR: rerunning it posts nothing for July.
        var count = await CountAsync(e);
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(count, await CountAsync(e));
    }

    [Fact]
    public async Task FR_HRM_LVE_14_a_retired_type_keeps_its_entries_without_being_reported_as_unexpected()
    {
        var o = await OrgAsync();
        var (casual, version) = await TypeAsync(o, "CASUAL");
        await PolicyAsync(o, new[] { new { leaveType = "CASUAL", entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "none" } } });
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Empty(await RecomputeAsync(o, e));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(o.Admin, $"{Types}/{casual}", new { isActive = false, rowVersion = version }, Ct)).Status);
        Assert.Empty(await RecomputeAsync(o, e)); // review S1.32a finding 2
        Assert.True((await BalanceAsync(o.Admin, e, casual)).GetProperty("balance").GetDecimal() > 0m); // the balance stays readable
    }

    [Fact]
    public async Task FR_HRM_LVE_02_the_leave_year_start_cannot_move_once_entries_exist_and_a_moved_policy_year_is_listed()
    {
        var o = await OrgAsync();
        var tenantAdmin = await _h.UserAsync(Ct, null, "tenant_admin");
        const string setting = "/api/cfg/settings/hrm.leave.year_start_month";
        var free = await _h.PutAsync(tenantAdmin, setting, new { layer = "company", scopeId = o.Company, value = 1, reason = "before the first entry" }, Ct);
        Assert.True(free.Status == HttpStatusCode.OK, free.Body); // no entries yet: allowed
        var (annual, _) = await TypeAsync(o, "ANNUAL");
        var policy = await PolicyAsync(o, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "none" } } });
        var e = await HireAsync(o, new DateOnly(Prev, 1, 1));
        await RunJobAsync("hrm.leave_accrual", e);
        var refused = await _h.PutAsync(tenantAdmin, setting, new { layer = "company", scopeId = o.Company, value = 7, reason = "fiscal year" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status); // review S1.32a finding 3
        Assert.Equal("errors.hrm.leave.year_start_fixed", refused.Text("title"));
        Assert.Equal(12m, await SumAsync(e, annual, Today.Year));

        // A policy version giving the type another start month from this year: this year's grant now falls in another year, and the check says so.
        await NewVersionAsync(o, policy, new[] { new { leaveType = "ANNUAL", yearStartMonth = 7, entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "none" } } }, new DateOnly(Today.Year, 1, 1));
        Assert.Contains(await RecomputeAsync(o, e), d => d.GetProperty("kind").GetString() == "year_moved" && d.GetProperty("leaveYear").GetInt32() == Today.Year);
    }

    [Fact]
    public async Task FR_HRM_LVE_02_a_change_from_a_yearly_grant_to_monthly_accrual_in_mid_year_gives_each_model_its_months()
    {
        var o = await OrgAsync();
        var (annual, _) = await TypeAsync(o, "ANNUAL");
        var policy = await PolicyAsync(o, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "none" } } });
        await NewVersionAsync(o, policy, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "none" } } }, new DateOnly(Prev, 7, 1));
        var e = await HireAsync(o, new DateOnly(Prev - 1, 1, 1));
        await RunJobAsync("hrm.leave_accrual", e);
        Assert.Equal(6m, await _h.ScalarAsync<decimal>("select quantity from hrm.leave_ledger where employment_id = $1 and reason = $2", Ct, e, "Y" + Prev)); // January to June yearly
        Assert.Equal(12m, await SumAsync(e, annual, Prev)); // plus July to December monthly: 12, not 18 (review S1.32a finding 4)
        Assert.Empty(await RecomputeAsync(o, e));

        // The other way round: monthly until June, yearly from July gives the yearly grant for July to December only, due on 1 July.
        var o2 = await OrgAsync();
        var (annual2, _) = await TypeAsync(o2, "ANNUAL");
        var policy2 = await PolicyAsync(o2, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "none" } } });
        await NewVersionAsync(o2, policy2, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "fixed_yearly", quantity = 12, proRata = "none" } } }, new DateOnly(Prev, 7, 1));
        var e2 = await HireAsync(o2, new DateOnly(Prev - 1, 1, 1));
        await RunJobAsync("hrm.leave_accrual", e2);
        Assert.Equal(new DateOnly(Prev, 7, 1), await _h.ScalarAsync<DateOnly>("select entry_date from hrm.leave_ledger where employment_id = $1 and reason = $2", Ct, e2, "Y" + Prev));
        Assert.Equal(12m, await SumAsync(e2, annual2, Prev));
    }

    // ------------------------------------------------------------------ who sees and changes what (permissions and scopes)

    [Fact]
    public async Task FR_HRM_LVE_10_an_employee_reads_only_their_own_balances_and_cannot_post_and_HR_stays_in_its_scope()
    {
        var o = await OrgAsync();
        var (annual, _) = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, new[] { new { leaveType = "ANNUAL", entitlement = new { model = "manual" } } });
        var user = await _h.UserAsync(Ct, null, "employee");
        var own = await HireAsync(o, new DateOnly(Prev, 1, 1), user);
        var other = await HireAsync(o, new DateOnly(Prev, 1, 1));
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, own, annual, "grant", 4m, new DateOnly(Today.Year, 1, 1))).Status);
        Assert.Equal(HttpStatusCode.Created, (await PostEntryAsync(o.Admin, other, annual, "grant", 7m, new DateOnly(Today.Year, 1, 1))).Status);

        // Their own balance comes from the token; a request cannot name somebody else.
        var mine = await _h.GetAsync(user, "/api/hrm/my/leave-balances", Ct);
        Assert.True(mine.Status == HttpStatusCode.OK, mine.Body);
        Assert.Equal(own, mine.Json.GetProperty("employmentId").GetGuid());
        Assert.Equal(4m, mine.Json.GetProperty("balances")[0].GetProperty("balance").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user, $"{Employments}/{own}/leave-balances", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"{Employments}/{other}/leave-balances", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostEntryAsync(user, own, annual, "grant", 100m, new DateOnly(Today.Year, 1, 2))).Status);
        Assert.Equal(1, mine.Json.GetProperty("employments").GetArrayLength());
        var unlinked = await _h.GetAsync(await _h.UserAsync(Ct, null, "employee"), "/api/hrm/my/leave-balances", Ct); // no employment linked: nothing to show
        Assert.Equal(HttpStatusCode.OK, unlinked.Status);
        Assert.Equal(JsonValueKind.Null, unlinked.Json.GetProperty("employmentId").ValueKind);
        Assert.Equal(0, unlinked.Json.GetProperty("balances").GetArrayLength());
        var foreign = await _h.GetAsync(user, $"/api/hrm/my/leave-balances?companyId={await _h.CompanyAsync(Ct)}", Ct); // a company that is not theirs
        Assert.Equal(0, foreign.Json.GetProperty("balances").GetArrayLength());

        // HR of another company only: this company's employments do not exist for them.
        var elsewhere = await _h.CompanyAsync(Ct);
        var scoped = await _h.UserAsync(Ct);
        object companies = await _h.ScopeAsync("companies", new { company_ids = new[] { elsewhere } }, Ct);
        await _h.AssignAsync(scoped, await _h.RoleAsync(Ct, ["hrm.leave_ledger.read", "hrm.leave_ledger.post", "hrm.leave_ledger.recompute"], [("*", companies)]), null, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(scoped, $"{Employments}/{own}/leave-balances", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostEntryAsync(scoped, own, annual, "grant", 1m, new DateOnly(Today.Year, 1, 2))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(scoped, $"{Employments}/{own}/leave-recompute", Ct)).Status);
        var entry = await _h.ScalarAsync<Guid>("select id from hrm.leave_ledger where employment_id = $1", Ct, own);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(scoped, $"/api/hrm/leave-ledger/{entry}/reverse", new { reason = "x" }, Ct)).Status);
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where employment_id in ($1, $2)", Ct, own, other));
    }
}
