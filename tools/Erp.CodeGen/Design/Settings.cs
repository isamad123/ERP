namespace Erp.CodeGen.Design;

/// <summary>tools/Erp.CodeGen/codegen-settings.yaml.</summary>
public sealed class Settings
{
    public required string Release { get; init; }

    public required string MigrationDate { get; init; }

    public required DateOnly PartitionsFrom { get; init; }

    public required DateOnly PartitionsTo { get; init; }

    public required IReadOnlyList<string> KernelSchemas { get; init; }

    public required IReadOnlyList<string> HrmSchemas { get; init; }

    public required IReadOnlyList<string> ControlPlaneSchemas { get; init; }

    public required IReadOnlySet<string> ApiExcluded { get; init; }

    public IEnumerable<string> TenantSchemas => KernelSchemas.Concat(HrmSchemas);

    public string ModuleOf(string schema) =>
        KernelSchemas.Contains(schema) ? "kernel"
        : HrmSchemas.Contains(schema) ? "hrm"
        : ControlPlaneSchemas.Contains(schema) ? "controlplane"
        : throw new DesignException(schema, "schema is not listed in codegen-settings.yaml");

    public static Settings Load(string path)
    {
        var map = YamlNode.Map(YamlNode.Load(path), path);
        string Get(string key) => YamlNode.Text(map[key], key);
        return new Settings
        {
            Release = Get("release"),
            MigrationDate = Get("migration_date"),
            PartitionsFrom = DateOnly.ParseExact(Get("partitions_from") + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            PartitionsTo = DateOnly.ParseExact(Get("partitions_to") + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            KernelSchemas = YamlNode.Texts(map["kernel_schemas"], "kernel_schemas"),
            HrmSchemas = YamlNode.Texts(map["hrm_schemas"], "hrm_schemas"),
            ControlPlaneSchemas = YamlNode.Texts(map["control_plane_schemas"], "control_plane_schemas"),
            ApiExcluded = YamlNode.Texts(map["api_excluded"], "api_excluded").ToHashSet(StringComparer.Ordinal),
        };
    }
}
