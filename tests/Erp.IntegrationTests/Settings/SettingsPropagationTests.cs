using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Settings;

/// <summary>
/// FR-KRN-SET-06 (slice S1.09 ruling Q13, option A): a settings or flag change made through one API host is in force on a
/// second API host, a separate operating-system process with its own memory and cache on the same customer database,
/// within about one second (its production refresh interval, <c>Settings:RefreshMilliseconds</c> = 1000), with no restart.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class SettingsPropagationTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task FR_KRN_SET_06_a_change_on_one_host_is_seen_by_a_second_process_within_about_one_second()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = "test.s109_prop_" + Guid.NewGuid().ToString("N")[..8];
        var flag = key + "_flag";
        await _h.ExecAsync("""
            insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n, is_visible, area)
            values ($1, 'test', 'int', '10', '{}', '{tenant}', false, '{"en":"Propagation","ur":"پھیلاؤ"}', true, 'test')
            """, Ct, key);
        await _h.ExecAsync("""
            insert into cfg.feature_flags (key, default_enabled, description_i18n, module_id) values ($1, false, '{"en":"Propagation","ur":"پھیلاؤ"}', 'test')
            """, Ct, flag);

        await using var second = await ApiProcess.StartAsync(fx, Ct);
        var token = _h.Token(admin);
        var host = TenancyFixture.HostOf(_h.Code);
        async Task<int> SettingOnSecondAsync() =>
            JsonDocument.Parse(await second.GetAsync(host, token, $"/api/cfg/settings/{key}", Ct)).RootElement.GetProperty("setting").GetProperty("value").GetInt32();
        async Task<bool> FlagOnSecondAsync() =>
            JsonDocument.Parse(await second.GetAsync(host, token, "/api/cfg/feature-flags", Ct)).RootElement.EnumerateArray()
                .Single(f => f.GetProperty("key").GetString() == flag).GetProperty("enabled").GetBoolean();

        // The second process has read (and cached) the old values.
        Assert.Equal(10, await SettingOnSecondAsync());
        Assert.False(await FlagOnSecondAsync());

        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/cfg/settings/{key}", new { layer = "tenant", value = 77, reason = "propagation" }, Ct)).Status);
        Assert.Equal(77, (await _h.GetAsync(admin, $"/api/cfg/settings/{key}", Ct)).Json.GetProperty("setting").GetProperty("value").GetInt32());
        var setting = await WaitAsync(async () => await SettingOnSecondAsync() == 77);

        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/cfg/feature-flags/{flag}", new { layer = "tenant", enabled = true, reason = "propagation" }, Ct)).Status);
        var flagTime = await WaitAsync(FlagOnSecondAsync);

        TestContext.Current.TestOutputHelper?.WriteLine($"FR-KRN-SET-06: setting seen by the second process after {setting.TotalMilliseconds:0} ms, flag after {flagTime.TotalMilliseconds:0} ms (refresh interval 1000 ms).");
        Assert.True(setting < TimeSpan.FromSeconds(2), $"setting took {setting.TotalMilliseconds:0} ms");
        Assert.True(flagTime < TimeSpan.FromSeconds(2), $"flag took {flagTime.TotalMilliseconds:0} ms");
    }

    private static async Task<TimeSpan> WaitAsync(Func<Task<bool>> done)
    {
        var watch = Stopwatch.StartNew();
        while (!await done())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the second process never saw the change");
            await Task.Delay(50, Ct);
        }

        return watch.Elapsed;
    }

    /// <summary>The real API host (<c>Erp.Host.Api.dll</c>) started as its own process, with the fixture's settings.</summary>
    private sealed class ApiProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _client;
        private readonly StringBuilder _log = new();

        private ApiProcess(Process process, int port)
        {
            _process = process;
            _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        }

        public static async Task<ApiProcess> StartAsync(TenancyFixture fx, CancellationToken ct)
        {
            var dll = HostDll();
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }

            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(dll)!,
            };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add("--urls");
            start.ArgumentList.Add($"http://127.0.0.1:{port}");
            foreach (var (name, value) in fx.ApiProcessEnvironment())
            {
                start.Environment[name] = value;
            }

            start.Environment["Logging__LogLevel__Default"] = "Warning";
            var process = Process.Start(start) ?? throw new InvalidOperationException("the API host did not start");
            var api = new ApiProcess(process, port);
            process.OutputDataReceived += (_, e) => api.Append(e.Data);
            process.ErrorDataReceived += (_, e) => api.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var watch = Stopwatch.StartNew();
            while (true)
            {
                Assert.False(process.HasExited, "the API host process stopped: " + api.Log);
                try
                {
                    using var health = await api._client.GetAsync(new Uri("/health", UriKind.Relative), ct);
                    if (health.IsSuccessStatusCode)
                    {
                        return api;
                    }
                }
                catch (HttpRequestException)
                {
                    // not listening yet
                }

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "the API host process did not answer /health: " + api.Log);
                await Task.Delay(200, ct);
            }
        }

        public string Log
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        public async Task<string> GetAsync(string host, string token, string path, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Host = host;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"second host: {(int)response.StatusCode} {body} {Log}");
            return body;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            _process.Dispose();
        }

        private void Append(string? line)
        {
            if (line is not null)
            {
                lock (_log)
                {
                    _log.AppendLine(line);
                }
            }
        }

        /// <summary>The API host built with this test run: src/Host/Erp.Host.Api/bin/{configuration}/{framework}/Erp.Host.Api.dll.</summary>
        private static string HostDll()
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var framework = output.Name;
            var configuration = output.Parent!.Name;
            var root = output;
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Erp.sln")))
            {
                root = root.Parent;
            }

            var dll = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Erp.sln not found"), "src", "Host", "Erp.Host.Api", "bin", configuration, framework, "Erp.Host.Api.dll");
            return File.Exists(dll) ? dll : throw new InvalidOperationException($"{dll} is missing; build the solution first.");
        }
    }
}
