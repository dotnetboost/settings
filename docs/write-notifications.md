# Write notifications

**Auditing is not built into this library.** It used to be, and the implementation could not be
frozen into 1.0: it recorded no actor, recorded nothing at all for a group clear, wrote outside
the write's own transaction, could fail a write that had already committed, exposed an unbounded
history endpoint, and existed only for Entity Framework Core — the Dapper bootstrap created a
`SettingAudits` table that nothing ever wrote to.

What the library does instead is report every write, through two hooks, and let you decide
what to do with it. A general `DotNetBoost.Auditing` package will implement recording over them,
the same way `DotNetBoost.Settings.FluentValidation` implements `ISettingValidator` — port here,
implementation in a package of its own.

| | `ISettingWriteParticipant` | `ISettingWriteObserver` |
|---|---|---|
| Runs | Inside the write, before the store saves | After the write has committed |
| An exception | Aborts the write | Is logged, counted and swallowed |
| Commits with the write | Yes, what it enlists | No — it runs after |
| Providers | Entity Framework Core | All |
| Good for | An audit trail: anything that must not be lost | Cache signals, webhooks, indexing, logging |

Both receive the same `SettingWrite` for a given write, so an audit entry and a log line share a
correlation id.

## `ISettingWriteParticipant`

Called once inside every write to any settings group, before the store saves it. What it
**enlists** is committed by the store's own save, in the same transaction as the rows, so the
two commit together or not at all.

```csharp
public sealed class SettingAuditParticipant(EfCoreSettingWriteScope scope) : ISettingWriteParticipant
{
    public Task OnWritingAsync(SettingWrite write, CancellationToken ct = default)
    {
        scope.Add(new SettingAuditEntry
        {
            Group         = write.GroupName,
            Kind          = write.Kind.ToString(),
            Actor         = write.Actor,
            CorrelationId = write.CorrelationId,
            Changes       = JsonSerializer.Serialize(write.Changes),
            OccurredAt    = write.OccurredAt
        });
        return Task.CompletedTask;
    }
}
```

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseWriteParticipant<SettingAuditParticipant>()
    .Build();
```

`SettingAuditEntry` here is your own entity, mapped on `AppDbContext`, the same context the
settings are on.

### Enlist, never save

- **Add through `EfCoreSettingWriteScope`**, not through a context of your own. The scope wraps
  the exact context instance the settings store saves, so what you add goes out in its save. An
  entity added to any other context would never be saved, and nothing would tell you. An entity
  whose type the settings context does not map throws on `Add`, which aborts the write.
- **Do not call `SaveChangesAsync`**, and do nothing with an effect outside the context — no
  messages, no HTTP calls. The write can still fail after every participant has run, on a
  concurrency conflict for one, and only what was enlisted is undone. Work that should happen
  only once the write has committed belongs in an observer.
- **Enlist standalone entities.** If the write does not commit, each entity you added is
  detached again, so the application's next `SaveChangesAsync` on the same context cannot save
  it. Related entities reached only through a navigation are not detached with it.

### When a participant fails

**A participant's exception aborts the write**, and reaches the caller unchanged. The store is
not called, the cached copy of the group stays, no observer or change handler runs, and what
earlier participants enlisted is discarded. Participants run in registration order, and the
first to throw stops the rest.

The same happens when the store itself fails after the participants ran: a
`SettingConcurrencyException` discards what they enlisted, and the store takes back its own
rows, so nothing from the failed write is left tracked on the context.

### What the store does with it

A settings write calls `SaveChangesAsync` on the shared context, which saves everything tracked
there — your application's own pending changes included. That has always been true of the
Entity Framework Core store; a participant is what makes it useful.

A clear is the one write that does not normally go through the change tracker: it is a single
`ExecuteDeleteAsync`. When something was enlisted, the store runs the delete and a
`SaveChangesAsync` in one transaction instead:

- inside **your transaction**, when the context has one, which you then commit or roll back;
- otherwise in **its own**, opened inside the context's execution strategy, so it works under
  `EnableRetryOnFailure`.

Inside your own transaction, participants and observers have already run by the time you roll
back: an observer can be told about a write your rollback then undid. What a participant
enlisted is undone with everything else.

### Which providers accept participants

Only Entity Framework Core. Dapper opens a transaction of its own inside each write, and MongoDB
uses none, so neither can commit something a participant enlists together with the rows.
`Build()` refuses a participant for either, rather than run it with no atomicity — the mistake
the old Dapper audit table made. A custom store opts in by implementing `IEnlistingSettingStore`
and declaring it through `SettingBuilderGuard.EnsureProviderNotConfigured(builder, name,
supportsWriteParticipants: true)`.

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
| `Changes` | One `SettingChange` per property that actually changed, or per row a clear deleted |
| `Actor` | Who made the change, or `null` for "not captured" |
| `CorrelationId` | One value per write, so the several changes of one save are identifiable as one edit |
| `OccurredAt` | UTC timestamp, taken just before the store is called |
| `GroupType` | The settings class, for reading its attributes |

Identify a group by `GroupName`, not by `GroupType`'s name. Moving the class to another
namespace changes its full name but not the rows it reads, so anything keyed on the type name
would split one group's history in two.

Each `SettingChange` is `(PropertyName, OldValue, NewValue, IsRedacted)`. `OldValue` is `null`
when the property had never been stored.

A clear lists what it deleted: one entry per stored row, with the value it held as `OldValue` and
`NewValue` `null`. It reads the group first to do that, but only when a participant or observer
is registered. A row written between that read and the delete is deleted without being listed.

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

So an observer is the wrong place for anything that must not be lost; that is what
[a participant](#isettingwriteparticipant) is for. An observer's failures are counted
rather than surfaced: each one also adds 1 to a counter on the `DotNetBoost.Settings` meter, so
a rate of them shows up on a dashboard instead of only in the logs.

| Instrument | Counts | Tags |
|---|---|---|
| `dotnetboost.settings.write_observer.failures` | Write observers that threw | `dotnetboost.settings.observer`, `dotnetboost.settings.group`, `error.type` |
| `dotnetboost.settings.change_handler.failures` | [Change handlers](change-notifications.md) that threw | `dotnetboost.settings.handler`, `dotnetboost.settings.group`, `error.type` |

The observer and handler tags are the implementation's class name, as in the log line. Every
tag is bounded by your own types, never by a value. To export them with OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("DotNetBoost.Settings"));
```

The meter is created through the container's `IMeterFactory`, which every generic host
registers, so each host — a test host included — gets its own.

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
real history, implement an [`ISettingWriteParticipant`](#isettingwriteparticipant) that records
into your own table.

## Upgrading from `1.0.0-preview.1`

`ISettingAuditStore`, `SettingAuditEntry`, `EfCoreAuditStore`, `UseAuditStore<T>()` and
`GET /api/settings/{route}/audit` are gone, along with the `SettingAudits` table in the EF model
and the Dapper bootstrap. Implement `ISettingWriteObserver`, or call `.UseLoggingWriteObserver()`
in the meantime.

Nothing drops an existing `SettingAudits` table for you — your rows are still there. Drop it
yourself once you no longer want them.
