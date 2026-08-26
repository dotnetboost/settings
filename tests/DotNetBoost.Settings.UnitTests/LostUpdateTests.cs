using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// <c>SetAsync</c> now writes only the properties whose values differ from what is stored,
/// which is the prerequisite for per-property concurrency control.
/// <para>
/// Note what is <em>not</em> covered here: a caller holding a stale copy of the group still
/// overwrites another writer's change, because a plain POCO carries no record of the revision
/// it was read at. Closing that needs the concurrency token to travel out to the caller and
/// back — see the "known gap" note in the README.
/// </para>
/// </summary>
public class LostUpdateTests
{
    public class MailSettings
    {
        public string? Host { get; set; }
        public int     Port { get; set; }
    }

    [Fact]
    public async Task WritingAnUnchangedModel_TouchesNothing()
    {
        var store = new VersionedStore();
        var mgr = NewManager(store);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        var writesAfterFirstSave = store.Writes;

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });

        Assert.Equal(writesAfterFirstSave, store.Writes);
    }

    [Fact]
    public async Task ChangingOneProperty_WritesOnlyThatProperty()
    {
        var store = new VersionedStore();
        var mgr = NewManager(store);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        store.WrittenKeys.Clear();

        var model = await mgr.For<MailSettings>().GetAsync(refreshCache: true);
        model.Port = 587;
        await mgr.For<MailSettings>().SetAsync(model);

        Assert.Equal([nameof(MailSettings.Port)], store.WrittenKeys);
    }

    private static SettingManager NewManager(ISettingStore store)
        => new(store, new NoCache(), new ServiceCollection().BuildServiceProvider(),
               NullLogger<SettingManager>.Instance);

    /// <summary>An in-memory store that honours Setting.RowVersion the way a real one must.</summary>
    private sealed class VersionedStore : ISettingStore
    {
        private readonly Dictionary<string, Setting> _data = new(StringComparer.Ordinal);
        private static string K(string g, string k) => g + "|" + k;

        public int Writes { get; private set; }
        public List<string> WrittenKeys { get; } = [];

        public Task<IReadOnlyList<Setting>> GetGroupAsync(string g, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Setting>>(_data.Values.Where(x => x.Group == g).Select(Copy).ToList());

        public Task<Setting?> GetAsync(string g, string k, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(K(g, k), out var s) ? Copy(s) : null);

        public Task UpsertAsync(Setting setting, CancellationToken ct = default)
            => UpsertManyAsync([setting], ct);

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

                Writes++;
                WrittenKeys.Add(s.Key);
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

    private sealed class NoCache : ISettingCache
    {
        public bool TryGetValue<T>(string key, out T? value) { value = default; return false; }
        public void Set<T>(string key, T value, TimeSpan d) { }
        public void Remove(string key) { }
    }
}
