using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness only. Module endpoints arrive in later slices; tenancy and permissions arrive in S1.03 onwards.
app.MapHealthChecks("/health");

await app.RunAsync().ConfigureAwait(false);
