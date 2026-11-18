using Erp.Host.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Worker host (slice S1.11a): the job scheduler and runner and the event dispatcher. The licence refresh and the approval
// escalation that ran as loops here (S1.09b, S1.10b) are jobs now (kernel.licence_refresh, kernel.workflow_escalate).
// Modules register their job manifests, handlers, event consumers, workflow document types and, for HR, the approver
// directory in WorkerHost.Configure.
var builder = Host.CreateApplicationBuilder(args);
WorkerHost.Configure(builder.Services, builder.Configuration);
builder.Services.AddHostedService<JobWorkerService>();
builder.Services.AddHostedService<EventDispatcherService>();
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
