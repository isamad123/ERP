using Erp.BuildingBlocks.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.IntegrationTests.Infrastructure;

/// <summary>
/// Allow-all access doubles: every permission granted, every row visible, every field editable. The tenant-isolation
/// suite (S1.04) keeps them so its calls reach the data; with the real engine every probe would be a trivial 403.
/// Tests of access control itself use the real engine (slice S1.06).
/// </summary>
public static class AllowAllAccess
{
    public static IServiceCollection UseAllowAllAccess(this IServiceCollection services)
    {
        services.RemoveAll<IPermissionChecker>();
        services.AddSingleton<IPermissionChecker, AllowAll>();
        services.RemoveAll<IDataScopeFilter>();
        services.AddSingleton<IDataScopeFilter, AllRows>();
        services.RemoveAll<IFieldAccess>();
        services.AddSingleton<IFieldAccess, AllFields>();
        services.RemoveAll<ICallerAccess>();
        services.AddSingleton<ICallerAccess, AllowAll>();
        return services;
    }

    public sealed class AllowAll : IPermissionChecker, ICallerAccess
    {
        public ValueTask<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool> HoldsAsync(string permission, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool> CoversCompanyAsync(string permission, Guid? companyId, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    public sealed class AllRows : IDataScopeFilter
    {
        public ValueTask<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query, ScopeTarget<T> target, string action, IScopeQuerySource? source, CancellationToken cancellationToken)
            where T : class => ValueTask.FromResult(query);

        public ValueTask<bool> AllowsAsync<T>(T row, ScopeTarget<T> target, string action, IScopeQuerySource? source, CancellationToken cancellationToken)
            where T : class => ValueTask.FromResult(true);
    }

    public sealed class AllFields : IFieldAccess
    {
        public ValueTask<FieldAccessMap> ForAsync(FieldCatalog catalog, string action, Guid? companyId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FieldAccessMap.Unrestricted(catalog));

        public ValueTask<FieldAccessPlan<T>> PlanAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, string action, IScopeQuerySource? source, CancellationToken cancellationToken)
            where T : class => ValueTask.FromResult(FieldAccessPlans.Same<T>(FieldAccessMap.Unrestricted(catalog)));
    }
}

/// <summary>Test double of the access log (slice S1.07) for hosts without a customer database: keeps the calls in memory.</summary>
public sealed class RecordingAccessLog : ISensitiveAccessLog
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Resource, Guid? EntityId, IReadOnlyList<string> Fields, string Action, int Rows)> Calls { get; } = new();

    public Task RecordAsync(string resource, Guid? entityId, IReadOnlyList<string> fields, string action, int rowCount, CancellationToken cancellationToken)
    {
        Calls.Enqueue((resource, entityId, fields, action, rowCount));
        return Task.CompletedTask;
    }
}
