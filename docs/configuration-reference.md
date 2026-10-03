# Configuration reference

## `AddSettings()` builder methods

| Method | Package | Description |
|---|---|---|
| `.UseEntityFrameworkCore<TContext>()` | EntityFrameworkCore | Backing store |
| `.UseDapper(factory, migrateSchema)` | Dapper | Backing store |
| `.UseMongoDb(connStr, dbName, createIndexes)` | MongoDb | Backing store, provider-owned client |
| `.UseMongoDb(databaseFactory, createIndexes)` | MongoDb | Backing store, reusing your own `IMongoClient` |
| `.UseCustomCache<TCache>()` | Core | Replace the default cache |
| `.WithCacheDuration(TimeSpan)` | Core | Override the 10-minute default |
| `.UseAesEncryption(key, retiredKeys...)` | Core | Enable built-in AES-256-GCM encryption; retired keys decrypt only |
| `.IgnoreDecryptionFailures()` | Core | Fall back to defaults instead of throwing when a value will not decrypt |
| `.UseCustomEncryption<TEncryptor>()` | Core | Plug in a custom encryptor |
| `.UseAuditStore<TStore>()` | Core (+ EF Core impl) | Enable change history |
| `.OnChanged<TSettings, THandler>()` | Core | Register a runtime change handler |
| `.UseProjector<TSettings, TProjector>()` | Core (applied by API) | Reshape a group for `GET` only; never touches the `ETag` |
| `.UseFluentValidation(assembly)` | FluentValidation | Register validators |
| `.Build()` | Core | Validate configuration, return `IServiceCollection` |

