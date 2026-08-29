# Changelog

All notable changes to this project are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [1.0.0] — Unreleased

### Added
- Multi-targets **`net8.0`** (LTS) and **`net10.0`**. On `net8.0` the EF Core provider resolves the EF Core 8.x family; on `net10.0`, 10.x.
- **Change notifications**: `ISettingChangedHandler<T>`, registered via `.OnChanged<T, THandler>()`. Fired after every successful `SetAsync`.
- **Encryption at rest**: `[Sensitive]` attribute + `ISettingEncryptor`. Ships with `AesSettingEncryptor` (AES-256-GCM). Register via `.UseAesEncryption(key)` or `.UseCustomEncryption<T>()`.
- **Audit trail**: `ISettingAuditStore` records every change (old value, new value, timestamp). EF Core implementation (`EfCoreAuditStore`) included. Exposed via `GET /api/settings/{route}/audit`.
- **Validation enforced everywhere**: `SetAsync` now runs registered `ISettingValidator`s (previously only the REST API validated). Throws `SettingValidationException` on failure.
- **`[SettingDefault]` attribute**: specify a fallback value used when no row exists in the store, instead of the CLR default.
- **Dashboard**: Nuxt 4 single-page client in `clients/dashboard`, built on the Nuxt UI dashboard template — sidebar navigation plus a Settings page whose top menu reads and writes one settings group per entry, through the generated REST endpoints. Nitro proxies the API, so no CORS setup is needed.
- **.NET Aspire orchestration**: `aspire/DotNetBoost.Settings.AppHost` starts the API, the Nuxt dashboard, PostgreSQL, Redis, pgweb and RedisInsight from one `dotnet run` (or `aspire run` with the optional CLI), injecting every connection string and dashboard URL. Configuration for each storage provider (PostgreSQL, SQL Server, SQLite, MongoDB, Dapper) is written out in full, with one active and the rest commented.
- **`aspire/DotNetBoost.Settings.ServiceDefaults`**: OpenTelemetry traces/metrics/logs over OTLP, `/health` and `/alive` endpoints, service discovery, and resilient `HttpClient` defaults, shared by every orchestrated service.
- **`RedisSettingCache`** in the sample: a distributed `ISettingCache` over Redis, registered with `.UseCustomCache<T>()`, so a settings write is visible to every API instance immediately rather than after the local `IMemoryCache` entry expires.
- Repository reorganised into `src/`, `tests/`, `samples/`, `docs/` — standard OSS layout.
- `Directory.Build.props` / `Directory.Packages.props` for centralized build and package version management. The shipped version is declared once as `<Version>` in the root props rather than repeated in all six project files, so the packages cannot drift apart and publish a provider that depends on a `Core` version that was never released.
- CI runs unit, provider, API and integration tests on .NET 8 and .NET 10.
- Package icon — the DotNetBoost mark, so the NuGet listing shows the project rather than a grey placeholder. The full logo is kept in `assets/` for re-exporting other sizes.
- README documents that the generated endpoints are anonymous unless the settings class carries `[Authorize]`, and that `[Sensitive]` is encryption at rest rather than access control — an unsecured group returns its secrets decrypted. Authorization stays the consuming application's decision; the library supplies the mechanism.
- **Relational engines supported: SQL Server, PostgreSQL and SQLite.** MySQL is not supported in this release — the hand-written Dapper upsert was never valid MySQL (`SELECT ... WHERE NOT EXISTS` with no `FROM` clause) and no test ever executed it. Rather than ship an untested dialect, `DatabaseProvider.MySql` and the MySQL DDL have been removed.
- Integration tests now cover **SQL Server** (Testcontainers) for both the Dapper and EF Core stores, alongside the existing PostgreSQL and MongoDB suites — the SQL Server DDL and T-SQL upsert previously had no execution coverage at all.

### Changed
- Microsoft.Extensions.* and EF Core packages moved from 10.0.6 to 10.0.11 — the floor required by the Aspire 13.5 client integrations.
- The sample API now reads its AES key from `Settings:EncryptionKey` (supplied by the AppHost) instead of generating a throwaway one at startup, so values encrypted in one run stay readable in the next.
- `SettingManager` constructor now requires `IServiceProvider` and `ILogger<SettingManager>` (for validation, change notifications, and structured logging).
- `Setting` model gained `IsEncrypted` and `UpdatedBy` columns (migration required for existing databases).

