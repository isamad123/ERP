using System.Security.Cryptography;
using System.Text;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// Recomputes the hash chain of <c>aud.change_log</c> for a range of days (FR-KRN-AUD-03; rulings Q2, Q3, P2, P3) and names
/// the first broken row. The verifier rebuilds each row's canonical text here, in the application, from the stored
/// columns (the same layout as <c>aud.change_canonical</c>), so a changed database function cannot hide a changed row.
/// Checks: every row's hash, every row's link to the row before, each day's anchor against its last row, and each
/// finished day's anchor against the copy kept in the control plane (CR-027).
/// </summary>
public static class ChainVerifier
{
    public const string RowChanged = "row_changed";
    public const string LinkBroken = "link_broken";
    public const string AnchorMismatch = "anchor_mismatch";
    public const string ControlPlaneMismatch = "control_plane_mismatch";
    public const string RowsMissing = "rows_missing";
    public const string BrokenEvent = "aud.chain_broken";

    private static readonly byte[] Genesis = new byte[32];

    private const string Utc = "'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'";

    private static readonly string RowsSql = $"""
        select c.id, c.occurred_at, c.prev_hash, c.row_hash,
               c.id::text, to_char(c.occurred_at at time zone 'UTC', {Utc}), c.actor_user_id::text, c.actor_kind, c.entity_type, c.entity_id::text,
               c.company_id::text, c.action, c.changes::text, c.subject_person_id::text, c.correlation_id::text, c.ip_address::text, c.source,
               to_char(c.created_at at time zone 'UTC', {Utc}), c.created_by::text
        from aud.change_log c where c.occurred_at >= $1 and c.occurred_at < $2 order by c.occurred_at, c.id
        """;

