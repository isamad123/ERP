using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>What became of a batch of device punches.</summary>
public sealed record IngestResult(int Stored, int AlreadyReceived, int Duplicates, int Unmapped, int Refused, IReadOnlyList<string> Errors);

/// <summary>
/// The one way device punches enter the system, for push and pull alike (slice S1.27b Build items 7 and 8; FR-HRM-ATT-03): each punch's
/// device user number is resolved to an employment through the mapping valid on the punch's local date (a mapping of this device wins over one
/// for all devices); a mapped punch is appended through <see cref="IAttendanceEventStore"/> with the key
/// <c>biometric:{deviceId}:{key}</c>, so a resend or a late batch causes no duplicate and no loss and keeps the original times; a punch whose number maps to
/// nobody is kept in <c>hrm.unmapped_punches</c> and brought in when the mapping is made. Nothing but user number, time and direction is handled:
/// no template, photo or name exists in these types (FR-HRM-ATT-20).
/// </summary>
internal sealed class DeviceIngestion(HrmDbContext db, IClock clock, ICurrentActor actor, IAttendanceEventStore store, EmploymentApplier applier)
{
    private const int Chunk = 200;

    public async Task<IngestResult> IngestAsync(AttendanceDevice device, IReadOnlyList<DeviceLogRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(records);
        var now = clock.UtcNow;
        var numbers = records.Select(r => r.DeviceUserNo).Distinct(StringComparer.Ordinal).ToList();
        var maps = await db.Set<DeviceUserMap>().AsNoTracking()
            .Where(m => m.CompanyId == device.CompanyId && (m.DeviceId == device.Id || m.DeviceId == null) && numbers.Contains(m.DeviceUserNo)).ToListAsync(ct).ConfigureAwait(false);
        var dates = new Dictionary<DateTimeOffset, DateOnly>();
        var mapped = new List<(AttendanceEventInput Input, DeviceLogRecord Record)>();
        var unmapped = new List<(DeviceLogRecord Record, string Key)>();
        foreach (var record in records)
        {
            var key = DeviceRules.PunchKey(device.Id, record);
            if (!dates.TryGetValue(record.At, out var date))
            {
                dates[record.At] = date = await applier.TodayAsync(device.CompanyId, record.At, ct).ConfigureAwait(false);
            }

            var map = maps.Where(m => m.DeviceUserNo == record.DeviceUserNo && m.ValidFrom <= date && (m.ValidTo == null || m.ValidTo > date))
                .OrderBy(m => m.DeviceId is null ? 1 : 0).FirstOrDefault();
            if (map is null)
            {
                unmapped.Add((record, key));
                continue;
            }

            mapped.Add((new AttendanceEventInput(map.EmploymentId, record.At, record.Direction, AttendanceSources.Biometric, key, device.Id), record));
        }

        int stored = 0, already = 0, duplicates = 0, refused = 0;
        var errors = new List<string>();
        for (var i = 0; i < mapped.Count; i += Chunk)
        {
            var slice = mapped.Skip(i).Take(Chunk).ToList();
            var outcomes = await store.AppendAsync([.. slice.Select(s => s.Input)], ct).ConfigureAwait(false);
            foreach (var o in outcomes)
            {
                switch (o.Status)
                {
                    case AttendanceEventOutcome.Stored:
                        stored++;
                        if (o.IsDuplicate)
                        {
                            duplicates++;
                        }

                        break;
                    case AttendanceEventOutcome.AlreadyReceived:
                        already++;
                        break;
                    default:
                        refused++;
                        if (o.Error is { } e && !errors.Contains(e))
                        {
                            errors.Add(e);
                        }

                        break;
                }
            }
        }

        foreach (var (record, key) in unmapped)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into hrm.unmapped_punches (id, company_id, device_id, device_user_no, punched_at, direction, idempotency_key, received_at, status, created_at, created_by, updated_at, updated_by, row_version)
                values ({Guid.CreateVersion7(now)}, {device.CompanyId}, {device.Id}, {record.DeviceUserNo}, {record.At}, {record.Direction}, {key}, {now}, 'open', {now}, {actor.UserId}, {now}, {actor.UserId}, 1)
                on conflict (company_id, idempotency_key) do nothing
                """, ct).ConfigureAwait(false);
        }

        return new IngestResult(stored, already, duplicates, unmapped.Count, refused, errors);
    }

    /// <summary>
    /// Brings the open unmapped punches that a new mapping now explains into the event store with their original times (FR-HRM-ATT-03), and marks
    /// them resolved. A punch before the mapping's first day, or on or after its end, stays open.
    /// </summary>
    public async Task<int> ReplayAsync(DeviceUserMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        var now = clock.UtcNow;
        var open = await db.Set<UnmappedPunch>().Where(p => p.CompanyId == map.CompanyId && p.Status == "open" && p.DeviceUserNo == map.DeviceUserNo && (map.DeviceId == null || p.DeviceId == map.DeviceId))
            .OrderBy(p => p.PunchedAt).ToListAsync(ct).ConfigureAwait(false);
        var replayed = 0;
        foreach (var punch in open)
        {
            var date = await applier.TodayAsync(map.CompanyId, punch.PunchedAt, ct).ConfigureAwait(false);
            if (date < map.ValidFrom || (map.ValidTo is { } end && date >= end))
            {
                continue;
            }

            var outcome = (await store.AppendAsync([new AttendanceEventInput(map.EmploymentId, punch.PunchedAt, punch.Direction, AttendanceSources.Biometric, punch.IdempotencyKey, punch.DeviceId)], ct).ConfigureAwait(false))[0];
            if (outcome.Status == AttendanceEventOutcome.Refused)
            {
                continue;
            }

            punch.Status = "resolved";
            punch.EmploymentId = map.EmploymentId;
            punch.ResolvedAt = now;
            punch.ResolvedBy = actor.UserId;
            punch.UpdatedAt = now;
            punch.UpdatedBy = actor.UserId;
            punch.RowVersion++;
            replayed++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return replayed;
    }

    /// <summary>Records that the device was heard from (and, after a pull, read): clears the last error and moves the cursor. Heartbeat updates are not versioned or audited.</summary>
    public async Task TouchAsync(Guid deviceId, bool synced, DateTimeOffset? newest, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            update hrm.attendance_devices set last_seen_at = {now}, last_error = null, status = 'active',
                last_sync_at = case when {synced} then {now} else last_sync_at end,
                sync_cursor_at = case when {newest}::timestamptz is not null and (sync_cursor_at is null or {newest}::timestamptz > sync_cursor_at) then {newest}::timestamptz else sync_cursor_at end,
                sync_requested_at = case when {synced} then null else sync_requested_at end
            where id = {deviceId} and status <> 'retired'
            """, ct).ConfigureAwait(false);
    }

    /// <summary>Records a failed contact: the device's last error is a localization key plus a short reason, never a secret.</summary>
    public async Task FailedAsync(Guid deviceId, string error, string? detail, bool offline, CancellationToken ct)
    {
        var text = detail is null ? error : error + ": " + detail;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            update hrm.attendance_devices set last_error = {text}, status = case when {offline} then 'offline' else status end, sync_requested_at = null
            where id = {deviceId} and status <> 'retired'
            """, ct).ConfigureAwait(false);
    }
}
