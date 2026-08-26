# DotNetBoost.Settings — Dashboard

A [Nuxt 4](https://nuxt.com) single-page client for the REST endpoints that
`DotNetBoost.Settings.API` generates. A sidebar links to **Settings**, where a top menu
holds one entry per `[SettingGroup]` class — each one reads and writes its own group.

Built on the [Nuxt UI dashboard template](https://github.com/nuxt-ui-templates/dashboard)
(collapsible sidebar, command palette, light/dark mode, theme picker).

## Setup

```bash
npm install
```

## Development

The dashboard needs the settings API running. From the repository root:

```bash
dotnet run --project samples/SampleApp --urls http://localhost:5199
```

Then, in `clients/dashboard`:

```bash
npm run dev
```

The dashboard is at `http://localhost:3000`.

## Configuration

Copy `.env.example` to `.env` to point the dashboard at a different API.

| Variable | Default | Purpose |
|---|---|---|
| `NUXT_SETTINGS_API_URL` | `http://localhost:5199` | Base address of the settings API |
| `NUXT_PUBLIC_API_REFERENCE_URL` | `http://localhost:5199/scalar` | Target of the sidebar's "API reference" link |

The browser never calls the API directly: `server/api/settings/[...path].ts` proxies
`/api/settings/**` through Nitro to `NUXT_SETTINGS_API_URL`. That keeps the API free of
CORS configuration and makes the target a deploy-time setting rather than a build-time one.

## How it maps to the API

| File | Role |
|---|---|
| `app/utils/settings.ts` | The group registry — `route` must match `[SettingGroup("…")]` |
| `app/composables/useSettingsGroup.ts` | Load, dirty-tracking, save, and server-error mapping for one group |
| `app/components/settings/GroupForm.vue` | Renders the form for a group |
| `app/pages/settings.vue` | Top menu, one entry per group |
| `app/pages/settings/index.vue`, `payment.vue` | The group pages themselves |
| `server/api/settings/[...path].ts` | Proxy to the .NET API |

Form fields are generated from whatever `GET api/settings/{route}` returns, so a property
added to a C# settings class appears without any change here — `settings.ts` only supplies
labels, help text, and input constraints. Booleans render as switches, numbers as numeric
inputs, and `[Sensitive]` properties as masked inputs with a reveal toggle.

`POST` replaces the whole group, so every property is sent on save. A rejected write comes
back as `ValidationProblemDetails` and each message is attached to the property it names.

## Adding a settings group

1. Add an entry to `settingsGroups` in `app/utils/settings.ts`, with `route` matching the
   `[SettingGroup("…")]` attribute on the C# class.
2. Add a page under `app/pages/settings/` that renders `<SettingsGroupForm :group="…" />`.

The sidebar and the top menu both build from the registry, so no other file needs to change.

## Production

```bash
npm run build
node .output/server/index.mjs
```

`npm run lint` and `npm run typecheck` cover the project.
