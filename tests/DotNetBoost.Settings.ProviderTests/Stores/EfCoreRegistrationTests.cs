using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.ProviderTests.Stores;

/// <summary>
/// <c>UseEntityFrameworkCore&lt;TContext&gt;</c> asks nothing of the context beyond being a
/// <see cref="DbContext"/>. <see cref="TestDbContext"/> implements no interface and declares
/// no <c>DbSet</c> properties, so these tests only compile and pass if the constraint and the
/// marker interface are genuinely gone.
/// </summary>
public class EfCoreRegistrationTests
{
    [Fact]
    public void UseEntityFrameworkCore_ResolvesTheStore_FromAPlainDbContext()
    {
        using var provider = BuildProvider(b => b.UseEntityFrameworkCore<TestDbContext>());
        using var scope    = provider.CreateScope();

        Assert.IsType<EfCoreSettingStore>(scope.ServiceProvider.GetRequiredService<ISettingStore>());
    }

    /// <summary>
    /// <c>EfCoreAuditStore</c> is activated by DI and now takes a <c>DbContext</c>, which only
    /// resolves because <c>UseEntityFrameworkCore</c> registers the chosen context as one.
    /// </summary>
    [Fact]
    public void UseAuditStore_ResolvesTheEfCoreAuditStore()
    {
        using var provider = BuildProvider(
            b => b.UseEntityFrameworkCore<TestDbContext>().UseAuditStore<EfCoreAuditStore>());
        using var scope = provider.CreateScope();

        Assert.IsType<EfCoreAuditStore>(scope.ServiceProvider.GetRequiredService<ISettingAuditStore>());
    }

    [Fact]
    public void UseEntityFrameworkCore_RegistersTheChosenContextAsTheScopedDbContext()
    {
        using var provider = BuildProvider(b => b.UseEntityFrameworkCore<TestDbContext>());
        using var scope    = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<TestDbContext>(),
            scope.ServiceProvider.GetRequiredService<DbContext>());
    }

    private static ServiceProvider BuildProvider(Action<Core.SettingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestDbContext>(o => o.UseSqlite("DataSource=:memory:"));

        configure(services.AddSettings());

        // Validate on build so a lifetime mistake in the registrations fails here rather than
        // on some later request.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes  = true,
            ValidateOnBuild = true
        });
    }
}