### Fixed
- **Writes are now per-property and optimistically concurrent.** `SetAsync` persists only the properties that actually changed, and each write is conditional on a `RowVersion` token enforced by every provider (SQL Server, PostgreSQL, SQLite, MongoDB); a lost race throws `SettingConcurrencyException`. Previously every save rewrote the whole group unconditionally, and `RowVersion` was modelled but never used. Across an application-level read-edit-write cycle the revision travels as an HTTP entity tag: GET returns an `ETag`, POST honours `If-Match` and answers `412 Precondition Failed` on a lost race, and `MapSettingsEndpoints(requireIfMatch: true)` makes the header mandatory. Programmatic callers use `GetVersionAsync()` with the new `SetAsync(model, expectedVersion)` overload. The bundled dashboard round-trips the tag and, on a refused save, offers to re-apply your edits on top of the latest values rather than discarding either side's work.
- **AES key rotation no longer loses data silently.** Encrypted values now carry a key-id envelope (`v1:{keyId}:{base64}`), and `UseAesEncryption(primary, retired...)` accepts retired decrypt-only keys, so a rotation can be staged. Values written under the old format are still readable. Separately, a value that cannot be decrypted now throws `SettingDecryptionException` instead of silently leaving the property on its compile-time default — which previously meant a botched rotation left the application running on default credentials. Opt out with `IgnoreDecryptionFailures()`.
- **MongoDB no longer hijacks the application's Mongo client.** `UseMongoDb()` registered `IMongoClient` and `IMongoDatabase` as singletons; because the last registration wins, that silently replaced a client the host had configured itself — Aspire's, for instance, along with its telemetry and resilience settings. Neither type is registered any more: the provider holds its database privately, so the two cannot override one another in either order. A new `UseMongoDb(databaseFactory, createIndexes)` overload lets the store share a client the application already has. **Breaking:** applications that relied on `UseMongoDb()` to register `IMongoClient`/`IMongoDatabase` for their own use must now register them themselves.
- **MongoDB: the unique index is no longer created on every request.** `MongoSettingStore` is registered scoped, so its constructor issued a blocking `createIndex` per resolution. Index creation moved to `MongoSettingStore.EnsureIndexesAsync`, run once at startup by a hosted service (opt out with `UseMongoDb(..., createIndexes: false)`). The constructor now performs no I/O.
- Removed stray broken `using` referencing a nonexistent JS interop type in the Dapper store.
- **Removed `ISettingStore.GroupExistsAsync`.** No code path through `ISettingManager` ever called it — `ExistsAsync` needs a row *count* to compare against the property count, which a boolean cannot answer — so every provider author had to implement a method the engine never invoked. Its name also invited a dangerous misreading: it returns true when *any* row exists, so used as a "is this group configured" guard it passes on a half-saved group and hands back nulls. Use `ExistsAsync(allProperties: true)` for that; it is unaffected. **Breaking** for custom `ISettingStore` implementations, which simply drop the method.
- **Removed the synchronous `Get()` overloads from `ISettingAccessor<T>`.** They were sync-over-async — `GetAsync(...).GetAwaiter().GetResult()` — which parks a thread-pool thread on database I/O; under load that starves the pool for the whole application, not just for settings. An earlier entry claimed this had been fixed; it had not. Rather than paper over it, the methods are gone: `GetAsync` covers every call site, and a value needed inside a synchronous lambda should be read once with `await` beforehand and captured, which is also cheaper than resolving it per element. **Breaking**, and deliberately taken before 1.0 rather than after. A genuinely non-blocking synchronous read — cache-backed, in the shape of `IOptionsMonitor<T>.CurrentValue` — remains open as a feature.
- Fixed O(n²) lookup in `EfCoreSettingStore.UpsertManyAsync`.
- Fixed `EfCoreSettingStore.DeleteGroupAsync` loading all rows into memory before deleting.
- Fixed `Extensions.ConvertFrom` returning `""` instead of a culture-correct value for non-string primitives.
- Fixed case-sensitive setting key lookups causing silent data loss when casing didn't match exactly.
- Fixed duplicate FluentValidation validator registration when `UseFluentValidation` was called with an assembly containing multiple validators for the same type.

## [0.1.0] — Prior release

Initial release with EF Core, Dapper, and MongoDB providers, in-memory caching, and a minimal-API REST layer.
