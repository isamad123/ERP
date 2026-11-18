using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Jobs;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Jobs;

/// <summary>
/// The job centre API (FR-KRN-JOB-03, FR-ADM-OPS-01; slice S1.11 Build item 7; screen in S1.16): jobs with their schedule
/// and next run, schedule change or switch-off (editable jobs only), run now, the run list with filters, a run with its
/// log, rerun, cancel, and the health numbers (queue depth, event backlog, worker activity). Error titles are localization
/// keys (<c>Jobs/Localization/jobs.*.json</c>). Paths are under <c>/api/jobs</c>; the generated job endpoints under <c>/api/job/keys (<c>Jobs/Localization/jobs.*.json</c>). The generated job endpoints only readlt;tablekeys (<c>Jobs/Localization/jobs.*.json</c>). The generated job endpoints only readgt;</c> only read (CR-046 item 2). Downloading a run's
/// result file comes with the file store (S1.11c).
/// </summary>
public static class JobEndpoints
{
    private const string Definitions = "job.definitions";
    private const string Runs = "job.runs";

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/jobs").WithTags("job.jobs");
        Tag(g.MapGet("/definitions", (IJobCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.DefinitionsAsync(ct).ConfigureAwait(false)))), "catalog", Definitions, JobPermissions.DefinitionsRead);
        Tag(g.MapPut("/definitions/{id:guid}/schedule", (Guid id, JobScheduleRequest body, IJobCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ChangeScheduleAsync(id, body, ct).ConfigureAwait(false)))), "record_action", Definitions, JobPermissions.DefinitionsUpdate);
        Tag(g.MapPost("/definitions/{id:guid}/run", (Guid id, RunJobRequest body, IJobCentre s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var run = await s.RunNowAsync(id, body, ct).ConfigureAwait(false);
                return Results.Created("/api/jobs/runs/" + run.Id, run);
            })), "record_action", Runs, JobPermissions.RunsCreate);
        Tag(g.MapGet("/runs", ([AsParameters] RunParameters q, IJobCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.RunsAsync(new JobRunQuery(q.Status, q.Code, q.CompanyId, q.From, q.To, q.Limit, q.Offset), ct).ConfigureAwait(false)))),
            "catalog", Runs, JobPermissions.RunsRead);
        Tag(g.MapGet("/runs/{id:guid}", (Guid id, IJobCentre s, CancellationToken ct) =>
            RunAsync(async () => await s.RunAsync(id, ct).ConfigureAwait(false) is { } run ? Results.Ok(run) : NotFound("errors.job.run_not_found"))),
            "record_action", Runs, JobPermissions.RunsRead);
        Tag(g.MapPost("/runs/{id:guid}/rerun", (Guid id, IJobCentre s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var run = await s.RerunAsync(id, ct).ConfigureAwait(false);
                return Results.Created("/api/jobs/runs/" + run.Id, run);
            })), "record_action", Runs, JobPermissions.RunsCreate);
        Tag(g.MapPost("/runs/{id:guid}/cancel", (Guid id, IJobCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.CancelAsync(id, ct).ConfigureAwait(false)))), "record_action", Runs, JobPermissions.RunsUpdate);
        Tag(g.MapGet("/health", (IJobCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.HealthAsync(ct).ConfigureAwait(false)))), "catalog", Runs, JobPermissions.RunsRead);
        return app;
    }

    /// <summary>Job-centre refusals become problem answers with their status (400, 403, 404, 409).</summary>
    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (JobCentreException ex)
        {
            return Results.Problem(statusCode: ex.Status, title: ex.Message, extensions: ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
    }

    private static IResult NotFound(string key) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: key);

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new JobEndpointMetadata(kind, resource, permission));

    /// <summary>Query string of <c>GET /api/jobs/runs</c>.</summary>
    public sealed class RunParameters
    {
        public string? Status { get; init; }

        public string? Code { get; init; }

        public Guid? CompanyId { get; init; }

        public DateTimeOffset? From { get; init; }

        public DateTimeOffset? To { get; init; }

        public int? Limit { get; init; }

        public int? Offset { get; init; }
    }
}
