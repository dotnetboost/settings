using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// Projectors do not compose: <c>ProjectAsync</c> takes the group, so a second projector has
/// nowhere to receive the first's output. Two registrations for one group therefore meant the
/// last silently won and the other was never constructed — a response quietly missing half its
/// shape. <c>Build()</c> rejects it, in the same place that already rejects duplicate group
/// names and routes.
/// </summary>
public class ProjectorRegistrationTests
{
    [Fact]
    public void TwoDifferentProjectors_ForOneGroup_ThrowsAndNamesBoth()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.ValidateProjectors(Services(s =>
            {
                s.AddScoped<ISettingProjector<AlphaSettings>, FirstAlphaProjector>();
                s.AddScoped<ISettingProjector<AlphaSettings>, SecondAlphaProjector>();
            })));

        Assert.Contains(nameof(FirstAlphaProjector), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondAlphaProjector), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AlphaSettings), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The error should say what to do instead, not only that something is wrong.</summary>
    [Fact]
    public void TheError_PointsAtReplaceProjector()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.ValidateProjectors(Services(s =>
            {
                s.AddScoped<ISettingProjector<AlphaSettings>, FirstAlphaProjector>();
                s.AddScoped<ISettingProjector<AlphaSettings>, SecondAlphaProjector>();
            })));

        Assert.Contains("ReplaceProjector", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An idempotent mistake rather than an ambiguity — both registrations name the same
    /// implementation, so there is nothing to be wrong about.
    /// </summary>
    [Fact]
    public void TheSameProjectorTwice_DoesNotThrow()
        => Assert.Null(Record.Exception(() =>
            SettingBuilderValidator.ValidateProjectors(Services(s =>
            {
                s.AddScoped<ISettingProjector<AlphaSettings>, FirstAlphaProjector>();
                s.AddScoped<ISettingProjector<AlphaSettings>, FirstAlphaProjector>();
            }))));

    [Fact]
    public void OneProjectorEach_ForTwoGroups_DoesNotThrow()
        => Assert.Null(Record.Exception(() =>
            SettingBuilderValidator.ValidateProjectors(Services(s =>
            {
                s.AddScoped<ISettingProjector<AlphaSettings>, FirstAlphaProjector>();
                s.AddScoped<ISettingProjector<BetaSettings>, BetaProjector>();
            }))));

    [Fact]
    public void NoProjectors_DoesNotThrow()
        => Assert.Null(Record.Exception(() => SettingBuilderValidator.ValidateProjectors(Services(_ => { }))));

    /// <summary>
    /// A factory registration has no <c>ImplementationType</c>, so it cannot be compared with
    /// anything. Folding two unknowns together would silently pass the case the guard most
    /// wants to catch.
    /// </summary>
    [Fact]
    public void TwoFactoryRegistrations_ForOneGroup_StillThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.ValidateProjectors(Services(s =>
            {
                s.AddScoped<ISettingProjector<AlphaSettings>>(_ => new FirstAlphaProjector());
                s.AddScoped<ISettingProjector<AlphaSettings>>(_ => new SecondAlphaProjector());
            })));

        Assert.Contains("factory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFactoryAlongsideAType_ForOneGroup_StillThrows()
        => Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.ValidateProjectors(Services(s =>
            {
                s.AddScoped<ISettingProjector<AlphaSettings>, FirstAlphaProjector>();
                s.AddScoped<ISettingProjector<AlphaSettings>>(_ => new SecondAlphaProjector());
            })));

    [Fact]
    public void ReplaceProjector_AfterUseProjector_LeavesExactlyOne_AndDoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddSettings()
            .UseProjector<AlphaSettings, FirstAlphaProjector>()
            .ReplaceProjector<AlphaSettings, SecondAlphaProjector>();

        var registered = services
            .Where(d => d.ServiceType == typeof(ISettingProjector<AlphaSettings>))
            .ToList();

        Assert.Single(registered);
        Assert.Equal(typeof(SecondAlphaProjector), registered[0].ImplementationType);
        Assert.Null(Record.Exception(() => SettingBuilderValidator.ValidateProjectors(services)));
    }

    // A Build()-level test is not reachable from this assembly: Build() scans every loaded
    // assembly for [SettingGroup] classes first, and this one deliberately contains broken
    // fixtures (EmptyRouteSettings, DupRouteA/B) for the tests above it, so Build() always
    // throws on those before reaching the projector check. The check itself is covered here
    // directly; the one line wiring it into Build() is covered by inspection.

    [Fact]
    public void ValidateProjectors_NullServices_Throws()
        => Assert.Throws<ArgumentNullException>(() => SettingBuilderValidator.ValidateProjectors(null!));

    private static ServiceCollection Services(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services;
    }

    public class AlphaSettings { public string? Host { get; set; } }
    public class BetaSettings  { public string? Host { get; set; } }

    private sealed class FirstAlphaProjector : ISettingProjector<AlphaSettings>
    {
        public Task<object> ProjectAsync(AlphaSettings group, CancellationToken ct = default)
            => Task.FromResult<object>(group);
    }

    private sealed class SecondAlphaProjector : ISettingProjector<AlphaSettings>
    {
        public Task<object> ProjectAsync(AlphaSettings group, CancellationToken ct = default)
            => Task.FromResult<object>(group);
    }

    private sealed class BetaProjector : ISettingProjector<BetaSettings>
    {
        public Task<object> ProjectAsync(BetaSettings group, CancellationToken ct = default)
            => Task.FromResult<object>(group);
    }
}
