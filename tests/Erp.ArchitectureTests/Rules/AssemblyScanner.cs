using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Erp.ArchitectureTests.Rules;

/// <summary>Reads compiled IL with Mono.Cecil, so the rules see what actually ships, not only source text.</summary>
public static class AssemblyScanner
{
    public const string ClockInterface = "Erp.BuildingBlocks.Domain.Time.IClock";
    public const string ControlPlaneNamespaceSegment = "ControlPlane";

    private static readonly HashSet<string> ForbiddenClockMembers =
    [
        "System.DateTime::get_Now",
        "System.DateTime::get_UtcNow",
        "System.DateTime::get_Today",
        "System.DateTimeOffset::get_Now",
        "System.DateTimeOffset::get_UtcNow",
    ];

    /// <summary>Direct reads of the system clock outside a type that implements <see cref="ClockInterface"/>.</summary>
    public static IReadOnlyList<string> FindSystemClockCalls(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        var violations = new List<string>();

        foreach (var type in AllTypes(module))
        {
            if (IsInsideClockImplementation(type))
            {
                continue;
            }

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.FlowControl is not FlowControl.Call && instruction.OpCode.Code is not Code.Ldftn)
                    {
                        continue;
                    }

                    if (instruction.Operand is MethodReference called
                        && ForbiddenClockMembers.Contains($"{called.DeclaringType.FullName}::{called.Name}"))
                    {
                        violations.Add($"{module.Assembly.Name.Name}: {type.FullName}.{method.Name} calls " +
                                       $"{called.DeclaringType.Name}.{called.Name[4..]}; inject IClock instead.");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>Fields or properties named TenantId / tenant_id (any case) outside a ControlPlane namespace (D-47).</summary>
    public static IReadOnlyList<string> FindTenantIdMembers(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        var violations = new List<string>();

        foreach (var type in AllTypes(module).Where(t => !IsControlPlane(t)))
        {
            var names = type.Properties.Select(p => p.Name)
                .Concat(type.Fields.Select(f => f.Name))
                .Concat(type.Methods.SelectMany(m => m.Parameters).Select(p => p.Name));
            foreach (var name in names.Where(IsTenantIdName).Distinct(StringComparer.Ordinal))
            {
                violations.Add($"{module.Assembly.Name.Name}: {type.FullName} has member '{name}'; " +
                               "business tables carry no tenant_id (D-47).");
            }
        }

        return violations;
    }

    public static bool IsTenantIdName(string name)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return letters.Contains("tenantid", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes)
            {
                stack.Push(nested);
            }
        }
    }

    // Compiler-generated nested types (lambdas, async state machines) inherit the exemption of their parent.
    private static bool IsInsideClockImplementation(TypeDefinition type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.Interfaces.Any(i => i.InterfaceType.FullName == ClockInterface))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsControlPlane(TypeDefinition type)
    {
        var outer = type;
        while (outer.DeclaringType is not null)
        {
            outer = outer.DeclaringType;
        }

        return outer.Namespace.Split('.').Contains(ControlPlaneNamespaceSegment, StringComparer.Ordinal);
    }
}
