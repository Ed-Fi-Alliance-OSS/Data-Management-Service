---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Resolve Secret References in Stored Connection Strings

## Description

This is the capability.
Everything before it builds the seam a plugin arrives through; this story is where an operator's database password stops being stored in the Configuration Service.

A stored connection string may carry `${secret:<name>}` tokens.
CMS stores the text exactly as any other connection string, encrypted, through the unchanged write path and validator.
When CMS reads the row, it decrypts, substitutes each token with what the plugin-supplied `ISecretResolver` returns, re-encrypts, and returns Base64.

DMS receives the shape it receives today and is not touched by this story at all.

Per `reference/design/secrets-DMS-1503/design.md` ("### The Secret Reference: Runtime-Data Secrets", "### Where Resolution Happens", "### Freshness, Caching, and the Tenant Set", "### Failure Semantics").

## Technical Implementation

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.
`Backend/`, `Backend.OpenIddict/`, `Backend.Postgresql/`, and `Backend.Mssql/` name the corresponding directories under `src/config/backend/EdFi.DmsConfigurationService.`.
`DataModel/` names `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/`.

**The token sits inside a value because a whole-value reference cannot survive CMS's existing validation.**
Every submitted connection string is parsed by the configured engine's own `DbConnectionStringBuilder` and rejected if the provider cannot read it (`Backend/Services/DataStoreConnectionStringValidator.cs:56-75`).
Probe-measured on `net10.0` against `Npgsql` 8.0.4 and `Microsoft.Data.SqlClient` 6.1.4: `vault://prod/dms/ds-2026` throws `ArgumentException` on both builders, while `Password=${secret:prod/dms/ds-2026}` parses on both and the builder hands the token back through the `Password` key unchanged.

**Textual substitution is measurably wrong, so substitution goes through the builder.**
Probe-measured on the same versions, a resolved value with a leading space comes back without it on both engines, and values containing `;` or the combination `a;b=c` throw.
Assigning through the builder's indexer and re-emitting `builder.ConnectionString` is correct for all seven measured values on both engines.

**The builder has to come from the same object that validated the string, and nothing exposes one today.**
The seam lives in the engine-agnostic `Backend/Services/`, whose project references neither Npgsql nor Microsoft.Data.SqlClient.
`IDataStoreConnectionStringValidator` declares only `Validate`, and `CreateBuilder` is `protected abstract` (`Backend/Services/DataStoreConnectionStringValidator.cs:83`).
So the abstract validator additionally implements a new one-member `IDataStoreConnectionStringBuilderSource` exposing that parse, and `AddDataStoreConnectionStringValidator` (`Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:215-231`) registers the one engine validator under both interfaces, the second forwarded to the first.
That is what makes "validation and substitution cannot disagree" a property of the registration rather than a hope.

**The row's `Provider` column is not read.**
Derivative rows carry no provider at all (`DataModel/Model/DataStoreDerivative/DataStoreDerivativeResponse.cs:11-31`), so the engine comes from the deployment's configuration, which is the same choice `AddDataStoreConnectionStringValidator` already makes.

**Eight of the twelve call sites cannot carry the failure semantics as written today.**
They are lazy `Select` projections returned unmaterialized inside `Success(...)`, so the lambda runs during response serialization, outside the repository's `try`/`catch` and after `Results.Ok` was chosen.
A synchronous lambda also cannot await `ResolveAsync`.
Each becomes an eager per-row awaited projection so `Success(...)` carries a completed list, and the four single-row `Get` sites gain an `await` in place.

**Every non-null row is decrypted, token-free ones included.**
Token-freeness is unknowable in cipher text, so there is no way to skip the decrypt and still find the tokens.
A token-free row must come back byte-identical to what main returns, which is what makes this safe.

**Re-emission normalizes more than casing.**
Probe-measured: `Server=db;Database=edfi;User Id=sa` returns as `Data Source=db;Initial Catalog=edfi;User ID=sa`, so SQL Server canonicalizes keyword names and not only their casing.
DMS keys connection-pool ownership on this text verbatim, so re-emission changes that key once, the way a rotation changes it each time.

**The re-encryption assertion uses CMS's own decryptor.**
No project under `src/config/` references any `EdFi.DataManagementService.*` assembly, and this story must not create the first such reference.
A fixed-string expectation is impossible anyway, because `ConnectionStringEncryptionService.Encrypt` generates a fresh initialization vector per call (`Backend/Services/ConnectionStringEncryptionService.cs:19-38`).

**The seam is transient and the cache is a singleton, and the split is not stylistic.**
The seam reads the request's tenant from `ITenantContextProvider`, which is registered scoped (`Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:126`), and the file already documents that a singleton depending on it fails DI scope validation in the Development environment (`:116-119`).
The cache holds no scoped dependency and receives the tenant as an argument, which is the shape the host-owned `IConnectionStringEncryptionService` beside it already has.

**The resolve timeout races a host-owned task rather than the returned `ValueTask`.**
A resolver that blocks before returning its `ValueTask` never hands the host anything to race, so racing the return value alone would not bound that case.

