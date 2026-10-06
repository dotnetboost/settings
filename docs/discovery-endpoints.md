# Discovery: listing groups and describing them

Two endpoints let a client find the settings groups and learn what is in one, so a form can be
generated from the class rather than from guesswork about the shape of a JSON response.

| Method | Route | Returns |
|---|---|---|
| `GET` | `/api/settings` | The registered groups |
| `GET` | `/api/settings/{route}/schema` | One group's properties |

**Neither returns a stored value.** That is what `GET /api/settings/{route}` is for, and keeping
these two free of values is what makes them safe to expose more widely than values are.

Both are registered by `MapSettingsEndpoints()` by default. Turn them off with:

```csharp
app.MapSettingsEndpoints(requireIfMatch: false, includeDiscovery: false);
```

## `GET /api/settings`

```json
[
  { "route": "mail-server", "name": "MailSettings",    "requiresAuthorization": false },
  { "route": "payment",     "name": "PaymentSettings", "requiresAuthorization": true  }
]
```

`name` is the persistence key — the `Name` on `[SettingGroup]`, or the class name — which is not
necessarily the route. `requiresAuthorization` says whether the class carries `[Authorize]`.

This endpoint is anonymous. What it reveals is the inventory of route segments and persistence
keys, which the per-group endpoints — anonymous by default, and named after your classes —
already make guessable. If you would rather not publish the list anyway, turn discovery off.

## `GET /api/settings/{route}/schema`

```json
{
  "route": "mail-server",
  "name": "MailSettings",
  "properties": [
    { "name": "host",     "type": "String",  "nullable": false, "default": "smtp.example.com",
      "sensitive": false, "constraints": { "required": true, "minLength": 3, "maxLength": 255 } },
    { "name": "port",     "type": "Int32",   "nullable": false, "default": 587,
      "sensitive": false, "constraints": { "min": 1, "max": 65535 } },
    { "name": "password", "type": "String",  "nullable": false, "default": null,
      "sensitive": true,  "constraints": null }
  ]
}
```

- **`name`** is spelled the way the `GET` body spells it, so a client can match the two up
  without guessing at a casing convention.
- **`default`** is what a read falls back to when nothing is stored: the `[SettingDefault]` value
  when there is one, otherwise whatever the class initialises the property to. It is a
  compile-time fact about the class, never anything read from the store.
- **`default` is omitted for a `[Sensitive]` property.** A compile-time default on a secret is a
  value like any other, and this endpoint is meant to be safe to expose more widely than values
  are. You still get `sensitive: true`, which is what a form needs in order to render a password
  field.
- The properties listed are exactly the ones the engine stores — public, with a getter and a
  setter. The schema and the engine read that rule from the same place, so a property cannot
  appear here that a write would ignore.

The schema endpoint sits inside its group, so it is covered by the group's `[Authorize]` exactly
as `GET` and `POST` are. A group's endpoints should not disagree about who may look at it.

## Publishing validation constraints

`constraints` is the valuable part for a generated form, and `ISettingValidator` cannot supply
it: it answers `ValidateAsync` and `CanValidate`, so it can reject a value but has no way to say
what it would accept. Without somewhere to publish rules, every client hardcodes its own — and a
bound that lives only in a dashboard is enforced nowhere.

**With `UseFluentValidation(assembly)` you get this for free.** The same registration that wires
up your validators also registers a contributor that reads their rules out of FluentValidation's
own descriptor:

| Rule | Published as |
|---|---|
| `NotEmpty()` / `NotNull()` | `required: true` |
| `InclusiveBetween(a, b)` | `min`, `max` |
| `GreaterThanOrEqualTo(n)` / `LessThanOrEqualTo(n)` | `min` / `max` |
| `GreaterThan(n)` / `LessThan(n)` | `exclusiveMin` / `exclusiveMax` |
| `Length(a, b)`, `MinimumLength`, `MaximumLength` | `minLength`, `maxLength` |
| `Matches(pattern)` | `pattern` |

A rule it cannot read — a `Must(...)`, a custom validator, a comparison against another property
— is **skipped rather than guessed at**. A wrong published bound is worse than a missing one,
because a client would believe it.

For anything else, implement `ISettingSchemaContributor`:

```csharp
public sealed class MySchemaContributor : ISettingSchemaContributor
{
    public bool CanDescribe(Type type) => type == typeof(BrandingSettings);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Describe(Type type)
        => new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["HeaderLogoSize"] = new Dictionary<string, object?> { ["min"] = 16, ["max"] = 64 }
        };
}
```

Keys are property names as spelled on the class. Constraint names are conventional rather than
closed — `required`, `min`, `max`, `exclusiveMin`, `exclusiveMax`, `minLength`, `maxLength`,
`pattern` — so you can describe a rule this library has never heard of.

### Several contributors compose

**Every** contributor whose `CanDescribe` returns `true` is consulted, and their constraints are
merged per property. Constraints are additive facts about a property, not a decision, so
describing different aspects of one group is the normal case — one contributor publishing the
FluentValidation rules while another publishes something this library cannot know about, such as
a rule carried by an attribute from a different package:

```csharp
builder.Services.AddSettings()
    .UseFluentValidation(assembly)   // min, max, pattern, required
    .Build();

builder.Services.AddSingleton<ISettingSchemaContributor, MultiLingualContributor>();  // translated
```

Both survive; neither deletes the other.

(This is deliberately unlike `ISettingValidator`, where the first match wins. Running two
validators over one property risks rejecting or reporting a value twice — but there is nothing
wrong with two contributors each naming a different fact about it.)

On a collision — the same constraint name on the same property from two contributors — **the
first registered wins** and the clash is logged at `Warning`, naming both contributors, the
group, the property and the constraint. Resolving it quietly would make the published schema
depend on registration order.

With none registered, every property reports `constraints: null` and the endpoint is otherwise
unchanged. A contributor that throws costs its own constraints — not the response, and not the
other contributors' constraints.

> **Publishing a constraint changes nothing about enforcement.** The validator is still what
> rejects a bad write. The schema exists so a client can show the same bounds the server will
> apply, instead of inventing its own.

## See also

- [REST API endpoints](rest-api.md) — the read and write endpoints, and how to secure them
- [Dashboard](dashboard.md) — the SPA client these endpoints are for
- [Validation](validation.md) — defining the rules in the first place
