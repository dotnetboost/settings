# Contributing to DotNetBoost.Settings

Thanks for considering a contribution! This guide covers everything you need to get started.

## Repository layout

```
dotnetboost/
├── src/                          # Library projects (each ships as its own NuGet package)
│   ├── DotNetBoost.Settings.Core
│   ├── DotNetBoost.Settings.EntityFrameworkCore
│   ├── DotNetBoost.Settings.Dapper
│   ├── DotNetBoost.Settings.MongoDb
│   ├── DotNetBoost.Settings.FluentValidation
│   └── DotNetBoost.Settings.API
├── tests/
│   ├── DotNetBoost.Settings.UnitTests
│   ├── DotNetBoost.Settings.ProviderTests
│   └── DotNetBoost.Settings.IntegrationTests   # Testcontainers, needs Docker
├── samples/
│   └── SampleApp                 # Runnable ASP.NET Core demo
└── docs/                         # Additional documentation
```

## Prerequisites

- .NET 10 SDK (install via `dotnet-install` or your OS package manager)
- .NET 8 runtime — the libraries and test projects multi-target `net8.0`, so the net8.0 test
  pass needs it. The .NET 10 SDK compiles `net8.0` without it, but cannot run those tests.
- Docker — required for the integration tests, which start real PostgreSQL and
  MongoDB servers via Testcontainers. Any Docker-compatible runtime works
  (Docker Desktop, OrbStack, Colima, Podman).

## Getting started

```bash
git clone https://github.com/dotnetboost/settings.git
cd settings
dotnet restore
dotnet build
dotnet test
```

The last command runs all three suites. The integration tests need a running Docker
daemon; without one they fail rather than silently skipping. To run everything else:

```bash
dotnet test --filter "Category!=Integration"
```

## Making a change

1. Fork the repo and create a branch: `git checkout -b feature/my-change`
2. Make your change. Follow the existing code style (`.editorconfig` is enforced).
3. Add or update tests — PRs without test coverage for new behaviour will be asked to add it.
4. Run the full test suite: `dotnet test` (or `--filter "Category!=Integration"` without Docker)
5. Update `README.md` if you changed public API surface.
6. Open a pull request against `develop`. The CI pipeline runs automatically.

## Coding guidelines

- Nullable reference types are enabled solution-wide — don't suppress warnings without a comment explaining why.
- Public APIs need XML doc comments.
- Prefer `ConfigureAwait(false)` in library code (not required in the sample app).
- New store providers must inherit `SettingStoreContractTests` in `DotNetBoost.Settings.ProviderTests` — this guarantees behavioural parity across backends.
- Don't introduce breaking changes to `ISettingStore`, `ISettingAccessor<T>`, or `ISettingManager` without discussing in an issue first — these are implemented by external provider authors.
- Every package ships under one version, set once as `<Version>` in the root `Directory.Build.props`. Bump it there — never in an individual `.csproj`. The packages depend on each other, so versions drifting apart would produce a release whose provider package asks for a `Core` that was never published. Releases override it anyway: CI derives the published version from the git tag and passes `-p:PackageVersion=…`.

## Adding a new provider

1. Create `src/DotNetBoost.Settings.YourProvider/`
2. Implement `ISettingStore`
3. Add a `Use{Provider}()` extension method on `SettingBuilder`, guarded with `SettingBuilderGuard.EnsureProviderNotConfigured`
4. Add a contract test class in `tests/DotNetBoost.Settings.ProviderTests` inheriting `SettingStoreContractTests`,
   and — if the backend is a real server — a Testcontainers-backed class in
   `tests/DotNetBoost.Settings.IntegrationTests` inheriting the same contract
5. Document it in the main `README.md`

## Releasing

Releases are cut by pushing a `v*.*.*` tag; CI derives the package version from it. Publishing
to nuget.org uses **Trusted Publishing** rather than a stored API key: the workflow exchanges a
GitHub OIDC token for a credential that lives one hour and is single-use, so there is no
long-lived secret to leak or rotate.

One-time setup on nuget.org (Account → Trusted Publishing → new policy):

| Field | Value |
|---|---|
| Repository Owner | `dotnetboost` |
| Repository | `settings` |
| Workflow File | `ci.yml` — filename only, no `.github/workflows/` prefix |
| Environment | `nuget-release` |

Then add `NUGET_USER` as a repository secret: your nuget.org **profile name**, not your email.
It is an identifier rather than a credential, but keeping it in secrets avoids baking a username
into the workflow.

Scoping the policy to the `nuget-release` environment is what makes that environment a real
gate — add required reviewers to it and a publish cannot happen without an approval, because
the credential is only issued inside it.

## Reporting bugs

Use the [Bug Report template](.github/ISSUE_TEMPLATE/bug_report.md). Include a minimal repro where possible.

## Proposing features

Use the [Feature Request template](.github/ISSUE_TEMPLATE/feature_request.md) — please discuss significant API changes in an issue before submitting a PR.

## Code of Conduct

Be respectful. We follow the [Contributor Covenant](https://www.contributor-covenant.org/).
