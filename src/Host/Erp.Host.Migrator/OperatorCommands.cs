using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Backup;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Notifications.Email;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
                                                         [--change-address yes] (only to re-point a cluster that holds databases)
                                                         (password from ERP_CLUSTER_ADMIN_PASSWORD)
          signing-key add --key-id <id> --public-key <file.pem> [--purpose entitlement]
          entitlement sign --payload <file.json> --key-id <id> --private-key <file.pem> --out <file.json>
          entitlement draft --edition <starter|business|enterprise> --tenant <code> --out <file.json> [--from <yyyy-mm-dd>] [--to <yyyy-mm-dd>]
                                                         unsigned payload of the next version, pre-filled from the edition template
          entitlement apply --tenant <code> --file <signed.json>
                                                         checks and applies a signed entitlement (future start date: scheduled)
          entitlement history --tenant <code>
          licence refresh [--tenant <code>]              records licence-state changes, activates scheduled entitlements,
                                                         takes usage readings (the worker does this every hour)
          usage statement --tenant <code> --month <yyyymm>
          workflow escalate [--tenant <code>]            moves approval tasks past their time limit to the next level
                                                         (the worker does this every minute; never approves anything)
          tenant provision --code <c> --name <n> --host <h[,h2]> --region <r> --country <CC> --currency <CUR>
                           --size-profile <micro|sme|mid|enterprise> --cluster <n> --legal-name <n>
                           --admin-email <e> --admin-name <n> --entitlement <file.json>
          tenant admin-activation-link --code <c>        prints a new activation link for the invited first administrator
          tenant admin-role-repair --code <c> --reason <text>
                                                         Super Admin: restores the protected Tenant Admin role (CR-024)
          tenant admin-grant --code <c> --email <e> --reason <text>
                                                         Super Admin: gives the Tenant Admin role to that user
          tenant admin-revoke --code <c> --email <e> --reason <text>
                                                         Super Admin: takes the Tenant Admin role away, even from the last admin
          auth-key rotate                                adds a new access-token signing key and makes it current
          tenant list
          tenant suspend|resume --code <c> --reason <text>
          tenant backup --code <c> --out <file>          (key from ERP_BACKUP_KEY)
          tenant restore --code <c> --file <file> --target-database <name>
          audit anchors-publish [--tenant <code>]        copies finished days' audit-chain anchors to the control plane (S1.07 P5);
                                                         exit code 3 when an anchor differs from its earlier copy
          audit verify --tenant <code> --from <yyyy-mm-dd> --to <yyyy-mm-dd>
                                                         recomputes the audit hash chain; exit code 3 names the first broken row
          mail sender --tenant <code> --from <address>
                                                         sender address on the customer's own domain, after the SPF and DKIM
                                                         checks (docs/runbooks/mail-setup.md); sent through the operator relay
          mail server --tenant <code> --host <h> --port <p> --security <starttls|ssl|none> --from <address> [--username <u>]
                                                         the customer's own mail server (on-premises); password from ERP_MAIL_PASSWORD
          mail clear --tenant <code>                     back to the operator relay and its sender address
          mail show --tenant <code>
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
                "entitlement draft" => await EntitlementDraftAsync(context, cancellationToken).ConfigureAwait(false),
                "entitlement apply" => await EntitlementApplyAsync(context, cancellationToken).ConfigureAwait(false),
                "entitlement history" => await EntitlementHistoryAsync(context, cancellationToken).ConfigureAwait(false),
                "licence refresh" => await LicenceRefreshAsync(context, cancellationToken).ConfigureAwait(false),
                "usage statement" => await UsageStatementAsync(context, cancellationToken).ConfigureAwait(false),
                "workflow escalate" => await WorkflowEscalateAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant provision" => await ProvisionAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant list" => await ListAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant admin-activation-link" => await ActivationLinkAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant admin-role-repair" => await RecoveryAsync(context, (r, a) => r.RepairRoleAsync(a, context.Option("code"), context.OptionalOption("reason"), cancellationToken)).ConfigureAwait(false),
                "tenant admin-grant" => await RecoveryAsync(context, (r, a) => r.GrantAsync(a, context.Option("code"), context.Option("email"), context.OptionalOption("reason"), cancellationToken)).ConfigureAwait(false),
                "tenant admin-revoke" => await RecoveryAsync(context, (r, a) => r.RevokeAsync(a, context.Option("code"), context.Option("email"), context.OptionalOption("reason"), cancellationToken)).ConfigureAwait(false),
                "auth-key rotate" => await RotateTokenKeyAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant suspend" => await LifecycleAsync(context, suspend: true, cancellationToken).ConfigureAwait(false),
                "tenant resume" => await LifecycleAsync(context, suspend: false, cancellationToken).ConfigureAwait(false),
                "tenant backup" => await BackupAsync(context, cancellationToken).ConfigureAwait(false),
                "tenant restore" => await RestoreAsync(context, cancellationToken).ConfigureAwait(false),
                "audit anchors-publish" => await AnchorsPublishAsync(context, cancellationToken).ConfigureAwait(false),
                "audit verify" => await AuditVerifyAsync(context, cancellationToken).ConfigureAwait(false),
                "mail sender" => await MailAsync(context, (m, a) => m.SetSenderAsync(context.Option("tenant"), context.Option("from"), a, cancellationToken)).ConfigureAwait(false),
                "mail server" => await MailAsync(context, (m, a) => m.SetServerAsync(context.Option("tenant"), context.Option("host"),
                    Int(context.Option("port")), context.OptionalOption("security") ?? "starttls", context.OptionalOption("username"), context.Option("from"),
                    Env("ERP_MAIL_PASSWORD"), a, cancellationToken)).ConfigureAwait(false),
                "mail clear" => await MailAsync(context, async (m, a) =>
                {
                    if (!await m.ClearAsync(context.Option("tenant"), a, cancellationToken).ConfigureAwait(false))
                    {
                        await context.Output.WriteLineAsync("no mail connector was active").ConfigureAwait(false);
                    }
                }).ConfigureAwait(false),
                "mail show" => await MailShowAsync(context, cancellationToken).ConfigureAwait(false),
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
        SchemaMigrationResult? controlPlane = null;
        var admin = await c.Admin.BootstrapAsync(Context.Migrations, Required(Env("ERP_PLATFORM_ADMIN_EMAIL"), "ERP_PLATFORM_ADMIN_EMAIL"),
            Required(Env("ERP_PLATFORM_ADMIN_NAME"), "ERP_PLATFORM_ADMIN_NAME"), cancellationToken, r => controlPlane = r).ConfigureAwait(false);
        await TokenSigningKeys.EnsureAsync(c.Secrets, NewTokenKeyId(c.Clock), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"control plane: up to date, {controlPlane?.Applied} applied, {controlPlane?.AlreadyApplied} already applied (token signing key present)").ConfigureAwait(false);
        var actor = Env("ERP_PLATFORM_ACTOR") is { } email ? await c.Admin.ActorAsync(email, cancellationToken).ConfigureAwait(false) : admin;
        var outcomes = await new FleetMigrator(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations).MigrateAsync(actor, c.OptionalOption("tenant"), cancellationToken).ConfigureAwait(false);
        foreach (var o in outcomes)
        {
            await c.Output.WriteLineAsync(o.Succeeded
                ? $"{o.Code}: ok, {o.Applied} applied, schema {o.SchemaVersion}"
                : $"{o.Code}: FAILED, now in maintenance: {o.Error}").ConfigureAwait(false);
            foreach (var warning in o.Warnings)
            {
                await c.Output.WriteLineAsync($"{o.Code}: WARNING {warning}; a platform Super Admin can run `tenant admin-role-repair --code {o.Code} --reason ...`").ConfigureAwait(false);
            }
        }

        await c.Output.WriteLineAsync($"customers: {outcomes.Count(o => o.Succeeded)} migrated, {outcomes.Count(o => !o.Succeeded)} failed").ConfigureAwait(false);
        return outcomes.Any(o => !o.Succeeded) ? 3 : 0;
    }

    private static async Task<int> ClusterAddAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var credential = new DatabaseCredential(c.Option("admin-user"), Required(Env("ERP_CLUSTER_ADMIN_PASSWORD"), "ERP_CLUSTER_ADMIN_PASSWORD"));
        await c.Admin.AddClusterAsync(actor, c.Option("name"), c.Option("region"), c.Option("host"), Int(c.Option("port")), Int(c.Option("max-databases")), credential, cancellationToken,
            allowAddressChange: c.OptionalOption("change-address") == "yes").ConfigureAwait(false);
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

    private static async Task<int> EntitlementDraftAsync(Context c, CancellationToken cancellationToken)
    {
        var edition = c.Option("edition");
        if (edition is not ("starter" or "business" or "enterprise"))
        {
            throw new ArgumentException("--edition is starter, business or enterprise (the three templates, D-26).");
        }

        var folder = Env("ERP_TEMPLATES_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "EntitlementTemplates");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, edition + ".json"), cancellationToken).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidDataException("The template is not a JSON object.");
        var payload = await Applier(c).DraftAsync(c.Option("tenant"), template, Date(c.OptionalOption("from")), Date(c.OptionalOption("to")), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(c.Option("out"), payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"entitlement draft for {c.Option("tenant")} v{payload["version"]}: written to {c.Option("out")}; review every value (the template's are placeholders), then sign it").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> EntitlementApplyAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var result = await Applier(c).ApplyAsync(actor, c.Option("tenant"), await File.ReadAllTextAsync(c.Option("file"), cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var modules = result.ModulesInstalled.Count == 0 ? "no new modules" : "installed " + string.Join(", ", result.ModulesInstalled);
        await c.Output.WriteLineAsync(result.Status == "scheduled"
            ? $"{result.Tenant}: entitlement v{result.Version} scheduled from {result.EffectiveFrom:yyyy-MM-dd} ({modules})"
            : $"{result.Tenant}: entitlement v{result.Version} in force, state {result.Status}{(result.Superseded is { } old ? $", supersedes v{old}" : string.Empty)} ({modules}); API hosts apply it within their host cache time").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> EntitlementHistoryAsync(Context c, CancellationToken cancellationToken)
    {
        await c.Output.WriteLineAsync("version\tedition\tstatus\tfrom\tto\tstate_since\tapplied_at\tapplied_by\tmodules").ConfigureAwait(false);
        foreach (var r in await Applier(c).HistoryAsync(c.Option("tenant"), cancellationToken).ConfigureAwait(false))
        {
            await c.Output.WriteLineAsync(string.Join('\t', r.Version.ToString(CultureInfo.InvariantCulture), r.Edition, r.Status, r.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "perpetual", r.StateSince.ToString("u", CultureInfo.InvariantCulture),
                r.CreatedAt.ToString("u", CultureInfo.InvariantCulture), r.AppliedBy, string.Join(',', r.Modules))).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>One escalation run for every served customer, or one (slice S1.10b), as the worker's loop does it.</summary>
    private static async Task<int> WorkflowEscalateAsync(Context c, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton(c.Clock);
        services.AddSingleton(TimeProvider.System);
        services.AddWorkflowEscalation(Context.ControlPlane, c.Secrets, TimeSpan.Zero);
        await using var provider = services.BuildServiceProvider();
        var failed = false;
        foreach (var o in await provider.GetRequiredService<WorkflowEscalationRunner>().RunAsync(c.OptionalOption("tenant"), cancellationToken).ConfigureAwait(false))
        {
            failed |= o.Error is not null;
            await c.Output.WriteLineAsync(o switch
            {
                { Error: { } error } => $"{o.Code}: FAILED: {error}",
                { Skipped: { } mode } => $"{o.Code}: skipped (access mode {mode})",
                _ => $"{o.Code}: {o.Run!.Escalated} escalated, {o.Run.NotPossible} with nobody above (overdue), {o.Run.DueDatesSet} time limits set",
            }).ConfigureAwait(false);
        }

        return failed ? 3 : 0;
    }

    private static async Task<int> LicenceRefreshAsync(Context c, CancellationToken cancellationToken)
    {
        var outcomes = await new LicenceMaintenance(Context.ControlPlane, c.Secrets, c.Clock).RunAsync(c.OptionalOption("tenant"), cancellationToken).ConfigureAwait(false);
        foreach (var o in outcomes)
        {
            await c.Output.WriteLineAsync(o.Error is null
                ? $"{o.Code}: state {o.State}{(o.Changed ? $" (was {o.PreviousState})" : string.Empty)}{(o.Activated is { } v ? $", scheduled v{v} now in force" : string.Empty)}, {o.Readings} usage readings, {o.LimitEventsCopied} limit events copied"
                : $"{o.Code}: FAILED: {o.Error}").ConfigureAwait(false);
        }

        return outcomes.Any(o => o.Error is not null) ? 3 : 0;
    }

    private static async Task<int> UsageStatementAsync(Context c, CancellationToken cancellationToken)
    {
        await using var cp = new NpgsqlConnection(Context.ControlPlane.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        // The customer's time zone decides where its months end (S1.09b review finding 7).
        var tenant = await Erp.Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory.FindByCodeAsync(cp, c.Option("tenant"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {c.Option("tenant")} is not registered.");
        string timeZone;
        await using (var owner = await TenantProvisioner.OpenTenantAsync(c.Secrets, tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
        {
            timeZone = await Erp.Kernel.Infrastructure.Entitlements.CustomerLicence.TimeZoneAsync(owner, null, UtcToday(c.Clock), cancellationToken).ConfigureAwait(false);
        }

        var statement = await UsageMeters.StatementAsync(cp, c.Option("tenant"), c.Option("month"), timeZone, cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"usage statement {c.Option("tenant")} {statement.Period}").ConfigureAwait(false);
        await c.Output.WriteLineAsync("meter\tvalue\tlimit\tpercent").ConfigureAwait(false);
        foreach (var l in statement.Lines)
        {
            await c.Output.WriteLineAsync(string.Join('\t', l.Meter, l.Value.ToString(CultureInfo.InvariantCulture),
                l.Limit?.ToString(CultureInfo.InvariantCulture) ?? "none", l.Percent?.ToString(CultureInfo.InvariantCulture) ?? "-")).ConfigureAwait(false);
        }

        await c.Output.WriteLineAsync($"highest percent: {statement.HighestPercent?.ToString(CultureInfo.InvariantCulture) ?? "-"}").ConfigureAwait(false);
        return 0;
    }

    private static DateOnly UtcToday(IClock clock) => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    private static EntitlementApplier Applier(Context c) => new(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations);

    private static DateOnly? Date(string? value) =>
        value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

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
        if (!result.AlreadyProvisioned)
        {
            await PrintActivationLinkAsync(c, result.Code, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> ActivationLinkAsync(Context c, CancellationToken cancellationToken)
    {
        await c.ActorAsync(cancellationToken).ConfigureAwait(false); // only a registered platform operator may issue links
        return await PrintActivationLinkAsync(c, c.Option("code"), cancellationToken).ConfigureAwait(false) ? 0 : 4;
    }

    /// <summary>
    /// Platform Super Admin recovery and override of a customer's tenant administration (CR-024). The actor must be a
    /// platform user with role platform_admin; every run is recorded in cp.platform_audit and the customer's iam events.
    /// </summary>
    private static async Task<int> RecoveryAsync(Context c, Func<PlatformAdminRecovery, PlatformActor, Task<PlatformAdminRecovery.Outcome>> action)
    {
        var actor = await c.ActorAsync(CancellationToken.None).ConfigureAwait(false);
        var outcome = await action(new PlatformAdminRecovery(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations), actor).ConfigureAwait(false);
        foreach (var message in outcome.Messages)
        {
            await c.Output.WriteLineAsync(c.Option("code") + ": " + message).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Prints the first administrator's activation link (slice S1.05 ruling Q6). The link is a secret: hand it over privately.</summary>
    private static async Task<bool> PrintActivationLinkAsync(Context c, string code, CancellationToken cancellationToken)
    {
        var link = await new FirstAdminActivation(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations).IssueAsync(code, cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync(link is null
            ? $"{code}: no invited tenant administrator is waiting for activation"
            : $"{code}: activation link for the first administrator (valid until {link.ExpiresAt:yyyy-MM-dd HH:mm} UTC, works once):\n{link.Url}").ConfigureAwait(false);
        return link is not null;
    }

    private static async Task<int> RotateTokenKeyAsync(Context c, CancellationToken cancellationToken)
    {
        await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var id = NewTokenKeyId(c.Clock);
        await TokenSigningKeys.RotateAsync(c.Secrets, id, cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"token signing key {id}: current (restart the API hosts to use it)").ConfigureAwait(false);
        return 0;
    }

    private static string NewTokenKeyId(IClock clock) => "tk-" + clock.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>Slice S1.07 ruling P5: copy daily anchors to cp.audit_anchors; a changed anchor is a critical finding.</summary>
    private static async Task<int> AnchorsPublishAsync(Context c, CancellationToken cancellationToken)
    {
        var actor = await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var results = await new AnchorPublisher(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations).PublishAsync(actor, c.OptionalOption("tenant"), cancellationToken).ConfigureAwait(false);
        foreach (var r in results)
        {
            await c.Output.WriteLineAsync(r.Mismatches.Count == 0
                ? $"{r.TenantCode}: {r.Copied} anchor(s) copied, {r.AlreadyCopied} already copied and unchanged"
                : $"{r.TenantCode}: CRITICAL: anchor(s) changed since they were copied: {string.Join(", ", r.Mismatches)}").ConfigureAwait(false);
        }

        return results.Any(r => r.Mismatches.Count > 0) ? 3 : 0;
    }

    /// <summary>Slice S1.07 build item 2: the chain check as an operator command.</summary>
    private static async Task<int> AuditVerifyAsync(Context c, CancellationToken cancellationToken)
    {
        await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var code = c.Option("tenant");
        var from = DateOnly.ParseExact(c.Option("from"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateOnly.ParseExact(c.Option("to"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var host = await Erp.Kernel.Infrastructure.ControlPlane.Tenancy.TenantHosts.PrimaryAsync(Context.ControlPlane, code, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {code} is not registered.");
        var tenant = await new Erp.Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory(Context.ControlPlane).FindByHostAsync(host, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {code} has no database yet.");
        var copies = await AuditAnchorCopies.ReadAsync(Context.ControlPlane, code, from, to, cancellationToken).ConfigureAwait(false);
        await using var db = await new TenantProvisioner(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations).OpenTenantAsync(tenant, TenantLogins.ReadWrite, cancellationToken).ConfigureAwait(false);
        var result = await ChainVerifier.VerifyAsync(db, copies, from, to, markVerified: true, c.Clock, cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"{code}: {result.RowsChecked} row(s) on {result.DaysChecked} day(s) checked; control-plane copies compared: {(result.ControlPlaneCompared ? "yes" : "no")}").ConfigureAwait(false);
        await c.Output.WriteLineAsync(result.Intact ? $"{code}: intact" : $"{code}: BROKEN at row {result.BrokenRowId?.ToString() ?? "-"} ({result.BrokenAt:O}): {result.Problem}").ConfigureAwait(false);
        foreach (var finding in result.Findings)
        {
            await c.Output.WriteLineAsync("  " + finding).ConfigureAwait(false);
        }

        return result.Intact ? 0 : 3;
    }

    /// <summary>Slice S1.11b2 (ruling Q10, plan point P2): a customer's mail connector; only a registered platform operator may change it.</summary>
    private static async Task<int> MailAsync(Context c, Func<MailSetup, string, Task> action)
    {
        var actor = await c.ActorAsync(CancellationToken.None).ConfigureAwait(false);
        await action(new MailSetup(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations), actor.Email).ConfigureAwait(false);
        await c.Output.WriteLineAsync($"{c.Option("tenant")}: mail connector saved").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> MailShowAsync(Context c, CancellationToken cancellationToken)
    {
        await c.ActorAsync(cancellationToken).ConfigureAwait(false);
        var view = await new MailSetup(Context.ControlPlane, c.Secrets, c.Clock, Context.Migrations).ShowAsync(c.Option("tenant"), cancellationToken).ConfigureAwait(false);
        await c.Output.WriteLineAsync(view is null
            ? $"{c.Option("tenant")}: no mail connector (the operator relay and its sender address are used)"
            : $"{c.Option("tenant")}: {view.Provider} {view.Status} from {view.FromAddress ?? "-"}{(view.Host is null ? string.Empty : $" via {view.Host}:{view.Port} ({view.Security}, user {view.Username ?? "-"}, password {(view.HasPassword ? "stored" : "none")})")}").ConfigureAwait(false);
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

        public async ValueTask WaitingForOtherRunAsync(string code, CancellationToken cancellationToken) =>
            await output.WriteLineAsync($"  another provisioning run of {code} is in progress; waiting for it to end (Ctrl+C stops waiting)").ConfigureAwait(false);
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
