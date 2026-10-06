using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Settings.ApiTests;

/// <summary>
/// Spins up a real ASP.NET Core pipeline over an in-memory transport. The endpoints under
/// test are built by reflection at startup, so nothing short of actually starting the host
/// exercises the code path where they are constructed.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CapturingLoggerProvider _log;

    public HttpClient Client { get; }

    /// <summary>Warning-and-above messages logged by the application under test.</summary>
    public IReadOnlyList<string> Warnings => _log.Messages;

    /// <summary>The running host's root provider, for tests that need to resolve a service.</summary>
    public IServiceProvider Services => _app.Services;

    private TestApp(WebApplication app, CapturingLoggerProvider log)
    {
        _app   = app;
        _log   = log;
        Client = app.GetTestClient();
    }

    public static async Task<TestApp> StartAsync(
        Action<IServiceCollection>? configure = null,
        bool requireIfMatch = false,
        bool includeDiscovery = true,
        Action<SettingBuilder>? configureSettings = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<ISettingStore, StubStore>();

        // Warnings are captured so a test can assert that something was *not* logged.
        var log = new CapturingLoggerProvider();
        builder.Logging.AddProvider(log);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // configureSettings runs against the real SettingBuilder, so builder methods are
        // exercised through the chain an application actually writes. AddSettings is called
        // into a local first: inside a ?.Invoke argument it would not run at all when no
        // configureSettings was passed.
        var settings = builder.Services.AddSettings();
        configureSettings?.Invoke(settings);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapSettingsEndpoints(requireIfMatch, includeDiscovery);
        await app.StartAsync();

        return new TestApp(app, log);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// In-memory store that behaves like a real one: writes are visible to later reads and
    /// concurrency tokens are honoured. Seeded so a GET has something to return.
    /// </summary>
    internal sealed class StubStore : ISettingStore
    {
        private readonly Dictionary<string, Setting> _data = new(StringComparer.Ordinal);
        private static string K(string g, string k) => g + "|" + k;

        public StubStore()
        {
            var seed = new Setting
            {
                Group = "api-test-group", Key = "Host",
                Value = "stored.example.com", Type = "System.String",
                RowVersion = Setting.NewRowVersion()
            };
            _data[K(seed.Group, seed.Key)] = seed;
        }

        public Task<IReadOnlyList<Setting>> GetGroupAsync(string g, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Setting>>(_data.Values.Where(x => x.Group == g).Select(Copy).ToList());

        public Task<Setting?> GetAsync(string g, string k, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(K(g, k), out var s) ? Copy(s) : null);

        public Task UpsertAsync(Setting s, CancellationToken ct = default) => UpsertManyAsync([s], ct);

        public Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
        {
            foreach (var s in settings)
            {
                if (_data.TryGetValue(K(s.Group, s.Key), out var existing) &&
                    s.RowVersion is not null &&
                    !existing.RowVersion.AsSpan().SequenceEqual(s.RowVersion))
                {
                    throw new SettingConcurrencyException(s.Group, s.Key);
                }

                var stored = Copy(s);
                stored.RowVersion = Setting.NewRowVersion();
                _data[K(s.Group, s.Key)] = stored;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string g, string k, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteGroupAsync(string g, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountAsync(string g, CancellationToken ct = default)
            => Task.FromResult(_data.Values.Count(x => x.Group == g));

        private static Setting Copy(Setting s) => new()
        {
            Id = s.Id, Group = s.Group, Key = s.Key, Value = s.Value, Type = s.Type,
            IsEncrypted = s.IsEncrypted, UpdatedAt = s.UpdatedAt, UpdatedBy = s.UpdatedBy,
            RowVersion = s.RowVersion
        };
    }
}

/// <summary>Collects log messages so a test can assert on what was, or was not, logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) return [.. _messages]; }
    }

    public ILogger CreateLogger(string categoryName) => new Capturing(this);

    public void Dispose() { }

    private void Add(string message)
    {
        lock (_messages) _messages.Add(message);
    }

    private sealed class Capturing(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
            => owner.Add(formatter(state, exception));
    }
}

/// <summary>
/// The group the endpoint tests exercise. Its Name differs from the class name on purpose,
/// so anything keyed on the persistence key cannot pass by using the class name instead.
/// <see cref="TestApp.StubStore"/> seeds it.
/// </summary>
[SettingGroup("api-test", Name = "api-test-group")]
public class ApiTestSettings
{
    public string Host { get; set; } = string.Empty;
}
