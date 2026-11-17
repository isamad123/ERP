using Erp.CodeGen.Design;
using Erp.CodeGen.Emit;

namespace Erp.CodeGen;

/// <summary>Command line: <c>generate [--root dir] [--out dir]</c> and <c>dictionary --connection cs [--root dir]</c>.</summary>
public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "help";
        var options = ParseOptions(args.Skip(1).ToArray());
        var root = options.TryGetValue("root", out var r) ? Path.GetFullPath(r) : RepositoryRoot.Find(Directory.GetCurrentDirectory());

        switch (command)
        {
            case "generate":
            {
                var output = Generator.Generate(root);
                var target = options.TryGetValue("out", out var o) ? Path.GetFullPath(o) : root;
                var (written, unchanged, deleted) = output.WriteTo(target);
                Console.WriteLine($"Erp.CodeGen: {output.Files.Count} files ({written} written, {unchanged} unchanged, {deleted} stale deleted).");
                return 0;
            }

            case "dictionary":
            {
                if (!options.TryGetValue("connection", out var connection))
                {
                    Console.Error.WriteLine("dictionary needs --connection \"<PostgreSQL connection string with rights to create a database>\"");
                    return 1;
                }

                var model = Generator.LoadModel(root);
                var written = await Dictionary.DictionaryWriter.BuildAndWriteAsync(model, root, connection, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Erp.CodeGen: data dictionary written to {written}.");
                return 0;
            }

            default:
                Console.WriteLine("Usage: Erp.CodeGen generate [--root <repo>] [--out <folder>]");
                Console.WriteLine("       Erp.CodeGen dictionary --connection <connection string> [--root <repo>]");
                return command == "help" ? 0 : 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"unexpected argument '{args[i]}'");
            }

            options[args[i][2..]] = args[i + 1];
        }

        return options;
    }
}

public static class Generator
{
    public static DesignModel LoadModel(string root) =>
        DesignLoader.Load(Path.Combine(root, "docs", "data-design"), Path.Combine(root, "tools", "Erp.CodeGen"));

    /// <summary>Produces every generated file in memory. Pure: the same design always gives the same files.</summary>
    public static OutputSet Generate(string root)
    {
        var model = LoadModel(root);
        var output = new OutputSet();
        SqlEmitter.Emit(model, output);
        CSharpEmitter.Emit(model, output);
        TypeScriptEmitter.Emit(model, output);
        CatalogEmitter.Emit(model, output);
        SettingsEmitter.Emit(SettingsManifestLoader.Load(root, model.Settings), output);
        return output;
    }
}

public static class RepositoryRoot
{
    public static string Find(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException($"Erp.sln not found above {start}; pass --root.");
    }
}
