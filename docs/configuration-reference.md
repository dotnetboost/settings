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
| `.SynchronizeThroughDistributedCache(prefix, interval)` | Core | Propagate writes between instances over `IDistributedCache` |
| `.UseChangeSignal<TSignal>(interval)` | Core | Propagate writes over your own transport |
| `.UseAesEncryption(key, retiredKeys...)` | Core | Enable built-in AES-256-GCM encryption; retired keys decrypt only |
| `.IgnoreDecryptionFailures()` | Core | Fall back to defaults instead of throwing when a value will not decrypt |
| `.UseCustomEncryption<TEncryptor>()` | Core | Plug in a custom encryptor |
| `.UseWriteObserver<TObserver>()` | Core | Observe every completed write; several may be registered |
| `.UseLoggingWriteObserver()` | Core | Log one line per write — property names, never values |
| `.UseActorAccessor<TAccessor>()` | Core | Supply who is making the change |
| `.UseHttpContextActor()` | API | Take the actor from the signed-in user of the request |
| `.OnChanged<TSettings, THandler>()` | Core | Register a runtime change handler |
| `.UseProjector<TSettings, TProjector>()` | Core (applied by API) | Reshape a group for `GET` only; never touches the `ETag` |
| `.UseFluentValidation(assembly)` | FluentValidation | Register validators |
| `.Build()` | Core | Validate configuration, return `IServiceCollection` |

