using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>Regression tests for <c>WithCacheDuration()</c> reaching the manager.</summary>
public class SettingOptionsTests
{
    [Fact]
    public async Task WithCacheDuration_IsAppliedToCachedEntries()
    {
        var (mgr, cache) = BuildWith(b => b.WithCacheDuration(TimeSpan.FromMinutes(5)));

        await mgr.For<OptionsSettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:OptionsSettings",
            It.IsAny<Setting[]>(), TimeSpan.FromMinutes(5)), Times.Once);
    }

    [Fact]
    public async Task CacheDuration_DefaultsToTenMinutes()
    {
        var (mgr, cache) = BuildWith(_ => { });

        await mgr.For<OptionsSettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:OptionsSettings",
            It.IsAny<Setting[]>(), TimeSpan.FromMinutes(10)), Times.Once);
    }

    [Fact]
    public void WithCacheDuration_RejectsNonPositiveDuration()
    {
        var builder = new ServiceCollection().AddSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCacheDuration(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCacheDuration(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AddSettings_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddSettings().WithCacheDuration(TimeSpan.FromHours(2));

        var options = services.BuildServiceProvider().GetRequiredService<SettingOptions>();

        Assert.Equal(TimeSpan.FromHours(2), options.CacheDuration);
    }

    // Resolves the manager through DI so the test covers the real registration path,
    // which is where the builder-to-manager wiring was previously lost.
    private static (ISettingManager, Mock<ISettingCache>) BuildWith(Action<SettingBuilder> configure)
    {
        var store = new Mock<ISettingStore>();
        store.Setup(x => x.GetGroupAsync("OptionsSettings", It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);

        var cache = new Mock<ISettingCache>();
        Setting[]? miss = null;
        cache.Setup(x => x.TryGetValue<Setting[]>(It.IsAny<string>(), out miss)).Returns(false);

        var services = new ServiceCollection();
        services.AddSingleton(store.Object);
        services.AddSingleton(cache.Object);   // registered first so AddSettings' TryAdd defers
        services.AddLogging();

        configure(services.AddSettings());

        return (services.BuildServiceProvider().GetRequiredService<ISettingManager>(), cache);
    }

    public class OptionsSettings { public int Port { get; set; } }
}
