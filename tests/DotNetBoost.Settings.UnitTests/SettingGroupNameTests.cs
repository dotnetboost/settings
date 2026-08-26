using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// A settings group's persistence key must be decoupled from its CLR type name, so a class
/// can be renamed or moved without orphaning its rows. The default has to stay the class
/// name — anything else would silently strand data already in production stores.
/// </summary>
public class SettingGroupNameTests
{
    [Fact]
    public void ResolveName_DefaultsToClassName_WhenNameUnset()
    {
        Assert.Equal(nameof(RouteOnlySettings), SettingGroupAttribute.ResolveName(typeof(RouteOnlySettings)));
        Assert.Equal(nameof(NoAttributeSettings), SettingGroupAttribute.ResolveName(typeof(NoAttributeSettings)));
    }

    [Fact]
    public void ResolveName_UsesExplicitName_WhenSet()
        => Assert.Equal("billing-v1", SettingGroupAttribute.ResolveName(typeof(RenamedSettings)));

    [Fact]
    public void ResolveName_IsIndependentOfRoute()
    {
        // The route is a URL concern and must stay changeable without touching stored data.
        Assert.Equal("billing-v1", SettingGroupAttribute.ResolveName(typeof(RenamedSettings)));
        Assert.NotEqual(typeof(RenamedSettings).GetCustomAttributes(typeof(SettingGroupAttribute), false)
            .Cast<SettingGroupAttribute>().Single().Route, SettingGroupAttribute.ResolveName(typeof(RenamedSettings)));
    }

    [Fact]
    public void ResolveName_NullType_Throws()
        => Assert.Throws<ArgumentNullException>(() => SettingGroupAttribute.ResolveName(null!));

    [Fact]
    public async Task Reads_UseTheClassName_WhenNoExplicitNameIsSet()
    {
        var (store, mgr) = Build<RouteOnlySettings>();

        await mgr.For<RouteOnlySettings>().GetAsync();

        store.Verify(x => x.GetGroupAsync(nameof(RouteOnlySettings), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reads_UseTheExplicitName_WhenSet()
    {
        var (store, mgr) = Build<RenamedSettings>();

        await mgr.For<RenamedSettings>().GetAsync();

        store.Verify(x => x.GetGroupAsync("billing-v1", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(x => x.GetGroupAsync(nameof(RenamedSettings), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Writes_UseTheExplicitName()
    {
        var (store, mgr) = Build<RenamedSettings>();
        List<Setting>? written = null;
        store.Setup(x => x.UpsertManyAsync(It.IsAny<IEnumerable<Setting>>(), It.IsAny<CancellationToken>()))
             .Callback<IEnumerable<Setting>, CancellationToken>((rows, _) => written = rows.ToList())
             .Returns(Task.CompletedTask);

        await mgr.For<RenamedSettings>().SetAsync(new RenamedSettings { Port = 1 });

        Assert.All(written!, r => Assert.Equal("billing-v1", r.Group));
    }

    [Fact]
    public async Task Clear_UsesTheExplicitName()
    {
        var (store, mgr) = Build<RenamedSettings>();

        await mgr.For<RenamedSettings>().ClearAsync();

        store.Verify(x => x.DeleteGroupAsync("billing-v1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exists_UsesTheExplicitName()
    {
        var (store, mgr) = Build<RenamedSettings>();
        store.Setup(x => x.CountAsync("billing-v1", It.IsAny<CancellationToken>())).ReturnsAsync(1);

        Assert.True(await mgr.For<RenamedSettings>().ExistsAsync());
    }

    [Fact]
    public async Task CacheKey_FollowsTheGroupName_NotTheClassName()
    {
        var (_, mgr, cache) = BuildWithCache<RenamedSettings>();

        await mgr.For<RenamedSettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:billing-v1", It.IsAny<RenamedSettings>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task CacheKey_IsUnchanged_ForGroupsWithoutAnExplicitName()
    {
        // Guards the upgrade path: existing deployments must keep hitting the same keys.
        var (_, mgr, cache) = BuildWithCache<RouteOnlySettings>();

        await mgr.For<RouteOnlySettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:RouteOnlySettings", It.IsAny<RouteOnlySettings>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    private static (Mock<ISettingStore>, ISettingManager) Build<T>() where T : new()
    {
        var (store, mgr, _) = BuildWithCache<T>();
        return (store, mgr);
    }

    private static (Mock<ISettingStore>, ISettingManager, Mock<ISettingCache>) BuildWithCache<T>() where T : new()
    {
        var store = new Mock<ISettingStore>();
        store.Setup(x => x.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var cache = new Mock<ISettingCache>();
        T? miss = default;
        cache.Setup(x => x.TryGetValue<T>(It.IsAny<string>(), out miss)).Returns(false);

        var mgr = new SettingManager(store.Object, cache.Object,
            new ServiceCollection().BuildServiceProvider(), NullLogger<SettingManager>.Instance);
        return (store, mgr, cache);
    }

    [SettingGroup("route-only")]
    public class RouteOnlySettings { public int Port { get; set; } }

    [SettingGroup("billing", Name = "billing-v1")]
    public class RenamedSettings { public int Port { get; set; } }

    public class NoAttributeSettings { public int Port { get; set; } }
}
