using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetBoost.Settings.IntegrationTests;

/// <summary>
/// Registration-level tests for <c>UseMongoDb()</c>. No container: nothing here connects, it
/// only inspects and resolves the service collection.
/// <para>
/// The provider used to register <see cref="IMongoClient"/> and <see cref="IMongoDatabase"/>
/// as singletons. Since the last registration wins, that silently replaced whatever client the
/// host application had configured — credentials, telemetry and resilience settings included.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class MongoRegistrationTests
{
    private const string Conn = "mongodb://127.0.0.1:27017";

    [Fact]
    public void UseMongoDb_DoesNotRegisterIMongoClientOrIMongoDatabase()
    {
        var services = Build(s => s.AddSettings().UseMongoDb(Conn, "app_db"));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMongoClient));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMongoDatabase));
    }

    [Fact]
    public void UseMongoDb_LeavesAnApplicationsOwnMongoClientIntact()
    {
        var appClient = new MongoClient(Conn);

        var provider = Build(s =>
        {
            s.AddSingleton<IMongoClient>(appClient);          // the application's own client
            s.AddSettings().UseMongoDb(Conn, "app_db");
        }).BuildServiceProvider();

        Assert.Same(appClient, provider.GetRequiredService<IMongoClient>());
    }

    [Fact]
    public void UseMongoDb_IsNotOverriddenByAnApplicationRegisteringMongoAfterwards()
    {
        // Order must not matter either way round.
        var provider = Build(s =>
        {
            s.AddSettings().UseMongoDb(Conn, "settings_db");
            s.AddSingleton<IMongoClient>(new MongoClient(Conn));
            s.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("app_db"));
        }).BuildServiceProvider();

        var store = provider.GetRequiredService<ISettingStore>();
        Assert.NotNull(store);
        Assert.Equal("settings_db", DatabaseNameOf(provider));
    }

    [Fact]
    public void UseMongoDb_WithFactory_UsesTheApplicationsDatabase()
    {
        var provider = Build(s =>
        {
            s.AddSingleton<IMongoClient>(new MongoClient(Conn));
            s.AddSettings().UseMongoDb(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("shared_db"));
        }).BuildServiceProvider();

        Assert.Equal("shared_db", DatabaseNameOf(provider));
    }

    [Fact]
    public void UseMongoDb_WithFactory_SharesTheApplicationsClient()
    {
        var appClient = new MongoClient(Conn);

        var provider = Build(s =>
        {
            s.AddSingleton<IMongoClient>(appClient);
            s.AddSettings().UseMongoDb(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("shared_db"));
        }).BuildServiceProvider();

        Assert.Same(appClient, provider.GetRequiredService<SettingsMongoContext>().Database.Client);
    }

    [Fact]
    public void UseMongoDb_ResolvesASettingStore()
        => Assert.NotNull(Build(s => s.AddSettings().UseMongoDb(Conn, "app_db"))
            .BuildServiceProvider().GetRequiredService<ISettingStore>());

    [Fact]
    public void UseMongoDb_RegistersTheIndexInitializer_ByDefault()
    {
        var services = Build(s => s.AddSettings().UseMongoDb(Conn, "app_db"));
        Assert.Contains(services, d => d.ImplementationType == typeof(MongoIndexInitializer));
    }

    [Fact]
    public void UseMongoDb_SkipsTheIndexInitializer_WhenOptedOut()
    {
        var services = Build(s => s.AddSettings().UseMongoDb(Conn, "app_db", createIndexes: false));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(MongoIndexInitializer));
    }

    [Fact]
    public void UseMongoDb_StillGuardsAgainstASecondProvider()
    {
        var builder = new ServiceCollection().AddSettings();
        builder.UseMongoDb(Conn, "app_db");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.UseMongoDb(Conn, "other_db"));
        Assert.Contains("already configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseMongoDb_WithFactory_RejectsNulls()
    {
        var builder = new ServiceCollection().AddSettings();
        Assert.Throws<ArgumentNullException>(() => builder.UseMongoDb((Func<IServiceProvider, IMongoDatabase>)null!));
    }

    private static string DatabaseNameOf(IServiceProvider provider)
        => provider.GetRequiredService<SettingsMongoContext>().Database.DatabaseNamespace.DatabaseName;

    private static ServiceCollection Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        configure(services);
        return services;
    }
}
