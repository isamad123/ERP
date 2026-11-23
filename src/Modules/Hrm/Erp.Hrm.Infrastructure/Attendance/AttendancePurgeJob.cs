using System.Data.Common;
using System.Globalization;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The daily job <c>hrm.attendance_purge</c> (slice S1.27a Build item 5; FR-HRM-DAT-04): removes whole monthly partitions of
/// <c>hrm.attendance_events</c> that ended more than <c>hrm.att.event_retention_years</c> ago, through <c>hrm.drop_expired_attendance_partitions</c>
/// (the application login cannot drop tables). A month that is only partly older than the retention is kept; single punches are never deleted.
/// The partitions are created ahead by the kernel job <c>kernel.partitions_ensure</c> (this month and the next three).
/// </summary>
internal sealed class AttendancePurgeJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.attendance_purge";

    public string Code => JobCode;

    /// <summary>The first day of the month the retention reaches back to: months that ended on or before it are removed.</summary>
    public static DateOnly Cutoff(DateOnly today, int retentionYears)
    {
        var back = today.AddYears(-retentionYears);
        return new DateOnly(back.Year, back.Month, 1);
    }

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.Services.GetRequiredService<ISettings>();
        var value = (await settings.GetAsync(HrmSettingKeys.HrmAttEventRetentionYears, new SettingContext(null, null), cancellationToken).ConfigureAwait(false)).Value;
        var years = value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 1 ? n : 7;
        var cutoff = Cutoff(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), years);
        var dropped = new List<(string Name, long Rows)>();
        await job.CommitChunkAsync(async (connection, transaction, ct) =>
        {
            dropped.Clear();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "select partition_name, row_count from hrm.drop_expired_attendance_partitions(@before)";
            AddParameter(command, "before", cutoff);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                dropped.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }, null, 100m, cancellationToken).ConfigureAwait(false);
        foreach (var (name, rows) in dropped)
        {
            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{name}: dropped with {rows} punch(es), older than {years} year(s)"), cancellationToken).ConfigureAwait(false);
        }

        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{dropped.Count} month(s) removed; months ending on or before {cutoff:yyyy-MM-dd} qualify"), cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
