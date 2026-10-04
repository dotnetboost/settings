# Write notifications

**Auditing is not built into this library.** It used to be, and the implementation could not be
frozen into 1.0: it recorded no actor, recorded nothing at all for a group clear, wrote outside
the write's own transaction, could fail a write that had already committed, exposed an unbounded
history endpoint, and existed only for Entity Framework Core — the Dapper bootstrap created a
`SettingAudits` table that nothing ever wrote to.

What the library does instead is report every write, and let you decide what to do with it.
A general `DotNetBoost.Auditing` package will implement recording over this hook, the same way
`DotNetBoost.Settings.FluentValidation` implements `ISettingValidator` — port here,
implementation in a package of its own.

## `ISettingWriteObserver`

Called once after every completed write to any settings group.

```csharp
public sealed class WriteForwarder(IMessageBus bus) : ISettingWriteObserver
{
    public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
        => bus.PublishAsync(write, ct);
}
```

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseWriteObserver<WriteForwarder>()
    .Build();
```

Several observers can be registered; all of them run, in registration order.

### What a `SettingWrite` carries

| Member | |
|---|---|
| `GroupName` | The persistence key the rows are stored under |
| `Route` | The group's API route segment |
| `Kind` | `Updated`, or `Cleared` for `ClearAsync` |
| `Changes` | One `SettingChange` per property that actually changed; empty when `Cleared` |
| `Actor` | Who made the change, or `null` for "not captured" |
| `CorrelationId` | One value per write, so the several changes of one save are identifiable as one edit |
| `OccurredAt` | UTC timestamp |

Each `SettingChange` is `(PropertyName, OldValue, NewValue, IsRedacted)`. `OldValue` is `null`
when the property had never been stored.

A write that changes nothing raises no notification at all. Change detection compares
`[Sensitive]` values as plaintext rather than as stored ciphertext: AES-GCM draws a fresh nonce
on every call, so an unchanged secret re-encrypts to different bytes each save and a ciphertext
comparison would report a change every time.

### `[Sensitive]` properties are redacted, not reported

A `[Sensitive]` property arrives with `IsRedacted` set and **both values `null`**. The library
decides what is sensitive; an observer only learns that it changed. This is not configurable,
and that is the point — an observer is precisely the component that forwards writes somewhere
else, so it should not be the place a secret escapes.

Redaction follows the `[Sensitive]` attribute, not whether a value happened to be encrypted at
rest, so it holds in an application with no encryptor configured.

### Capturing the actor

`Actor` is `null` until you register an accessor, and `null` means *not captured* — never read
it as a claim about who acted. ASP.NET applications want the one from the API package:

```csharp
settings.UseHttpContextActor();   // DotNetBoost.Settings.API
```

It reads `ClaimTypes.NameIdentifier`, then `Identity.Name`, and returns `null` outside a request
or for an unauthenticated one. Implement `ISettingActorAccessor` for anything else — a background
job's name, a CLI user, a tenant id.

### Failure policy

**An observer's exception is logged and swallowed.** An observer runs after the store write has
committed and the cache entry has been evicted, so failing the call would report a write that in
fact succeeded. Each observer is wrapped on its own, so one failure does not skip the rest.

This is the same policy as [`ISettingChangedHandler<T>`](change-notifications.md) and
deliberately not the one for [`ISettingProjector<T>`](rest-api.md#reshaping-what-get-returns),
which *can* fail a request — a failed notification must not break a committed write, while a
failed projection would otherwise return a wrong response.

## The stopgap: logging writes

`LoggingSettingWriteObserver` ships in Core so the hook is not an empty port:

```csharp
settings.UseLoggingWriteObserver();
```

One log line per write, at `Information`, naming the group, the kind, the actor, the correlation
id and **the properties that changed — never their values**, redacted or not. A settings value
is exactly the kind of thing that should not reach a log aggregator because a default turned it
on. It is off unless you ask for it.

This is a stopgap, not a trail: there is nothing to query, and nothing enforces retention. For a
real history, implement `ISettingWriteObserver` over your own store.

## Upgrading from `1.0.0-preview.1`

`ISettingAuditStore`, `SettingAuditEntry`, `EfCoreAuditStore`, `UseAuditStore<T>()` and
`GET /api/settings/{route}/audit` are gone, along with the `SettingAudits` table in the EF model
and the Dapper bootstrap. Implement `ISettingWriteObserver`, or call `.UseLoggingWriteObserver()`
in the meantime.

Nothing drops an existing `SettingAudits` table for you — your rows are still there. Drop it
yourself once you no longer want them.
