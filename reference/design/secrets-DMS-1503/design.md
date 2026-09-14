# Secrets Manager Design

## Status

Drafted 2026-09-14 as the design output of spike `DMS-1503`, under epic [DMS-1504](https://edfi.atlassian.net/browse/DMS-1504) Shared Plugin Extension Infrastructure.
It is the Secrets Manager companion to [plugins-DMS-1462](../plugins-DMS-1462/design.md), which specifies the shared delivery mechanism and explicitly defers this type to its own spike.

This document decides two things the spine deliberately left open, and it treats them as one because an operator experiences them as one.

1. **The Secrets Manager plugin type**: its contracts, their cardinality, their lifetimes, their caching behavior, the multi-tenant dimension, and the `IClientSecretHasher` relocation CMS needs before a plugin can claim it.
2. **The DMS equivalent of [external configuration of ODS connection strings](https://docs.ed-fi.org/reference/ed-fi-api/platform-dev-guide/configuration/external-configuration-of-ods-connection-strings/)**: an operator supplying data store connection strings from outside the application's own configuration.

In ODS those are one mechanism.
In DMS they cannot be, and this document says what each one is instead.

**What is inherited and not reopened.**
Every Phase A statement in the spine is a decision this document builds on rather than revisits: the `ContributeConfiguration(IConfigurationBuilder, IConfiguration bootstrapConfiguration)` hook shape, the placement of plugin sources immediately below the last environment variable source, the additive `Sources` guard, the read-only nature of `bootstrapConfiguration`, and `Replace` cardinality for the secret resolver and the secret hasher.
The spine's findings about DMS's data shape are likewise inherited: tenants are runtime data in both hosts, per-tenant connection strings never pass through `IConfiguration`, and the capability an operator asks for lands in CMS rather than in DMS.
Where this document's own research sharpens one of those findings rather than contradicting it, it says so at the point where it matters and the underlying decision stands.

**Ground truth at the time of writing.**
`DMS-1496`, `DMS-1497`, and `DMS-1498` are merged: `src/plugins/` exists with the contract, the loader, the recording wrapper, and the cardinality guard.
`DMS-1499` through `DMS-1502` are open.
The contract as merged carries `Name` and `ContributeServices` and no Phase A member (`src/plugins/EdFi.Api.Plugins/EdFiApiPlugin.cs`), which is the shape the spine specified.

- [README.md](./README.md) - spike manifest, story index, blocker table, filing gate
- Jira: [DMS-1503](https://edfi.atlassian.net/browse/DMS-1503)
- Spine: [DMS-1462](https://edfi.atlassian.net/browse/DMS-1462), [plugins-DMS-1462/design.md](../plugins-DMS-1462/design.md)

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.
`Backend/`, `Backend.OpenIddict/`, `Backend.Postgresql/`, and `Backend.Mssql/` name the corresponding directories under `src/config/backend/EdFi.DmsConfigurationService.`.
`DataModel/` names `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/`.
Anything under `src/dms/` is written in full.
ODS/API citations are relative to `Application/` in the Ed-Fi-ODS repository at commit `90c75ffed3fc2bc0dafa14a2600b3a0d050f82e9`, the same commit [ods-precedent.md](../plugins-DMS-1462/ods-precedent.md) pins.

---

## Table of Contents

- [Goals and Non-Goals](#goals-and-non-goals)
- [Problem Statement](#problem-statement)
- [Design](#design)
  - [Two Kinds of Secret, Two Mechanisms](#two-kinds-of-secret-two-mechanisms)
  - [Phase A: The Process-Global Secrets](#phase-a-the-process-global-secrets)
  - [The Secret Reference: Runtime-Data Secrets](#the-secret-reference-runtime-data-secrets)
  - [Where Resolution Happens](#where-resolution-happens)
  - [Freshness, Caching, and the Tenant Set](#freshness-caching-and-the-tenant-set)
  - [Failure Semantics](#failure-semantics)
  - [The Contract Package](#the-contract-package)
  - [The `IClientSecretHasher` Relocation](#the-iclientsecrethasher-relocation)
  - [Contract Cardinality](#contract-cardinality)
  - [CMS Host Integration](#cms-host-integration)
  - [Configuration Surface](#configuration-surface)
  - [Trust Model Notes](#trust-model-notes)
- [Where the Code Lives](#where-the-code-lives)
- [Testing Strategy](#testing-strategy)
- [Rejected Alternatives](#rejected-alternatives)
- [Out of Scope and Deferred](#out-of-scope-and-deferred)
- [Level of Effort](#level-of-effort)
- [Cross-References](#cross-references)

---

## Goals and Non-Goals

### Goals

- An operator keeps every value DMS and CMS treat as a secret in a secret store of their choosing, and neither host gains a dependency on any particular store.
- The two hosts' process-global secrets are served before anything reads them, by the Phase A mechanism the spine already decided.
- A data store connection string held by CMS on behalf of DMS can name a secret instead of carrying one, and the secret is fetched when the value is read rather than when it was written.
- Rotating a secret in the store takes effect without an operator touching CMS, and the window in which it takes effect is stated, configurable, and testable.
- `IClientSecretHasher` becomes a contract a plugin can claim, which means it stops shipping from an assembly named for one identity provider.
- CMS loads plugins, which the spine showed it can do for very little and deferred only because no epic drove it. This spike is that epic.
- The Data Management Service gains no part of the capability. One story here touches it, and only to build the Phase A composition point the plugin spine designed and assigned to this spike; no DMS story resolves a secret, declares a contract, or reads a new configuration key.

### Non-Goals

- A DMS-side secret resolver. DMS holds two secrets of its own and obtains everything else from CMS; both of its own are served by Phase A.
- An Ed-Fi-authored vault plugin, for any vault. The documentation carries worked examples the way the ODS documentation does; the repository ships fixtures for its own tests and nothing an operator installs.
- Re-encrypting or re-keying anything already stored. This design adds a way for a stored value to name a secret; it migrates no stored value and changes no stored format.
- Secret material in logs, ever, including the names of the keys a plugin supplied. The spine's observability rule is inherited unchanged.
- Push-based invalidation. No secret store in scope notifies a subscriber, and inventing a channel for one would be surface with no consumer.
- Identity-validation secrets. Those belong to [identity-DMS-1413](../identity-DMS-1413/design.md) under epic DMS-1412, and nothing in the research below couples them to this type.

---

## Problem Statement

**Eight configuration values across the two hosts are secrets, and every one of them is plain text in a file or an environment variable today.**

DMS reads two, both from its own configuration.

| Key | What it is | Read at |
| --- | --- | --- |
| `ConfigurationServiceSettings:ClientSecret` | The client secret DMS presents to CMS | `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs:360-366`, captured into a `ConfigurationServiceContext` singleton |
| `ConfigurationServiceSettings:EncryptionKey` | The key DMS decrypts CMS's stored connection strings with | `DmsCoreServiceExtensions.cs:367-371`, captured into a `ConnectionStringDecryptionService` singleton |

CMS reads six.
The spine counted four; the four it counted are the ones `Config.Frontend/appsettings.json` declares, and two more reach `IdentityOptions` through the same binder without appearing in that file.

| Key | What it is | Declared in `appsettings.json` |
| --- | --- | --- |
| `DatabaseSettings:DatabaseConnection` | CMS's own database connection string, password included | yes, `:18` |
| `DatabaseSettings:EncryptionKey` | The key CMS encrypts stored connection strings with, validated at least 32 characters and not the shipped default (`Backend/DatabaseOptions.cs:42-77`) | yes, `:19` |
| `IdentitySettings:ClientSecret` | CMS's own client secret, length- and complexity-validated (`Config.Frontend/Configuration/IdentitySettings.cs:41-63`) | yes, `:36` |
| `IdentitySettings:EncryptionKey` | The key protecting OpenIddict signing keys held in the database (`Backend.OpenIddict/Services/OpenIddictTokenManager.cs:147-151`) | yes, `:39` |
| `IdentitySettings:CertificatePassword` | The production signing certificate's password (`Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:50-51`, read at `OpenIddictTokenManager.cs:123` and `:474`) | no |
| `IdentitySettings:DevCertificatePassword` | The development certificate's password, defaulting to the literal `password` (`OpenIddictServiceCollectionExtensions.cs:46-47`, read at `OpenIddictTokenManager.cs:92` and `:446`) | no |

All eight are singular values read once at startup, with no tenant dimension at all.
The count correction changes nothing structural: Phase A serves eight as readily as it serves six, and the two extra rows matter only because a document that promised to cover "the six" would have left two secrets unserved and unmentioned.

**The connection strings are a different problem, and they are the one the ODS documentation is actually about.**

An operator POSTs a data store connection string to CMS at `/v3/dataStores/` (`Config.Frontend/Modules/DataStoreModule.cs:22`).
CMS encrypts it under `DatabaseSettings:EncryptionKey` and stores the cipher text (`Backend.Postgresql/Repositories/DataStoreRepository.cs:50`, `Backend/Services/ConnectionStringEncryptionService.cs:19-38`).
DMS fetches the data stores from `v3/dataStores/` (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:465`) and decrypts each value with the same key (`:529`, `:590`, `Configuration/ConnectionStringDecryptionService.cs:18`).
So the value never reaches `IConfiguration` in either host, it is created by an API call rather than by a deployment, and the party that holds it is CMS.

**An operator who keeps that value in a vault has no seam to reach today.**
They can put every one of the eight configuration secrets in a vault only by teaching their deployment to inject them as environment variables, which moves the problem rather than solving it, and they cannot do even that for a connection string, because a connection string is not configuration in this architecture.

**The ODS answer solves both at once and does not transfer whole.**
ODS lets a plugin add a configuration source before the host is built (`EdFi.Ods.Common/IHostConfigurationActivity.cs:13`), and then reads connection string overrides out of configuration keyed by ODS instance id, or by tenant and ODS instance id in a multi-tenant deployment (`EdFi.Ods.Api/Configuration/ConnectionStringOverridesApplicator.cs`, `EdFi.Ods.Features/MultiTenancy/MultiTenantConnectionStringOverridesApplicator.cs`).
The loading seam transfers and the spine already adopted and narrowed it as Phase A.
The data shape does not, for the reason the spine records and for a second reason this document's research adds; see [The Secret Reference](#the-secret-reference-runtime-data-secrets).

---

## Design

### Two Kinds of Secret, Two Mechanisms

The eight configuration secrets and the connection strings differ in exactly one property, and that property decides the mechanism.

| | Process-global secrets | Data store connection strings |
| --- | --- | --- |
| Where the host looks today | `IConfiguration` | the CMS database, through the CMS API |
| Who put it there | the deployment, before the process started | an API client, while the process ran |
| How many there are | eight, fixed, named at compile time | unbounded, created and deleted at runtime, keyed by identities CMS assigns |
| When the host needs it | once, at startup, before the container is built | at every read of a data store, after tenant resolution |
| Mechanism | **Phase A**, a configuration source the plugin contributes | **Phase B**, a service the plugin registers and CMS calls |

That table is the whole design.
It is also the reason the spine kept Phase A even though nothing on its own table consumed it: a Phase B-only mechanism serves the right-hand column and leaves the left-hand one with no seam earlier than the first configuration read.

### Phase A: The Process-Global Secrets

Phase A is decided in the spine and built by this spike's first story.
Nothing about it is redesigned here.
What this document adds is the list of values it serves, the statement that it serves them with no new contract, and the one key it cannot serve.

The contract gains one member, and the package's version moves from `1.0.0` to `1.1.0` as the spine specified:

```csharp
/// Phase A. Override to contribute configuration sources before configuration is read.
public virtual void ContributeConfiguration(
    IConfigurationBuilder configurationBuilder, IConfiguration bootstrapConfiguration) { }
```

This is the first exercise of the contract's additive-only policy, and the story that lands it carries the whole deferred list the spine assigned to it: the loader's Phase A invocation, the placement of contributed sources immediately below the last environment variable source, the additive `Sources` guard, the `Contribute` row of the cardinality table, the `Contribute`-only exemption in the no-contract-registered check, the `docs/CONFIGURATION.md` precedence order, and the Phase A rows of the test plan.

**A Phase A plugin needs nothing from this document to serve all eight values.**
It adds `AddAzureKeyVault`, `AddSystemsManager`, or any other `IConfigurationSource`, and the eight keys resolve from it because they are ordinary configuration keys and the loader placed the source above every JSON source.
`bootstrapConfiguration` is how the plugin finds its own vault address without that address having to come from somewhere a plugin controls: it is the operator configuration already layered at that moment, readable and not mutable through that parameter.

**One key is out of reach, and it is out of reach in CMS as well as in DMS.**
DMS reads `AppSettings:StartupStatusFilePath` at `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs:30-33`, before plugins load, because that file is how a loader fatal is reported.
CMS has no equivalent, since `Config.Frontend/Program.cs` is a plain minimal-API startup with no bootstrap status signal, so in CMS the exception is that there is no exception.
Neither key is a secret, so nothing in this design wants them.

**Phase A is not a way to supply a connection string.**
It can only supply values that are read out of `IConfiguration`, and a data store connection string is not one.
An operator who puts `ConnectionStrings:Whatever` in a vault has supplied a key nothing reads.

### The Secret Reference: Runtime-Data Secrets

**The stored connection string may name a secret instead of carrying one.**

```text
Host=db;Port=5432;Username=edfi;Database=edfi_datastore_2026;Password=${secret:prod/dms/ds-2026}
```

The same shape on SQL Server, in that engine's own keywords:

```text
Server=db;Database=edfi_datastore_2026;User ID=edfi;Password=${secret:prod/dms/ds-2026};Encrypt=False
```

CMS stores that text exactly as it stores any other connection string: encrypted under `DatabaseSettings:EncryptionKey`, in the same column, through the same write path.
When CMS reads the row, it substitutes each `${secret:<name>}` token with the value a plugin-supplied resolver returns, re-encrypts, and returns the result.
DMS receives the same shape it receives today: Base64 cipher text it decrypts with the shared key.

Four things follow, and each of them is the reason a simpler-looking alternative was rejected.

**The token sits inside a connection string value rather than replacing the whole value, because a whole-value reference cannot survive CMS's existing validation.**
Every submitted connection string is parsed by the configured engine's own `DbConnectionStringBuilder` and rejected if the provider cannot read it (`Backend/Services/DataStoreConnectionStringValidator.cs:56-75`).
Probe-measured on `net10.0` against `Npgsql` 8.0.4 and `Microsoft.Data.SqlClient` 6.1.4, the versions `src/Directory.Packages.props` pins at `:65` and `:7`: `vault://prod/dms/ds-2026` throws `ArgumentException: Format of the initialization string does not conform to specification starting at index 0.` on both builders, while `Password=${secret:prod/dms/ds-2026}` parses on both, inside each engine's own keyword set, and the builder hands the token back through the `Password` key unchanged.
A whole-value reference would therefore have required relaxing that validator, which is the one thing standing between an operator and a connection string no reader can open.
A token inside a value needs no change to it at all.

**It also externalizes the right thing.**
A vault holds a password, not a hostname and a database name.
The token shape lets an operator keep the parts of a connection string that are configuration in CMS, where they are readable and auditable, and move only the part that is a secret.
Nothing prevents an operator from putting the whole string behind one token in a `Password` value if their engine tolerates it; the design neither encourages nor blocks that.

**The reference is data, not configuration, which is what makes it survive a tenant or data store being added.**
This is the point at which the spine's rejection of the ODS data shape becomes a design and not just a refusal, and the research sharpens it in a way worth recording so nobody re-derives it.
ODS's override is keyed by `OdsInstanceId`, which is a row identity in `EdFi_Admin` rather than a configuration value (`EdFi.Ods.Api/Configuration/OdsInstanceConfigurationExtensions.cs:28`), and it is read through `IOptionsMonitor<T>.CurrentValue` at the moment an instance configuration is resolved rather than once at startup (`ConnectionStringOverridesApplicator.cs:28`), so a reloading source such as Azure Key Vault with a `ReloadInterval` does track rows added after the process started.
The ODS shape is therefore less startup-frozen than a first reading suggests, and the spine's objection is nonetheless sound where it matters: the *tenant* half of the multi-tenant key is configuration in ODS (`EdFi.Ods.Common/Configuration/Sections/TenantsSection.cs`) and is runtime data in DMS, and the instance half forces the operator to learn a number CMS assigned before they can write the vault entry that overrides it.
A reference the row carries has neither problem.
The operator writes the token when they create the data store, in the same request, and nothing has to agree about an identifier afterwards.

**The stored value is still encrypted, and the vault is still the only place the secret exists.**
Anyone who reads the CMS database sees cipher text.
Anyone who decrypts it sees the token.
The secret itself is never written to the CMS database at all, which is the property that makes this worth building rather than a rearrangement of where the plain text lives.

**Token syntax.**
`${secret:<name>}`, where `<name>` is one or more characters from `A-Z`, `a-z`, `0-9`, and `_ - . / : @ +`.
The charset is deliberately the one `PluginsConfiguration`'s allowlist names already use, minus the path-segment restriction, so that a reader who has met one has met the other; it covers every vault's own naming rules in scope and excludes `{`, `}`, `$`, `;`, `=`, and whitespace, which are the characters that would make a token ambiguous inside a connection string or inside itself.
Matching is ordinal and case-sensitive, because vault names are.
A `${` that is not followed by `secret:` and a well-formed name closed by `}` is not a token and is left alone verbatim, which is what keeps a password that happens to contain `${` from being reinterpreted by an upgrade.
There is no escape sequence: adding one would impose a rule on every operator's password to serve a case no operator has, and the strict token shape already makes an accidental match implausible rather than merely unlikely.
A value may carry more than one token, and each resolves independently.

**Quoting is the connection string's business, not the token's.**
Probe-measured on both builders: a token containing `=` parses and round-trips unchanged, and a value containing `;` needs the provider's own double-quote form, which both builders strip on parse.
The excluded charset above means a token never needs quoting for its own sake; a surrounding value that needs quoting for other reasons is quoted by the operator exactly as it is today.

### Where Resolution Happens

**In CMS, at the point where a stored connection string becomes a response value, and nowhere else.**

There are twelve such points and they are all the same line:

```csharp
ConnectionString = row.ConnectionString is null ? null : Convert.ToBase64String(row.ConnectionString),
```

Six read methods, each present once per engine.
The two columns are `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Repositories/` and `...Backend.Mssql/Repositories/`, and each method lives in the file its name begins with.

| Method | PostgreSQL | SQL Server |
| --- | --- | --- |
| `DataStoreRepository.QueryDataStore` | `:193` | `:193` |
| `DataStoreRepository.GetDataStore` | `:265` | `:265` |
| `DataStoreDerivativeRepository.QueryDataStoreDerivative` | `:154` | `:149` |
| `DataStoreDerivativeRepository.GetDataStoreDerivative` | `:197` | `:192` |
| `DataStoreDerivativeRepository.GetDataStoreDerivativesByDataStore` | `:365` | `:355` |
| `DataStoreDerivativeRepository.GetDataStoreDerivativesByDataStoreIds` | `:406` | `:396` |

The insert and update paths are not resolution sites.
They encrypt what they were given (`Backend.Postgresql/Repositories/DataStoreRepository.cs:50`, `:320`) and a token is stored exactly as any other text is.

Twelve call sites of one rule across two resources and two engines is a shape this codebase has already met and already solved once.
`Backend/Services/ConnectionStringWrite.cs` exists because the four *write* paths must not drift apart, and says so in its own summary: "One rule for both resources on both engines, so the four update paths cannot drift apart."
The read rule gets the same treatment: one host-owned service in `Backend/Services/`, called from all twelve sites, with the engine-specific repositories carrying no policy.

The service takes the stored bytes and returns the Base64 the response carries.
It decrypts, substitutes any tokens, re-encrypts if it substituted anything, and returns.
Both directions already exist and no new cryptography is written: `IConnectionStringEncryptionService` declares `Encrypt` and `Decrypt` and the implementation carries both (`Backend/Services/IConnectionStringEncryptionService.cs:10-11`, `Backend/Services/ConnectionStringEncryptionService.cs:19-38`, `:41-63`).
`Decrypt` has no production caller today, only tests, so this design gives an existing member its first one rather than adding a member.
A value with no token is returned as the same Base64 the call site produces today, without a decrypt, because the token test runs against the decrypted text only when the row is non-null and the host has a resolver; see [Failure Semantics](#failure-semantics) for the case where those two disagree.

**Resolution happens on every read, including an administrator's.**
`/v3/dataStores/` is one endpoint with one shape, gated by `MapLimitedAccess` (`Config.Frontend/Modules/DataStoreModule.cs:23-24`), and DMS is one of its clients rather than a distinguished one.
Resolving only for DMS would mean either a second endpoint or a caller-dependent response body, and both are worse than the exposure they would avoid, which is not in fact an exposure: the value a read returns today is cipher text under a key every reader of that endpoint already needs in order to use it.
What changes is where the plain text came from, not who can obtain it.

**Derivative connection strings resolve on the same rule and are not resolved twice.**
`DataStoreRepository` obtains derivative rows through `IDataStoreDerivativeRepository` and nests the already-projected items (`Backend.Postgresql/Repositories/DataStoreRepository.cs:164-182`), so each of the twelve sites resolves the bytes it read and no site resolves a value another site already resolved.

**DMS is not a resolution site.**
It receives cipher text, decrypts it, and opens a connection, exactly as it does now.
This is the spine's "capability lands in CMS" finding taken to its conclusion: no story in this spike resolves a secret in DMS, adds a DMS configuration key, or changes a DMS read path.
The one DMS-touching story is the Phase A composition point, which the spine designed and assigned here and which serves DMS's own two configuration secrets rather than anything on this page.

### Freshness, Caching, and the Tenant Set

**CMS owns the cache, and the plugin is free to cache without being required to.**

A resolver is a pure function of a name and a tenant, which is what makes a host-owned cache possible at all, and the host is the only party that can state a freshness window an operator can read, configure, and test.
A plugin-owned cache would make rotation latency a per-vendor property with no configuration surface and no way to observe it.

The cache is keyed by the pair `(tenant, name)` with an absolute expiration, `SecretsSettings:CacheExpirationSeconds`, defaulting to 300.
Absolute rather than sliding, because the question an operator asks about a rotated secret is "how long until it takes effect", and only an absolute window answers it with a number.
Three hundred seconds is chosen against the interval already in the system rather than by preference: DMS caches data stores for `CacheSettings:DataStoreCacheExpirationSeconds`, 600 by default (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json`), and the ODS documentation's own worked examples use a ten-minute vault reload.
**The two windows add rather than overlap, and the documentation states the sum rather than this number alone.**
A rotated secret becomes visible to CMS within its own expiration and to DMS within DMS's, so on stock settings a rotation reaches a running DMS within about fifteen minutes and not within five.
Choosing a value at or below the consumer's is what keeps this cache from being the dominant term in that sum; choosing a longer one would add a delay nobody asked for on top of one that already exists.

**The tenant set is runtime data, and the cache is designed so that it never has to know.**
A tenant added through `/v3/tenants` has no entries in the cache, so there is nothing stale to invalidate and its first read populates.
A tenant removed leaves entries that nothing will ask for again and that expire on their own, since every lookup is keyed by the tenant and no other tenant's lookup can reach them.
There is deliberately no hook from tenant administration into the cache: coupling them would make a tenant write depend on a cache the tenant path has no other reason to know about, to fix a staleness that cannot be observed.

**Rotation in the store takes effect within the expiration, and that is the whole contract.**
No `IChangeToken`, no push, no invalidation endpoint.
Every secret store in scope is a pull interface, the plugin contract has no channel back to the host, and adding one would be new surface serving a capability no store offers.
An operator who needs a rotation to take effect immediately restarts CMS, and the documentation says so rather than leaving them to discover it.

**The bound is on the cache and not on the vault call rate, which is the honest way round.**
Without the cache, a resolver would be called once per token per row per read of `/v3/dataStores/`, which is a list endpoint.
With it, the call rate is bounded by distinct `(tenant, name)` pairs per expiration window regardless of read volume, which is the property that makes a vault round trip viable at all.
The spine's inherited input says the contract must be async and cache-aware "because a vault round trip per request is not viable on the write path"; the write path in question is DMS's, and this is the mechanism by which it never sees one.

### Failure Semantics

**A token that cannot be resolved fails the read, and is never passed through.**

The alternative is returning a connection string whose password is the literal text `${secret:prod/dms/ds-2026}`, which DMS would decrypt successfully, hand to a driver, and see fail as an authentication error naming nothing.
That is the silent-degradation failure mode the spine refuses everywhere, arriving at the worst possible place: a credential error an operator will spend the afternoon attributing to the database.

| Condition | Result |
| --- | --- |
| A token is present and no resolver is registered | The read fails, naming the data store and the token, and saying that a secret reference needs a plugin registering `ISecretResolver` |
| The resolver throws, times out, or returns null or empty | The read fails, naming the data store and the token, with the resolver's failure as the inner exception and its message never surfaced to the client |
| The token is malformed, that is `${secret:` opens and no well-formed name closes it | The stored text is left alone and no read fails, because this is not a token; see [The Secret Reference](#the-secret-reference-runtime-data-secrets) |
| Two plugins register `ISecretResolver` | Startup is fatal, by the spine's replace-cardinality rule, naming both plugins |
| One plugin registers `ISecretResolver` twice | Startup is fatal, naming the plugin and its registration count, addressed to its author |
| No token is present anywhere | Nothing changes, no resolver is called, and the response is byte-identical to today's |

The failure reaches the API as the repository's existing unknown-failure shape rather than as a new one, so nothing about the endpoint's contract changes.
The log line names the data store id, the tenant, and the token *name*, and never the resolved value; that is the spine's observability rule, which already requires a Phase A source's own keys and values to stay out of the log, applied to the same class of data arriving by the other mechanism.

**Resolution failure is a read failure and not a startup failure.**
A vault outage at boot must not stop CMS from starting, because CMS serves claim sets, tenants, and applications that have nothing to do with a data store connection string, and because a CMS that will not start is a DMS that cannot start either.
The read of the affected data store fails for as long as the outage lasts, and every other read succeeds.

### The Contract Package

**One package, `EdFi.Api.Secrets`, carrying both CMS secrets contracts.**

```csharp
namespace EdFi.DmsConfigurationService.Secrets;

/// Resolves a named secret for a tenant. Replace cardinality: zero or one implementation.
public interface ISecretResolver
{
    ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}

/// The name of a secret, and the tenant it is being resolved for.
public sealed record SecretReference(string Name, string? Tenant);

/// Hashes and verifies client secrets. Replace cardinality: zero or one implementation.
public interface IClientSecretHasher
{
    Task<string> HashSecretAsync(string plainTextSecret);
    Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret);
    bool IsSecretHashed(string secret);
}
```

**Both contracts in one package, because they are one type's contracts and versioning them apart buys nothing.**
The additive-only policy is a property of a package, so two packages would mean two versions, two entries in the skew preflight, and two package ids burned permanently, to separate two interfaces the same plugin will usually implement and that the same host consumes.
A single id also keeps the failure message an implementer reads short: one package, one version, one thing to upgrade.

**The package id follows the platform prefix and the assembly follows the tree it lives in.**
`EdFi.Api.*` is the prefix for a contract package an Ed-Fi API host consumes, as `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation` already are and as `EdFi.Api.Identity` will be.
The assembly and namespace are `EdFi.DmsConfigurationService.Secrets`, matching the convention `EdFi.Api.Identity` sets by building from `src/dms/core/EdFi.DataManagementService.Identity/` under namespace `EdFi.DataManagementService.Identity` ([identity-DMS-1413/01](../identity-DMS-1413/01-add-identity-contract-package-and-host-default.md)).
The two names are never used interchangeably, for the reason the spine states: an assembly reference carries an assembly name, so a skew preflight matching on a package id would silently check nothing.

**That assembly name puts both contracts inside the host-owned prefix set, and that is correct rather than a collision.**
`HostOwnedServiceTypes` treats a service type as the host's when its declaring assembly's simple name starts with `EdFi.DataManagementService.` or `EdFi.DmsConfigurationService.` (`src/plugins/EdFi.Api.Plugins.Hosting/HostOwnedServiceTypes.cs:38-42`), so `EdFi.DmsConfigurationService.Secrets` matches.
It is admitted anyway because the displacement check exempts declared contracts before it asks whether the type is host-owned (`src/plugins/EdFi.Api.Plugins.Hosting/PluginRegistrationAudit.cs:175-182`), which is exactly the arrangement `EdFi.DataManagementService.CustomValidation` already relies on.
The consequence is worth stating once: a contract is registrable by a plugin only while it is in the host's registry, so removing an entry from `CmsPluginContracts` does not merely stop the host calling it, it makes every plugin that registers it fatal.
That is the right behavior and it is not obvious from either half on its own.

**`SecretReference` is a record rather than two parameters, and that is a compatibility decision.**
The additive-only policy forbids changing a member's signature for the life of the package, so a two-parameter method could never gain a third input.
A record can gain a property with a default, which is the same additive move a virtual with a no-op body is for the base class.
`Tenant` is null in a single-tenant deployment and carries the tenant name in a multi-tenant one, which CMS has at the resolution point as `TenantContext.Multitenant.TenantName` (`Backend/Services/TenantContext.cs`).
That the context is populated on the read DMS actually makes is grounded rather than assumed: DMS sends the tenant on the `Tenant` header of its `v3/dataStores/` request (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:32`, `:472-474`), and `TenantResolutionMiddleware` resolves and validates it ahead of the endpoint (`Config.Frontend/Program.cs:76`).
Without that header the resolver would be handed a null tenant on the one call that matters most, which is the failure this paragraph exists to rule out.

**The tenant is an argument and not ambient state**, which is the spine's inherited input and is load-bearing rather than stylistic: a plugin instance is constructed once per process and outlives every tenant, so a resolver that read a tenant from a static or from an injected accessor would be reading state its own lifetime does not permit it to hold.

**`ValueTask` rather than `Task`**, because a resolver that can answer without going anywhere should not be forced to allocate in order to say so.
A plugin holding its own cache, or one reading from a mounted file, returns synchronously on most calls, and `ValueTask` is what lets it.
It buys nothing when the resolver really does make a round trip, and it costs nothing then either, which is the whole case for it: the contract is fixed for the life of the package and this is the shape that leaves an implementer the most room.
`IClientSecretHasher` keeps `Task`, because moving it is a signature change to a merged interface and the relocation is already changing everything else about where it lives; its call sites are per-client-write and per-token-request rather than per-row.

**Lifetime rule: both contracts are registered as singletons, and a plugin registering either with any other lifetime is refused.**
The hasher is a singleton today at all four of its registration sites, so nothing changes there.
The resolver is a singleton because a vault client is a connection-pooled, credential-holding object whose whole cost is construction, because the contract takes every input it needs as an argument and therefore has no per-request state to hold, and because the host caches in front of it and a per-scope resolver would make that cache the only thing keeping a per-request vault client from being constructed.
The rule is enforced the way custom validation enforces its own transient-only rule: by the host's startup check over the composition snapshot, not by the loader, and its message names the plugin and the lifetime it used.

### Contract Cardinality

Both contracts are `Replace`, which the spine's [Contract Cardinality](../plugins-DMS-1462/design.md#contract-cardinality) rules then govern unchanged.

| Contract | Cardinality | Host default | Registration |
| --- | --- | --- | --- |
| `ISecretResolver` | `Replace` | **none** | a single `Add`, never a `TryAdd` |
| `IClientSecretHasher` | `Replace` | `ClientSecretHasher`, PBKDF2-SHA256 | a single `Add`, never a `TryAdd` |

**`ISecretResolver` deliberately has no host default, and that is a different shape from every replace contract the spine has met.**
Identity's `IIdentityService` has `NoIdentityService`, and the hasher has a real implementation, because in both cases the host has something meaningful to do when no plugin is installed.
Here it does not: a host default would be either a resolver that fails every call, which is the "no resolver registered" row of the failure table wearing a costume, or one that reads secrets from configuration, which is Phase A and does not need a second name.
So the absence of a registration is the signal, the failure table's first row is where it is read, and nothing resolves `ISecretResolver` without checking whether one exists.

**A plugin that claims a replace contract with `TryAdd` is not caught at this seam**, and the spine records why: a declined `TryAdd` never hands its candidate to the collection, so no wrapper can see it.
For `IClientSecretHasher` the decline is guaranteed, because CMS registers a default and the `TryAdd` will always find it; for `ISecretResolver` there is no default, so a `TryAdd` succeeds and behaves exactly as an `Add` would.
The implementer obligation is stated in the package's XML documentation and in `PLUGINS.md` and is the same one the spine already states: a replace contract has one claimant, so there is nothing to try.

**The replace-conflict count deliberately excludes the hasher's host default**, which is what makes the case that must pass pass: four host registrations of `ClientSecretHasher` plus one plugin claim is five descriptors on the collection and exactly one claim.
That the host registers the default more than once is a pre-existing property of CMS rather than something this design introduces; see the next section.

### The `IClientSecretHasher` Relocation

**Moving it is prerequisite work for the type, not part of it.**
`IClientSecretHasher` lives in `Backend.OpenIddict/Services/IClientSecretHasher.cs`, and a contract a third party compiles against cannot ship from an assembly named for one identity provider: CMS also runs against Keycloak (`Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:246`, `:387`), and an operator on that path would be taking a dependency on an assembly whose name says their deployment does not use it.

The interface moves to `EdFi.Api.Secrets`.
The implementation, `Backend.OpenIddict/Services/ClientSecretHasher.cs`, stays where it is: it is the host default, it is not part of the contract, and it reads `IdentityOptions`, which is an OpenIddict type.

**Four registration sites change, and one of them is unreachable.**

| Site | Reached when |
| --- | --- |
| `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206`, inside `ConfigureDatastore` | always, on both identity providers and both engines |
| `Backend.Postgresql/OpenIddict/PostgresOpenIddictServiceExtensions.cs:37`, inside the three-argument `AddPostgresOpenIddictStores` | self-contained identity on PostgreSQL, from `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:376` |
| `Backend.Mssql/OpenIddict/MssqlOpenIddictServiceExtensions.cs:35` | self-contained identity on SQL Server, from `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:372` |
| `Backend.Postgresql/OpenIddict/PostgresOpenIddictServiceExtensions.cs:93`, inside the four-argument `AddPostgresOpenIddictStores` | **never.** That overload has no caller in the repository |

The unreachable one still changes, because it still compiles, and a namespace that moves has to move everywhere.
It is named here so that a reviewer counting reachable registrations does not report the fourth as a phantom, and so that nobody spends the relocation story deciding whether to delete an overload; deleting it is a separate question this spike does not take up.

**On a self-contained deployment the host registers its default twice**, once at `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206` inside `ConfigureDatastore` and once inside the OpenIddict extension that `ConfigureIdentityProvider` calls immediately after it (`:90-91`).
Both register the same implementation, so the duplication changes nothing today, and it changes nothing under this design either: the replace-conflict count reads descriptors the per-hook diffs attribute to plugins and a host default is in nobody's record.
It is recorded because a reader who finds two host descriptors for a replace contract and expects one would otherwise conclude the guard is broken.

**The hashing-iterations key that actually binds is `IdentitySettings:ClientSecretHashingIterations`, and this design makes it live.**

The spine recorded the defect and changed no code.
Confirmed against the tree as it stands: the only `services.Configure<IdentityOptions>` in the repository is `Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:26`, it assigns thirteen properties, and `HashingIterations` is not among them.
`ClientSecretHasher` reads `IdentityOptions.HashingIterations` at `:38` and `:97` and therefore always reads the model default of `210000` (`Backend.OpenIddict/Models/IdentityOptions.cs:73`).
Both keys an operator would reach for are unbound: `IdentitySettings:ClientSecretHashingIterations`, declared at `Config.Frontend/appsettings.json:45`, and `IdentitySettings:HashingIterations`, which `eng/docker-compose/published-config.yml:54` and `eng/docker-compose/local-config.yml:58` both set.

Of the two, **the declared one becomes live**, and the compose files move to it.
Three reasons, in order of weight.

- It is the only one an operator can discover.
  `appsettings.json` is the shipped surface and the file the documentation points at; the compose variable is an artifact of this repository's own deployment examples.
- Making it live changes no deployment's behavior.
  Its shipped value is `210000`, which equals the model default, so every deployment that does not override it hashes exactly as it does today.
  Making `HashingIterations` live instead would silently change the work factor of every deployment that set `DMS_CONFIG_IDENTITY_HASHING_ITERATIONS` to something other than the default, which is a change to how stored secrets are verified arriving without anyone asking for it.
- It names what it configures.
  `IdentityOptions` has two other iteration-shaped and cache-shaped numbers already, and a bare `HashingIterations` on a type that also manages token lifetimes and key caches says less than it should.

The model property is renamed to `ClientSecretHashingIterations` in the same pass, so that the key, the property, and the setting the operator reads all carry one name.
The binder gains the assignment; the two compose files change to `IdentitySettings__ClientSecretHashingIterations`; and `IdentitySettings:HashingIterations` remains unbound, which is what it already is.

**The contract needs nothing from configuration, and that is why the defect is prerequisite rather than contained.**
A replacing plugin reads its own settings and applies its own work factor; the iteration count belongs to the host default alone.
What the defect costs is the thing a plugin exists to make possible: an operator whose policy mandates a work factor cannot reach it today, so their only remedy is to replace the hasher, and if they replace the hasher they never find out that the host's own knob was dead.
Fixing it is what makes the plugin an option rather than the only option.

**Changing the work factor does not invalidate stored secrets.**
The stored format is a version byte, a salt length, the salt, and the subkey (`Backend.OpenIddict/Services/ClientSecretHasher.cs:52-55`), and verification derives with `_identityOptions.Value.HashingIterations` rather than with a count read from the stored value (`:97`).
So a secret hashed at one count and verified at another fails verification.
This is a pre-existing property of the format, it is unchanged here, and it is stated in the operator documentation with the consequence spelled out: raising the count invalidates every client secret hashed at the old one, and the remedy is to re-issue them.
A stored-count format that would not have this property is [out of scope](#out-of-scope-and-deferred) and recorded there.

### CMS Host Integration

**This spike is the consuming epic the spine's deferred CMS rows were waiting for.**
The spine marked both CMS rows deferred on the ground that no epic drove them and that CMS's own candidate contract was a secrets contract.
It is, and here it is.

**The integration is the minimal one the spine described, and no bootstrap scaffolding is ported.**
`Config.Frontend/Program.cs` is a plain minimal-API startup: `CreateBuilder` at `:14`, `AddServices()` at `:16`, `Build()` at `:53`, `RunAsync()` at `:113`.
There is no `FileStartupStatusSignal`, no `RunBootstrapPhase`, and no `StartupPhaseExecutor`, and none is added.
The loader reports its own outcomes to `Console.Error` and throws, which is fail-loud behavior with nothing to build first.

```csharp
var builder = WebApplication.CreateBuilder(args);

LoadedPlugins loadedPlugins = PluginLoader.Load(
    builder.Configuration, CmsPluginContracts.Registry.ContractAssemblyNames);
loadedPlugins.ContributeConfiguration(builder.Configuration);

builder.AddServices(loadedPlugins);
```

`CmsPluginContracts` is CMS's own instance of the host-agnostic `PluginContractRegistry`, exactly as `DmsPluginContracts` is DMS's, and it declares two entries: `ISecretResolver` and `IClientSecretHasher`, both `Replace`.
`ContractAssemblyNames` is the registry's derived property, so the skew preflight covers `EdFi.Api.Plugins` and `EdFi.Api.Secrets` without a second list.

`AddServices` invokes `ContributeServices` after its own registrations and before `Build()`, which is where the recording wrapper's pre-existing-descriptor scope needs it, and registers the returned `PluginAuditInput` in the same call so that a plugin cannot displace it.

**The audit is a direct call rather than a startup task, because CMS has no startup-task machinery and inventing some for this would be the scaffolding the spine said not to port.**
After `Build()` and before `RunAsync()`, CMS resolves the `PluginAuditInput` it registered, calls `PluginRegistrationAudit.AuditAsync(input, app.Services)`, and throws on any finding after writing each one.
The audit function is host-agnostic by construction: it takes the input and a service provider and returns findings rather than throwing (`src/plugins/EdFi.Api.Plugins.Hosting/PluginRegistrationAudit.cs:47`).
The lifetime rule from [The Contract Package](#the-contract-package) is a CMS-side check beside it, over the same input, because a lifetime rule is per-contract metadata the shared audit does not hold.

**The inventory event is emitted before the audit runs**, for the attribution reason the spine gives: an audit failure names a service type, and only the inventory maps that type back to the plugin that registered it.
In CMS that ordering is trivially available, because both sit between `Build()` and `RunAsync()` and the logger exists at both.

**`src/config/Dockerfile` needs the same build-stage change `src/dms/Dockerfile` already carries, and it is a real change rather than a copy.**
The CMS image builds from a per-project `COPY` list inside the `src/config/` context with `src/` available as the named context `parentdir` (`src/config/Dockerfile:11`, and `build-contexts: parentdir=./src` at `.github/workflows/on-config-pullrequest.yml:395`, `:489`, `:583`), so `src/plugins/` is outside the tree the build stage assembles and the frontend's project reference into it would not resolve.
The DMS Dockerfile solved exactly this and its solution transfers whole: a `pluginsource` stage that copies `plugins/` from the named context and removes `bin/`, `obj/`, and `results.sarif` before the build stage sees it (`src/dms/Dockerfile:22-30`), the plugin project and lock files copied ahead of the restore (`:53-57`), `WORKDIR` moved down one level so both trees sit under a common parent holding the shared `Directory.Packages.props`, `nuget.config`, and `.editorconfig` (`:33-43`, `:59`), `dotnet restore` extended to the hosting project so a bad lock file fails inside the image (`:87`), and the plugin sources copied after the restore (`:109`).
The reason the DMS version stages and filters rather than copying the tree directly is measured and recorded in that file: `src/dms/.dockerignore` filters the default build context and a named context is filtered by a `.dockerignore` of its own, which `src/` does not have, so a direct copy carried a local build's `bin/`, `obj/`, and `results.sarif` into the image.
That reason applies unchanged to the CMS image, and a `src/.dockerignore` is still the wrong fix for the same reason it was then: it would change both images' contexts at once.

Two smaller CI notes travel with it.
`on-config-pullrequest.yml:124` already names `src/plugins/*` in the case that sets `config_relevant`, so a pull request touching only the plugin tree already runs the CMS lanes.
`:119`, which sets `fresh_build_required`, names `src/config/Dockerfile` and not `src/plugins/*`; once the CMS image's build context includes that tree, the same argument that put the Dockerfile on that list puts the tree on it.

**CMS's image needs nothing else.**
Acquisition is a deployment step in both recipes and both end in a read-only mount, and `eng/docker-compose/published-config.yml:60` already declares a `volumes:` block for that mount to join.
That CMS ships no NuGet client is irrelevant, because neither recipe asks it to fetch anything.

### Configuration Surface

CMS gains the spine's `Plugins` section, identical in shape and default to DMS's, and one section of this design's own.

```json
"Plugins": {
  "Directory": "/app/plugins",
  "Allowed": ""
},
"SecretsSettings": {
  "CacheExpirationSeconds": 300
}
```

`Plugins` is the spine's and is documented there; `Allowed` ships empty, so a CMS deployment that adopts nothing boots exactly as it does today.

`SecretsSettings` has one key and is deliberately not named for a plugin or a vault.
It configures the host's cache, which exists whether or not a resolver is installed and which is the host's to explain.
A value of `0` disables caching and resolves on every read, which is a supportable configuration for a deployment with very few data stores and a hard freshness requirement, and is documented as the thing to set while diagnosing a rotation that appears not to have taken effect.

Nothing in this design adds a per-feature switch.
A resolver runs if and only if a plugin supplying one is named in `Plugins:Allowed`, which is the spine's rule and the only question ever asked.

### Trust Model Notes

The spine's trust model is inherited whole: a loaded plugin runs with full process trust, the plugin root is read-only to the runtime in every deployment, and the operator's allowlist is consumed before Phase A runs so no plugin can influence what loads.
Three things are worth stating because this is the type that makes them concrete.

**A secrets plugin is the highest-value plugin in the system, and the design does not pretend otherwise.**
It is handed the process's configuration builder in Phase A and is called with the name of every secret CMS resolves in Phase B.
Nothing here contains it, and the spine's non-goal of sandboxing is inherited rather than revisited.
What the design does provide is that an operator who installed no plugin has no such component, and that an operator who installed one named it themselves in a configuration value a plugin cannot reach.

**The vault credential is not itself a secret this mechanism can hold.**
A plugin authenticating to a vault needs something to authenticate with, and it reads that from `bootstrapConfiguration` or from the ambient environment, which is exactly the plain-text configuration this design exists to get away from.
That is not circular in practice, because every store in scope supports an ambient workload identity rather than a static credential: `DefaultAzureCredential` and the AWS SDK's default chain are what the ODS documentation's own examples use, and neither puts a secret in configuration.
An operator who uses a static credential has reduced the problem from eight secrets to one, which is a real improvement and is the honest way to describe it.
The documentation says both halves.

**Secret names are logged; secret values never are.**
A resolution failure names the data store, the tenant, and the token name, because none of those is the secret and all of them are what an operator needs.
The resolved value appears in no log at any level, and the inner exception a resolver threw is recorded without its message being surfaced to an API client, on the same reasoning that already keeps a submitted connection string out of the validator's failure message (`Backend/Services/DataStoreConnectionStringValidator.cs:60-71`).

---

## Where the Code Lives

| Project | Packaged as | Referenced by |
| --- | --- | --- |
| `src/config/contracts/EdFi.DmsConfigurationService.Secrets` | `EdFi.Api.Secrets`, published | Third-party plugins; `Config.Frontend`, `Backend`, `Backend.OpenIddict`, `Backend.Postgresql`, and `Backend.Mssql` by project reference |

One new production project, in a new `src/config/contracts/` folder, which is where a package a third party compiles against belongs and which keeps it out of `backend/`, whose every existing project is an implementation.
It is added to `src/config/EdFi.DmsConfigurationService.sln` and, because the DMS end-to-end lanes bring CMS up, to nothing else; the DMS solution does not build CMS projects today and this is not the change that should make it start.

`src/plugins/` gains nothing.
`EdFi.Api.Plugins` and `EdFi.Api.Plugins.Hosting` are already in both solutions (`src/config/EdFi.DmsConfigurationService.sln:42`, `:44`, `:46`), which is work DMS-1496 did precisely so that CMS integration would not have to.

**The resolution seam is host code in `Backend/Services/`, beside the write rule it mirrors.**
`ConnectionStringWrite.cs` and `ConnectionStringCipherText.cs` are already there, and both exist to keep one rule from drifting across two resources and two engines.
The read rule joins them, and the engine-specific repositories keep no policy of their own.

**`CmsPluginContracts` is frontend-owned**, as `DmsPluginContracts` is, because the set of contracts a host declares is a host-specific value and `EdFi.Api.Plugins.Hosting` stays host-agnostic.

**Core is not involved, on either side.**
Neither `EdFi.DataManagementService.Core` nor any CMS backend gains a reference into `src/plugins/`.
The only DMS files this spike touches belong to the Phase A story: the contract in `src/plugins/`, the DMS frontend's `Program.cs` call site, and `docs/CONFIGURATION.md`.

---

## Testing Strategy

The layering follows the spine's: the contract and the resolution rule are unit-testable without a host, host integration is testable without a real vault, and the expensive combination is needed only for the end-to-end proof.

**Unit, over the token grammar.**
A value with one token, with two tokens, with a token at the start, at the end, and adjacent to another token; a value with no token, asserted to be returned without the resolver being called at all and without a decrypt; `${secret:}` with an empty name, `${secret:a b}` with a space, `${secret:a{b}` with a brace, and a `${` with no closing brace, each asserted to be left verbatim and to fail nothing.
A password whose literal text contains `${` but not a well-formed token is asserted unchanged, which is the regression test for the upgrade case.
Matching is asserted case-sensitive, so `${Secret:x}` is not a token.
The charset is asserted by table rather than by example, one case per permitted punctuation character and one per excluded one.

**Unit, over resolution.**
A resolver is faked and asserted to receive the token name and the tenant, with the tenant null under `TenantContext.NotMultitenant` and the tenant name under `TenantContext.Multitenant`.
The substituted value is asserted to be re-encrypted, and the assertion is a round trip through `ConnectionStringDecryptionService` rather than a comparison against a fixed string, because a fixed string would pin the initialization vector and `Encrypt` generates a fresh one per call.
A resolver that throws, one that times out, one that returns null, and one that returns an empty string are each asserted to fail the read with the data store and the token named and the resolved value absent from the message and from the captured log.
A token present with no resolver registered is asserted to fail with its own message naming the missing contract, distinct from the resolver-failed message.

**Unit, over caching.**
Two reads inside the window call the resolver once; a read after it calls again; two tenants asking for the same name are two entries and two calls; `CacheExpirationSeconds: 0` calls on every read.
A tenant removed while entries exist is asserted to leave every other tenant's entries intact and to require no invalidation, which is the staleness rule stated as a test.
Time is controlled through a fake time provider, so no test sleeps.

**Unit, over the twelve call sites.**
One test per repository method that projects a connection string, asserting the resolved value rather than the stored bytes reaches the response, driven from the same table for both engines so that a site added to one engine and not the other fails.
A test asserts the count: every `Convert.ToBase64String` over a connection string column in `Backend.Postgresql` and `Backend.Mssql` goes through the seam, asserted by reflection over the repository assemblies rather than by a reviewer counting, because twelve identical lines are exactly the shape a thirteenth gets added to unnoticed.
Nested derivative items are asserted to be resolved once, not twice.

**Unit, over the hashing-iterations key.**
`IdentitySettings:ClientSecretHashingIterations` set to a non-default value is asserted to reach `ClientSecretHasher`, which is the assertion that fails today.
The shipped `appsettings.json` value is asserted to equal the model default, so that the "no deployment changes behavior" claim is pinned rather than asserted in prose.
`IdentitySettings:HashingIterations` is asserted **not** to bind, so that nobody later makes both live and reintroduces the ambiguity this resolves.
A secret hashed at one count and verified at another is asserted to fail verification, which pins the stored-format property the documentation warns about.

**Unit, over cardinality and lifetime.**
Two plugins each registering `ISecretResolver` is fatal and names both; one plugin registering it twice is fatal and names the plugin and the count.
A plugin registering `IClientSecretHasher` once is asserted to load with the four host registrations present, which is the case that must pass and the one a naive descriptor count would refuse.
A plugin registering either contract as scoped or transient is refused with the plugin and the lifetime named.
A plugin that registers `ISecretResolver` with `TryAdd` is asserted to load, because there is no host default for the call to decline against, and the test carries a comment saying that is why rather than leaving a reader to infer that the implementer obligation is unenforced here for a different reason.

**Integration, over CMS host startup.**
A CMS host booted with `Plugins:Directory` pointed at a fixture plugin directory and `Plugins:Allowed` naming it, asserting an observable effect of both phases: a configuration value only the Phase A hook could have supplied, and a resolver only the Phase B hook could have registered, reached through a real read of a real data store row.
Fatal cases assert on the exception escaping host creation, because plugin loading happens before the container exists and CMS has no startup status file to record a phase in.
A boot with `Plugins:Allowed` empty and no plugin root present is asserted to behave exactly as the existing CMS suites expect, by those suites passing unchanged.
One test asserts a key supplied both by a Phase A source and by an environment variable resolves to the environment value, which is the spine's precedence rule arriving in the second host.

**Integration, over the read path.**
A data store inserted through the real `/v3/dataStores/` endpoint with a token in its `Password`, then read back, asserting the response decrypts to the resolved connection string; and the same row read with the resolver failing, asserting the documented failure rather than a pass-through.
The insert is asserted to succeed through the real validator, which is the probe-measured claim from [The Secret Reference](#the-secret-reference-runtime-data-secrets) turned into a test that fails if a future validator change makes the token unsubmittable.

**End-to-end, the load-bearing one.**
A fixture secrets plugin published `--no-self-contained`, delivered by the spine's Recipe 1 mount into a CMS deployment, supplying a Phase A source and a resolver backed by a file the harness writes rather than by a real vault.
The deployment brings up CMS and DMS together, creates a data store whose password is a token, and asserts that DMS serves a resource out of that data store.
That is the whole claim of this design: an operator's secret reaches the database driver without ever having been written to CMS, to a configuration file, or to an environment variable.
It runs against a locally built image, for the same reason the spine's equivalent tier does, and the pulled-stock-image proof is the spine's `DMS-1502` rather than a second one here.

**Consumer proof.**
The per-PR lane packs `EdFi.Api.Secrets` and compiles a scratch consumer against the produced nupkg, following `eng/verification/CustomValidationConsumer/`, and asserts that the `AssemblyVersion` inside the packed nupkg equals the package version, which is the assertion that keeps the skew preflight from going blind.

---

## Rejected Alternatives

| Alternative | Disposition | Reason |
| --- | --- | --- |
| Phase A for the eight configuration secrets, a Phase B resolver for secrets named by runtime data, a token inside the stored connection string, resolution in CMS, DMS unchanged | **Adopted** | Serves both kinds of secret from one plugin load, needs no change to how a connection string is validated or stored, and leaves the value's identity with the row that owns it rather than with a number CMS assigned |
| **The ODS data shape**: `DataStores:<id>:ConnectionString` in configuration, applied as an override at read time | **Rejected**, inheriting the spine's rejection and adding a reason | The spine rejects it because the tenant half of ODS's key is configuration in ODS and runtime data in DMS. The research adds the second half: ODS reads its override through `IOptionsMonitor<T>.CurrentValue` at resolution time rather than at startup (`ConnectionStringOverridesApplicator.cs:28`), so the shape is less startup-frozen than it looks, and it still forces the operator to create the row, learn the id CMS assigned, and then write a vault entry named after it. A reference the row carries is written in the same request that creates the row |
| **A whole-value reference**, `vault://prod/dms/ds-2026` as the stored connection string | **Rejected, probe-refuted** | Measured on `net10.0`: both `NpgsqlConnectionStringBuilder` and `SqlConnectionStringBuilder` throw `ArgumentException: Format of the initialization string does not conform to specification starting at index 0.` on it, so `DataStoreConnectionStringValidator` rejects it at the API before anything stores it. Accepting it means relaxing the one check that stands between an operator and an unopenable connection string, and it externalizes the hostname and database name along with the password |
| **A reference smuggled as a connection string keyword**, `SecretRef=prod/dms/ds-2026` | **Rejected, probe-refuted** | Both builders reject unrecognized keywords: measured, `Host=...` throws `Keyword not supported: 'host'` under `SqlConnectionStringBuilder` and `Encrypt=False` throws `Couldn't set encrypt` under `NpgsqlConnectionStringBuilder`, so neither engine has room for a keyword it does not know |
| **Resolving on write**, storing what the vault held at the moment the operator created the data store | Rejected | It is a copy, not a reference. Rotation would not propagate, which is the capability the feature exists for, and the secret would be back in the CMS database in the one form this design removes it from |
| **Resolving in DMS** rather than in CMS | Rejected | DMS does not hold the value, holds no vault credential, and would need a second resolver contract, a second plugin per deployment, and a second copy of the trust model. The spine's finding that the capability lands in CMS is what this follows |
| **A new DMS-facing endpoint** that resolves, leaving `/v3/dataStores/` unresolved for administrators | Rejected | Two shapes for one resource, and the exposure it would avoid is not an exposure: today's response is cipher text under a key every client of that endpoint already needs. It would also make the resolved and unresolved values differ by caller, which is the kind of thing that is debugged once per operator |
| **An escape sequence** so a password may contain a literal `${secret:...}` | Rejected | It puts a rule on every operator's password to serve a case no operator has, and the strict token grammar already leaves every `${` that is not a well-formed token verbatim |
| **Passing an unresolvable token through** to the caller rather than failing the read | Rejected | DMS would decrypt it successfully, hand `${secret:...}` to a driver as a password, and surface an authentication failure naming nothing. That is the silent degradation the spine refuses, arriving where it costs the most to diagnose |
| **Failing CMS startup** when a resolver cannot reach its store | Rejected | CMS serves claim sets, tenants, and applications that have nothing to do with a data store connection string, and a CMS that will not start is a DMS that cannot start. A vault outage fails the reads it actually affects |
| **A plugin-owned cache**, with the host caching nothing | Rejected | Rotation latency would become a per-vendor property with no configuration surface, no default, and no way for an operator to observe or change it. The contract is a pure function of name and tenant precisely so that the host can cache it |
| **A sliding expiration** on the host cache | Rejected | The question an operator asks is how long until a rotated secret takes effect, and a sliding window's answer is "it depends on traffic". An absolute window answers with a number |
| **Invalidating the cache from tenant administration** | Rejected | A new tenant has no entries to invalidate and a removed tenant's entries are unreachable by construction, so the coupling would exist to fix a staleness nothing can observe |
| **An `IChangeToken` or a push channel** from a plugin back into the host | Rejected | No secret store in scope pushes. The contract would be surface with no implementer, and the pull interval every store does offer is already what the host cache's expiration expresses |
| **Two packages**, one per contract | Rejected | Two versions, two skew-preflight entries, and two package ids burned permanently, to separate two contracts the same plugin usually implements and the same host consumes. The additive-only policy costs nothing to apply to a package with two interfaces in it |
| **Putting `ISecretResolver` in `src/plugins/`** beside the shared contract | Rejected | `src/plugins/` holds what both hosts consume. Only CMS resolves secrets, and a contract placed there would tell an implementer that DMS calls it |
| **Leaving `IClientSecretHasher` in `Backend.OpenIddict`** and packaging that assembly | Rejected | CMS also runs against Keycloak, and a contract shipping from an assembly named for one identity provider tells half the operators that the extension point is not for them. It would also publish an implementation assembly's whole surface as a compatibility commitment |
| **A host default for `ISecretResolver`** | Rejected | The two candidates are a resolver that fails every call, which is the no-resolver failure row under another name, and one that reads from configuration, which is Phase A. Absence is the clearer signal and the failure table is where it is read |
| **Making `IdentitySettings:HashingIterations` the live key** instead | Rejected | It is set in this repository's own compose files with an operator-overridable default, so making it live would silently change the work factor of any deployment that had set it, and a work-factor change invalidates every secret hashed at the old count. The declared key's shipped value equals the model default, so making that one live changes no deployment's behavior |
| **Making both keys live**, with one falling back to the other | Rejected | Two keys for one setting is the state this resolves, not a resolution of it, and a fallback means an operator who sets both has to know which wins |
| **Leaving the hashing-iterations defect to its own ticket** outside this spike | Rejected | The spine handed the question here explicitly, and a plugin contract for the hasher that left the host's own knob dead would make replacing the hasher the only way to reach a setting that was supposed to be configuration |
| **Changing the stored hash format** to carry its iteration count, so raising the work factor does not invalidate stored secrets | **Deferred, not rejected** | It is a real improvement and it is a different change: it touches a stored format, needs a migration path for values already written, and is not needed by anything in this design. Recorded in [Out of Scope and Deferred](#out-of-scope-and-deferred) |
| **Porting DMS's bootstrap phase wrapper to CMS** so plugin load failures are recorded in a startup status file | Rejected | The spine already settled it: the loader reports its own outcomes and throws, so CMS gets fail-loud behavior with nothing to build. Porting `FileStartupStatusSignal`, `RunBootstrapPhase`, and `StartupPhaseExecutor` to serve one caller is scaffolding this spike does not need and a second host's worth of it to maintain |
| **A CMS `IDmsStartupTask` equivalent** to carry the plugin audit | Rejected | CMS has no startup-task machinery and one interface plus one executor plus an ordering convention would exist for a single call between `Build()` and `RunAsync()`. The spine offers a direct call as the alternative and it is the right one here |
| **A shipped Ed-Fi vault plugin**, for Azure Key Vault or AWS Parameter Store | Rejected for this spike | It commits the Alliance to a cloud SDK's release cadence, its authentication surface, and its CVEs, for a component every deployment would want configured differently. The ODS documentation's own answer is worked examples an implementer copies, and that is what the documentation story writes. Repository fixtures for the tests are not that |
| **A `SecretsSettings:Enabled` switch** | Rejected | A resolver runs if and only if a plugin supplying one is allowlisted, which is the spine's one question. A second switch means a deployment can be allowlisted and silently inert |

---

## Out of Scope and Deferred

| Item | Status | What would bring it back |
| --- | --- | --- |
| A DMS-side secret resolver | Out of scope | A DMS secret that is not read from `IConfiguration` and is not obtained from CMS. There is none today |
| Identity-validation secrets | Out of scope | Nothing. They belong to [identity-DMS-1413](../identity-DMS-1413/design.md) under DMS-1412, and no coupling surfaced in the research |
| A stored hash format carrying its own iteration count | **Deferred** | A decision to raise the default work factor on an existing deployment, which is when invalidating every stored client secret stops being acceptable. It is a stored-format change with a migration path and belongs to whoever takes that decision |
| Deleting the unreachable four-argument `AddPostgresOpenIddictStores` overload | Deferred | It is dead code adjacent to this work rather than part of it. Named in [The `IClientSecretHasher` Relocation](#the-iclientsecrethasher-relocation) so a reviewer does not read it as an omission |
| `build-config.ps1`'s `${(Get-Date).year)}` in the props file `SetDMSAssemblyInfo` regenerates (`:157`), which is not a subexpression and empties the copyright | **Observed, not taken up** | The twin of a `build-dms.ps1` defect the plugin spine already recorded as a ticket candidate, found here while establishing which lanes stamp a version over this contract's own. It affects a generated file's copyright line and nothing this design turns on |
| The two other dead `IdentitySettings` keys, `MaxKeyCacheSize` at `Config.Frontend/appsettings.json:44` against the live `IdentitySettings:KeyFormatCacheSize`, and `OpenIddictTokenExpirationTimeMinutes` at `:40` against the live `IdentitySettings:TokenExpirationMinutes` | **Observed, not taken up** | Found while establishing that the hashing-iterations pair is dead, and the same defect in the same section. Neither is a secret and neither blocks anything here, so taking them would widen this spike into a configuration-key audit. They are recorded so the next reader of that file does not have to rediscover them, and either is a ticket whenever someone wants one |
| Encrypting CMS's own `DatabaseSettings:DatabaseConnection` at rest | Out of scope | It is the connection CMS opens to read everything else, so there is nowhere earlier to hold a key. Phase A is the answer to it and this design already gives it one |
| Re-keying or migrating stored connection strings | Out of scope | Nothing here changes the stored format. A row keeps whatever it holds until someone writes it again |
| A secret reference anywhere other than a data store connection string | **Deferred** | A second CMS-held value an operator wants in a vault. The token grammar and the resolver contract are indifferent to what they are substituting into, so the extension is a second call site rather than a second mechanism |
| Rotation without a restart for the eight process-global secrets | Out of scope | Every one of them is captured into a singleton at startup: `ConfigurationServiceContext` and `ConnectionStringDecryptionService` on the DMS side (`DmsCoreServiceExtensions.cs:360-371`), `IOptions<T>` snapshots on the CMS side. Making them reloadable is a change to how each host reads its own configuration and has nothing to do with plugins. A reloading Phase A source will fetch a new value; nothing will read it until a restart, and the documentation says so |
| Push-based or event-driven invalidation | Out of scope | No store in scope offers it |
| Sandboxing a secrets plugin | Out of scope | Inherited from the spine. Nothing available in .NET delivers it in-process |

---

## Level of Effort

Type only.
The delivery mechanism, the loader, the recording wrapper, and the cardinality guard are costed in the spine and are merged.

| Work | Size | Notes |
| --- | --- | --- |
| Phase A on the contract: `ContributeConfiguration`, the loader's invocation, source placement below the last environment variable source, the additive `Sources` guard, the `Contribute` cardinality row and its exemption in the no-contract check, the `docs/CONFIGURATION.md` precedence order, and the Phase A test rows | Small | The spine designed every line of it and assigned it here. One list move in the loader plus the test cases. The contract's version moves `1.0.0` to `1.1.0`, which is the first exercise of the additive-only policy and the reason the base class was chosen |
| `EdFi.Api.Secrets`: the new project, `ISecretResolver`, `SecretReference`, the relocated `IClientSecretHasher`, the solution entry, the lock file, and the pack-and-consumer-verify lane | Small | Follows the DMS-1432 and DMS-1496 pack-and-assert lanes exactly. The relocation is a namespace move across four registration sites and two consumers, one of the four unreachable |
| The hashing-iterations key: bind `IdentitySettings:ClientSecretHashingIterations`, rename the model property, move the two compose files | Small | Three files plus tests. Its value today equals the model default, so the behavior change on a stock deployment is nil, which is what makes it safe to take in the same story as the relocation |
| CMS host integration: the loader call, the Phase A invocation, `AddServices` taking the aggregate, `CmsPluginContracts`, the direct audit call, the lifetime check, the inventory event, the `Plugins` section, the frontend's `ProjectReference` | Small | `Config.Frontend/Program.cs` is a plain minimal-API startup and no scaffolding is ported. The registry and the audit function are host-agnostic and merged |
| `src/config/Dockerfile` build stage: the `pluginsource` filtering stage, the plugin project and lock file copies, the `WORKDIR` move, the extended restore, and the post-restore source copy | Small | Mechanical but load-bearing, and the DMS Dockerfile carries a worked version of every line of it with the measurements that produced each one. Both plugin projects already have committed lock files, or `--locked-mode` would already be failing the DMS image build |
| The resolution seam: the token grammar, the host cache, the failure semantics, and the twelve call sites | Medium | The grammar and the cache are the only genuinely new code. Twelve identical call sites are mechanical, and the reflection test that pins the count is what keeps a thirteenth from appearing unwatched. `Backend/Services/ConnectionStringWrite.cs` is the precedent for where the rule lives and why |
| `SecretsSettings`, the `Plugins` section in CMS, and their `docs/CONFIGURATION.md` entries | Small | One key of this design's own |
| Documentation: the operator chapter, the implementer guide, the worked vault examples, and the work-factor warning | Medium | The examples are the deliverable an implementer actually uses, and the ODS documentation is the model: source for an Azure Key Vault source and an AWS Parameter Store source, written against `ContributeConfiguration` instead of `ConfigureHost` |
| Publish `EdFi.Api.Secrets` | Small, **release-gated** | Blocked by every other row and blocking none. Burns a package id permanently, so it wants its own review |
| Test assets: the fixture secrets plugin, the file-backed resolver, and the CMS end-to-end deployment | Medium | The fixture plugin exercises both phases, which no fixture in the spine does, and the end-to-end tier brings up CMS and DMS together |

No row changes shipped behavior on a path DMS or CMS already ships, with one exception that is stated rather than buried: `IdentitySettings:ClientSecretHashingIterations` becomes live, and a deployment that had set it to something other than `210000` while believing it took effect will find that it now does.
Such a deployment does not exist in this repository's own examples, which set the other key, and the documentation names the consequence.

---

## Cross-References

- [plugins-DMS-1462/design.md](../plugins-DMS-1462/design.md) - the delivery mechanism, the two composition phases, cardinality, the trust model, and "The Secrets Spike", which is the handover this document answers
- [plugins-DMS-1462/ods-precedent.md](../plugins-DMS-1462/ods-precedent.md) - the ODS/API survey, its two seams, and the dispositions this document inherits
- [identity-DMS-1413/design.md](../identity-DMS-1413/design.md) - the other companion under the same spine, and the precedent for a replace-cardinality contract with a host default
- `src/plugins/EdFi.Api.Plugins/EdFiApiPlugin.cs` - the contract as merged, carrying `Name` and `ContributeServices` and no Phase A member
- `src/plugins/EdFi.Api.Plugins.Hosting/PluginLoader.cs:40`, `LoadedPlugins.cs:74`, `PluginContractRegistry.cs:145,:158`, `PluginRegistrationAudit.cs:47` - the loader entry point, the Phase B invoker, the registry and its derived assembly names, and the host-agnostic audit function CMS calls directly
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/PluginCompositionServiceExtensions.cs` - DMS's composition seam, and the shape CMS's own mirrors
- `src/dms/Dockerfile:22-30,:53-57,:87,:109` - the `pluginsource` staging stage, the pre-restore project copies, the extended restore, and the post-restore source copy that `src/config/Dockerfile` needs
- `.github/workflows/on-config-pullrequest.yml:119,:124,:395` - the fresh-build and relevance filters, and the `parentdir=./src` named context the CMS image build already declares
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Program.cs:14,:16,:53,:113` - the four lines CMS host integration sits between
- `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206,:372,:376` - the always-reached `IClientSecretHasher` registration and the two self-contained identity paths that register it again
- `Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:26` - the repository's only `Configure<IdentityOptions>`, which assigns thirteen properties and not `HashingIterations`
- `Backend.OpenIddict/Models/IdentityOptions.cs:73`, `Backend.OpenIddict/Services/ClientSecretHasher.cs:38,:97` - the model default the hasher always reads today
- `Config.Frontend/appsettings.json:44,:45`, `eng/docker-compose/published-config.yml:54`, `eng/docker-compose/local-config.yml:58` - the declared key, the one other dead key beside it, and the two compose files that set the other dead one
- `Backend/Services/ConnectionStringWrite.cs`, `Backend/Services/ConnectionStringCipherText.cs` - the two existing host-owned rules the read rule joins, and the precedent for one rule across two resources and two engines
- `Backend/Services/DataStoreConnectionStringValidator.cs:56-75` - the provider parse the token is designed to survive
- `Backend/Services/ConnectionStringEncryptionService.cs:19-38`, `Backend/Services/TenantContext.cs` - the encryption the resolved value is written back through, and the tenant the resolver is handed
- `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs:360-371` - DMS's two secrets, captured into singletons at startup, which is why Phase A serves them and nothing reloads them
- `src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:465,:529,:590` - the endpoint DMS reads and the two places it decrypts, neither of which changes
- `EdFi.Ods.Api/Configuration/ConnectionStringOverridesApplicator.cs`, `OdsInstanceConfigurationExtensions.cs:28`, `EdFi.Ods.Features/MultiTenancy/MultiTenantConnectionStringOverridesApplicator.cs` - the ODS override, read at resolution time and keyed by an admin-database row id, which is the precedent this design departs from with its reasons stated
