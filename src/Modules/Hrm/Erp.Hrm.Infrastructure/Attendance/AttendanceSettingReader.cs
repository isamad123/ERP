using System.Text.Json;
using Erp.Hrm.Contracts.Generated;
using Erp.Kernel.Contracts.Settings;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>Reads the attendance settings of a company (<c>hrm.att.*</c>, slice S1.27a); a value that is not what its type says falls back to the default.</summary>
internal sealed class AttendanceSettingReader(ISettings settings)
{
    public async Task<int> IntAsync(string key, Guid companyId, int fallback, CancellationToken ct)
    {
        var value = (await settings.GetAsync(key, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : fallback;
    }

    public async Task<IReadOnlyList<string>> NetworksAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(HrmSettingKeys.HrmAttAllowedNetworks, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
    }

    public Task<int> DuplicateWindowAsync(Guid companyId, CancellationToken ct) => IntAsync(HrmSettingKeys.HrmAttDuplicateWindowSeconds, companyId, 60, ct);

    public Task<int> FutureToleranceAsync(Guid companyId, CancellationToken ct) => IntAsync(HrmSettingKeys.HrmAttFutureToleranceMinutes, companyId, 5, ct);

    public Task<int> QrWindowAsync(Guid companyId, CancellationToken ct) => IntAsync(HrmSettingKeys.HrmAttQrWindowSeconds, companyId, 60, ct);
}
