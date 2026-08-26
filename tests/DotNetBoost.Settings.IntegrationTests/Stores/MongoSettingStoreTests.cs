using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.MongoDb;
using DotNetBoost.Settings.ProviderTests.Stores;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

/// <summary>
/// Runs the full <see cref="SettingStoreContractTests"/> suite against a real MongoDB server.
/// The Mongo store had no automated coverage at all before this, since it cannot be faked
/// with an in-memory relational database the way the EF Core and Dapper stores were.
/// </summary>
[Collection(MongoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoSettingStoreTests(MongoDbFixture fixture) : SettingStoreContractTests
{
    protected override async Task<ISettingStore> CreateStoreAsync()
    {
        var db = fixture.CreateDatabase();
        await MongoSettingStore.EnsureIndexesAsync(db);
        return new MongoSettingStore(db);
    }

    [Fact]
    public async Task Data_Survives_A_New_Client_Against_The_Same_Database()
    {
        var db = fixture.CreateDatabase();

        await new MongoSettingStore(db).UpsertAsync(
            new Setting { Group = "Mail", Key = "Host", Value = "smtp.live", Type = "System.String" });

        // A second store over a freshly resolved database handle proves the write was persisted
        // by the server rather than held in the driver.
        var reread = await new MongoSettingStore(db.Client.GetDatabase(db.DatabaseNamespace.DatabaseName))
            .GetAsync("Mail", "Host");

        Assert.Equal("smtp.live", reread!.Value);
    }

    [Fact]
    public async Task EnsureIndexesAsync_CreatesUniqueIndexOnGroupAndKey()
    {
        var db = fixture.CreateDatabase();
        await MongoSettingStore.EnsureIndexesAsync(db);

        var indexes = await (await db.GetCollection<BsonDocument>("settings").Indexes.ListAsync())
            .ToListAsync();

        var unique = indexes.Where(i => i.TryGetValue("unique", out var u) && u.AsBoolean).ToList();
        Assert.Single(unique);

        var keys = unique[0]["key"].AsBsonDocument;
        Assert.True(keys.Contains("Group"), $"expected a 'Group' key, got: {keys}");
        Assert.True(keys.Contains("Key"),   $"expected a 'Key' key, got: {keys}");
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateGroupKeyPair()
    {
        var db = fixture.CreateDatabase();
        await MongoSettingStore.EnsureIndexesAsync(db);
        var raw = db.GetCollection<BsonDocument>("settings");

        await raw.InsertOneAsync(new BsonDocument { ["Group"] = "Mail", ["Key"] = "Host", ["Value"] = "a" });

        // The index must be enforced by the server, not merely by the store's upsert filter.
        await Assert.ThrowsAsync<MongoWriteException>(() => raw.InsertOneAsync(
            new BsonDocument { ["Group"] = "Mail", ["Key"] = "Host", ["Value"] = "b" }));
    }

    [Fact]
    public async Task EnsureIndexesAsync_IsIdempotent()
    {
        // The hosted service runs this on every application start.
        var db = fixture.CreateDatabase();

        await MongoSettingStore.EnsureIndexesAsync(db);
        await MongoSettingStore.EnsureIndexesAsync(db);

        var indexes = await (await db.GetCollection<BsonDocument>("settings").Indexes.ListAsync()).ToListAsync();
        Assert.Single(indexes, i => i.TryGetValue("unique", out var u) && u.AsBoolean);
    }
}

/// <summary>
/// The store is registered scoped, so anything the constructor does on the wire happens once
/// per request. It used to issue a blocking createIndex there; these run without a container
/// precisely because a correct constructor never reaches the network.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MongoSettingStoreConstructorTests
{
    // Nothing listens here. Server selection is capped so a constructor that does reach the
    // network fails in about a second rather than hanging for the 30s default.
    private static IMongoDatabase UnreachableDatabase()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:1/");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
        settings.ConnectTimeout         = TimeSpan.FromSeconds(1);
        return new MongoClient(settings).GetDatabase("unreachable");
    }

    [Fact]
    public void Constructor_DoesNotTouchTheServer()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var ex = Record.Exception(() => new MongoSettingStore(UnreachableDatabase()));

        Assert.Null(ex);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"constructor took {elapsed.Elapsed}, which means it went to the server");
    }

    [Fact]
    public async Task EnsureIndexesAsync_DoesTouchTheServer()
    {
        // Guards the premise of the test above: the unreachable handle really is unreachable,
        // so the constructor passing is meaningful rather than vacuous.
        await Assert.ThrowsAnyAsync<TimeoutException>(
            () => MongoSettingStore.EnsureIndexesAsync(UnreachableDatabase()));
    }

    [Fact]
    public void Constructor_StillRejectsNull()
        => Assert.Throws<ArgumentNullException>(() => new MongoSettingStore(null!));
}
