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
        var design = Model.Value.DesignTables.ToList();
        Assert.Equal(204, design.Count);
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
        Assert.Equal(30, stubs.Count);
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
