using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Backup;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;

namespace Erp.Host.Migrator;

/// <summary>
/// Command line of the migrator host. Settings come from environment variables only (never the repository):
/// ERP_CONTROL_PLANE (connection string), ERP_SECRETS_FILE, ERP_SECRETS_KEY, ERP_PLATFORM_ADMIN_EMAIL,
/// ERP_PLATFORM_ADMIN_NAME, ERP_PLATFORM_ACTOR (defaults to the admin), ERP_BACKUP_KEY, ERP_CLUSTER_ADMIN_PASSWORD,
/// ERP_MIGRATIONS_ROOT (defaults to the copy next to the executable). See docs/runbooks/tenant-operations.md.
/// </summary>
public static class OperatorCommands
{
    private const string Usage = """
        Usage: Erp.Host.Migrator <command> [options]
          migrate [--tenant <code>]                      control-plane set-up, then pending migrations for every customer
          cluster add --name <n> --region <r> --host <h> --port <p> --max-databases <n> --admin-user <u>
                                                         (password from ERP_CLUSTER_ADMIN_PASSWORD)
          signing-key add --key-id <id> --public-key <file.pem> [--purpose entitlement]
          entitlement sign --payload <file.json> --key-id <id> --private-key <file.pem> --out <file.json>
          tenant provision --code <c> --name <n> --host <h[,h2]> --region <r> --country <CC> --currency <CUR>
                           --size-profile <micro|sme|mid|enterprise> --cluster <n> --legal-name <n>
                           --admin-email <e> --admin-name <n> --entitlement <file.json>
          tenant list
          tenant suspend|resume --code <c> --reason <text>
          tenant backup --code <c> --out <file>          (key from ERP_BACKUP_KEY)
          tenant restore --code <c> --file <file> --target-database <name>
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var command = args[0] is "migrate" ? "migrate" : args.Length > 1 ? args[0] + " " + args[1] : args[0];
            var options = Options(args.Skip(command.Contains(' ', StringComparison.Ordinal) ? 2 : 1).ToArray());
            var context = new Context(output, options);
            return command switch
            {
                "migrate" => await MigrateAsync(context, cancellationToken).ConfigureAwait(false),
                "cluster add" => await ClusterAddAsync(context, cancellationToken).ConfigureAwait(false),
                "signing-key add" => await SigningKeyAddAsync(context, cancellationToken).ConfigureAwait(false),
                "entitlement sign" => await EntitlementSignAsync(context).ConfigureAwait(false),
                "tenant provision" => await ProvisionAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant list" => await ListAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant suspend" => await LifecycleAsync(context, suspend: true, cancellationToken).ConfigureAwait(false),
                "tenant resume" => await LifecycleAsync(context, suspend: false, cancellationToken).ConfigureAwait(false),
                "tenant backup" => await BackupAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant restore" => await RestoreAsync(context, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown command '{command}'.\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or CryptographicException or Npgsql.NpgsqlException)
        {
            await errors.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private sealed class Context(TextWriter output, Dictionary<string, string> options)
    {
        private readonly Lazy<EncryptedFileSecretStore> _secrets = new(() => EncryptedFileSecretStore.FromSettings(Env("ERP_SECRETS_FILE"), Env("ERP_SECRETS_KEY")));

        public TextWriter Output { get; } = output;

        public IClock Clock { get; } = new SystemClock();

        public static ControlPlaneOptions ControlPlane => new() { ConnectionString = Required(Env("ERP_CONTROL_PLANE"), "ERP_CONTROL_PLANE") };

        public EncryptedFileSecretStore Secrets => _secrets.Value;

        public static MigrationSet Migrations
        {
            get
            {
                var root = Env("ERP_MIGRATIONS_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "migrations");
                return MigrationSet.FromRoot(root);
            }
        }

        public ControlPlaneAdmin Admin => new(ControlPlane, Secrets, Clock);

        public string Option(string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");

        public string? OptionalOption(string name) => options.GetValueOrDefault(name);

        public Task<PlatformActor> ActorAsync(CancellationToken cancellationToken) =>
            Admin.ActorAsync(Required(Env("ERP_PLATFORM_ACTOR") ?? Env("ERP_PLATFORM_ADMIN_EMAIL"), "ERP_PLATFORM_ACTOR"), cancellationToken);
    }

    private static async Task<int> MigrateAsync(Context c, CancellationToken cancellationToken)
    {
        var admin = await c.Admin.BootstrapAsync(Context.Migrations, Required(Env("ERP_PLATFORM_ADMIN_EMAIL"), "ERP_PLATFORM_ADMIN_EMAIL"),
            Required(Env("ERP_PLATFORM_ADMIN_NAME"), "ERP_PLATFORM_ADMIN_NAME"), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync("control plane: up to date").ConfigureAwait(false);
        var actor = Env("ERP_PLATFORM_ACTOR") is { } email ? await c.Admin.ActorAsync(email, cancellationToken).ConfigureAwait(false) : admin;
        var outcomes = await new FleetMigrator(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations).MigrateAsync(actor, c.OptionalOption("tenant"), cancellationToken).ConfigureAwait(false);
        foreach (var o in outcomes)
        {
            await c.Output.WriteLineAsync(o.Succeeded
                ? $"{o.Code}: ok, {o.Applied} applied, schema {o.SchemaVersion}"
                : $"{o.Code}: FAILED, now in maintenance: {o.Error}").ConfigureAwait(false);
        }

        await c.Output.WriteLineAsync($"customers: {outcomes.Count(o => o.Succeeded)} migrated, {outcomes.Count(o => !o.Succeeded)} failed").ConfigureAwait(false);
        return outcomes.Any(o => !o.Succeeded) ? 3 : 0;
    }

    private static async Task<int> ClusterAddAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var credential = new DatabaseCredential(c.Option("admin-user"), Required(Env("ERP_CLUSTER_ADMIN_PASSWORD"), "ERP_CLUSTER_ADMIN_PASSWORD"));
        await c.Admin.AddClusterAsync(actor, c.Option("name"), c.Option("region"), c.Option("host"), Int(c.Option("port")), Int(c.Option("max-databases")), credential, cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"cluster {c.Option("name")}: registered").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SigningKeyAddAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(c.Option("public-key"), cancellationToken).ConfigureAwait(false));
        await c.Admin.AddSigningKeyAsync(actor, c.Option("key-id"), c.OptionalOption("purpose") ?? "entitlement", key.ExportSubjectPublicKeyInfo(), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"signing key {c.Option("key-id")}: registered").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> EntitlementSignAsync(Context c)
    {
        var payload = EntitlementPayload.FromJson(JsonNode.Parse(await File.ReadAllTextAsync(c.Option("payload")).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidDataException("The payload file is not a JSON object."));
        var signed = SignedEntitlement.Sign(payload, c.Option("key-id"), await File.ReadAllTextAsync(c.Option("private-key")).ConfigureAwait(false));
        await File.WriteAllTextAsync(c.Option("out"), signed.ToFileJson()).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"entitlement for {payload.Tenant} v{payload.Version}: signed").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ProvisionAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var request = new ProvisioningRequest(
            c.Option("code"), c.Option("name"), c.Option("host").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            c.Option("region"), c.Option("country"), c.Option("currency"), c.Option("size-profile"), c.Option("cluster"), c.Option("legal-name"),
            c.Option("admin-email"), c.Option("admin-name"), await File.ReadAllTextAsync(c.Option("entitlement"), cancellationToken).ConfigureAwait(false));
        var result = await new TenantProvisioner(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations)
            .ProvisionAsync(actor, request, new ConsoleProgress(c.Output), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync(result.AlreadyProvisioned
            ? $"{result.Code}: already provisioned (database {result.DatabaseName}); nothing changed"
            : $"{result.Code}: provisioned (database {result.DatabaseName})").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ListAsync(Context c, CancellationToken cancellationToken)
    {
        var rows = await new TenantLifecycle(Context.ControlPlane, c.Clock).ListAsync(cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync("code\tstatus\tdatabase\tregion\tcluster\tedition\tentitlement\tmodules\tschema\tlegal_hold\tlast_backup\tlast_restore_test\tname").ConfigureAwait(false);
        foreach (var r in rows)
        {
            await c.Output.WriteLineAsync(string.Join('\t', r.Code, r.Status, r.DatabaseStatus, r.RegionCode, r.Cluster, r.Edition ?? "-", r.EntitlementStatus ?? "-",
                string.Join(',', r.Modules), r.SchemaVersion ?? "-", r.LegalHold ? "yes" : "no",
                r.LastBackupAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-", r.LastRestoreTestAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-", r.Name)).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> LifecycleAsync(Context c, bool suspend, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var lifecycle = new TenantLifecycle(Context.ControlPlane, c.Clock);
        if (suspend)
        {
            await lifecycle.SuspendAsync(actor, c.Option("code"), c.Option("reason"), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await lifecycle.ResumeAsync(actor, c.Option("code"), c.Option("reason"), cancellationToken).ConfigureAwait(false);
        }

        await c.Output.WriteLineAsync($"{c.Option("code")}: {(suspend ? "suspended" : "resumed")} (API hosts apply it within their host cache time)").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> BackupAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await new TenantBackup(Context.ControlPlane, c.Secrets, c.Clock, Env("ERP_PG_BIN"))
            .BackupAsync(actor, c.Option("code"), c.Option("out"), BackupCipher.KeyFromSetting(Env("ERP_BACKUP_KEY")), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"{c.Option("code")}: backup written to {c.Option("out")} ({bytes} bytes before encryption)").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RestoreAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var result = await new TenantBackup(Context.ControlPlane, c.Secrets, c.Clock, Env("ERP_PG_BIN"))
            .RestoreAsync(actor, c.Option("code"), c.Option("file"), c.Option("target-database"), BackupCipher.KeyFromSetting(Env("ERP_BACKUP_KEY")), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync(result.Verified
            ? $"{c.Option("code")}: restored into {result.TargetDatabase}; {result.TablesCompared} tables match the source"
            : $"{c.Option("code")}: restored into {result.TargetDatabase} but {result.Differences.Count} tables differ: {string.Join(", ", result.Differences)}").ConfigureAwait(false);
        return result.Verified ? 0 : 4;
    }

    private sealed class ConsoleProgress(TextWriter output) : IProvisioningProgress
    {
        public async ValueTask StepCompletedAsync(string stepName, CancellationToken cancellationToken) =>
            await output.WriteLineAsync($"  step {stepName}: done").ConfigureAwait(false);
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected --name value pairs, found '{args[i]}'.");
            }

            options[args[i][2..]] = args[i + 1];
        }

        return options;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string Required(string? value, string name) => value ?? throw new InvalidOperationException($"{name} must be set.");

    private static int Int(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
