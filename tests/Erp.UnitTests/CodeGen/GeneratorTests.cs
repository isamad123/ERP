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
        Assert.Equal(27, stubs.Count); // 30 from S1.02; S1.03 implemented SC-2 (org.branches), S1.10a FR-XM-32 (wf.workflow_tasks), S1.14b the unit link cycle check (org.org_unit_links)
        Assert.Equal(30, Model.Value.AllStubs.Count());
        Assert.All(stubs, s => Assert.Matches(@"^S1\.\d{2}$", s.Owner));
        Assert.Contains(stubs, s => s.Name == "attendance_periods_lock" && s.Owner == "S1.31"); // FR-XM-26
        Assert.Contains(Model.Value.ImplementedStubs, s => s.Name == "workflow_tasks_no_self_approval" && s.Owner == "S1.10"); // FR-XM-32
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
        var implemented = Assert.Single(Model.Value.ImplementedStubs, s => s.Implementation!.Slice == "S1.03");
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
    public void FR_XM_32_S110a_self_approval_rule_replaces_its_placeholder_in_a_new_migration()
    {
        var implemented = Assert.Single(Model.Value.ImplementedStubs, s => s.Implementation!.Slice == "S1.10a");
        Assert.Equal("workflow_tasks_no_self_approval", implemented.Name);
        var files = Generator.Generate(Root).Files;

        // The merged S1.02 migration still creates the placeholder; the S1.10a rules file drops it and adds the task trigger.
        Assert.Contains("create or replace function wf.todo_workflow_tasks_no_self_approval()", files[SqlEmitter.KernelMigrations + "/V202611150005__kernel__wf.sql"], StringComparison.Ordinal);
        var rules = files[SqlEmitter.KernelMigrations + "/V202611150208__kernel__rules_s1_10a.sql"];
        Assert.Contains("drop function if exists wf.todo_workflow_tasks_no_self_approval();", rules, StringComparison.Ordinal);
        Assert.Contains("create or replace trigger tg_workflow_tasks_no_self_approval", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("tg_workflow_instances_parties_fixed", rules, StringComparison.Ordinal);

        // The trigger that fixes requester and subjects names a column the S1.10a design file adds, so it comes after it.
        Assert.Contains("create or replace trigger tg_workflow_instances_parties_fixed", files[SqlEmitter.KernelMigrations + "/V202611150337__kernel__functions_s1_10a.sql"], StringComparison.Ordinal);
    }

    [Fact]
    public void S110b_CR043_a_later_part_of_a_slice_gets_its_own_design_file_and_the_first_part_keeps_its_name()
    {
        var files = Generator.Generate(Root).Files;
        Assert.True(files.ContainsKey(SqlEmitter.KernelMigrations + "/V202611150307__kernel__design_s1_10a.sql")); // merged, frozen
        var design = files[SqlEmitter.KernelMigrations + "/V202611151307__kernel__design_s1_10b.sql"];
        Assert.Contains("create index if not exists ix_workflow_tasks_status_due_at on wf.workflow_tasks (status, due_at);", design, StringComparison.Ordinal);
        Assert.DoesNotContain("add column", design, StringComparison.Ordinal);
        Assert.DoesNotContain("V202611150307__kernel__design_s1_10b.sql", string.Join("\n", files.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void S103_spec03_roles_app_rw_cannot_update_or_delete_append_only_tables()
    {
        var files = Generator.Generate(Root).Files;
        var grants = files[SqlEmitter.KernelMigrations + "/V202611150200__kernel__grants.sql"] + files[SqlEmitter.HrmMigrations + "/V202611150101__hrm__grants.sql"]
            + string.Concat(files.Where(f => f.Key.Contains("__kernel__design_", StringComparison.Ordinal) || f.Key.Contains("__hrm__design_", StringComparison.Ordinal)).Select(f => f.Value));
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
    public void S111b2_review_CR062_api_create_only_columns_are_set_on_create_and_an_update_that_changes_them_is_refused()
    {
        var files = Generator.Generate(Root).Files;
        var users = files["src/Platform/Erp.Kernel.Api/Generated/Iam/UserEndpoints.g.cs"];
        Assert.Contains("if (!update && request.Email is null)", users, StringComparison.Ordinal);
        var apply = users[users.IndexOf("private static global::System.Collections.Generic.List<string> Apply(", StringComparison.Ordinal)..];
        Assert.Contains("else if (request.Email is not null)", apply, StringComparison.Ordinal);
        Assert.Contains("else if (!global::Erp.BuildingBlocks.Application.Security.FieldShaping.Unchanged(request.Email, row.Email, update))", apply, StringComparison.Ordinal);
        Assert.Contains("\"createOnly\": true", files["web/generated/metadata/iam.users.json"], StringComparison.Ordinal);

        // The mail connector's secret reference is never accepted through the API (finding 1).
        var connectors = files["src/Platform/Erp.Kernel.Api/Generated/Itg/ConnectorEndpoints.g.cs"];
        var request = connectors[connectors.IndexOf("public sealed class ConnectorWriteRequest", StringComparison.Ordinal)..];
        Assert.DoesNotContain("CredentialsRef", request[..request.IndexOf('}')], StringComparison.Ordinal);
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
    public void S108_CR033_numbering_changes_go_to_new_files_and_the_frozen_cfg_file_stays()
    {
        var files = Generator.Generate(Root).Files;
        const string Frozen = "V202611150004__kernel__cfg.sql";
        var frozen = files[SqlEmitter.KernelMigrations + "/" + Frozen];
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, Frozen)), frozen);
        Assert.Contains("company_id uuid not null,", frozen[frozen.IndexOf("create table if not exists cfg.numbering_schemes", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("check (reset_rule in ('never', 'yearly', 'monthly'))", frozen, StringComparison.Ordinal);
        Assert.Contains("ix_numbering_voids_scheme_id", frozen, StringComparison.Ordinal);

        var design = files[SqlEmitter.KernelMigrations + "/V202611150305__kernel__design_s1_08.sql"];
        Assert.Contains("alter table cfg.numbering_schemes alter column company_id drop not null;", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_numbering_schemes_reset_rule check (reset_rule in ('never', 'yearly', 'monthly', 'fiscal_yearly'));", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_numbering_schemes_branch_company check (branch_id is null or company_id is not null);", design, StringComparison.Ordinal);
        Assert.Contains("alter table cfg.numbering_voids add column if not exists document_id uuid;", design, StringComparison.Ordinal);
        Assert.Contains("add constraint uq_numbering_voids_scheme_id_number unique (scheme_id, number);", design, StringComparison.Ordinal);

        var functions = files[SqlEmitter.KernelMigrations + "/V202611150335__kernel__functions_s1_08.sql"];
        Assert.Contains("create or replace function cfg.numbering_next(", functions, StringComparison.Ordinal);
        Assert.Contains("set lock_timeout = '5s'", functions, StringComparison.Ordinal);
        Assert.Contains("grant execute on function cfg.numbering_next(uuid, bigint, text, timestamptz) to app_rw;", functions, StringComparison.Ordinal);
    }

    [Fact]
    public void S108_FR_KRN_NUM_03_numbered_columns_are_filled_by_the_numbering_service_and_never_accepted_from_the_caller()
    {
        var files = Generator.Generate(Root).Files;
        var employments = files["src/Modules/Hrm/Erp.Hrm.Api/Generated/EmploymentEndpoints.g.cs"];
        var request = employments[employments.IndexOf("public sealed class EmploymentWriteRequest", StringComparison.Ordinal)..employments.IndexOf("public sealed class EmploymentListQuery", StringComparison.Ordinal)];
        Assert.DoesNotContain("EmployeeNo", request, StringComparison.Ordinal);
        Assert.Contains("row.EmployeeNo = await numbers.AssignOnCreateAsync(new global::Erp.Kernel.Contracts.Numbering.NumberRequest(\"hrm.employee_no\", row.CompanyId, null, null)", employments, StringComparison.Ordinal);
        Assert.Contains("NumberRequest(\"hrm.contract_no\", row.CompanyId, null, row.StartDate)", files["src/Modules/Hrm/Erp.Hrm.Api/Generated/ContractEndpoints.g.cs"], StringComparison.Ordinal);
        Assert.Contains("NumberRequest(\"hrm.leave_request_no\", row.CompanyId, row.BranchId, row.StartDate)", files["src/Modules/Hrm/Erp.Hrm.Api/Generated/LeaveRequestEndpoints.g.cs"], StringComparison.Ordinal);

        // Slice S1.20a (ruling Q1): persons have no generated create, so nobody can type a person number; the person service
        // takes it from the kernel numbering service.
        var persons = files["src/Modules/Hrm/Erp.Hrm.Api/Generated/PersonEndpoints.g.cs"];
        Assert.DoesNotContain("WriteRequest", persons, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignOnCreateAsync", persons, StringComparison.Ordinal);
        Assert.Contains("new NumberRequest(\"hrm.person_no\", null, null, null)", File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/People/PersonService.cs")), StringComparison.Ordinal);

        var registry = files["src/Modules/Hrm/Erp.Hrm.Infrastructure/Generated/HrmNumberingTypes.g.cs"];
        Assert.Equal(9, Model.Value.Tables.Sum(t => t.Numbering.Count));
        Assert.Contains("new(\"hrm.employee_no\", \"hrm\", \"hrm.employments\", \"employee_no\", \"create\", false, true, false, \"EMP-{seq:5}\", \"never\", false),", registry, StringComparison.Ordinal);
        Assert.Contains("\"hrm.person_no\"", files["src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/numbering.g.json"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default: \"EMP-{seq:5}\", reset: never}", "default: \"EMP-{seq:5}\", reset: yearly}", "errors.numbering.pattern_year_needed")]
    [InlineData("assign: create, override: yes", "assign: create, gap_free: yes, override: yes", "cannot be gap-free")]
    [InlineData("type: hrm.employee_no", "type: hrm.letter_no", "declared more than once")]
    public void S108_Validator_stops_on_an_invalid_numbering_key(string from, string to, string message)
    {
        var root = CopyDesign();
        try
        {
            var file = Path.Combine(root, "docs", "data-design", "hrm-people.yaml");
            var text = File.ReadAllText(file);
            Assert.Contains(from, text, StringComparison.Ordinal);
            File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
            var error = Assert.Throws<DesignException>(() => Generator.LoadModel(root));
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    [Fact]
    public void S109a_CR036_CR037_settings_columns_counters_and_triggers_go_to_new_files_and_the_frozen_cfg_file_stays()
    {
        var files = Generator.Generate(Root).Files;
        const string Frozen = "V202611150004__kernel__cfg.sql";
        Assert.Equal(File.ReadAllText(Path.Combine(Root, SqlEmitter.KernelMigrations, Frozen)), files[SqlEmitter.KernelMigrations + "/" + Frozen]);
        Assert.DoesNotContain("is_visible", files[SqlEmitter.KernelMigrations + "/" + Frozen], StringComparison.Ordinal);

        // A slice built in parts (S1.09a) keeps its slice's place in the file order; the part letter keeps the name distinct.
        var design = files[SqlEmitter.KernelMigrations + "/V202611150306__kernel__design_s1_09a.sql"];
        Assert.Contains("alter table cfg.settings add column if not exists is_visible boolean not null default true;", design, StringComparison.Ordinal);
        Assert.Contains("alter table cfg.settings add column if not exists self_service boolean not null default false;", design, StringComparison.Ordinal);
        Assert.Contains("alter table cfg.settings add column if not exists tighten_only boolean not null default false;", design, StringComparison.Ordinal);
        Assert.Contains("alter table cfg.settings add column if not exists name_i18n jsonb;", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_settings_name_i18n_object check (jsonb_typeof(name_i18n) = 'object');", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_settings_area_code check (length(area) between 1 and 40 and area = btrim(area));", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_settings_role_rule check (role_rule in ('any', 'all', 'lowest', 'highest'));", design, StringComparison.Ordinal);
        Assert.Contains("add constraint ck_feature_flags_module_id_code check", design, StringComparison.Ordinal);
        Assert.Contains("create table if not exists cfg.config_versions (", design, StringComparison.Ordinal);

        var functions = files[SqlEmitter.KernelMigrations + "/V202611150336__kernel__functions_s1_09a.sql"];
        Assert.Contains("create or replace function cfg.tg_config_version()", functions, StringComparison.Ordinal);
        Assert.Contains("create or replace trigger tg_setting_values_config_version after insert or update or delete or truncate on cfg.setting_values", functions, StringComparison.Ordinal);

        // Triggers declared by a functions entry are known to the expected catalogue (DataDesignTests).
        var catalog = files["tests/Erp.IntegrationTests/Generated/expected-catalog.json"];
        Assert.Contains("tg_feature_flag_overrides_config_version", catalog, StringComparison.Ordinal);

        // Generated writes closed (CR-036 part 4, CR-037): only the read endpoints and codes stay.
        Assert.DoesNotContain("cfg.setting_values.update", files["src/Platform/Erp.Kernel.Contracts/Generated/KernelPermissions.g.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain("cfg.setting_value_history.create", files["src/Platform/Erp.Kernel.Infrastructure/Access/permissions.g.json"], StringComparison.Ordinal);
        Assert.Contains("cfg.feature_flag_overrides.read", files["src/Platform/Erp.Kernel.Infrastructure/Access/permissions.g.json"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("    - [unit, text, opt,", "    - [unit, text, req,", "required with a constant default")]
    [InlineData("    - [area, code, opt,", "    - [area, code, req,", "required with a constant default")]
    public void S109a_Validator_refuses_a_later_column_that_existing_rows_could_not_satisfy(string from, string to, string message)
    {
        var root = CopyDesign();
        try
        {
            var file = Path.Combine(root, "docs", "data-design", "kernel.yaml");
            var text = File.ReadAllText(file);
            Assert.Contains(from, text, StringComparison.Ordinal);
            File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
            var error = Assert.Throws<DesignException>(() => Generator.LoadModel(root));
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void S109a_FR_KRN_SET_01_the_kernel_manifest_compiles_to_the_registered_definitions_and_key_constants()
    {
        var files = Generator.Generate(Root).Files;
        var manifest = System.Text.Json.JsonDocument.Parse(files["src/Platform/Erp.Kernel.Infrastructure/Access/settings.g.json"]).RootElement;
        var settings = manifest.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal(52, settings.Count); // six of S1.15a (cfg.branding.product_name, logo_file_id, banner_en, banner_ur, terms_en, terms_ur: login branding, FR-ADM-SEC-08), iam.report.inactive_days (S1.13b, ruling Q8), loc.number.digits and loc.number.grouping (S1.12b, ruling Q6), two of S1.11c2 (doc.template.max_kb, doc.pdf.max_pages), four of S1.11c1 (doc.scan.mode, doc.upload.max_mb, doc.upload.allowed_types, doc.expiry.reminder_days), 22 of S1.09a, cfg.limits.warning_percent (S1.09b, ruling Q7), four of S1.10b (CR-041), two of S1.11a (Q9, P3), ntf.in_app.retention_days (S1.11b1), seven of S1.11b2 (P3, P9, P11)
        Assert.Equal(50, settings.Single(s => s.GetProperty("key").GetString() == "aud.alerts.sensitive_people_per_hour").GetProperty("default").GetInt32()); // S1.11 ruling Q9
        Assert.Equal(30, settings.Single(s => s.GetProperty("key").GetString() == "job.retention.run_days").GetProperty("default").GetInt32()); // S1.11a P3
        var off = settings.Single(s => s.GetProperty("key").GetString() == "org.weekly_off_days");
        Assert.Equal("""["saturday","sunday"]""", string.Concat(off.GetProperty("default").GetRawText().Where(ch => !char.IsWhiteSpace(ch)))); // ruling Q16
        Assert.Equal(2, settings.Single(s => s.GetProperty("key").GetString() == "wf.escalation.default_working_days").GetProperty("default").GetInt32()); // ruling Q9
        Assert.Equal(90, settings.Single(s => s.GetProperty("key").GetString() == "wf.delegation.max_days").GetProperty("default").GetInt32()); // ruling Q8
        Assert.Equal(100, settings.Single(s => s.GetProperty("key").GetString() == "wf.bulk_approval.max_items").GetProperty("default").GetInt32()); // ruling Q12
        var warning = settings.Single(s => s.GetProperty("key").GetString() == "cfg.limits.warning_percent");
        Assert.Equal(90, warning.GetProperty("default").GetInt32());
        Assert.Equal("""{"min":50,"max":99}""", string.Concat(warning.GetProperty("validation").GetRawText().Where(ch => !char.IsWhiteSpace(ch))));
        var mfa = settings.Single(s => s.GetProperty("key").GetString() == "iam.mfa.required");
        Assert.Equal("any", mfa.GetProperty("roleRule").GetString());
        Assert.True(mfa.GetProperty("tightenOnly").GetBoolean());
        var month = settings.Single(s => s.GetProperty("key").GetString() == "cfg.numbering.fiscal_year_start_month");
        Assert.Equal("""{"min":1,"max":12}""", string.Concat(month.GetProperty("validation").GetRawText().Where(ch => !char.IsWhiteSpace(ch))));
        Assert.Contains("pack", month.GetProperty("layers").EnumerateArray().Select(l => l.GetString())); // the country default (CR-061)
        Assert.All(settings, s => Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("help").GetProperty("ur").GetString())));
        Assert.Contains("public const string IamPasswordMinLength = \"iam.password.min_length\";", files["src/Platform/Erp.Kernel.Contracts/Generated/KernelSettingKeys.g.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void S112b_FR_KRN_LOC_02_number_settings_use_the_reserved_loc_prefix_of_the_kernel_only()
    {
        // S1.12 ruling Q6 names loc.number.digits and loc.number.grouping; the kernel reserves the prefix (deviation k of CR-063).
        var files = Generator.Generate(Root).Files;
        var manifest = System.Text.Json.JsonDocument.Parse(files["src/Platform/Erp.Kernel.Infrastructure/Access/settings.g.json"]).RootElement;
        Assert.Contains("loc", manifest.GetProperty("moduleIds").EnumerateArray().Select(i => i.GetString()));
        var settings = manifest.GetProperty("settings").EnumerateArray().ToList();
        var digits = settings.Single(s => s.GetProperty("key").GetString() == "loc.number.digits");
        Assert.Equal("western", digits.GetProperty("default").GetString());
        Assert.Equal(["tenant", "company", "user"], digits.GetProperty("layers").EnumerateArray().Select(l => l.GetString()!).ToArray());
        Assert.True(digits.GetProperty("selfService").GetBoolean());
        Assert.Equal("international", settings.Single(s => s.GetProperty("key").GetString() == "loc.number.grouping").GetProperty("default").GetString());

        // A business module cannot take the prefix: its ids are its schemas only.
        var text = File.ReadAllText(Path.Combine(Root, "src/Platform/Erp.Kernel.Infrastructure", SettingsManifestLoader.ManifestPath));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            var error = Assert.Throws<DesignException>(() => SettingsManifestLoader.Read("hrm", "src/Modules/Hrm/Erp.Hrm.Infrastructure", Model.Value.Settings.HrmSchemas, YamlNode.Load(path), "settings.yaml"));
            Assert.Contains("must start with one of this module's ids", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("type: int\n    default: 10\n", "type: int\n    default: 3\n", "breaks the setting's own rules")]
    [InlineData("    layers: [tenant, role, user]\n    role_rule: any\n", "    layers: [tenant, role, user]\n", "the role layer needs a role_rule")]
    [InlineData("    role_rule: any\n    tighten_only: true\n", "    role_rule: lowest\n    tighten_only: true\n", "role_rule is any or all for bool")]
    [InlineData("  - key: iam.password.min_length\n", "  - key: hrm.password.min_length\n", "must start with one of this module's ids")]
    [InlineData("    validation: {min: 6}\n", "    validation: {min: 6, smallest: 2}\n", "unknown keys: smallest")]
    [InlineData("    name: {en: \"Minimum password length\", ur: \"پاس ورڈ کی کم از کم لمبائی\"}\n", "    name: {en: \"Minimum password length\"}\n", "needs English and Urdu text")]
    [InlineData("    layers: [tenant]\n    area: security.passwords\n    unit: characters\n", "    layers: [tenant]\n    self_service: true\n    area: security.passwords\n    unit: characters\n", "self_service needs the user layer")]
    // S1.09a review findings 3 and 8 (CR-061): approval is not available for iam.* keys nor for self-service settings.
    [InlineData("    layers: [tenant]\n    area: security.passwords\n    unit: characters\n", "    layers: [tenant]\n    requires_approval: true\n    area: security.passwords\n    unit: characters\n", "requires_approval is not available for iam.* settings")]
    [InlineData("    layers: [pack, tenant, company]  # pack: the country default, CR-061\n    area: organization\n", "    layers: [pack, tenant, company, user]\n    self_service: true\n    requires_approval: true\n    area: organization\n", "cannot be self_service")]
    public void S109a_FR_KRN_SET_01_an_invalid_manifest_stops_the_generator(string from, string to, string message)
    {
        var text = File.ReadAllText(Path.Combine(Root, "src/Platform/Erp.Kernel.Infrastructure", SettingsManifestLoader.ManifestPath));
        Assert.Contains(from, text, StringComparison.Ordinal);
        var index = text.IndexOf(from, StringComparison.Ordinal);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text[..index] + to + text[(index + from.Length)..]);
            var model = Model.Value;
            var error = Assert.Throws<DesignException>(() => SettingsManifestLoader.Read("kernel", "src/Platform/Erp.Kernel.Infrastructure", model.Settings.KernelSchemas, YamlNode.Load(path), "settings.yaml"));
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
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
