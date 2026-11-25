using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>
/// The job <c>hrm.payroll_calculate</c> (slice S1.35 Build item 9, ruling Q6; FR-HRM-PAY-34): one lane of a run's calculation, acting as the payroll officer
/// who asked (runs as the requester, needs <c>hrm.pay.run</c>). The run service queues one job run per lane (setting <c>hrm.pay.max_parallel_chunks</c>);
/// each lane takes the run's eligible employees of its lane (a fixed function of the employment id) in id order, in chunks of <c>hrm.pay.chunk_size</c>.
/// Each chunk is one transaction with the job's checkpoint (the last employment passed and the counts), so a lane stopped by a crash or a lost lease
/// continues after the last chunk it saved, and an employee is never calculated twice in a request (an employee with a result is skipped). Cancel
/// stops the lane at its next chunk and is recorded once in the run's stage log. When its employees are done the lane asks the run to finish: the last
/// lane to finish makes the run calculated (or records it incomplete), and calculates any employee no lane took.
/// </summary>
internal sealed class PayrollCalculateJob : IJobHandler
{
    public const string JobCode = "hrm.payroll_calculate";
    public const string ParametersInvalid = "errors.hrm.pay.job.parameters";
    public const string RunMissing = "errors.hrm.pay.job.run_missing";

    public string Code => JobCode;

    /// <summary>The checkpoint of a lane: the last employment passed, and the counts of this lane in this request.</summary>
    public sealed class LaneCheckpoint
    {
        public string? After { get; set; }

        public int Calculated { get; set; }

        public int Incomplete { get; set; }

