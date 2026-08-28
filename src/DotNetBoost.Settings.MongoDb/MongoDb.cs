using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace DotNetBoost.Settings.MongoDb;

/// <summary>BSON shape of a document in the <c>settings</c> collection.</summary>
public sealed class MongoSettingDocument
{
    /// <summary>MongoDB document id, assigned by the server on insert.</summary>
    [BsonId][BsonRepresentation(BsonType.ObjectId)]
    public string? Id          { get; set; }

    /// <summary>Persistence key of the settings group.</summary>
    public string  Group       { get; set; } = default!;

    /// <summary>Property name within the group.</summary>
    public string  Key         { get; set; } = default!;

    /// <summary>Serialised value, or ciphertext when <see cref="IsEncrypted"/> is set.</summary>
    public string  Value       { get; set; } = default!;

    /// <summary>CLR type name the value was written from.</summary>
    public string  Type        { get; set; } = default!;

    /// <summary>Whether <see cref="Value"/> holds ciphertext.</summary>
    public bool    IsEncrypted { get; set; }

    /// <summary>UTC timestamp of the last write.</summary>
    public DateTime UpdatedAt  { get; set; }

    /// <summary>Who last wrote the value, when supplied.</summary>
    public string? UpdatedBy   { get; set; }

    /// <summary>Optimistic concurrency token for the document's current revision.</summary>
    public byte[]? RowVersion  { get; set; }
}

/// <summary>MongoDB-backed <see cref="ISettingStore"/>.</summary>
public sealed class MongoSettingStore : ISettingStore
{
    /// <summary>Collection used when none is configured.</summary>
    public const string DefaultCollectionName = "settings";

    private readonly IMongoCollection<MongoSettingDocument> _col;

    /// <summary>
    /// Resolving a collection handle is a local, lazy operation — this constructor performs
    /// no server round-trip. Index creation is deliberately *not* done here: the store is
    /// registered scoped, so it would issue a blocking createIndex on every request. See
    /// <see cref="EnsureIndexesAsync"/>, which <c>UseMongoDb()</c> runs once at startup.
    /// </summary>
    /// <param name="db">The database holding the settings collection.</param>
    /// <param name="collectionName">Collection name. Defaults to <c>settings</c>.</param>
    public MongoSettingStore(IMongoDatabase db, string collectionName = DefaultCollectionName)
    {
        ArgumentNullException.ThrowIfNull(db);
        _col = db.GetCollection<MongoSettingDocument>(ValidCollection(collectionName));
    }

