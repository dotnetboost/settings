using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetBoost.Settings.UnitTests;

public class SettingManagerTests
{
    [Fact]
    public async Task GetAsync_MapsRowsToModel()
    {
        var (store, cache, mgr) = Build();
        SetCacheMiss<MailSettings>(cache);
        store.Setup(x => x.GetGroupAsync("MailSettings", default))
             .ReturnsAsync(Rows("MailSettings",
                 ("Port",   "587",            "System.Int32"),
                 ("Host",   "smtp.gmail.com", "System.String"),
                 ("UseSsl", "true",           "System.Boolean")));

        var result = await mgr.For<MailSettings>().GetAsync();

        Assert.Equal(587, result.Port);
        Assert.Equal("smtp.gmail.com", result.Host);
        Assert.True(result.UseSsl);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefault_WhenStoreEmpty()
    {
        var (store, cache, mgr) = Build();
        SetCacheMiss<MailSettings>(cache);
        store.Setup(x => x.GetGroupAsync("MailSettings", default)).ReturnsAsync([]);

        var result = await mgr.For<MailSettings>().GetAsync();

        Assert.NotNull(result);
        Assert.Equal(0, result.Port);
    }

    [Fact]
    public async Task GetAsync_UsesCache_WhenHit()
    {
        var (store, cache, mgr) = Build();
        var cached = new MailSettings { Port = 9999 };
        cache.Setup(x => x.TryGetValue<MailSettings>("dnb:setting:MailSettings", out cached)).Returns(true);

        var result = await mgr.For<MailSettings>().GetAsync();

        Assert.Equal(9999, result.Port);
        store.Verify(x => x.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_BypassesCache_WhenRefreshCacheTrue()
    {
        var (store, cache, mgr) = Build();
        var cached = new MailSettings { Port = 9999 };
        cache.Setup(x => x.TryGetValue<MailSettings>("dnb:setting:MailSettings", out cached)).Returns(true);
        store.Setup(x => x.GetGroupAsync("MailSettings", default)).ReturnsAsync([]);

        await mgr.For<MailSettings>().GetAsync(refreshCache: true);

        store.Verify(x => x.GetGroupAsync("MailSettings", default), Times.Once);
    }

    [Fact]
    public async Task GetAsync_StoresResultInCache()
    {
        var (store, cache, mgr) = Build();
        SetCacheMiss<MailSettings>(cache);
        store.Setup(x => x.GetGroupAsync("MailSettings", default)).ReturnsAsync([]);

        await mgr.For<MailSettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:MailSettings", It.IsAny<MailSettings>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_SkipsUnparseableRow_Gracefully()
    {
        var (store, cache, mgr) = Build();
        SetCacheMiss<MailSettings>(cache);
        store.Setup(x => x.GetGroupAsync("MailSettings", default))
             .ReturnsAsync(Rows("MailSettings",
                 ("Port", "not-a-number", "System.Int32"),
                 ("Host", "smtp.test",    "System.String")));

        var result = await mgr.For<MailSettings>().GetAsync();

        Assert.Equal(0,          result.Port);
        Assert.Equal("smtp.test", result.Host);
    }

    [Fact]
    public async Task SetAsync_CallsUpsertMany()
    {
        var (store, _, mgr) = Build();
        store.Setup(x => x.GetGroupAsync("MailSettings", default)).ReturnsAsync([]);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Port = 587, Host = "smtp" });

        store.Verify(x => x.UpsertManyAsync(It.Is<IEnumerable<Setting>>(r => r.Any()), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAsync_InvalidatesCache()
    {
        var (store, cache, mgr) = Build();
        store.Setup(x => x.GetGroupAsync("MailSettings", default)).ReturnsAsync([]);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Port = 25 });

        cache.Verify(x => x.Remove("dnb:setting:MailSettings"), Times.Once);
    }

    [Fact]
    public async Task SetAsync_ThrowsSettingValidationException_WhenValidatorRejects()
    {
        var validator = new Mock<ISettingValidator>();
        validator.Setup(v => v.CanValidate(typeof(MailSettings))).Returns(true);
        validator.Setup(v => v.ValidateAsync(It.IsAny<object>()))
                 .ReturnsAsync((false, (IDictionary<string, string[]>)new Dictionary<string, string[]>
                 {
                     { "Port", ["Port must be > 0"] }
                 }));

        var sp    = new ServiceCollection().AddSingleton(validator.Object).BuildServiceProvider();
        var store = new Mock<ISettingStore>();
        var cache = new Mock<ISettingCache>();
        var mgr   = new SettingManager(store.Object, cache.Object, sp, NullLogger<SettingManager>.Instance);

        await Assert.ThrowsAsync<SettingValidationException>(
            () => mgr.For<MailSettings>().SetAsync(new MailSettings { Port = 0 }));

        store.Verify(x => x.UpsertManyAsync(It.IsAny<IEnumerable<Setting>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_FiresChangeHandler()
    {
        var handlerFired = false;
        var handler      = new TestChangeHandler(() => handlerFired = true);

        var sp    = new ServiceCollection()
            .AddSingleton<ISettingChangedHandler<MailSettings>>(handler)
            .BuildServiceProvider();
        var store = new Mock<ISettingStore>();
        store.Setup(x => x.GetGroupAsync("MailSettings", default)).ReturnsAsync([]);
        var cache = new Mock<ISettingCache>();
        var mgr   = new SettingManager(store.Object, cache.Object, sp, NullLogger<SettingManager>.Instance);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Port = 587 });

        Assert.True(handlerFired);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenCountZero()
    {
        var (store, _, mgr) = Build();
        store.Setup(x => x.CountAsync("MailSettings", default)).ReturnsAsync(0);
        Assert.False(await mgr.For<MailSettings>().ExistsAsync());
    }

    [Fact]
    public async Task ExistsAsync_AllProperties_ReturnsFalse_WhenPartial()
    {
        var (store, _, mgr) = Build();
        store.Setup(x => x.CountAsync("MailSettings", default)).ReturnsAsync(1);
        Assert.False(await mgr.For<MailSettings>().ExistsAsync(allProperties: true));
    }

    [Fact]
    public async Task ExistsAsync_AllProperties_ReturnsTrue_WhenFull()
    {
        var (store, _, mgr) = Build();
        store.Setup(x => x.CountAsync("MailSettings", default)).ReturnsAsync(3);
        Assert.True(await mgr.For<MailSettings>().ExistsAsync(allProperties: true));
    }

    [Fact]
    public async Task ClearAsync_DeletesGroup_AndEvictsCache()
    {
        var (store, cache, mgr) = Build();
        await mgr.For<MailSettings>().ClearAsync();
        store.Verify(x => x.DeleteGroupAsync("MailSettings", default), Times.Once);
        cache.Verify(x => x.Remove("dnb:setting:MailSettings"), Times.Once);
    }

    private static (Mock<ISettingStore>, Mock<ISettingCache>, ISettingManager) Build()
    {
        var store = new Mock<ISettingStore>();
        var cache = new Mock<ISettingCache>();
        SetCacheMiss<MailSettings>(cache);
        var mgr = new SettingManager(store.Object, cache.Object,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SettingManager>.Instance);
        return (store, cache, mgr);
    }

    private static void SetCacheMiss<T>(Mock<ISettingCache> cache)
    {
        T? nothing = default;
        cache.Setup(x => x.TryGetValue<T>(It.IsAny<string>(), out nothing)).Returns(false);
    }

    private static List<Setting> Rows(string group, params (string Key, string Value, string Type)[] data)
        => data.Select(d => new Setting { Group = group, Key = d.Key, Value = d.Value, Type = d.Type }).ToList();

    public class MailSettings { public int Port { get; set; } public string? Host { get; set; } public bool UseSsl { get; set; } }

    private sealed class TestChangeHandler(Action onChanged) : ISettingChangedHandler<MailSettings>
    {
        public Task OnChangedAsync(MailSettings previous, MailSettings current, CancellationToken cancellationToken = default)
        {
            onChanged();
            return Task.CompletedTask;
        }
    }
}
