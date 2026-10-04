# DotNetBoost.Settings documentation

New here? Start with the [5-minute quick start](../README.md#get-started-in-5-minutes).

**Setting up**
- [Defining settings](defining-settings.md): `[SettingGroup]`, group names, default values
- [Storage providers](storage-providers.md): EF Core, Dapper, MongoDB
- [Reading and writing settings](reading-and-writing.md): the accessor API, concurrent writes

**Features**
- [Encrypting sensitive values](encryption.md): `[Sensitive]`, AES-256-GCM, key rotation
- [Validation](validation.md): Data Annotations and FluentValidation
- [Change notifications](change-notifications.md): run code when a setting changes
- [Write notifications](write-notifications.md): observe every write — what changed, by whom
- [Caching](caching.md): cache duration, Redis for multi-server deployments
- [REST API endpoints](rest-api.md): generated endpoints and **how to secure them**
- [Discovery endpoints](discovery-endpoints.md): list the groups, describe one's properties and constraints

**Tooling**
- [Dashboard (SPA client)](dashboard.md): a web UI for editing settings
- [Running everything with .NET Aspire](aspire.md): the full demo stack in one command

**Reference**
- [Configuration reference](configuration-reference.md): every `AddSettings()` builder method
- [Architecture](architecture.md): how the pieces fit, repo layout, running the tests
