using Erp.ArchitectureTests.Rules;

namespace Erp.ArchitectureTests;

/// <summary>D-47: one database per customer; business and kernel code carry no tenant_id.</summary>
public sealed class TenantIdTests
{
    [Fact]
    public void D47_module_and_kernel_types_have_no_tenant_id_member()
    {
        var violations = ProductAssemblies.Product
            .Where(p => p.Kind is ProjectKind.Kernel or ProjectKind.BusinessModule or ProjectKind.BuildingBlocks)
            .SelectMany(p => AssemblyScanner.FindTenantIdMembers(ProductAssemblies.PathOf(p)))
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void D47_module_and_kernel_source_and_sql_have_no_tenant_id_column()
    {
        var violations = SourceTextScanner.FindTenantIdText(RepositoryLayout.TenantDatabaseCodeFolders);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Rule_detects_a_tenant_id_member_and_exempts_the_control_plane()
    {
        var violations = AssemblyScanner.FindTenantIdMembers(typeof(TenantIdTests).Assembly.Location);

        Assert.Contains(violations, v => v.Contains("TenantIdViolationSample", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("PermittedTenantRegistrySample", StringComparison.Ordinal));
    }

    [Fact]
    public void Rule_detects_tenant_id_in_sql_and_exempts_a_control_plane_folder()
    {
        var root = Directory.CreateTempSubdirectory("erp-arch-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Hrm", "Migrations"));
            Directory.CreateDirectory(Path.Combine(root, "Kernel", "ControlPlane"));
            File.WriteAllText(Path.Combine(root, "Hrm", "Migrations", "0001.sql"), "create table hrm.employees (id uuid, TENANT_ID uuid);");
            File.WriteAllText(Path.Combine(root, "Kernel", "ControlPlane", "0001.sql"), "create table cp.tenant_domains (id uuid, tenant_id uuid);");
            File.WriteAllText(Path.Combine(root, "Hrm", "Clean.sql"), "create table hrm.leave_types (id uuid, company_id uuid);");

            var violations = SourceTextScanner.FindTenantIdText([root]);

            var single = Assert.Single(violations);
            Assert.Contains("0001.sql:1", single, StringComparison.Ordinal);
            Assert.Contains("Hrm", single, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("TenantId", true)]
    [InlineData("tenant_id", true)]
    [InlineData("<TenantId>k__BackingField", true)]
    [InlineData("CompanyId", false)]
    [InlineData("Tenant", false)]
    public void Rule_recognises_tenant_id_names(string name, bool expected) =>
        Assert.Equal(expected, AssemblyScanner.IsTenantIdName(name));
}
