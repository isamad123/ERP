using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Host.Worker;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// Licence loop (S1.09b, ruling Q6): settings from environment variables, as the migrator's (docs/runbooks/tenant-operations.md):
// ERP_CONTROL_PLANE, ERP_SECRETS_FILE, ERP_SECRETS_KEY; Licence__IntervalMinutes (default 60). S1.11 moves it into the jobs framework.
var controlPlane = config["ERP_CONTROL_PLANE"] ?? throw new InvalidOperationException("ERP_CONTROL_PLANE is not set (docs/runbooks/tenant-operations.md).");
var controlPlaneOptions = new ControlPlaneOptions { ConnectionString = controlPlane };
var secrets = EncryptedFileSecretStore.FromSettings(config["ERP_SECRETS_FILE"], config["ERP_SECRETS_KEY"]);
builder.Services.AddSingleton(secrets); // disposed by the host
builder.Services.AddSingleton(sp => new LicenceMaintenance(controlPlaneOptions, secrets, sp.GetRequiredService<IClock>()));
builder.Services.AddSingleton(new LicenceLoopOptions(TimeSpan.FromMinutes(config.GetValue("Licence:IntervalMinutes", 60))));
builder.Services.AddHostedService<LicenceLoop>();

// Workflow escalation (S1.10b, ruling Q9): every Workflow:EscalationIntervalSeconds (default 60). Modules register their
// workflow document types and, for HR, the approver directory here as in the API host. S1.11 moves it into the jobs framework.
builder.Services.AddWorkflowEscalation(controlPlaneOptions, secrets, TimeSpan.FromMilliseconds(config.GetValue("Settings:RefreshMilliseconds", 1000)));
builder.Services.AddSingleton(new WorkflowEscalationLoopOptions(TimeSpan.FromSeconds(config.GetValue("Workflow:EscalationIntervalSeconds", 60))));
builder.Services.AddHostedService<WorkflowEscalationLoop>();

// Background jobs and the outbox dispatcher are registered here in later slices.
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
