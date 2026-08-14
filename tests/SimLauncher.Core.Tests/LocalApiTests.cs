using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using SimLauncher.Core.Engine;
using Xunit;

namespace SimLauncher.Core.Tests;

/// <summary>The loopback control endpoint the Stream Deck plugin drives.</summary>
public sealed class LocalApiTests : IDisposable
{
    private const int Port = 18731;

    private readonly FakeSimStateSource _source = new();
    private readonly FakeProcessManager _procs = new();
    private readonly SessionStateMachine _sm;
    private readonly CheckpointEngine _engine;
    private readonly ConfigStore _config;
    private readonly SessionCoordinator _coordinator;
    private readonly LocalApi _api;
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };
    private readonly string _configPath;

    public LocalApiTests()
    {
        var time = new FakeTimeProvider();
        _configPath = Path.Combine(Path.GetTempPath(), $"simlauncher-api-{Guid.NewGuid():N}.json");
        _sm = new SessionStateMachine(NullLogger<SessionStateMachine>.Instance, time);
        _engine = new CheckpointEngine(_procs, NullLogger<CheckpointEngine>.Instance, time);
        _config = new ConfigStore(NullLogger<ConfigStore>.Instance, _configPath);
        _config.Load();
        _config.Update(c =>
        {
            c.LocalApiPort = Port;
            c.ActiveProfile = "api";
            c.Msfs = new MsfsConfig { Path = @"C:\sim\msfs.exe", ShellExecute = false, ProcessNames = { "msfs" } };
            c.Profiles.Clear();
            c.Profiles.Add(new ProfileConfig
            {
                Name = "api",
                Apps = { new AppConfig { Name = "SPAD", Path = @"C:\apps\spad.exe", Checkpoint = Checkpoint.LauncherStart } },
            });
        });
        _coordinator = new SessionCoordinator(_source, _sm, _engine, _config, _procs,
            NullLogger<SessionCoordinator>.Instance);
        _coordinator.Initialize();
        _api = new LocalApi(_coordinator, _procs, NullLogger<LocalApi>.Instance);
        _api.Start();
    }

    public void Dispose()
    {
        _http.Dispose();
        _api.Dispose();
        _coordinator.Dispose();
        _engine.Dispose();
        _sm.Dispose();
        _config.Dispose();
        File.Delete(_configPath);
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        var response = await _http.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path} -> {(int)response.StatusCode}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task StatusReportsState_AndToggleStartsThenStopsTheStack()
    {
        var idle = await GetAsync("/status");
        Assert.False(idle.GetProperty("sessionActive").GetBoolean());
        Assert.Equal("Idle", idle.GetProperty("phase").GetString());
        Assert.Equal(1, idle.GetProperty("appsTotal").GetInt32());

        var started = await GetAsync("/toggle");
        Assert.True(started.GetProperty("sessionActive").GetBoolean());
        Assert.Contains(_procs.Starts, s => s.Spec.Path.Contains("spad", StringComparison.OrdinalIgnoreCase));

        var stopped = await GetAsync("/toggle");
        Assert.False(stopped.GetProperty("sessionActive").GetBoolean());

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _http.GetAsync("/nope")).StatusCode);
    }
}
