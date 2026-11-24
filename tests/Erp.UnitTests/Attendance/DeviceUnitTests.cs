using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Infrastructure.Devices;

namespace Erp.UnitTests.Attendance;

/// <summary>Pure rules and drivers of the attendance devices (slice S1.27b): signatures, keys, directions, the ZKTeco record format and the push body.</summary>
public sealed class DeviceUnitTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private static readonly DeviceConnection Device = new(Guid.Parse("11111111-1111-7111-8111-111111111111"), "SN1", string.Empty, 0, "push", "secret", Karachi);

    [Fact]
    public void FR_HRM_ATT_03_a_signature_is_valid_only_for_its_secret_body_and_a_recent_time()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var header = DeviceRules.SignatureHeader("k", now.ToUnixTimeSeconds(), "{\"records\":[]}");
        Assert.True(DeviceRules.VerifySignature("k", header, "{\"records\":[]}", now));
        Assert.True(DeviceRules.VerifySignature("k", header, "{\"records\":[]}", now.AddSeconds(DeviceRules.SignatureToleranceSeconds)));
        Assert.False(DeviceRules.VerifySignature("k", header, "{\"records\":[]}", now.AddSeconds(DeviceRules.SignatureToleranceSeconds + 1)));
        Assert.False(DeviceRules.VerifySignature("k2", header, "{\"records\":[]}", now));
        Assert.False(DeviceRules.VerifySignature("k", header, "{\"records\":[1]}", now));
        Assert.False(DeviceRules.VerifySignature("k", null, "{}", now));
        Assert.False(DeviceRules.VerifySignature("k", "t=abc,v1=00", "{}", now));
        Assert.False(DeviceRules.VerifySignature(string.Empty, header, "{\"records\":[]}", now));
        Assert.False(DeviceRules.VerifySignature("k", header.Replace("t=", "t=-", StringComparison.Ordinal), "{\"records\":[]}", now));
    }

    [Theory]
    [InlineData(0, "in")]
    [InlineData(1, "out")]
    [InlineData(2, "break_out")]
    [InlineData(3, "break_in")]
    [InlineData(4, "in")]
    [InlineData(5, "out")]
    [InlineData(9, "unknown")]
    public void FR_HRM_ATT_03_a_device_status_code_becomes_a_direction(int status, string expected) => Assert.Equal(expected, DeviceRules.DirectionOf(status));

    [Fact]
    public void FR_HRM_DAT_02_a_punch_key_names_the_device_and_is_the_same_for_the_same_punch()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var a = DeviceRules.PunchKey(Device.DeviceId, new DeviceLogRecord("17", at, "in"));
        Assert.Equal(a, DeviceRules.PunchKey(Device.DeviceId, new DeviceLogRecord("17", at, "in")));
        Assert.StartsWith("biometric:11111111111171118111111111111111:", a, StringComparison.Ordinal);
        Assert.NotEqual(a, DeviceRules.PunchKey(Device.DeviceId, new DeviceLogRecord("17", at.AddSeconds(1), "in")));
        Assert.EndsWith(":rec-9", DeviceRules.PunchKey(Device.DeviceId, new DeviceLogRecord("17", at, "in", " rec-9 ")), StringComparison.Ordinal);
    }

    [Fact]
    public void FR_HRM_ATT_02_a_device_is_online_while_it_was_heard_from_within_the_window()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.True(DeviceRules.IsOnline(now.AddMinutes(-30), now, 30));
        Assert.False(DeviceRules.IsOnline(now.AddMinutes(-31), now, 30));
        Assert.False(DeviceRules.IsOnline(null, now, 30));
        Assert.True(DeviceRules.IsHost("192.168.1.201"));
        Assert.True(DeviceRules.IsHost("clock-1.office.example"));
        Assert.False(DeviceRules.IsHost("http://clock"));
        Assert.False(DeviceRules.IsHost("a b"));
        Assert.False(DeviceRules.IsHost(null));
    }

    [Fact]
    public void FR_HRM_ATT_03_the_push_driver_reads_user_time_and_direction_and_takes_a_time_without_offset_as_the_company_clock()
    {
        var driver = new GenericPushDriver();
        var records = driver.ParsePush("""{"records":[{"userNo":"5","at":"2026-11-24T08:00:00+05:00","direction":"in","key":"r-1"},{"userNo":"6","at":"2026-11-24T08:00:00","direction":"nonsense"}]}""", Device)!;
        Assert.Equal(2, records.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture), records[0].At);
        Assert.Equal("r-1", records[0].SourceKey);
        Assert.Equal(records[0].At, records[1].At); // Karachi is UTC+5
        Assert.Equal("unknown", records[1].Direction);
        Assert.False(driver.CanPull);
    }

    [Theory]
    [InlineData("""{"records":[{"userNo":"5","at":"2026-11-24T08:00:00Z","direction":"in","template":"AAAA"}]}""")]
    [InlineData("""{"records":[{"userNo":"5","at":"2026-11-24T08:00:00Z","photo":"AAAA"}]}""")]
    [InlineData("""{"records":[{"userNo":"5"}]}""")]
    [InlineData("""{"records":[{"userNo":"","at":"2026-11-24T08:00:00Z"}]}""")]
    [InlineData("""{"records":[{"userNo":"5","at":"yesterday"}]}""")]
    [InlineData("""{"records":"x"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void FR_HRM_ATT_20_the_push_driver_refuses_a_body_with_any_other_member_or_the_wrong_shape(string body) => Assert.Null(new GenericPushDriver().ParsePush(body, Device));

    [Fact]
    public void FR_HRM_ATT_03_the_zkteco_checksum_and_packed_time_follow_the_protocol()
    {
        // The vendor's checksum of a connect packet (command 1000, session 0, reply 65534): the 16-bit words add to 66534, fold to 999, and the device's
        // complement of 999 is 64535 (the published reference implementation adds 65535, not 65536, after complementing).
        var packet = ZkProtocol.Encode(ZkProtocol.Connect, 0, 65534, []);
        Assert.Equal(new byte[] { 0x50, 0x50, 0x82, 0x7D, 8, 0, 0, 0 }, packet[..8]);
        Assert.Equal(64535, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10)));
        Assert.Equal((ushort)64535, ZkProtocol.Checksum(new byte[] { 0xE8, 0x03, 0, 0, 0, 0, 0xFE, 0xFF }));
        var time = new DateTime(2026, 11, 24, 8, 5, 9, DateTimeKind.Unspecified);
        Assert.Equal(time, ZkProtocol.DecodeTime(ZkProtocol.EncodeTime(time)));
        Assert.Equal(DateTime.MinValue, ZkProtocol.DecodeTime(uint.MaxValue)); // an impossible date is dropped, not guessed
        Assert.Equal(4, ZkProtocol.CommKey(0, 0).Length);
        Assert.NotEqual(ZkProtocol.CommKey(1, 7), ZkProtocol.CommKey(2, 7));
    }

    [Fact]
    public void FR_HRM_ATT_20_the_zkteco_driver_can_send_only_the_commands_that_read_attendance_records()
    {
        // The commands that list users (8) and read fingerprint or face templates (9, 88, 1503 family) are not in the list the session sends from.
        Assert.Equal(new ushort[] { 1000, 1001, 1102, 50, 13, 1502 }.Order(), ZkProtocol.Allowed.Order());
        Assert.DoesNotContain((ushort)8, ZkProtocol.Allowed);
        Assert.DoesNotContain((ushort)9, ZkProtocol.Allowed);
        Assert.DoesNotContain((ushort)88, ZkProtocol.Allowed);
    }

    [Fact]
    public void FR_HRM_ATT_03_the_zkteco_log_is_split_into_records_of_40_16_or_8_bytes_and_an_old_record_is_skipped()
    {
        var first = new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Unspecified);
        var second = first.AddHours(9);
        var data = new byte[4 + 80];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 80);
        void Put(int index, string user, DateTime time, byte punch)
        {
            var r = data.AsSpan(4 + (index * 40), 40);
            Encoding.ASCII.GetBytes(user).CopyTo(r[2..]);
            BinaryPrimitives.WriteUInt32LittleEndian(r[27..], ZkProtocol.EncodeTime(time));
            r[31] = punch;
        }

        Put(0, "17", first, 0);
        Put(1, "17", second, 1);
        var all = ZkTecoDriver.Parse(data, 2, Karachi, null)!;
        Assert.Equal(["17", "17"], all.Select(r => r.DeviceUserNo));
        Assert.Equal(["in", "out"], all.Select(r => r.Direction));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(first, Karachi), all[0].At.UtcDateTime);
        Assert.Single(ZkTecoDriver.Parse(data, 2, Karachi, all[1].At)!);
        Assert.Null(ZkTecoDriver.Parse(data, 3, Karachi, null)); // a count that does not divide the data is not guessed at
        Assert.Null(ZkTecoDriver.Parse([1, 2], 1, Karachi, null));
    }

    [Fact]
    public void FR_HRM_ATT_20_the_driver_types_have_no_place_for_a_template_a_photo_or_a_name()
    {
        // The only data that can leave a driver is these members; adding one would show up here.
        Assert.Equal(["At", "DeviceUserNo", "Direction", "SourceKey"], typeof(DeviceLogRecord).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Detail", "ErrorKey", "Records", "Succeeded"], typeof(DevicePullResult).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FR_HRM_ATT_02_device_texts_exist_in_english_and_urdu_for_every_key_the_services_use()
    {
        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => f.Contains("Devices", StringComparison.Ordinal) || f.Contains("DeviceEndpoints", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.device\\.[a-z_]+)\"").Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
        Assert.All(DevicePermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c) && ur.ContainsKey("permissions." + c), c));
    }

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }
}