**Derivative containment follows DMS's own precedent.**
An undecryptable derivative already yields a null connection string and a successful parent read on the DMS side (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:576`, `:588-612`).
A parent token still fails the read, because there is nothing to contain it to.
The standalone derivative endpoints fail instead, because there the derivative is the requested resource rather than part of one.

**Blast radius is asymmetric and that asymmetry is the point.**
DMS calls `EnsureSuccessStatusCode()` on the data store fetch (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:465`, `:487`), so one failing parent token fails the whole collection read for that tenant, while one failing derivative token does not.

**`TimeProvider` is taken as an optional constructor parameter rather than injected.**
CMS registers one only in the self-contained branch (`Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:384`), so the cache defaults to `TimeProvider.System`, following the pattern at `Backend.OpenIddict/Services/TokenCleanupService.cs:22`.

## Acceptance Criteria

**Token grammar**

- The token is `${secret:<name>}`, where `<name>` is one or more of `A-Z a-z 0-9 _ - . / : @ +`.
- Matching is ordinal and case-sensitive, so `${Secret:x}` is not a token.
- A `${` not followed by a well-formed token is left verbatim and fails nothing.
- There is no escape sequence.
- Multiple tokens in one value resolve independently.
- A grammar table test carries:
  - one case per permitted punctuation character and one per excluded character
  - an empty name, an embedded space, an embedded brace, and an unterminated `${secret:`, each left verbatim and failing nothing

**Call sites**

- A host-owned service in `Backend/Services/` is awaited at all twelve projection sites.
- The eight collection sites become eager per-row awaited projections, so `Success(...)` carries a completed list.
- The four single-row `Get` sites gain an `await` in place.
- The twelve sites are: `DataStoreRepository.QueryDataStore` (PG and MSSQL `:193`), `GetDataStore` (`:265` both), `DataStoreDerivativeRepository.QueryDataStoreDerivative` (`:154` PG, `:149` MSSQL), `GetDataStoreDerivative` (`:197`, `:192`), `GetDataStoreDerivativesByDataStore` (`:365`, `:355`), and `GetDataStoreDerivativesByDataStoreIds` (`:406`, `:396`).
- A test drives a failing resolver through each collection method and asserts the documented failure result, not an exception escaping during response enumeration.
- A source-scan test asserts no `Convert.ToBase64String` over a connection string column remains in `Backend.Postgresql` or `Backend.Mssql` repository files outside the seam.

**Decryption**

- Every non-null connection string column is decrypted, including token-free ones.
- A null column returns null with no decrypt call.
- A token-free row returns the Base64 of its input bytes unchanged, with no resolver call, asserted against a value encrypted before this change.
- A decryption that throws fails the read with its own message, distinct from every resolution failure, and mentions neither secrets nor resolvers.
- That decryption-failure case is asserted on the parent path and on both derivative paths.
- Both directions use the existing `IConnectionStringEncryptionService` (`Backend/Services/IConnectionStringEncryptionService.cs:10-11`), and no new cryptography is written.
- `Decrypt` gains its first production caller, having been exercised only by tests until now.

**Substitution**

- Substitution assigns each affected keyword through the engine's `DbConnectionStringBuilder` indexer and re-emits `builder.ConnectionString`.
- No code path performs textual replacement on the connection string.
- The abstract validator implements a new one-member `IDataStoreConnectionStringBuilderSource` in `Backend/Services/` exposing its `CreateBuilder` parse.
- `AddDataStoreConnectionStringValidator` registers the one engine validator under both interfaces, the second forwarded to the first.
- A test resolves both interfaces and asserts the same instance.
- The row's `Provider` column is not read by the seam.
- A substitution-safety test covers `simple`, `has"quote`, `has=equals`, `has'apos`, `has;semi`, `a;b=c`, and a leading-space value, on both engines.
- A companion probe test records what textual substitution does with the same values, so a rewrite that reintroduces it fails here.
- A test records that the returned text is the builder's rendering rather than the operator's.
- The re-encryption assertion round-trips through CMS's own `IConnectionStringEncryptionService.Decrypt` and never references DMS's `ConnectionStringDecryptionService`.

**Tenant**

- The resolver receives the token name and the tenant.
- `SecretReference.Tenant` is null under `TenantContext.NotMultitenant`, asserted.
- `SecretReference.Tenant` is `TenantContext.Multitenant.TenantName` otherwise, asserted.
- A data store read with a tokenized derivative resolves that token exactly once, not twice, because the parent nests items the derivative repository already projected (`Backend.Postgresql/Repositories/DataStoreRepository.cs:164-182`).

**Lifetimes**

- The seam is transient, matching the repositories that call it, and is never registered as a singleton.
- The cache is a singleton and takes the tenant as an argument.
- `ISecretResolver` is registered as a singleton.
- A test boots the host in the Development environment so a lifetime regression fails DI scope validation.

**Cache**

