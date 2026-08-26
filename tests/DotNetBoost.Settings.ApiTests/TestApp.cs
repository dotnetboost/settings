using DotNetBoost.Settings.Core;
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

    public HttpClient Client { get; }

    private TestApp(WebApplication app)
    {
        _app    = app;
        Client  = app.GetTestClient();
    }

    public static async Task<TestApp> StartAsync(Action<IServiceCollection>? configure = null, bool requireIfMatch = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<ISettingStore, StubStore>();
        builder.Services.AddSettings();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapSettingsEndpoints(requireIfMatch);
        await app.StartAsync();

        return new TestApp(app);
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
        public Task<bool> GroupExistsAsync(string g, CancellationToken ct = default) => Task.FromResult(_data.Count > 0);
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

/// <summary>Records what it is asked for so tests can assert the group key used.</summary>
internal sealed class RecordingAuditStore : ISettingAuditStore
{
    public List<(string Group, string? Key)> Queries { get; } = [];

    public Task RecordAsync(SettingAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<SettingAuditEntry>> GetHistoryAsync(
        string group, string? key = null, CancellationToken ct = default)
    {
        Queries.Add((group, key));
        return Task.FromResult<IReadOnlyList<SettingAuditEntry>>(
        [
            new SettingAuditEntry
            {
                Group = group, Key = key ?? "Host",
                OldValue = "before", NewValue = "after", ChangedBy = "tester"
            }
        ]);
    }
}
