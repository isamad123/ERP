namespace Erp.ArchitectureTests.Rules;

/// <summary>
/// Project reference rules from spec 01 section 4.3 and D-10. Each method returns human-readable violations;
/// an empty list means the rule holds.
/// </summary>
public static class ReferenceRules
{
    private const string BbContracts = "Erp.BuildingBlocks.Contracts";
    private const string BbDomain = "Erp.BuildingBlocks.Domain";
    private const string BbApplication = "Erp.BuildingBlocks.Application";
    private const string BbInfrastructure = "Erp.BuildingBlocks.Infrastructure";

    public static IReadOnlyList<string> Check(IReadOnlyList<ProjectInfo> projects)
    {
        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var project in projects)
        {
            foreach (var referenceName in project.References)
            {
                if (!byName.TryGetValue(referenceName, out var target))
                {
                    violations.Add($"{project.Name} references unknown project {referenceName}.");
                    continue;
                }

                if (!IsAllowed(project, target))
                {
                    violations.Add($"{project.Name} ({Describe(project)}) must not reference {target.Name} ({Describe(target)}).");
                }
            }
        }

        violations.AddRange(CheckModuleShape(projects));
        return violations;
    }

    private static bool IsAllowed(ProjectInfo from, ProjectInfo to)
    {
        // Nothing but tests may reference a host or a tool, and nothing may reference a test project.
        if (to.Kind is ProjectKind.Test)
        {
            return false;
        }

        if (to.Kind is ProjectKind.Host or ProjectKind.Tool)
        {
            return from.Kind is ProjectKind.Test;
        }

        return from.Kind switch
        {
            ProjectKind.Host or ProjectKind.Test => true, // composition roots and tests
            ProjectKind.Tool => to.Kind is ProjectKind.BuildingBlocks,
            ProjectKind.BuildingBlocks => IsAllowedForBuildingBlocks(from.Name, to),
            ProjectKind.Kernel or ProjectKind.BusinessModule => IsAllowedForModuleLayer(from, to),
            _ => false,
        };
    }

    private static bool IsAllowedForBuildingBlocks(string from, ProjectInfo to)
    {
        if (to.Kind is not ProjectKind.BuildingBlocks)
        {
            return false;
        }

        return from switch
        {
            BbContracts or BbDomain => false,
            BbApplication => to.Name is BbDomain or BbContracts,
            BbInfrastructure => to.Name is BbDomain or BbApplication or BbContracts,
            _ => false,
        };
    }

    private static bool IsAllowedForModuleLayer(ProjectInfo from, ProjectInfo to)
    {
        var sameModule = string.Equals(from.Module, to.Module, StringComparison.Ordinal);
        var isAnyContracts = to.Kind is ProjectKind.Kernel or ProjectKind.BusinessModule && to.Layer == "Contracts";

        return from.Layer switch
        {
            "Contracts" => to.Name == BbContracts,
            "Domain" => to.Name == BbDomain,
            // Application may use its own Domain, BuildingBlocks.Application and any module's Contracts (D-10).
            "Application" => (sameModule && to.Layer == "Domain") || to.Name == BbApplication || isAnyContracts,
            "Infrastructure" => (sameModule && to.Layer is "Domain" or "Application") || to.Name == BbInfrastructure,
            "Api" => sameModule && to.Layer is "Application" or "Contracts",
            _ => false,
        };
    }

    /// <summary>
    /// Every project under src/Modules and src/Platform must carry a layer suffix, and every business module
    /// must have all five layer projects (spec 01 section 13). Kernel components may start lean.
    /// </summary>
    private static IEnumerable<string> CheckModuleShape(IReadOnlyList<ProjectInfo> projects)
    {
        foreach (var project in projects.Where(p => p.Kind is ProjectKind.Kernel or ProjectKind.BusinessModule && p.Layer is null))
        {
            yield return $"{project.Name} has no layer suffix ({string.Join(", ", ProjectInfo.Layers)}).";
        }

        foreach (var module in projects.Where(p => p.Kind is ProjectKind.BusinessModule && p.Layer is not null).GroupBy(p => p.Module))
        {
            var missing = ProjectInfo.Layers.Except(module.Select(p => p.Layer!)).ToList();
            if (missing.Count > 0)
            {
                yield return $"Business module {module.Key} is missing projects for: {string.Join(", ", missing)}.";
            }
        }
    }

    private static string Describe(ProjectInfo p) => p.Layer is null ? p.Kind.ToString() : $"{p.Kind} {p.Layer}";
}
