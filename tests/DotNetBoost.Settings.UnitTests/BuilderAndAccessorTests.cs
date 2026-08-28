using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetBoost.Settings.UnitTests;

public class BuilderTests
{
    [Fact]
    public void AddSettings_ReturnsBuilder()
        => Assert.NotNull(new ServiceCollection().AddSettings());

    [Fact]
    public void AddSettings_RegistersCoreServices()
    {
        var services = new ServiceCollection();
        services.AddSettings();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<ISettingCache>());
    }

    [Fact]
    public void Build_WithoutProvider_Throws()
    {
        var builder = new ServiceCollection().AddSettings();
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("No settings provider configured", ex.Message);
    }

    [Fact]
    public void EnsureProviderNotConfigured_TwiceSameBuilder_Throws()
    {
        var builder = new ServiceCollection().AddSettings();
        SettingBuilderGuard.EnsureProviderNotConfigured(builder, "TestProvider");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SettingBuilderGuard.EnsureProviderNotConfigured(builder, "AnotherProvider"));

        Assert.Contains("already configured", ex.Message);
    }

    [Fact]
    public void EnsureProviderNotConfigured_NullBuilder_Throws()
        => Assert.Throws<ArgumentNullException>(() => SettingBuilderGuard.EnsureProviderNotConfigured(null!, "x"));
}

public class SettingAccessorTests
{
    [Fact]
    public async Task GetAsync_ReturnsModel_WhenStoreEmpty()
    {
        var (_, mgr) = BuildManager([]);
        var result = await mgr.For<AccessorMailSettings>().GetAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetAsync_Selector_Works()
    {
        var rows = new[] { Row("Port", "587", "System.Int32") };
        var (_, mgr) = BuildManager(rows);
        var port = await mgr.For<AccessorMailSettings>().GetAsync(x => x.Port);
        Assert.Equal(587, port);
    }

    // The blocking Get()/Get(selector) pair these two used to cover is gone; the values they
    // asserted on are the same ones GetAsync returns.
    [Fact]
    public async Task GetAsync_ReadsAStoredValue()
    {
        var rows = new[] { Row("Port", "2525", "System.Int32") };
        var (_, mgr) = BuildManager(rows);
        var result = await mgr.For<AccessorMailSettings>().GetAsync();
        Assert.Equal(2525, result.Port);
    }

    [Fact]
    public async Task GetAsync_Selector_ReadsAStoredValue()
    {
        var rows = new[] { Row("Port", "2525", "System.Int32") };
        var (_, mgr) = BuildManager(rows);
        var port = await mgr.For<AccessorMailSettings>().GetAsync(x => x.Port);
        Assert.Equal(2525, port);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_WhenRowsExist()
    {
        var store = new Mock<ISettingStore>();
        store.Setup(x => x.CountAsync("AccessorMailSettings", default)).ReturnsAsync(1);
        var cache = new Mock<ISettingCache>();
        AccessorMailSettings? miss = null;
        cache.Setup(x => x.TryGetValue<AccessorMailSettings>(It.IsAny<string>(), out miss)).Returns(false);
        var mgr = new SettingManager(store.Object, cache.Object,
            new ServiceCollection().BuildServiceProvider(), NullLogger<SettingManager>.Instance);

        Assert.True(await mgr.For<AccessorMailSettings>().ExistsAsync());
    }

    private static (Mock<ISettingStore> store, ISettingManager manager) BuildManager(IEnumerable<Setting> rows)
    {
        var store = new Mock<ISettingStore>();
        var cache = new Mock<ISettingCache>();
        AccessorMailSettings? miss = null;
        cache.Setup(x => x.TryGetValue<AccessorMailSettings>(It.IsAny<string>(), out miss)).Returns(false);
        store.Setup(x => x.GetGroupAsync("AccessorMailSettings", default)).ReturnsAsync(rows.ToList());
        var mgr = new SettingManager(store.Object, cache.Object,
            new ServiceCollection().BuildServiceProvider(), NullLogger<SettingManager>.Instance);
        return (store, mgr);
    }

    private static Setting Row(string key, string value, string type)
        => new() { Group = "AccessorMailSettings", Key = key, Value = value, Type = type };

    public class AccessorMailSettings { public int Port { get; set; } public string? Host { get; set; } }
}
