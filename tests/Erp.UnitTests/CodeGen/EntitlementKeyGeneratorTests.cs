using Erp.CodeGen;
using Erp.CodeGen.Design;

namespace Erp.UnitTests.CodeGen;

/// <summary>
/// Slice S1.09b generator keys (CR-036 items 5 and 6, CR-039): the <c>entitlement:</c> key (file default, table override),
/// <c>uniques_replaced_in</c>, a partial <c>uniques_added_in</c> and control-plane columns added later.
/// </summary>
public sealed class EntitlementKeyGeneratorTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);
    private static readonly Lazy<DesignModel> Model = new(() => Generator.LoadModel(Root));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Files = new(() => Generator.Generate(Root).Files);

    [Theory]
    [InlineData("hrm.persons", "hrm.core")]
    [InlineData("hrm.leave_requests", "hrm.core")]
    [InlineData("hrm.pay_groups", "hrm.payroll")]
    [InlineData("hrm.form_definitions", "hrm.performance")]
    [InlineData("hrm.courses", "hrm.learning")]
    [InlineData("hrm.training_bonds", "hrm.learning")]
    [InlineData("org.companies", "platform.base")]
    public void FR_KRN_ENT_03_ruling_Q9_each_table_carries_the_feature_key_of_its_file_or_its_own(string table, string key) =>
        Assert.Equal(key, Model.Value.Table(table).Entitlement);

    [Fact]
    public void FR_KRN_ENT_03_every_customer_table_has_a_key_and_no_control_plane_table_has_one()
    {
        Assert.All(Model.Value.DesignTables.Where(t => t.Database == DatabaseKind.Tenant), t => Assert.NotNull(t.Entitlement));
        Assert.All(Model.Value.Tables.Where(t => t.Database == DatabaseKind.ControlPlane), t => Assert.Null(t.Entitlement));
    }

    [Fact]
    public void FR_KRN_ENT_03_generated_endpoints_carry_their_feature_key_and_module()
    {
        Assert.Contains("new global::Erp.BuildingBlocks.Contracts.Api.EntitlementMetadata(\"hrm.payroll\", \"hrm\")", Files.Value["src/Modules/Hrm/Erp.Hrm.Api/Generated/PayGroupEndpoints.g.cs"], StringComparison.Ordinal);
        Assert.Contains("new global::Erp.BuildingBlocks.Contracts.Api.EntitlementMetadata(\"platform.base\", \"kernel\")", Files.Value["src/Platform/Erp.Kernel.Api/Generated/Org/CompanyEndpoints.g.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void ADM_PLT_03_CR_039_the_control_plane_design_file_adds_scheduled_and_the_copy_key_while_the_first_file_keeps_its_condition()
    {
        var first = Files.Value["src/Platform/Erp.Kernel.Infrastructure/ControlPlane/Migrations/V202611150000__controlplane__cp.sql"];
        Assert.Contains("create unique index if not exists uq_entitlements_tenant_id on cp.entitlements (tenant_id) where (status <> 'superseded');", first, StringComparison.Ordinal);
        Assert.Contains("'superseded'))", first, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduled", first, StringComparison.Ordinal);
        var design = Files.Value["src/Platform/Erp.Kernel.Infrastructure/ControlPlane/Migrations/V202611150306__controlplane__design_s1_09b.sql"];
        Assert.Contains("'superseded', 'scheduled'))", design, StringComparison.Ordinal);
        Assert.Contains("drop index if exists cp.uq_entitlements_tenant_id;", design, StringComparison.Ordinal);
        Assert.Contains("where (status not in ('superseded', 'scheduled'))", design, StringComparison.Ordinal);
        Assert.Contains("alter table cp.license_events add column if not exists source_event_id uuid;", design, StringComparison.Ordinal);
        Assert.Contains("uq_license_events_source_event_id on cp.license_events (source_event_id) nulls not distinct where (source_event_id is not null)", design, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("- defaults:\n  entitlement: Hrm Core\n", "is not a feature key")]
    [InlineData("- defaults:\n  audit: false\n", "holds only `entitlement:`")]
    public void CR_036_the_generator_refuses_a_bad_defaults_entry(string entry, string problem)
    {
        var folder = Directory.CreateTempSubdirectory("erp-s109b-design-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, "docs", "data-design"), "*.yaml"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }

            var people = Path.Combine(folder, "hrm-people.yaml");
            File.WriteAllText(people, File.ReadAllText(people).Replace("- defaults:\n  entitlement: hrm.core\n", entry, StringComparison.Ordinal));
            var error = Assert.Throws<DesignException>(() => DesignLoader.Load(folder, Path.Combine(Root, "tools", "Erp.CodeGen")));
            Assert.Contains(problem, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CR_036_a_customer_table_without_an_entitlement_key_is_refused()
    {
        var folder = Directory.CreateTempSubdirectory("erp-s109b-design-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, "docs", "data-design"), "*.yaml"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }

            var people = Path.Combine(folder, "hrm-people.yaml");
            File.WriteAllText(people, File.ReadAllText(people).Replace("- defaults:\n  entitlement: hrm.core\n", string.Empty, StringComparison.Ordinal));
            var error = Assert.Throws<DesignException>(() => DesignLoader.Load(folder, Path.Combine(Root, "tools", "Erp.CodeGen")));
            Assert.Contains("needs an entitlement key", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
