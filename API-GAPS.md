# DotNetBoost.Settings — API gaps found by the sample app

**Part 1 of 4.** Source: a review of `dotnetboost-sample`, which installs Settings, Media and
MultiLingual side by side and composes them in application code only.

Global order across all four repos:

| | Repo | Items | Blocks |
|---|---|---|---|
| **P0** | sample | shared-image delete bug | nothing |
| **P0** | **this repo** | **cached mutable instance** | nothing |
| P1 | multilingual-v3 | `For(name, id)`, `MapTranslationEndpoints` | sample cleanup |
| P1 | media | bulk owner reads | sample cleanup |
| P2 | **this repo** | **`ISettingProjector<T>`** | sample `/api/branding` |
| P2 | media + multilingual | addressing version marker | sample tests |
| P3 | all three | marker-interface ceremony | sample `AppDbContext` |
| P4 | sample | delete the glue | needs P1/P2 packaged |

This repo owns **P0 #1**, **P2 #2**, and two P3 items. Nothing here depends on the other repos.

---

## P0 — `GetAsync` hands out the cached instance

`src/DotNetBoost.Settings.Core/Services/SettingManager.cs:86`

```csharp
var model = MapToModel<T>(rows);
_cache.Set(key, model, CacheDuration);
return model;
```

Every caller of `For<T>().GetAsync()` receives **the same mutable object**. Assigning to any
property of it writes into the entry every later reader of that group is served from — including
`GET /api/settings/{group}`, which then returns the mutated value as though it were stored.

This is a library defect, not a sample quirk: it affects every consumer. The sample currently
guards one assignment with a 10-line comment and an integration test
(`BrandingEndpoints.cs`, `Integration/BrandingCompositionTests.cs`), which is a warning label
where a fix belongs.

**Fix.** Cache the rows (or a serialized form) and materialise a fresh model per read. The cache's
value is skipping the store round trip, not the mapping — `MapToModel` is reflection over a handful
of properties.

Alternatives considered and rejected: returning a defensive copy (same cost, keeps the odd
invariant that the cached object is special); documenting the instance as frozen (a convention the
compiler cannot hold).

**Acceptance.** A test that reads a group, mutates a property, re-reads, and asserts the second
read is unaffected. Then delete the sample's guard test and comment.

---

## P2 — a read-path projection hook

`GET /api/settings/branding` returns the raw stored `StoreName`. The sample needs it in the
request's language, so it built a whole parallel endpoint (`BrandingEndpoints.cs`, 138 lines) and a
`BrandingText.vue` component, and left the Settings endpoint returning a value the UI never shows.

Settings does not need to know MultiLingual exists. It needs a hook:

```csharp
namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Shapes a settings group for the READ path only. Registered in DI; applied by
/// MapSettingsEndpoints on GET.
/// </summary>
public interface ISettingProjector<T> where T : new()
{
    Task<object> ProjectAsync(T group, CancellationToken ct = default);
}
```

**Constraints that matter:**

- Read path only. Never applied on POST/PUT — a projector must not be able to influence what is
  stored.
- **Never applied to the ETag.** `GetVersionAsync` must keep covering stored values, or conditional
  writes silently stop protecting anything. This is the one easy way to get this feature wrong.
- No projector registered = today's behaviour, byte for byte.

**What it buys the sample.** One ~8-line projector replaces the composition half of
`/api/branding`; the endpoint shrinks to the media lookup, or disappears.

---

## P3 — two small ergonomics items

**Endpoint tags.** `MapSettingsEndpoints` does not tag its routes, while Media and MultiLingual both
tag theirs. Every consumer therefore writes what the sample writes at `Program.cs:135`:

```csharp
app.MapGroup("").WithTags("Settings").MapSettingsEndpoints();
```

An empty prefix purely to carry a tag. Tag them in
`src/DotNetBoost.Settings.API/SettingsMinimalApiExtensions.cs` and that line becomes
`app.MapSettingsEndpoints()`.

**`ApplySettingsConfiguration` requires a provider the other two infer.**
`src/DotNetBoost.Settings.EntityFrameworkCore/EfCore.cs:104`

```csharp
modelBuilder.ApplySettingsConfiguration(DatabaseProvider.PostgreSql);  // Settings
modelBuilder.ApplyMedia();                                             // Media
modelBuilder.ApplyMultiLingual(this);                                  // MultiLingual
```

Add an overload taking the context (or read `ProviderName` off the model) so all three are
consistent. Keep the explicit one for anyone who needs to override.

---

## P3 — `ISettingDbContext` is the only real marker interface left

Verified across all three libraries:

- **Media**: `UseEntityFrameworkCore<TContext> where TContext : DbContext` — no interface constraint
  at all — and `EfCoreMediaStore(DbContext)` uses `context.Set<MediaFile>()`. `IMediaDbContext` is
  already dead.
- **MultiLingual**: constrains on the interface, but `EfCoreTranslationStore` uses
  `context.Set<Translation>()`. The constraint is removable today.
- **Settings**: genuinely uses `db.Settings` / `db.SettingAudits`
  (`EfCore.cs:121,129,148,171,191,201,209,220,228`). **This repo is the one that needs a change.**

Switch `EfCoreSettingStore` and `EfCoreAuditStore` to take `DbContext` and use `Set<Setting>()` /
`Set<SettingAuditEntry>()`, drop the constraint, and keep `ISettingDbContext` as an obsolete no-op
for one release.

**Why it is worth doing.** A consuming `AppDbContext` currently implements three interfaces and
declares six `DbSet` properties the libraries mostly never read. With all three done, registration
is three `Apply*` calls in `OnModelCreating` and nothing else — a better demonstration of library
independence than a class implementing three interfaces to prove it.

---

## Packaging

The sample restores `1.0.0-preview.1` from nuget.org and has no `nuget.config`. When the items above
land, pack a `preview.2` so the sample can consume it:

```bash
dotnet pack -c Release -o /Users/abuhattem/dev/.local-nuget /p:Version=1.0.0-preview.2
```

The sample repo's own brief covers wiring that folder up as a feed.
