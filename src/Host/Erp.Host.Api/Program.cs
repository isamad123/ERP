using Erp.Host.Api;

var app = ApiHost.Build(WebApplication.CreateBuilder(args));
await app.RunAsync().ConfigureAwait(false);