    /// <summary>
    /// MongoDB collection names are permissive, but a few forms are reserved or illegal and
    /// fail late with an obscure server error; reject those up front instead.
    /// </summary>
    private static string ValidCollection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains('$', StringComparison.Ordinal) ||
            name.Contains('\0', StringComparison.Ordinal) ||
            name.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{name}' is not a usable MongoDB collection name: it must not contain '$' or a " +
                "null character, nor start with 'system.'.", nameof(name));
        }

        return name;
    }

    /// <summary>
    /// Creates the unique index on (Group, Key) that the store's upsert semantics rely on.
    /// Idempotent: MongoDB treats a createIndex for an identical existing index as a no-op,
    /// so this is safe to run on every application start.
    /// </summary>
    public static Task EnsureIndexesAsync(
        IMongoDatabase db, string collectionName = DefaultCollectionName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keys  = Builders<MongoSettingDocument>.IndexKeys.Ascending(x => x.Group).Ascending(x => x.Key);
        var model = new CreateIndexModel<MongoSettingDocument>(
            keys, new CreateIndexOptions { Unique = true, Name = "ux_group_key" });

        return db.GetCollection<MongoSettingDocument>(ValidCollection(collectionName))
            .Indexes.CreateOneAsync(model, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var docs = await _col.Find(GroupFilter(group)).ToListAsync(ct).ConfigureAwait(false);
        return docs.ConvertAll(ToModel);
    }

    /// <inheritdoc/>
    public async Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var doc = await _col.Find(GroupKeyFilter(group, key)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc is null ? null : ToModel(doc);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(Setting setting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        await UpsertOneAsync(setting, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A write with no expected token can upsert blindly. One that carries an expectation must
    /// not upsert: an inserting upsert whose filter failed on the token would race the unique
    /// index instead of reporting the conflict, so it is a filtered update whose match count
    /// tells us what happened.
    /// </summary>
    private async Task UpsertOneAsync(Setting setting, CancellationToken ct)
    {
        if (setting.RowVersion is null)
        {
            await _col.UpdateOneAsync(
                GroupKeyFilter(setting.Group, setting.Key), BuildUpdate(setting),
                new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
            return;
        }

        var filter = Builders<MongoSettingDocument>.Filter.And(
            GroupKeyFilter(setting.Group, setting.Key),
            Builders<MongoSettingDocument>.Filter.Eq(x => x.RowVersion, setting.RowVersion));

        var result = await _col.UpdateOneAsync(filter, BuildUpdate(setting), new UpdateOptions(), ct)
            .ConfigureAwait(false);

        if (result.MatchedCount == 0)
            throw new SettingConcurrencyException(setting.Group, setting.Key);
    }

    /// <inheritdoc/>
    public async Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = settings as IList<Setting> ?? settings.ToList();
        if (list.Count == 0) return;

        // Written one at a time so a token mismatch can be attributed to the property that
        // lost the race; a bulk result reports only aggregate counts. MongoDB gives no
        // cross-document atomicity outside a transaction anyway, and a settings group is a
        // handful of properties.
        foreach (var setting in list)
            await UpsertOneAsync(setting, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _col.DeleteOneAsync(GroupKeyFilter(group, key), ct);
    }

    /// <inheritdoc/>
    public Task DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return _col.DeleteManyAsync(GroupFilter(group), ct);
    }

    /// <inheritdoc/>
    public async Task<bool> GroupExistsAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return await _col.CountDocumentsAsync(GroupFilter(group), cancellationToken: ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return (int)await _col.CountDocumentsAsync(GroupFilter(group), cancellationToken: ct).ConfigureAwait(false);
    }

    private static FilterDefinition<MongoSettingDocument> GroupFilter(string g)
        => Builders<MongoSettingDocument>.Filter.Eq(x => x.Group, g);

    private static FilterDefinition<MongoSettingDocument> GroupKeyFilter(string g, string k)
        => Builders<MongoSettingDocument>.Filter.And(
               Builders<MongoSettingDocument>.Filter.Eq(x => x.Group, g),
               Builders<MongoSettingDocument>.Filter.Eq(x => x.Key,   k));

    private static UpdateDefinition<MongoSettingDocument> BuildUpdate(Setting s)
        => Builders<MongoSettingDocument>.Update
            .Set(x => x.Group,       s.Group)
            .Set(x => x.Key,         s.Key)
            .Set(x => x.Value,       s.Value)
            .Set(x => x.Type,        s.Type)
            .Set(x => x.IsEncrypted, s.IsEncrypted)
            .Set(x => x.UpdatedAt,   s.UpdatedAt)
            .Set(x => x.UpdatedBy,   s.UpdatedBy)
            .Set(x => x.RowVersion,  Setting.NewRowVersion());

    private static Setting ToModel(MongoSettingDocument d)
        => new() { Group = d.Group, Key = d.Key, Value = d.Value, Type = d.Type, IsEncrypted = d.IsEncrypted, UpdatedAt = d.UpdatedAt, UpdatedBy = d.UpdatedBy, RowVersion = d.RowVersion };
}

/// <summary>
/// Holds the database the settings store works against.
/// <para>
/// Deliberately a private type rather than a registration of <see cref="IMongoDatabase"/> or
/// <see cref="IMongoClient"/>. Those are types an application may well register itself, and
/// the last registration wins — so publishing them would mean this provider and the host
/// application silently overriding one another's Mongo client.
/// </para>
/// </summary>
internal sealed class SettingsMongoContext(IMongoDatabase database, string collectionName)
{
    public IMongoDatabase Database { get; } = database;

    public string CollectionName { get; } = collectionName;
}

/// <summary>
/// Creates the settings collection's indexes once at application start, so the store itself
/// never has to. Registered by <c>UseMongoDb()</c> unless <c>createIndexes: false</c>.
/// </summary>
internal sealed class MongoIndexInitializer(SettingsMongoContext context) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
        => MongoSettingStore.EnsureIndexesAsync(context.Database, context.CollectionName, ct);

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