    /// <summary>Checks the days <paramref name="firstDay"/> to <paramref name="lastDay"/> (UTC, inclusive).</summary>
    /// <remarks>
    /// <paramref name="copies"/> are the control-plane copies of the range's anchors, or null when they cannot be read here.
    /// With <paramref name="markVerified"/>, an intact range stamps <c>verified_at</c> on its finished days (before
    /// today on <paramref name="clock"/>). <paramref name="connection"/> is the customer database;
    /// <paramref name="cancellationToken"/> cancels the check.
    /// </remarks>
    public static async Task<ChainVerification> VerifyAsync(NpgsqlConnection connection, IReadOnlyDictionary<DateOnly, byte[]>? copies, DateOnly firstDay, DateOnly lastDay, bool markVerified, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var start = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var findings = new List<string>();
        (Guid Id, DateTimeOffset At, string Problem)? first = null;
        void Broken(Guid id, DateTimeOffset at, string problem, string text)
        {
            first ??= (id, at, problem);
            findings.Add(text);
        }

        byte[] expected;
        await using (var head = new NpgsqlCommand("select row_hash from aud.change_log where occurred_at < $1 order by occurred_at desc limit 1", connection))
        {
            head.Parameters.Add(new NpgsqlParameter { Value = start });
            expected = await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[] ?? Genesis;
        }

        var lastOfDay = new SortedDictionary<DateOnly, (Guid Id, DateTimeOffset At, byte[] Hash)>();
        long rows = 0;
        await using (var command = new NpgsqlCommand(RowsSql, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = start });
            command.Parameters.Add(new NpgsqlParameter { Value = end });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows++;
                var id = reader.GetGuid(0);
                var at = reader.GetFieldValue<DateTimeOffset>(1);
                var prev = (byte[])reader.GetValue(2);
                var stored = (byte[])reader.GetValue(3);
                var canonical = new StringBuilder();
                for (var i = 4; i <= 18; i++)
                {
                    Append(canonical, reader.IsDBNull(i) ? null : reader.GetString(i));
                }

                var computed = SHA256.HashData([.. prev, .. Encoding.UTF8.GetBytes(canonical.ToString())]);
                if (!computed.AsSpan().SequenceEqual(stored))
                {
                    Broken(id, at, RowChanged, $"{RowChanged}: row {id} at {at:O} does not match its hash");
                }
                else if (!prev.AsSpan().SequenceEqual(expected))
                {
                    Broken(id, at, LinkBroken, $"{LinkBroken}: row {id} at {at:O} does not follow the row before it (a row was removed, inserted or changed)");
                }

                expected = stored;
                lastOfDay[DateOnly.FromDateTime(at.UtcDateTime)] = (id, at, stored);
            }
        }

        var anchors = new Dictionary<DateOnly, byte[]>();
        await using (var command = new NpgsqlCommand("select day, last_hash from aud.chain_anchors where day between $1 and $2", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = firstDay });
            command.Parameters.Add(new NpgsqlParameter { Value = lastDay });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                anchors[reader.GetFieldValue<DateOnly>(0)] = (byte[])reader.GetValue(1);
            }
        }

        foreach (var (day, last) in lastOfDay)
        {
            if (!anchors.TryGetValue(day, out var anchor) || !anchor.AsSpan().SequenceEqual(last.Hash))
            {
                Broken(last.Id, last.At, AnchorMismatch, $"{AnchorMismatch}: the anchor of {day:yyyy-MM-dd} does not match its last row {last.Id}");
            }
        }

        foreach (var day in anchors.Keys.Where(d => !lastOfDay.ContainsKey(d)).Order())
        {
            findings.Add($"{RowsMissing}: {day:yyyy-MM-dd} has an anchor but no rows");
            first ??= (Guid.Empty, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), RowsMissing);
        }

        if (copies is not null)
        {
            foreach (var (day, copy) in copies.OrderBy(c => c.Key))
            {
                if (!anchors.TryGetValue(day, out var anchor) || !anchor.AsSpan().SequenceEqual(copy))
                {
                    findings.Add($"{ControlPlaneMismatch}: the anchor of {day:yyyy-MM-dd} differs from the control-plane copy");
                    var (rowId, rowAt, _) = lastOfDay.TryGetValue(day, out var l) ? l : (Guid.Empty, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Genesis);
                    first ??= (rowId, rowAt, ControlPlaneMismatch);
                }
            }
        }

        var intact = findings.Count == 0;
        if (intact && markVerified)
        {
            foreach (var (day, anchor) in anchors.Where(a => a.Key < today))
            {
                await using var mark = new NpgsqlCommand("select aud.mark_anchor_verified($1, $2, $3)", connection);
                mark.Parameters.Add(new NpgsqlParameter { Value = day });
                mark.Parameters.Add(new NpgsqlParameter { Value = anchor });
                mark.Parameters.Add(new NpgsqlParameter { Value = now });
                await mark.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new ChainVerification(firstDay, lastDay, intact, rows, lastOfDay.Count, first?.Id is { } bid && bid != Guid.Empty ? bid : null, first?.At, first?.Problem, copies is not null, findings);
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("~;");
            return;
        }

        canonical.Append(Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}

/// <summary>
/// The verify endpoint's service: checks the bound customer's chain against its control-plane copies, marks verified
/// days, and records a critical security event when the chain is broken.
/// </summary>
public sealed class AuditVerifier(ITenantConnectionProvider tenant, ITenantContext context, ControlPlaneOptions controlPlane, IClock clock, IAuditContext audit) : IAuditVerifier
{
    public async Task<ChainVerification> VerifyAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        if (lastDay < firstDay || lastDay.DayNumber - firstDay.DayNumber >= AuditLimits.MaxVerifyDays)
        {
            throw new ArgumentException("errors.audit.range_invalid");
        }

        var now = clock.UtcNow;
        var copies = await AuditAnchorCopies.ReadAsync(controlPlane, context.TenantCode, firstDay, lastDay, cancellationToken).ConfigureAwait(false);
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = await ChainVerifier.VerifyAsync(connection, copies, firstDay, lastDay, markVerified: true, clock, cancellationToken).ConfigureAwait(false);
        if (!result.Intact)
        {
            var details = new System.Text.Json.Nodes.JsonObject
            {
                ["from"] = firstDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["to"] = lastDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["first_broken_row"] = result.BrokenRowId,
                ["problem"] = result.Problem,
                ["findings"] = new System.Text.Json.Nodes.JsonArray([.. result.Findings.Take(20).Select(f => (System.Text.Json.Nodes.JsonNode)f)]),
                ["correlation_id"] = audit.CorrelationId,
            };
            await AuditDb.AppendSecurityEventAsync(connection, null, new SecurityEventRecord(now, ChainVerifier.BrokenEvent, audit.ActorUserId?.ToString(), "aud.change_log", Severities.Critical, details, audit.ActorUserId ?? BaselineSeed.SystemUserId), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