        public int Total { get; set; }
    }

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Parse(job.Parameters) is not { } request)
        {
            // Ids, flags and the rounding mode only: never an amount.
            await job.LogAsync("error", "parameters not understood: " + job.Parameters.GetRawText(), cancellationToken).ConfigureAwait(false);
            throw new JobFailedException(ParametersInvalid);
        }

        var services = job.Services;
        var db = services.GetRequiredService<HrmDbContext>();
        var calculator = services.GetRequiredService<PayCalculator>();
        var user = services.GetRequiredService<ICurrentActor>().UserId;
        var state = Read(job.Checkpoint);
        var chunkSize = 200;
        try
        {
            // The start: the run's stage log records it once per request (who and when), with the snapshot time of the run.
            await job.CommitChunkAsync(async (connection, tx, ct) =>
            {
                await OnAsync(db, connection, tx, async () =>
                {
                    var ctx = await calculator.ContextAsync(request, ct).ConfigureAwait(false) ?? throw new JobFailedException(RunMissing);
                    chunkSize = await ChunkSizeAsync(services, ctx.Run.CompanyId, ct).ConfigureAwait(false);
                    if (state.Total == 0)
                    {
                        state.Total = await calculator.LaneSizeAsync(ctx, request, ct).ConfigureAwait(false);
                    }

                    if (PayCalculator.Calculable.Contains(ctx.Run.Status))
                    {
                        await calculator.StageOnceAsync(ctx.Run.Id, "calculation_started", request.RequestId, user,
                            new Dictionary<string, object?> { ["lanes"] = request.Lanes, ["engineVersion"] = PayEngine.Version }, snapshotTaken: true, ct).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
            }, state, Progress(state), cancellationToken).ConfigureAwait(false);
            await job.LogAsync("info", Invariant($"lane {request.Lane + 1} of {request.Lanes}: {state.Total} employees, chunks of {chunkSize}{(state.After is null ? string.Empty : ", continuing after " + state.After)}"), cancellationToken).ConfigureAwait(false);

            if (!await WalkAsync(job, db, calculator, request, state, chunkSize, user, anyLane: false, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // The lane is done: ask the run to finish (the last lane makes it calculated, and first calculates any employee no lane took).
            while (true)
            {
                var finish = PayFinish.Pending;
                await job.CommitChunkAsync(async (connection, tx, ct) =>
                {
                    await OnAsync(db, connection, tx, async () =>
                    {
                        var ctx = await calculator.ContextAsync(request, ct).ConfigureAwait(false) ?? throw new JobFailedException(RunMissing);
                        finish = await calculator.FinishAsync(ctx, request, user, ct).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                }, state, 100m, cancellationToken).ConfigureAwait(false);
                if (finish != PayFinish.Sweep)
                {
                    await job.LogAsync(finish == PayFinish.Incomplete ? "warning" : "info", Invariant($"lane {request.Lane + 1} done: {state.Calculated} calculated, {state.Incomplete} incomplete; run {finish.ToString().ToLowerInvariant()}"), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var sweep = new LaneCheckpoint { Total = state.Total, Calculated = state.Calculated, Incomplete = state.Incomplete };
                if (!await WalkAsync(job, db, calculator, request, sweep, chunkSize, user, anyLane: true, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                (state.Calculated, state.Incomplete) = (sweep.Calculated, sweep.Incomplete);
            }
        }
        catch (JobStoppedException e) when (e.Message == JobStoppedException.Cancelled)
        {
            // Recorded once per request on a connection of its own (the chunk that saw the cancel saved nothing); the results saved so far stay.
            await using var own = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
            await using var tx = await own.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            var recorder = ActivatorUtilities.CreateInstance<PayCalculator>(services, own);
            await recorder.StageOnceAsync(request.PayRunId, "calculation_cancelled", request.RequestId, user,
                new Dictionary<string, object?> { ["lane"] = request.Lane, ["calculated"] = state.Calculated }, snapshotTaken: false, CancellationToken.None).ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Chunks of the lane (or of any lane, for the sweep) until none is left; false when the run stopped being calculable.</summary>
    private static async Task<bool> WalkAsync(IJobExecution job, HrmDbContext db, PayCalculator calculator, PayCalculationRequest request, LaneCheckpoint state, int chunkSize, Guid user, bool anyLane, CancellationToken cancellationToken)
    {
        while (true)
        {
            var more = false;
            try
            {
                await job.CommitChunkAsync(async (connection, tx, ct) =>
                {
                    await OnAsync(db, connection, tx, async () =>
                    {
                        var ctx = await calculator.ContextAsync(request, ct).ConfigureAwait(false) ?? throw new JobFailedException(RunMissing);
                        Guid? after = state.After is { } a ? Guid.Parse(a) : null;
                        var (ids, cursor) = await calculator.NextAsync(ctx, request, after, chunkSize, anyLane, ct).ConfigureAwait(false);
                        if (ids.Count == 0)
                        {
                            return;
                        }

                        var outcome = await calculator.ChunkAsync(ctx, request, ids, user, ct).ConfigureAwait(false);
                        state.After = cursor?.ToString("D", CultureInfo.InvariantCulture);
                        state.Calculated += outcome.Calculated;
                        state.Incomplete += outcome.Incomplete.Count;
                        more = true;
                    }, ct).ConfigureAwait(false);
                }, state, Progress(state), cancellationToken).ConfigureAwait(false);
            }
            catch (PayRunNotCalculableException)
            {
                await job.LogAsync("warning", "the run is no longer calculable; the lane stops", cancellationToken).ConfigureAwait(false);
                return false;
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: "ex_pay_run_employees_employment_id_period" })
            {
                throw new JobFailedException(PayRunService.EmployeeConflict);
            }

            if (!more)
            {
                return true;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> with the scope's HR context on the chunk's connection and transaction, so the input providers and the statutory calculators read in it.</summary>
    private static async Task OnAsync(HrmDbContext db, DbConnection connection, DbTransaction transaction, Func<Task> work, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            await db.Database.UseTransactionAsync(null, ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    private static decimal? Progress(LaneCheckpoint s) =>
        s.Total == 0 ? null : Math.Min(99m, Math.Round((s.Calculated + s.Incomplete) * 100m / s.Total, 1));

    private static LaneCheckpoint Read(JsonElement? checkpoint)
    {
        if (checkpoint is not { ValueKind: JsonValueKind.Object } c)
        {
            return new LaneCheckpoint();
        }

        return c.Deserialize<LaneCheckpoint>() ?? new LaneCheckpoint();
    }

    private static PayCalculationRequest? Parse(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("pay_run_id", out var run) || !Guid.TryParse(run.GetString(), out var runId)
            || !p.TryGetProperty("request_id", out var req) || !Guid.TryParse(req.GetString(), out var requestId)
            || !p.TryGetProperty("lane", out var lane) || !lane.TryGetInt32(out var laneNo)
            || !p.TryGetProperty("lanes", out var lanes) || !lanes.TryGetInt32(out var laneCount) || laneCount < 1 || laneNo < 0 || laneNo >= laneCount
            || !p.TryGetProperty("fresh", out var fresh) || fresh.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !p.TryGetProperty("rounding_mode", out var mode) || mode.GetString() is not (PayRounding.HalfAwayFromZero or PayRounding.HalfEven)
            || !p.TryGetProperty("currency_decimals", out var decimals) || !decimals.TryGetInt32(out var places) || places is < 0 or > 4)
        {
            return null;
        }

        return new PayCalculationRequest(runId, requestId, laneNo, laneCount, fresh.GetBoolean(), mode.GetString()!, places);
    }

    private static async Task<int> ChunkSizeAsync(IServiceProvider services, Guid companyId, CancellationToken ct)
    {
        var value = (await services.GetRequiredService<ISettings>().GetAsync(HrmSettingKeys.HrmPayChunkSize, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0 ? n : 200;
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
