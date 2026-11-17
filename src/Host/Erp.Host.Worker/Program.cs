using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Host.Worker;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
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
builder.Services.AddSingleton(new ControlPlaneOptions { ConnectionString = controlPlane });
builder.Services.AddSingleton(_ => EncryptedFileSecretStore.FromSettings(config["ERP_SECRETS_FILE"], config["ERP_SECRETS_KEY"])); // disposed by the host
builder.Services.AddSingleton(sp => new LicenceMaintenance(sp.GetRequiredService<ControlPlaneOptions>(), sp.GetRequiredService<EncryptedFileSecretStore>(), sp.GetRequiredService<IClock>()));
builder.Services.AddSingleton(new LicenceLoopOptions(TimeSpan.FromMinutes(config.GetValue("Licence:IntervalMinutes", 60))));
builder.Services.AddHostedService<LicenceLoop>();

// Background jobs and the outbox dispatcher are registered here in later slices.
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
