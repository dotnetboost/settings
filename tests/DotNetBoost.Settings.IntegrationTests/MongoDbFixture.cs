using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace DotNetBoost.Settings.IntegrationTests;

/// <summary>
/// Starts a single MongoDB container for the whole collection. Each call to
/// <see cref="CreateDatabase"/> returns a fresh, empty database on that container.
/// </summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8").Build();

    private int _databaseCounter;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public IMongoDatabase CreateDatabase()
    {
        var client = new MongoClient(_container.GetConnectionString());
        return client.GetDatabase($"test_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}");
    }
}

[CollectionDefinition(Name)]
public sealed class MongoDbCollection : ICollectionFixture<MongoDbFixture>
{
    public const string Name = "mongodb";
}
