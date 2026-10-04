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
    /// The library must not claim a framework type it does not own. Registering
    /// <see cref="DbContext"/> would, in an application with two contexts, silently hand a
    /// bare <c>DbContext</c> dependency whichever one Settings happened to be pointed at —
    /// and nothing here needs it, because the store is constructed from
    /// <c>TestDbContext</c> explicitly.
    /// </summary>
    [Fact]
    public void UseEntityFrameworkCore_RegistersNothingAgainstDbContext()
    {
        using var provider = BuildProvider(b => b.UseEntityFrameworkCore<TestDbContext>());
        using var scope    = provider.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<DbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TestDbContext>());
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
