using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();

// Background jobs and the outbox dispatcher are registered here in later slices.
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