- The cache is keyed `(tenant, name)` with absolute expiration from `SecretsSettings:CacheExpirationSeconds`, default `300`.
- `CacheExpirationSeconds` of `0` disables caching.
- Cache behavior is asserted:
  - two reads inside the window call the resolver once
  - a read after the window calls the resolver again
  - two tenants resolving the same name are two cache entries
  - with `0`, every read calls the resolver
- Single-flight behavior is asserted:
  - concurrent misses on one key collapse to one resolver call whose result every waiter takes
  - a second key is not serialized behind the first
  - a failing call fails every waiter and caches nothing
- Time is taken through a `TimeProvider` optional constructor parameter defaulting to `TimeProvider.System`.
- A new tenant's first read populates the cache.
- A removed tenant's entries are unreachable and expire, asserted directly against the cache because tenant administration exposes no removal today (`Config.Frontend/Modules/TenantModule.cs:21-23`).

**Timeout**

- `SecretsSettings:ResolveTimeoutSeconds` defaults to `10`.
- The seam invokes the resolver on a task it owns and races that task, not the returned `ValueTask`.
- The seam passes a cancellation token to the resolver.
- Each of these fails within the window:
  - a resolver returning an incomplete `ValueTask` and ignoring the token
  - a resolver blocking before returning its `ValueTask`
- A cooperative resolver's cancellation token is observed as cancelled.
- A resolver returning just inside the window succeeds.

**Failure semantics**

- A token with no `ISecretResolver` registered fails the read, naming the data store, the token, and the missing contract.
- A resolver fails the read when it:
  - throws
  - cancels
  - returns null
  - returns an empty string
- The no-resolver message and the resolver-failed message are distinct.
- The log carries the exception type and never the exception message.
- No response ever carries a decrypted value still containing `${secret:`.
- A resolution failure surfaces to the client as the existing generic failure shape (`FailureResults.Unknown`, `Config.Frontend/Modules/DataStoreModule.cs:66-71`).
- A test correlates the log line by trace identifier and asserts it carries the data store id, tenant, and token name, and never the resolved value.

**Derivative containment**

- An unresolvable derivative token read as part of a data store yields a null connection string for that derivative and a successful parent read.
- That case logs the parent, tenant, derivative type, and token.
- The case is covered on both the collection and single-row paths, which reach derivatives through different arms.
- An unresolvable parent token fails the read, and one test asserts this asymmetry directly.
- The same unresolvable token fails through each standalone derivative endpoint:
  - `GET /v3/dataStoreDerivatives/` (`Config.Frontend/Modules/DataStoreDerivativeModule.cs:21`)
  - `GET /v3/dataStoreDerivatives/{id}` (`:22`)
- The seam takes an argument saying whether the row is read as a resource or as part of one.
- Pre-existing derivative-lookup-failure behavior is unchanged: the arms mapping a failed lookup to an empty collection still behave as today.

**Availability**

- CMS starts with an unreachable secret store, and a read of a token-free collection succeeds.
- With two data stores in one tenant and one carrying a failing parent token, the collection read fails.
- The companion case with a failing derivative token instead succeeds.

**Configuration**

- `SecretsSettings` is added to `Config.Frontend/appsettings.json` with both keys, bound and validated like its neighbors.
- Both keys are documented in `docs/CONFIGURATION.md` with their defaults, and carry `DMS_CONFIG_*` overrides in both compose files.

**Integration and end-to-end**

- A data store inserted through `/v3/dataStores/` with a token in its `Password` passes the real validator and reads back as the resolved connection string.
- The same row with a failing resolver produces the documented failure.
- An end-to-end test delivers a fixture secrets plugin published `--no-self-contained` through the `plugins-config.yml` mount, with its resolver backed by a harness-written file.
- That test brings CMS and DMS up together, creates a data store with a token password, and asserts DMS serves a resource out of it.
- A second end-to-end run rotates the file and asserts the new value takes effect after the configured expiration and not before, measured against CMS's own endpoint.

**Build**

- These pass:
  - `dotnet test src/config/EdFi.DmsConfigurationService.sln`
  - `build-config.ps1 E2ETest`

## Tasks

1. Add the token grammar parser and its table test.
2. Add `IDataStoreConnectionStringBuilderSource` on the abstract validator and the dual-interface registration.
3. Add the resolution seam in `Backend/Services/`, with builder-mediated substitution, the resource-versus-nested argument, and the failure semantics.
4. Add the host cache with single-flight, absolute expiration, and the injected `TimeProvider`.
5. Add the owned-task resolve-timeout race.
6. Materialize the eight collection projections and wire all twelve call sites through the seam.
7. Add `SecretsSettings` with binding, validation, compose overrides, and documentation.
8. Add the unit suites: substitution safety with probe companion, same-instance builder source, cache, timeout, failure semantics, tenant, and the parent-versus-derivative asymmetry.
9. Add the source-scan site-count test.
10. Add the integration read-path tests.
11. Add the two end-to-end proofs, capability and rotation.
