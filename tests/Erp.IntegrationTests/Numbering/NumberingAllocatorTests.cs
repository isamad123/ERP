#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Collections.Concurrent;
using System.Diagnostics;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.IntegrationTests.Numbering;

/// <summary>
/// Slice S1.08 acceptance for the numbering service itself, against a real PostgreSQL 16 database through the
/// application login: concurrency (integrity invariant 6), periods, scheme choice, defaults, manual numbers and voids.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class NumberingAllocatorTests(TenancyFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private AccessHarness _h = null!;
    private NumberingRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        _rig = await NumberingRig.StartAsync(_h, Ct);
    }

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    private static NumberRequest Letter(Guid company, DateOnly? date = null) => new("hrm.letter_no", company, null, date ?? new DateOnly(2026, 3, 10));

    // ------------------------------------------------------------------ concurrency (FR-KRN-NUM-02, FR-KRN-NUM-06, invariant 6)

    [Fact]
    [Trait("Invariant", "6")]
    public async Task FR_KRN_NUM_02_FR_KRN_NUM_06_invariant_6_100_concurrent_gap_free_posts_produce_exactly_1_to_100()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        var watch = Stopwatch.StartNew();
        var numbers = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => _rig.PostAsync(Letter(company), commit: true, Ct), Ct)));
        watch.Stop();
        output.WriteLine($"gap-free: 100 concurrent posts in {watch.ElapsedMilliseconds} ms ({watch.Elapsed.TotalMilliseconds / 100:0.00} ms per allocation, wall clock / 100)");

        Assert.Equal(Enumerable.Range(1, 100).Select(i => $"LTR-2026-{i:00000}"), numbers.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Invariant", "6")]
    public async Task FR_KRN_NUM_02_invariant_6_gap_free_posts_that_roll_back_give_their_number_back_so_committed_numbers_stay_continuous()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        var committed = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            var commit = i % 4 != 0; // every fourth post fails on purpose
            var number = await _rig.PostAsync(Letter(company), commit, Ct);
            if (commit)
            {
                committed.Add(number);
            }
        }, Ct)));

        Assert.Equal(75, committed.Count);
        Assert.Equal(Enumerable.Range(1, 75).Select(i => $"LTR-2026-{i:00000}"), committed.Order(StringComparer.Ordinal));
        Assert.Equal("LTR-2026-00076", await _rig.PostAsync(Letter(company), commit: true, Ct));
    }

    [Fact]
    public async Task FR_KRN_NUM_06_100_concurrent_gap_allowed_transactions_never_wait_on_each_other_and_never_duplicate()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.contract_no", "CON-{yyyy}-{seq:5}", "yearly", ct: Ct);
        // Deterministic instead of a wall-clock limit (R-317): every transaction takes its number and then stays open until all 100 are
        // open together. If a gap-allowed allocation made transactions wait on each other, the later ones would never get their
        // number, the rendezvous would never complete and the test would fail by its deadlock guard, however fast the machine is.
        const int Transactions = 100;
        var allOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = 0;
        async Task Rendezvous(CancellationToken token)
        {
            if (Interlocked.Increment(ref open) == Transactions)
            {
                allOpen.SetResult();
            }

            await allOpen.Task.WaitAsync(TimeSpan.FromSeconds(120), token); // deadlock guard only; a healthy run needs a fraction of a second
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, Transactions).Select(i => Task.Run(() =>
            _rig.PostAsync(new NumberRequest("hrm.contract_no", company, null, new DateOnly(2026, 5, 1)), commit: i % 10 != 0, Ct, Rendezvous), Ct)));
        output.WriteLine($"gap-allowed: {open} transactions were open at the same time, each holding its number");

        Assert.Equal(Transactions, open);
        Assert.Equal(Transactions, numbers.Distinct(StringComparer.Ordinal).Count());

        // Ten of them rolled back: their numbers are gaps that are never filled; the next number comes after all of them.
        Assert.Equal("CON-2026-00101", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, new DateOnly(2026, 5, 2)), Ct));
    }

    [Fact]
    public async Task FR_KRN_NUM_02_a_gap_free_post_waiting_more_than_5_seconds_fails_with_numbering_busy_and_saves_nothing()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        await using var first = await _rig.App.OpenConnectionAsync(Ct);
        await using var firstTx = await first.BeginTransactionAsync(Ct);
        Assert.Equal("LTR-2026-00001", await _rig.Allocator.AssignAsync(Letter(company), firstTx, Ct));

        var watch = Stopwatch.StartNew();
        await using (var second = await _rig.App.OpenConnectionAsync(Ct))
        await using (var secondTx = await second.BeginTransactionAsync(Ct))
        {
            var ex = await Assert.ThrowsAsync<NumberingException>(() => _rig.Allocator.AssignAsync(Letter(company), secondTx, Ct));
            Assert.Equal("errors.numbering.busy", ex.ErrorKey);
            await secondTx.RollbackAsync(Ct);
        }

        watch.Stop();
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(15));
        await firstTx.CommitAsync(Ct);
        Assert.Equal("LTR-2026-00002", await _rig.PostAsync(Letter(company), commit: true, Ct));
    }

    [Fact]
    public async Task FR_KRN_NUM_06_time_per_allocation_is_measured_for_both_kinds()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        await _rig.SchemeAsync(company, "hrm.contract_no", "CON-{yyyy}-{seq:5}", "yearly", ct: Ct);
        // R-317: no wall-clock limit. The cost of an allocation is compared with a plain round trip of the same shape measured in the
        // same run on the same connection pool (begin, a statement that gives the transaction an id, commit: an allocation writes, so its commit waits for the disk too), so a loaded or slow machine moves both alike.
        // The median of the rounds is used, so one stalled round cannot decide the result.
        const int Rounds = 200;
        const double MaxTimesBaseline = 100; // an allocation measured 45 to 50 times a plain round trip on the build machine (about 20 ms against 0.4 ms); the old limit of 100 ms was 100 times a 1 ms round trip
        await _rig.PostAsync(Letter(company), true, Ct); // warm-up (pool, plans)
        var request = new NumberRequest("hrm.contract_no", company, null, new DateOnly(2026, 3, 1));
        await _rig.Allocator.AssignOnCreateAsync(request, Ct);
        await BaselineAsync();

        var baseline = new List<double>();
        var gapFree = new List<double>();
        var gapAllowed = new List<double>();
        for (var i = 0; i < Rounds; i++) // interleaved, so a slow period hits all three alike
        {
            baseline.Add(await TimeAsync(BaselineAsync));
            gapFree.Add(await TimeAsync(() => _rig.PostAsync(Letter(company), true, Ct)));
            gapAllowed.Add(await TimeAsync(() => _rig.Allocator.AssignOnCreateAsync(request, Ct)));
        }

        var (b, f, a) = (Median(baseline), Median(gapFree), Median(gapAllowed));
        output.WriteLine($"median per number: gap-free {f:0.00} ms (begin, allocate, commit), gap-allowed {a:0.00} ms; plain round trip {b:0.00} ms; ratios {f / b:0.0} and {a / b:0.0} (limit {MaxTimesBaseline})");
        Assert.True(f / b < MaxTimesBaseline, $"gap-free allocation took {f / b:0.0} times a plain round trip ({f:0.00} ms against {b:0.00} ms)");
        Assert.True(a / b < MaxTimesBaseline, $"gap-allowed allocation took {a / b:0.0} times a plain round trip ({a:0.00} ms against {b:0.00} ms)");

        async Task BaselineAsync()
        {
            await using var connection = await _rig.App.OpenConnectionAsync(Ct);
            await using var tx = await connection.BeginTransactionAsync(Ct);
            await using var command = new NpgsqlCommand("select pg_current_xact_id()::text", connection, tx);
            await command.ExecuteScalarAsync(Ct);
            await tx.CommitAsync(Ct);
        }

        static async Task<double> TimeAsync(Func<Task> work)
        {
            var watch = Stopwatch.StartNew();
            await work();
            return watch.Elapsed.TotalMilliseconds;
        }

        static double Median(List<double> values)
        {
            var sorted = values.Order().ToList();
            return sorted[sorted.Count / 2];
        }
    }

    // ------------------------------------------------------------------ periods (Q5)

    [Fact]
    public async Task FR_KRN_NUM_01_a_yearly_series_starts_again_in_a_new_year_and_never_repeats_an_earlier_years_number()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.contract_no", "CON-{yyyy}-{seq:5}", "yearly", start: 10, ct: Ct);
        Task<string> At(int y, int m, int d) => _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, new DateOnly(y, m, d)), Ct);
        Assert.Equal("CON-2025-00010", await At(2025, 12, 31));
        Assert.Equal("CON-2026-00010", await At(2026, 1, 1));
        Assert.Equal("CON-2025-00011", await At(2025, 6, 1)); // the business date decides the period, not the clock
        Assert.Equal("CON-2026-00011", await At(2026, 2, 1));
    }

    [Fact]
    public async Task FR_KRN_NUM_01_monthly_and_fiscal_year_series_follow_their_periods()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.pay_run_no", "PR-{yyyy}{mm}-{seq:2}", "monthly", ct: Ct);
        Task<string> Run(int y, int m) => _rig.PostAsync(new NumberRequest("hrm.pay_run_no", company, null, new DateOnly(y, m, 28)), commit: true, Ct);
        Assert.Equal("PR-202601-01", await Run(2026, 1));
        Assert.Equal("PR-202601-02", await Run(2026, 1));
        Assert.Equal("PR-202602-01", await Run(2026, 2));

        // Fiscal year starting in July (company setting; the Pakistan default, ruling P4).
        await _h.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'cfg.numbering.fiscal_year_start_month', 'company', $2, '7', now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), company, BaselineSeed.SystemUserId);
        await _rig.SchemeAsync(company, "hrm.loan_no", "LN-{fy}-{seq:3}", "fiscal_yearly", ct: Ct);
        Task<string> Loan(int y, int m, int d) => _rig.PostAsync(new NumberRequest("hrm.loan_no", company, null, new DateOnly(y, m, d)), commit: true, Ct);
        Assert.Equal("LN-2025-26-001", await Loan(2026, 6, 30));
        Assert.Equal("LN-2026-27-001", await Loan(2026, 7, 1));
        Assert.Equal("LN-2025-26-002", await Loan(2026, 1, 15));
    }

    // ------------------------------------------------------------------ which scheme (Q6, Q7)

    [Fact]
    public async Task FR_KRN_NUM_01_a_branch_scheme_wins_over_the_company_scheme()
    {
        var company = await _h.CompanyAsync(Ct);
        var (branch, code) = await _rig.BranchAsync(company, Ct);
        var (other, _) = await _rig.BranchAsync(company, Ct);
        await _rig.SchemeAsync(company, "hrm.leave_request_no", "LV-{yyyy}-{seq:6}", "yearly", ct: Ct);
        await _rig.SchemeAsync(company, "hrm.leave_request_no", "LV-{branch}-{yyyy}-{seq:4}", "yearly", branch: branch, ct: Ct);
        var date = new DateOnly(2026, 4, 1);
        Assert.Equal($"LV-{code}-2026-0001", await _rig.PostAsync(new NumberRequest("hrm.leave_request_no", company, branch, date), true, Ct));
        Assert.Equal("LV-2026-000001", await _rig.PostAsync(new NumberRequest("hrm.leave_request_no", company, other, date), true, Ct));
        Assert.Equal("LV-2026-000002", await _rig.PostAsync(new NumberRequest("hrm.leave_request_no", company, null, date), true, Ct));
    }

    [Fact]
    public async Task FR_KRN_TEN_02_a_company_without_a_scheme_gets_its_default_scheme_on_first_use_audited_as_the_system()
    {
        var company = await _h.CompanyAsync(Ct);
        Assert.Equal("EMP-00001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, null), Ct));
        Assert.Equal("EMP-00002", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, null), Ct));
        var scheme = await _h.ScalarAsync<Guid>("select id from cfg.numbering_schemes where company_id = $1 and document_type = 'hrm.employee_no' and status = 'active' and pattern = 'EMP-{seq:5}' and not gap_free", Ct, company);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'cfg.numbering_schemes' and entity_id = $1 and action = 'create' and actor_kind = 'system'", Ct, scheme));

        // Tenant-wide person numbers have no company; a company is refused (Q6).
        var person = await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.person_no", null, null, null), Ct);
        Assert.Matches("^P-[0-9]{6}$", person);
        Assert.Equal("errors.numbering.company_not_allowed", (await Assert.ThrowsAsync<NumberingException>(() => _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.person_no", company, null, null), Ct))).ErrorKey);
        Assert.Equal("errors.numbering.company_required", (await Assert.ThrowsAsync<NumberingException>(() => _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", null, null, null), Ct))).ErrorKey);
    }

    [Fact]
    public async Task FR_KRN_NUM_02_a_gap_free_series_is_never_numbered_on_save_and_a_retired_scheme_is_not_brought_back()
    {
        var company = await _h.CompanyAsync(Ct);
        var scheme = await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        Assert.Equal("errors.numbering.numbered_at_posting", (await Assert.ThrowsAsync<NumberingException>(() => _rig.Allocator.AssignOnCreateAsync(Letter(company), Ct))).ErrorKey);

        await _h.ExecAsync("update cfg.numbering_schemes set status = 'retired', row_version = row_version + 1 where id = $1", Ct, scheme);
        Assert.Equal("errors.numbering.no_active_scheme", (await Assert.ThrowsAsync<NumberingException>(() => _rig.PostAsync(Letter(company), true, Ct))).ErrorKey);
    }

    [Fact]
    public async Task FR_KRN_NUM_06_a_scheme_continuing_a_series_never_restarts_it_even_when_created_at_the_same_instant_with_a_smaller_id()
    {
        var company = await _h.CompanyAsync(Ct);
        var first = await _rig.SchemeAsync(company, "hrm.contract_no", "CN-{yyyy}-{seq:4}", "yearly", ct: Ct);
        var date = new DateOnly(2026, 8, 1);
        Assert.Equal("CN-2026-0001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, date), Ct));
        Assert.Equal("CN-2026-0002", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, date), Ct));
        await _h.ExecAsync("update cfg.numbering_schemes set status = 'retired', row_version = row_version + 1 where id = $1", Ct, first);

        // Same created_at and an id that sorts first: the series stays with the scheme that issued its numbers (Q8).
        await _h.ExecAsync("""
            insert into cfg.numbering_schemes (id, company_id, document_type, pattern, reset_rule, gap_free, start_value, status, created_at, created_by, updated_at, updated_by)
            select '00000000-0000-7000-8000-000000000001'::uuid, company_id, document_type, pattern, reset_rule, gap_free, start_value, 'active', created_at, created_by, updated_at, updated_by
            from cfg.numbering_schemes where id = $1
            """, Ct, first);
        Assert.Equal("CN-2026-0003", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, date), Ct));
        await _h.ExecAsync("delete from cfg.numbering_schemes where id = '00000000-0000-7000-8000-000000000001'::uuid", Ct);
    }

    // ------------------------------------------------------------------ manual numbers (HRM-EMP-04, Q9)

    [Fact]
    public async Task FR_HRM_EMP_04_a_typed_employee_number_the_scheme_could_still_produce_is_refused_one_outside_the_series_is_accepted()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.employee_no", "EMP-{seq:5}", "never", ct: Ct);
        for (var i = 0; i < 3; i++)
        {
            await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, null), Ct);
        }

        async Task<string?> Check(string number) => (await _rig.Allocator.CheckManualAsync(new NumberRequest("hrm.employee_no", company, null, null), number, Ct)).ErrorKey;
        Assert.Equal("errors.numbering.manual_in_series", await Check("EMP-00004"));
        Assert.Equal("errors.numbering.manual_in_series", await Check("emp-01000"));
        Assert.Null(await Check("EMP-00003")); // below the next value: the series has passed it (duplicates are the table's unique key)
        Assert.Null(await Check("EMP-042")); // the pattern would never print it this way
        Assert.Null(await Check("OLD-1234"));
        Assert.Equal("errors.numbering.number_empty", await Check("  "));

        Assert.Equal("errors.numbering.override_not_allowed",
            (await _rig.Allocator.CheckManualAsync(new NumberRequest("hrm.contract_no", company, null, null), "X-1", Ct)).ErrorKey);
    }

    // ------------------------------------------------------------------ voids (FR-KRN-NUM-05, FR-XM-06, Q10)

    [Fact]
    public async Task FR_KRN_NUM_05_FR_XM_06_a_cancelled_document_records_a_void_once_and_the_void_cannot_be_changed_or_deleted()
    {
        var company = await _h.CompanyAsync(Ct);
        var scheme = await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        var number = await _rig.PostAsync(Letter(company), true, Ct);
        var document = Guid.CreateVersion7();

        await using (var c = await _rig.App.OpenConnectionAsync(Ct))
        await using (var tx = await c.BeginTransactionAsync(Ct))
        {
            await _rig.Voids.RecordAsync(new VoidRequest("hrm.letter_no", company, null, number, "issued to the wrong employee", document), tx, Ct);
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_voids where scheme_id = $1 and number = $2 and document_id = $3", Ct, scheme, number, document));
        Assert.Equal("errors.numbering.already_voided",
            (await Assert.ThrowsAsync<NumberingException>(() => _rig.Voids.RecordAsync(new VoidRequest("hrm.letter_no", company, null, number, "again", null), null, Ct))).ErrorKey);
        Assert.Equal("errors.numbering.void_reason_required",
            (await Assert.ThrowsAsync<NumberingException>(() => _rig.Voids.RecordAsync(new VoidRequest("hrm.letter_no", company, null, "LTR-2026-00002", " ", null), null, Ct))).ErrorKey);

        // Append-only: neither the application login nor the owner can change or delete a void.
        await using var app = await _rig.App.OpenConnectionAsync(Ct);
        await using (var update = new NpgsqlCommand("update cfg.numbering_voids set reason = 'changed' where scheme_id = $1", app) { Parameters = { new() { Value = scheme } } })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct))).SqlState);
        }

        await using var owner = await _h.OwnerAsync(null, Ct);
        await using (var delete = new NpgsqlCommand("delete from cfg.numbering_voids where scheme_id = $1", owner) { Parameters = { new() { Value = scheme } } })
        {
            Assert.Equal("ER001", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct))).SqlState);
        }
    }

    [Fact]
    public async Task FR_KRN_NUM_02_the_application_login_cannot_take_numbers_or_create_sequences_except_through_the_function()
    {
        await using var app = await _rig.App.OpenConnectionAsync(Ct);
        await using (var create = new NpgsqlCommand("create sequence cfg.numseq_probe", app))
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync(Ct))).SqlState);
        }

        var company = await _h.CompanyAsync(Ct);
        await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, null), Ct);
        var sequence = await _h.ScalarAsync<string>("select relname::text from pg_class where relkind = 'S' and relname like 'numseq_%' order by oid desc limit 1", Ct);
        await using var nextval = new NpgsqlCommand($"select nextval('cfg.{sequence}')", app);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => nextval.ExecuteScalarAsync(Ct))).SqlState);
    }
}
