# Caching

Reads go through `ISettingCache` (default: `IMemoryCache`, 10-minute absolute expiration). A per-group `SemaphoreSlim` prevents cache stampedes under concurrent load.

**What is cached is the stored rows, not the settings object.** Every `GetAsync` materialises a
fresh instance from them, so two callers never share one: assigning to a property of what you
read changes nothing for the next reader, and nothing for `GET /api/settings/{route}`. The cache
is there to skip the round trip to the store; the mapping is reflection over a handful of
properties and is cheap to repeat.

Two consequences worth knowing:

- A `[Sensitive]` value is decrypted per read rather than once per cache entry. For a
  distributed cache this is the better trade anyway — what is written to Redis is the stored
  ciphertext, not the decrypted model.
- A custom `ISettingCache` is handed `Setting[]`. Implementations that serialise, like the Redis
  one below, need nothing special for this; the type is public and plain.

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .WithCacheDuration(TimeSpan.FromMinutes(5))
    .UseCustomCache<RedisSettingCache>()   // swap in a distributed cache
    .Build();
```

## Running on more than one instance

A write evicts the cache entry on the instance that made it. Nothing else happens by itself, so
every other instance goes on serving the old values until its own entry expires — up to
`CacheDuration`, ten minutes by default. "Change it, reload, done" stops being true at two
replicas.

One line fixes it, using whatever `IDistributedCache` the application already has:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = "localhost:6379");

builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .SynchronizeThroughDistributedCache()
    .Build();
```

Every write publishes a fresh token for that group, and a cache hit compares its token against
the published one at most once per **`ChangeCheckInterval`, 5 seconds by default**.

**That interval is the staleness bound, and it is not zero.** An instance can serve a value
another instance has already replaced for up to five seconds. What the signal buys is that the
bound is five seconds rather than ten minutes — not that propagation is instant. Pass a
different interval if you want a different trade: `SynchronizeThroughDistributedCache(checkInterval: TimeSpan.FromSeconds(1))`
costs more round trips to Redis for a tighter bound.

The token is per group, so a write to one group does not make other groups reload. An
unreachable signalling store is logged and treated as "unchanged": a signal is an optimisation
over the cache duration, so a Redis outage should cost a slower reload, not an error. And with
no signal registered the check never fires, so a single-instance application behaves exactly as
it did before and needs no configuration.

Register your own transport — a bus, a database table, a pub/sub topic — with
`UseChangeSignal<T>()` over `ISettingChangeSignal`.

> **This is about cache coherence, and nothing else.** `ISettingChangedHandler<T>` and
> `ISettingWriteObserver` stay in-process by design: the signal makes other instances notice a
> new *value*, it does not run their handlers. If you need every node to react, publish from an
> observer onto something every node subscribes to.

### Or share one cache

The alternative is to give every instance the same cache, so there is only one entry to evict:

```csharp
public class RedisSettingCache(IConnectionMultiplexer redis) : ISettingCache
{
    private readonly IDatabase _db = redis.GetDatabase();

    public bool TryGetValue<T>(string key, out T? value)
    {
        var raw = _db.StringGet(key);
        if (!raw.HasValue) { value = default; return false; }
        value = JsonSerializer.Deserialize<T>(raw!);
        return value is not null;
    }

    public void Set<T>(string key, T value, TimeSpan duration)
        => _db.StringSet(key, JsonSerializer.Serialize(value), duration);

    public void Remove(string key) => _db.KeyDelete(key);
}
```

