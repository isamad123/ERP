using System.Globalization;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Attendance;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// The job <c>hrm.device_sync</c> (slice S1.27b Build items 6 and 7; FR-HRM-ATT-02, ATT-03), every minute: reads each active device whose driver pulls, when
/// a person asked (<c>sync_requested_at</c>) or it was last read more than <c>hrm.att.device_pull_minutes</c> ago; the punches go through
/// <see cref="DeviceIngestion"/> (keys remove repeats, original times are kept). A device that cannot be reached records the failure on its row (a key and a short
/// reason, never the secret) and is not read again until its turn. Every non-retired device silent for longer than <c>hrm.att.device_offline_minutes</c> is marked
/// offline. A company that has not switched <c>hrm.att.biometric_enabled</c> on is skipped. The change log names the job actor.
/// </summary>
internal sealed class DeviceSyncJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.device_sync";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var db = job.Services.GetRequiredService<HrmDbContext>();
        var runtime = job.Services.GetRequiredService<DeviceRuntime>();
        var ingestion = job.Services.GetRequiredService<DeviceIngestion>();
        var settings = job.Services.GetRequiredService<AttendanceSettingReader>();
        var now = clock.UtcNow;
        var devices = await db.Set<AttendanceDevice>().AsNoTracking().Where(d => !d.IsDeleted && d.Status != "retired").OrderBy(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var providers = await runtime.ProvidersAsync(devices.Select(d => d.ConnectorId), cancellationToken).ConfigureAwait(false);
        int pulled = 0, failed = 0, skipped = 0, stored = 0, unmapped = 0;
        foreach (var device in devices)
        {
            var offlineAfter = await settings.IntAsync(HrmSettingKeys.HrmAttDeviceOfflineMinutes, device.CompanyId, 30, cancellationToken).ConfigureAwait(false);
            if (runtime.Driver(providers.GetValueOrDefault(device.ConnectorId)) is { CanPull: true } driver && await runtime.BiometricEnabledAsync(device.CompanyId, cancellationToken).ConfigureAwait(false))
            {
                var every = await settings.IntAsync(HrmSettingKeys.HrmAttDevicePullMinutes, device.CompanyId, 5, cancellationToken).ConfigureAwait(false);
                var due = device.SyncRequestedAt is not null || device.LastSyncAt is null || now - device.LastSyncAt >= TimeSpan.FromMinutes(every);
                if (due)
                {
                    var connection = await runtime.ConnectionAsync(device, true, cancellationToken).ConfigureAwait(false);
                    var since = device.SyncCursorAt?.AddDays(-1);
                    var result = await driver.PullAsync(connection, since, cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        var ingested = await ingestion.IngestAsync(device, result.Records, cancellationToken).ConfigureAwait(false);
                        await ingestion.TouchAsync(device.Id, true, result.Records.Count == 0 ? null : result.Records.Max(r => r.At), cancellationToken).ConfigureAwait(false);
                        pulled++;
                        stored += ingested.Stored;
                        unmapped += ingested.Unmapped;
                        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"device {device.SerialNo}: {result.Records.Count} punch(es) read, {ingested.Stored} new, {ingested.AlreadyReceived} already held, {ingested.Unmapped} unmapped, {ingested.Refused} refused"), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var silent = device.LastSeenAt is null || now - device.LastSeenAt > TimeSpan.FromMinutes(offlineAfter);
                        await ingestion.FailedAsync(device.Id, result.ErrorKey ?? "errors.hrm.device.unreachable", result.Detail, silent, cancellationToken).ConfigureAwait(false);
                        failed++;
                        await job.LogAsync("warning", $"device {device.SerialNo}: {result.ErrorKey}", cancellationToken).ConfigureAwait(false);
                    }

                    await job.CommitChunkAsync(null, null, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            skipped++;
        }

        // Devices silent for too long show offline (push devices have no other way to be noticed).
        var marked = 0;
        var current = await db.Set<AttendanceDevice>().AsNoTracking().Where(d => !d.IsDeleted && d.Status == "active").ToListAsync(cancellationToken).ConfigureAwait(false); // as the reads above left them
        foreach (var device in current)
        {
            var minutes = await settings.IntAsync(HrmSettingKeys.HrmAttDeviceOfflineMinutes, device.CompanyId, 30, cancellationToken).ConfigureAwait(false);
            if (!DeviceRules.IsOnline(device.LastSeenAt ?? device.CreatedAt, now, minutes))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"update hrm.attendance_devices set status = 'offline' where id = {device.Id} and status = 'active'", cancellationToken).ConfigureAwait(false);
                marked++;
            }
        }

        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{pulled} device(s) read ({stored} new punches, {unmapped} unmapped), {failed} failed, {skipped} not due, {marked} marked offline."), cancellationToken).ConfigureAwait(false);
    }
}
