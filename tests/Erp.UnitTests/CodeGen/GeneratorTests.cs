using Erp.CodeGen;
using Erp.CodeGen.Design;
using Erp.CodeGen.Emit;

namespace Erp.UnitTests.CodeGen;

/// <summary>Slice S1.02: the code generator reads the approved data design exactly and deterministically.</summary>
public sealed class GeneratorTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);
    private static readonly Lazy<DesignModel> Model = new(() => Generator.LoadModel(Root));

    [Fact]
    public void S102_generator_output_is_identical_on_every_run()
    {
        var first = Generator.Generate(Root).Files;
        var second = Generator.Generate(Root).Files;

        Assert.Equal(first.Keys, second.Keys);
        Assert.All(first, file => Assert.Equal(file.Value, second[file.Key]));
    }

    [Fact]
    public void S102_committed_generated_files_match_a_fresh_run()
    {
        var stale = Generator.Generate(Root).Files
            .Where(f => !File.Exists(Path.Combine(Root, f.Key)) || File.ReadAllText(Path.Combine(Root, f.Key)) != f.Value)
            .Select(f => f.Key)
            .ToList();

        Assert.True(stale.Count == 0, "Run `dotnet run --project tools/Erp.CodeGen -- generate`; out of date: " + string.Join(", ", stale.Take(10)));
    }

    [Fact]
    public void S102_every_generated_file_is_marked_auto_generated()
    {
        Assert.All(Generator.Generate(Root).Files, f => Assert.Contains(OutputSet.Marker, f.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void D50_design_has_204_tables_in_six_files()
    {
        // 204 tables approved by D-50, plus the two added by CR-010 in slice S1.05 (added_in), which are not part of the D-50 files.
        var design = Model.Value.DesignTables.ToList();
        Assert.Equal(204, design.Count(t => t.AddedIn is null));
        Assert.Equal(["iam.mfa_recovery_codes", "iam.one_time_tokens"], design.Where(t => t.AddedIn == "S1.05").Select(t => t.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(6, design.Select(t => t.SourceFile).Distinct().Count());
    }

    [Fact]
    public void README_traits_expand_to_the_standard_columns_in_order()
    {
        var table = Model.Value.Table("hrm.leave_types"); // traits std, co, soft, name
        var names = table.Columns.Select(c => c.Name).ToList();

        Assert.Equal(["id", "company_id", "code", "name_i18n"], names.Take(4));
        Assert.Equal(["is_active", "is_deleted", "deleted_at", "deleted_by", "created_at", "created_by", "updated_at", "updated_by", "row_version"], names.TakeLast(9));
        Assert.Equal("1", table.Column("row_version").Default);
        Assert.Equal("true", table.Column("is_active").Default);
    }

    [Fact]
    public void README_types_map_to_postgresql_types()
    {
        var m = Model.Value;
        Assert.Equal("numeric(19,4)", m.Table("hrm.pay_inputs").Columns.First(c => c.Kind == ColumnKind.Money).SqlType);
        Assert.Equal("timestamptz", m.Table("iam.users").Column("last_login_at").SqlType);
        Assert.Equal("bytea", m.Table("hrm.person_identifiers").Column("value").SqlType);
        Assert.Equal("char(3)", m.Table("org.companies").Column("base_currency_code").SqlType);
    }

    [Fact]
    public void README_rule11_nullable_unique_columns_use_nulls_not_distinct()
    {
        var unique = Model.Value.Table("cfg.numbering_schemes").Uniques.Single(u => u.Items.Contains("branch_id"));
        Assert.True(unique.NullsNotDistinct);
        Assert.Equal("status='active'", unique.Where);
    }

    [Fact]
    public void README_rule12_company_scoped_references_use_composite_foreign_keys()
    {
        var m = Model.Value;
        var fk = m.Table("hrm.leave_requests").ForeignKeys.Single(f => f.Name == "fk_leave_requests_employment_id");
        Assert.Equal(["company_id", "employment_id"], fk.Columns);
        Assert.Equal(["company_id", "id"], fk.TargetColumns);
        Assert.Contains(m.Table("hrm.employments").Uniques, u => u.Items.SequenceEqual(["company_id", "id"]));
    }

    [Fact]
    public void CR003_foreign_keys_to_the_partitioned_attendance_days_include_work_date()
    {
        var fk = Model.Value.Table("hrm.overtime_entries").ForeignKeys.Single(f => f.TargetTable == "hrm.attendance_days");
        Assert.Equal(["company_id", "attendance_day_id", "work_date"], fk.Columns);
        Assert.Equal(["company_id", "id", "work_date"], fk.TargetColumns);
    }

    [Fact]
    public void README_eff_trait_creates_an_overlap_exclusion_and_nullable_keys_are_coalesced()
    {
        var exclusion = Model.Value.Table("iam.user_roles").Exclusions.Single();
        Assert.Contains($"(coalesce(company_id, {DesignLoader.NilUuid})) with =", exclusion.Elements);
        Assert.Equal("daterange(valid_from, valid_to, '[)') with &&", exclusion.Elements[^1]);
        Assert.Equal("status <> 'draft'", Model.Value.Table("hrm.policy_versions").Exclusions.Single().Where);
    }

    [Fact]
    public void README_rule8_partitioned_tables_carry_the_partition_column_in_the_key()
    {
        var m = Model.Value;
        Assert.Equal(["id", "work_date"], m.Table("hrm.attendance_days").PrimaryKey);
        Assert.Null(m.Table("hrm.pay_result_lines").Partition); // "once volume requires" (rules.yaml)
        Assert.Equal(7, m.Tables.Count(t => t.Partition is not null));
    }

    [Fact]
    public void Notes_defaults_only_become_database_defaults_when_they_are_literal_values()
    {
        var m = Model.Value;
        Assert.Equal("0", m.Table("iam.user_credentials").Column("failed_attempts").Default);
        Assert.Equal("'production'", m.Table("cp.tenant_databases").Column("env_type").Default);
        Assert.Null(m.Table("iam.users").Column("time_zone").Default); // "IANA, default Asia/Karachi" stays a comment
        Assert.Null(m.Table("hrm.salary_structures").Column("assign_rule").Default); // "default by grade or employee type"
    }

    [Fact]
    public void Rules_every_free_text_rule_becomes_sql_or_a_tracked_placeholder()
    {
        var stubs = Model.Value.Stubs.ToList();
        Assert.Equal(29, stubs.Count); // 30 from S1.02; S1.03 implemented SC-2 (org.branches)
        Assert.Equal(30, Model.Value.AllStubs.Count());
        Assert.All(stubs, s => Assert.Matches(@"^S1\.\d{2}$", s.Owner));
        Assert.Contains(stubs, s => s.Name == "attendance_periods_lock" && s.Owner == "S1.31"); // FR-XM-26
        Assert.Contains(stubs, s => s.Name == "workflow_tasks_no_self_approval" && s.Owner == "S1.10"); // FR-XM-32
    }

    [Fact]
    public void Rules_a_placeholder_owner_slice_that_has_finished_must_have_implemented_its_rule()
    {
        // A slice is finished when its handoff note exists (docs/handoff/<date>-<slice>.md).
        var handoffs = Directory.GetFiles(Path.Combine(Root, "docs", "handoff"), "*.md").Select(Path.GetFileNameWithoutExtension).ToList();
        var overdue = Model.Value.Stubs.Where(s => handoffs.Exists(h => h!.EndsWith("-" + s.Owner, StringComparison.Ordinal))).Select(s => $"{s.Owner}: {s.Table} {s.Name}").ToList();
        Assert.True(overdue.Count == 0, "Finished slices left placeholders: " + string.Join("; ", overdue));
    }

    [Fact]
    public void S103_implemented_rule_goes_into_a_new_migration_and_leaves_the_merged_one_unchanged()
    {
        var implemented = Assert.Single(Model.Value.ImplementedStubs);
        Assert.Equal("branches_default_branch_per_company", implemented.Name);
        var files = Generator.Generate(Root).Files;

        // The S1.02 migration (merged) still creates the placeholder exactly as before ...
        Assert.Contains("create or replace function org.todo_branches_default_branch_per_company()", files[SqlEmitter.KernelMigrations + "/V202611150002__kernel__org.sql"], StringComparison.Ordinal);

        // ... and the S1.03 migration, ordered after it, drops it and creates the real triggers.
        var rules = files[SqlEmitter.KernelMigrations + "/V202611150201__kernel__rules_s1_03.sql"];
        Assert.Contains("drop function if exists org.todo_branches_default_branch_per_company();", rules, StringComparison.Ordinal);
        Assert.Contains("create or replace trigger tg_companies_default_branch", rules, StringComparison.Ordinal);
        Assert.Contains("create or replace trigger tg_branches_keep_one_per_company", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void S103_spec03_roles_app_rw_cannot_update_or_delete_append_only_tables()
    {
        var files = Generator.Generate(Root).Files;
        var grants = files[SqlEmitter.KernelMigrations + "/V202611150200__kernel__grants.sql"] + files[SqlEmitter.HrmMigrations + "/V202611150101__hrm__grants.sql"]
            + string.Concat(files.Where(f => f.Key.Contains("__kernel__design_", StringComparison.Ordinal)).Select(f => f.Value));
        foreach (var table in Model.Value.Tables.Where(t => t.Database == DatabaseKind.Tenant))
        {
            var lines = grants.Split('\n').Where(l => l.EndsWith($" on {table.FullName} to app_rw;", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(lines);
            if (table.IsAppendOnly || table.Schema == "aud")
            {
                Assert.DoesNotContain(lines, l => l.Contains("delete", StringComparison.Ordinal) || l.StartsWith("grant select, insert, update", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void S105_CR010_later_design_changes_go_to_a_new_migration_and_leave_the_frozen_files_unchanged()
    {
        var files = Generator.Generate(Root).Files;
        var frozenIam = files[SqlEmitter.KernelMigrations + "/V202611150003__kernel__iam.sql"];
        var frozenGrants = files[SqlEmitter.KernelMigrations + "/V202611150200__kernel__grants.sql"];
        var design = files[SqlEmitter.KernelMigrations + "/V202611150302__kernel__design_s1_05.sql"];

        // The merged files are byte-for-byte what is committed (merged migrations are never edited).
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, "V202611150003__kernel__iam.sql")), frozenIam);
        Assert.DoesNotContain("one_time_tokens", frozenIam, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token_hash", frozenIam, StringComparison.Ordinal);
        Assert.DoesNotContain("one_time_tokens", frozenGrants, StringComparison.Ordinal);

        Assert.Contains("create table if not exists iam.one_time_tokens (", design, StringComparison.Ordinal);
        Assert.Contains("create table if not exists iam.mfa_recovery_codes (", design, StringComparison.Ordinal);
        Assert.Contains("alter table iam.sessions add column if not exists refresh_token_hash text;", design, StringComparison.Ordinal);
        Assert.Contains("tg_refuse_change('last_seen_at', 'revoked_at', 'revoked_reason', 'refresh_token_hash')", design, StringComparison.Ordinal);
        Assert.Contains("grant update (refresh_token_hash) on iam.sessions to app_rw;", design, StringComparison.Ordinal);
    }

    [Fact]
    public void S105_CR016_a_revision_slice_writes_its_own_file_and_leaves_the_merged_design_file_unchanged()
    {
        var files = Generator.Generate(Root).Files;
        const string Merged = "V202611150302__kernel__design_s1_05.sql";
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, Merged)), files[SqlEmitter.KernelMigrations + "/" + Merged]);
        Assert.DoesNotContain("previous_refresh_hash", files[SqlEmitter.KernelMigrations + "/" + Merged], StringComparison.Ordinal);

        var revision = files[SqlEmitter.KernelMigrations + "/V202611150402__kernel__design_s1_05_r1.sql"];
        Assert.Contains("alter table iam.sessions add column if not exists previous_refresh_hash text;", revision, StringComparison.Ordinal);
        Assert.Contains("tg_refuse_change('last_seen_at', 'revoked_at', 'revoked_reason', 'refresh_token_hash', 'previous_refresh_hash')", revision, StringComparison.Ordinal);
        Assert.Contains("grant update (previous_refresh_hash) on iam.sessions to app_rw;", revision, StringComparison.Ordinal);
    }

    [Fact]
    public void S105_CR012_api_read_only_columns_are_returned_but_never_accepted_by_the_generated_api()
    {
        var files = Generator.Generate(Root).Files;
        var users = files["src/Platform/Erp.Kernel.Api/Generated/Iam/UserEndpoints.g.cs"];
        var request = users[users.IndexOf("public sealed class UserWriteRequest", StringComparison.Ordinal)..users.IndexOf("public sealed class UserListQuery", StringComparison.Ordinal)];
        Assert.DoesNotContain("Status", request, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSystem", request, StringComparison.Ordinal);
        Assert.DoesNotContain("LastLoginAt", request, StringComparison.Ordinal);
        Assert.Contains("row.Status = \"invited\";", users, StringComparison.Ordinal);
        Assert.Contains("public string Status { get; init; }", users, StringComparison.Ordinal);
    }

    [Fact]
    public void S107_CR026_CR029_later_columns_index_and_control_plane_table_go_to_new_files_and_frozen_files_stay()
    {
        var files = Generator.Generate(Root).Files;
        const string FrozenAud = "V202611150006__kernel__aud.sql";
        var frozen = files[SqlEmitter.KernelMigrations + "/" + FrozenAud];
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, FrozenAud)), frozen);
        Assert.DoesNotContain("source", frozen, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_change_log_occurred_at", frozen, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.ControlPlaneMigrations, "V202611150000__controlplane__cp.sql")), files[SqlEmitter.ControlPlaneMigrations + "/V202611150000__controlplane__cp.sql"]);

        var design = files[SqlEmitter.KernelMigrations + "/V202611150304__kernel__design_s1_07.sql"];
        Assert.Contains("alter table aud.change_log add column if not exists source text;", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_change_log_source check (source in ('ui', 'api', 'import', 'job', 'system'))", design, StringComparison.Ordinal);
        Assert.Contains("alter table aud.access_log add column if not exists correlation_id uuid;", design, StringComparison.Ordinal);
        Assert.Contains("create index if not exists ix_change_log_occurred_at on aud.change_log (occurred_at);", design, StringComparison.Ordinal);
        Assert.Contains("create table if not exists cp.audit_anchors (", files[SqlEmitter.ControlPlaneMigrations + "/V202611150304__controlplane__design_s1_07.sql"], StringComparison.Ordinal);

        var functions = files[SqlEmitter.KernelMigrations + "/V202611150334__kernel__functions_s1_07.sql"];
        Assert.Contains("create or replace function aud.append_changes(p_entries jsonb)", functions, StringComparison.Ordinal);
        Assert.Contains("revoke all on function aud.append_changes(jsonb) from public;", functions, StringComparison.Ordinal);
        Assert.Contains("language plpgsql security definer", functions, StringComparison.Ordinal);
    }

    [Fact]
    public void S107_r1_CR031_CR032_the_revision_writes_its_own_functions_file_and_the_merged_one_stays()
    {
        var files = Generator.Generate(Root).Files;
        const string Merged = "V202611150334__kernel__functions_s1_07.sql";
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, Merged)), files[SqlEmitter.KernelMigrations + "/" + Merged]);
        var revision = files[SqlEmitter.KernelMigrations + "/V202611150434__kernel__functions_s1_07_r1.sql"];
        Assert.Contains("revoke insert on all tables in schema aud from app_audit;", revision, StringComparison.Ordinal);
        Assert.Contains("if at > db_now + interval '5 minutes' then", revision, StringComparison.Ordinal);
        Assert.Contains("'aud.clock_ahead'", revision, StringComparison.Ordinal);

        // The frozen S1.03 grants file keeps its text; later design files never grant INSERT on audit tables to app_audit.
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, "V202611150200__kernel__grants.sql")), files[SqlEmitter.KernelMigrations + "/V202611150200__kernel__grants.sql"]);
        Assert.All(files.Where(f => f.Key.Contains("__kernel__design_", StringComparison.Ordinal)), f => Assert.DoesNotContain("to app_audit", f.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void S107_Q4_append_only_tables_and_machinery_opt_out_of_the_change_log_with_a_reason()
    {
        var m = Model.Value;
        Assert.All(m.Tables.Where(t => t.Database == DatabaseKind.Tenant && t.IsAppendOnly), t => Assert.False(t.Audited, t.FullName));
        Assert.All(m.Tables.Where(t => !t.Audited && !t.IsOutboxOrInbox), t => Assert.False(string.IsNullOrWhiteSpace(t.AuditReason), t.FullName));
        Assert.True(m.Table("hrm.persons").Audited);
        Assert.False(m.Table("iam.sessions").Audited);
        Assert.Equal("personal", m.Table("hrm.persons").Column("date_of_birth").FieldClass);
        Assert.Equal("personal", m.Table("hrm.employee_bank_accounts").Column("account_number").FieldClass);
    }

    [Fact]
    public void S107_Q4_an_append_only_table_without_an_audit_decision_stops_the_generator()
    {
        var root = CopyDesign();
        try
        {
            var file = Path.Combine(root, "docs", "data-design", "hrm-time.yaml");
            var text = File.ReadAllText(file);
            var start = text.IndexOf("- table: hrm.leave_ledger", StringComparison.Ordinal);
            var audit = text.IndexOf("  audit: false\n", start, StringComparison.Ordinal);
            var end = text.IndexOf('\n', text.IndexOf("  audit_reason:", audit, StringComparison.Ordinal)) + 1;
            File.WriteAllText(file, text[..audit] + text[end..]);

            var error = Assert.Throws<DesignException>(() => Generator.LoadModel(root));
            Assert.Contains("hrm.leave_ledger: append-only table must declare audit: false", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void S107_P6_personal_fields_do_not_make_a_permission_sensitive_but_salary_fields_do()
    {
        var files = Generator.Generate(Root).Files;
        var manifest = files["src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.g.json"];
        using var doc = System.Text.Json.JsonDocument.Parse(manifest);
        bool Sensitive(string code) => doc.RootElement.GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("code").GetString() == code).GetProperty("sensitive").GetBoolean();
        Assert.False(Sensitive("hrm.persons.read"));
        Assert.False(Sensitive("hrm.person_contacts.read"));
        Assert.True(Sensitive("hrm.pay_results.read"));
        Assert.True(Sensitive("hrm.person_identifiers.read")); // encrypted column, as before S1.07
    }

    [Theory]
    [InlineData("ix", "short", new[] { "a" }, "ix_short_a")]
    [InlineData("uq", "employment_statutory_registrations", new[] { "company_id", "lower(code)" }, "uq_employment_statutory_registrations_company_id_code")]
    public void Naming_follows_the_prefix_table_columns_rule(string prefix, string table, string[] parts, string expected) =>
        Assert.Equal(expected, Naming.DbName(prefix, table, parts));

    [Fact]
    public void Naming_long_names_are_cut_to_63_characters_with_a_stable_hash()
    {
        var parts = new[] { "a_very_long_column_name", "another_very_long_column_name", "third" };
        var name = Naming.DbName("ix", "employment_statutory_registrations", parts);
        Assert.Equal(Naming.MaxIdentifierLength, name.Length);
        Assert.Equal(name, Naming.DbName("ix", "employment_statutory_registrations", parts));
        Assert.NotEqual(name, Naming.DbName("ix", "employment_statutory_registrations", [.. parts, "x"]));
        Assert.All(Model.Value.Tables.SelectMany(t => t.Indexes.Select(i => i.Name).Concat(t.Uniques.Select(u => u.Name))), n => Assert.True(n.Length <= 63, n));
    }

    [Theory]
    [InlineData("users", "user")]
    [InlineData("companies", "company")]
    [InlineData("payment_batches", "payment_batch")]
    [InlineData("addresses", "address")]
    [InlineData("leave_ledger", "leave_ledger")]
    [InlineData("access_log", "access_log")]
    public void Naming_singular_entity_names(string table, string expected) => Assert.Equal(expected, Naming.Singular(table));

    [Fact]
    public void Validator_stops_on_untranslated_design_text_instead_of_guessing()
    {
        var root = CopyDesign();
        try
        {
            var file = Path.Combine(root, "docs", "data-design", "kernel.yaml");
            File.WriteAllText(file, File.ReadAllText(file).Replace("total_debit = total_credit", "total debit equals total credit", StringComparison.Ordinal));

            var error = Assert.Throws<DesignException>(() => Generator.LoadModel(root));
            Assert.Contains("untranslated check", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Validator_stops_on_a_foreign_key_to_a_missing_table()
    {
        var root = CopyDesign();
        try
        {
            var file = Path.Combine(root, "docs", "data-design", "hrm-time.yaml");
            File.WriteAllText(file, File.ReadAllText(file).Replace("fk hrm.shifts]", "fk hrm.no_such_table]", StringComparison.Ordinal));

            var error = Assert.Throws<DesignException>(() => Generator.LoadModel(root));
            Assert.Contains("hrm.no_such_table does not exist", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CopyDesign()
    {
        var root = Directory.CreateTempSubdirectory("erp-codegen-").FullName;
        foreach (var folder in new[] { Path.Combine("docs", "data-design"), Path.Combine("tools", "Erp.CodeGen") })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            foreach (var file in Directory.GetFiles(Path.Combine(Root, folder), "*.yaml"))
            {
                File.Copy(file, Path.Combine(root, folder, Path.GetFileName(file)));
            }
        }

        File.WriteAllText(Path.Combine(root, "Erp.sln"), string.Empty);
        return root;
    }
}
