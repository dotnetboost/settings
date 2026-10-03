# Reading and writing settings

```csharp
public class EmailService(ISettingManager settings)
{
    public async Task<SmtpClient> CreateClientAsync()
    {
        var mail = await settings.For<MailSettings>().GetAsync();
        return new SmtpClient(mail.Host, mail.Port) { EnableSsl = mail.UseSsl };
    }

    public async Task<int> GetPortAsync()
        => await settings.For<MailSettings>().GetAsync(x => x.Port);

    public async Task UpdatePortAsync(int port)
        => await settings.For<MailSettings>().SetAsync(x => x.Port, port);

    public async Task<bool> IsConfiguredAsync()
        => await settings.For<MailSettings>().ExistsAsync(allProperties: true);
}
```

| Method | Description |
|---|---|
| `GetAsync(refreshCache, ct)` | Returns the full settings object |
| `GetAsync(selector, refreshCache, ct)` | Returns one property |
| `SetAsync(model, ct)` | Persists the full object — validates, encrypts, audits, notifies |
| `SetAsync(selector, value, ct)` | Updates a single property |
| `ExistsAsync(allProperties, ct)` | Checks row existence |
| `ClearAsync(ct)` | Deletes all settings for the group |
| `GetVersionAsync(ct)` | Current revision, for conditional writes |
| `SetAsync(model, expectedVersion, ct)` | Persists only if the group is still at that revision |

> **Each read returns its own object.** `GetAsync` materialises a fresh instance every time,
> even on a cache hit — the cache holds the stored rows, not the model. Assigning to a property
> of what you read changes nothing anyone else sees; persist it with `SetAsync`. See
> [Caching](caching.md).

> **There is no synchronous read.** A blocking `Get()` would park a thread-pool thread on
> database I/O, and under load that starves the pool for the whole application — not just for
> settings. Where a value is needed inside a synchronous lambda, read it once with `await`
> beforehand and capture it; that is both correct and cheaper than resolving it per element.

## Concurrent writes

`SetAsync` writes **only the properties whose values differ from what is stored**, and each of
those writes is conditional on an optimistic concurrency token (`Setting.RowVersion`). Every
provider enforces it: SQL Server, PostgreSQL, SQLite and MongoDB. A write whose token no longer
matches throws `SettingConcurrencyException` rather than silently overwriting.

```csharp
try
{
    await settings.For<MailSettings>().SetAsync(model);
}
catch (SettingConcurrencyException ex)
{
    // ex.Group / ex.Key name the property that moved. Re-read, re-apply, retry.
}
```

### Across a read-edit-write cycle

Per-row tokens cannot, on their own, protect an edit made *by your application* — a settings
POCO carries no record of the revision it was loaded at, so a stale copy of a field is
indistinguishable from a deliberate edit. The revision therefore has to travel out to the
caller and back.

Over HTTP that is an entity tag, and the generated endpoints do it for you:

```http
GET /api/settings/mail-server
200 OK
ETag: "9f2c1a7b3e5d0148"

POST /api/settings/mail-server
If-Match: "9f2c1a7b3e5d0148"
→ 204 No Content     if the group is still at that revision
→ 412 Precondition Failed   if someone saved in between
```

A POST without `If-Match` writes unconditionally, so existing clients keep working. Once every
client round-trips the tag, make it mandatory — a POST without the header is then rejected with
`428 Precondition Required`:

```csharp
app.MapSettingsEndpoints(requireIfMatch: true);
```

Programmatic callers get the same thing through the accessor:

```csharp
var version = await settings.For<MailSettings>().GetVersionAsync();
var model   = await settings.For<MailSettings>().GetAsync();
model.Host  = "smtp.new.example.com";
await settings.For<MailSettings>().SetAsync(model, version);   // throws if the group moved
```

> The check runs against the same snapshot the writes are built from, and the per-row tokens
> still guard the individual UPDATEs — so there is no window between checking the version and
> applying the change.

The bundled dashboard does this already: it keeps the `ETag` from the load and sends it on
save. When the API refuses with `412` it does **not** discard your edits — it shows what
happened and offers two ways out:

- **Re-apply my changes** — re-reads the group, keeps the other writer's edits to fields you did
  not touch, replays only your own on top, and saves against the fresh revision.
- **Discard mine and reload** — throws your edits away and starts from the current values.

Because the SPA reaches the API through its own Nitro proxy, the `ETag` is same-origin and
readable from JavaScript without `Access-Control-Expose-Headers`.

