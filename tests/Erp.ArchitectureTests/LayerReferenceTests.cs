using System.Reflection;
using Erp.ArchitectureTests.Rules;
using NetArchTest.Rules;

namespace Erp.ArchitectureTests;

/// <summary>Spec 01 section 4.3 layer rules and D-10 module isolation.</summary>
public sealed class LayerReferenceTests
{
    [Fact]
    public void D10_project_references_follow_the_layer_rules()
    {
        var violations = ReferenceRules.Check(ProductAssemblies.Projects);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void D10_compiled_types_do_not_depend_on_another_modules_internals_or_on_a_lower_layer_breach()
    {
        var modules = ProductAssemblies.Product.Where(p => p.Kind is ProjectKind.Kernel or ProjectKind.BusinessModule).ToList();
        var failures = new List<string>();

        foreach (var project in modules)
        {
            var forbidden = modules
                .Where(other => other.Module != project.Module && other.Layer != "Contracts")
                .Select(other => other.Name)
                .Concat(["Erp.Host"])
                .ToList();
            if (project.Layer is "Domain" or "Contracts")
            {
                forbidden.AddRange(ProjectInfo.Layers
                    .Where(l => l is "Application" or "Infrastructure" or "Api")
                    .Select(l => $"{project.Module}.{l}"));
            }

            var assembly = Assembly.LoadFrom(ProductAssemblies.PathOf(project));
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny([.. forbidden]).GetResult();
            if (!result.IsSuccessful)
            {
                failures.AddRange(result.FailingTypeNames?.Select(t => $"{project.Name}: {t} depends on one of {string.Join(", ", forbidden)}") ?? []);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Rule_detects_a_module_referencing_another_modules_domain()
    {
        var projects = new List<ProjectInfo>
        {
            ProjectInfo.Create("Erp.BuildingBlocks.Domain", ProjectKind.BuildingBlocks, []),
            ProjectInfo.Create("Erp.BuildingBlocks.Application", ProjectKind.BuildingBlocks, ["Erp.BuildingBlocks.Domain"]),
            ProjectInfo.Create("Erp.Kernel.Domain", ProjectKind.Kernel, ["Erp.BuildingBlocks.Domain"]),
            ProjectInfo.Create("Erp.Hrm.Application", ProjectKind.BusinessModule, ["Erp.Kernel.Domain"]),
        };

        var violations = ReferenceRules.Check(projects);

        Assert.Contains(violations, v => v.StartsWith("Erp.Hrm.Application", StringComparison.Ordinal) && v.Contains("Erp.Kernel.Domain", StringComparison.Ordinal));
    }

    [Fact]
    public void Rule_detects_domain_referencing_infrastructure()
    {
        var projects = new List<ProjectInfo>
        {
            ProjectInfo.Create("Erp.Hrm.Infrastructure", ProjectKind.BusinessModule, []),
            ProjectInfo.Create("Erp.Hrm.Domain", ProjectKind.BusinessModule, ["Erp.Hrm.Infrastructure"]),
        };

        var violations = ReferenceRules.Check(projects);

        Assert.Contains(violations, v => v.StartsWith("Erp.Hrm.Domain", StringComparison.Ordinal));
    }

    [Fact]
    public void Rule_allows_application_to_use_another_modules_contracts()
    {
        var projects = new List<ProjectInfo>
        {
            ProjectInfo.Create("Erp.BuildingBlocks.Contracts", ProjectKind.BuildingBlocks, []),
            ProjectInfo.Create("Erp.Kernel.Contracts", ProjectKind.Kernel, ["Erp.BuildingBlocks.Contracts"]),
            ProjectInfo.Create("Erp.Hrm.Application", ProjectKind.BusinessModule, ["Erp.Kernel.Contracts"]),
        };

        var violations = ReferenceRules.Check(projects).Where(v => !v.StartsWith("Business module", StringComparison.Ordinal));

        Assert.Empty(violations);
    }

    [Fact]
    public void Rule_detects_a_business_module_missing_one_of_its_five_projects()
    {
        var projects = new List<ProjectInfo> { ProjectInfo.Create("Erp.Hrm.Domain", ProjectKind.BusinessModule, []) };

        var violations = ReferenceRules.Check(projects);

        Assert.Contains(violations, v => v.Contains("missing projects", StringComparison.Ordinal));
    }
}
