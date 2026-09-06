# Configuration

The sections below describe custom configuration options in the `appSettings.json`
file.

> [!NOTE]
> Environment Variables are supported and will take priority over
> `appsettings.json`. No special prefix is required on environment variable
> names. The standard convention for reading hierarchial keys from environment is
> to use a double underscore `__` separator. For example
> `AppSettings__Datastore=mssql`

## AppSettings

| Parameter                        | Description                                                                                                                                                                                                                   |
| --------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Datastore                        | The primary datastore used by the DataManagementService. Valid values are `postgresql` and `mssql`                                                                                                        |
| BypassTypeCoercion               | Type coercion attempts to coerce schema-guided request values to their proper type on `POST` and `PUT` requests. This includes boolean strings such as `"true"`, numeric strings such as `"100"`, and boolean numeric aliases such as `1` and `"0"`. This setting bypasses all request value type coercion for performance. |
| AllowIdentityUpdateOverrides     | Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates.                                                                           |
| MaskRequestBodyInLogs            | Controls whether to mask HTTP request bodies in log statements to avoid potentially logging PII. This setting only applies to `DEBUG` logging where requests are logged.                                  |
| UseApiSchemaPath                 | When set to `true`, the application loads core data standard and extension artifacts from the manifest-backed workspace at `ApiSchemaPath`. When `false`, it loads the bundled manifest-backed ApiSchema workspace from the application output. Loose no-manifest `ApiSchema*.json` folders are not a runtime loading contract. |
| ApiSchemaPath                    | Specifies the runtime ApiSchema workspace directory containing `bootstrap-api-schema-manifest.json` and the manifest-declared core and extension schema files. The ApiSchemaDownloader CLI can be used to download and extract the published ApiSchema packages before the root manifest is materialized. |
| DomainsExcludedFromOpenApi       | Comma separated list of domain names to exclude from OpenAPI documentation generation. Domains listed here will not appear in the generated OpenAPI specifications. Case insensitive. |
| IdentityProvider                 | Specifies the authentication provider. Valid values are `keycloak` (to use Keycloak's authentication) and `self-contained` (to use self-contained authentication). When using `self-contained`, you must also provide a value for `IdentitySettings:EncryptionKey`. Default: self-contained |
| RouteQualifierSegments           | Comma separated list of route qualifier context segments as defined by `dataStoreContexts` in Configuration Service. Example: "districtId,schoolYear" |
| MultiTenancy                     | When `true`, enables multi-tenancy mode where the tenant identifier is extracted from the URL route. Default: `false` |
| MaximumPageSize                  | Upper bound for the `limit` and `pageSize` query parameters on GET-many requests, and the page size applied when neither is supplied. Also the `default` and `maximum` published for those parameters in the OpenAPI specification. Must be greater than `0`; the service refuses to start otherwise. Environment override: `AppSettings__MaximumPageSize`. Default: `500` |
| DefaultPartitionCount            | Number of partitions returned by a resource or descriptor `/partitions` request that omits the `number` query parameter. Also the `default` published for `numberOfPartitions` in the OpenAPI specification. Must be between `1` and `200`, the same range accepted for `number`; the service refuses to start otherwise. Environment override: `AppSettings__DefaultPartitionCount`. See [Cursor Paging](./CURSOR-PAGING.md). Default: `10` |
| UseLegacyDocumentIdOrderingForChangeQueries | When `true`, restores unconditional `DocumentId` ordering and anchoring for change-version-filtered collection reads, disabling the conditional `ContentVersion` ordering and anchoring used for bounded and max-only change-version windows. Governs all three paging shapes of a GET-many collection: `limit`/`offset` page selection, `pageToken` cursor pages, and `/partitions` boundary calculation. **Changing this setting invalidates cursor and partition tokens already issued**: a client replaying one is answered with the invalid-page-token response and must restart its walk, so expect in-flight walks and distributed partition tokens to fail after a flip in either direction. Deployment-wide rollback switch for incident response; not per-client. See [Cursor Paging](./CURSOR-PAGING.md). Default: `false` |
| ReverseProxy:UseForwardedHeaders | When `true`, the application respects reverse proxy `X-Forwarded-*` headers for URL generation, but only from trusted sources configured in `ReverseProxy`. Default: `false`. See [Reverse Proxy and Forwarded Headers](#reverse-proxy-and-forwarded-headers). |
| ReverseProxy:KnownProxies        | Comma-separated list of exact trusted reverse-proxy IP addresses (IPv4 or IPv6) whose `X-Forwarded-*` headers are honored. Used only when `ReverseProxy:UseForwardedHeaders` is `true`. Example: `10.0.0.5,10.0.0.6` |
| ReverseProxy:KnownNetworks       | Comma-separated list of trusted reverse-proxy networks in CIDR notation whose `X-Forwarded-*` headers are honored. Used only when `ReverseProxy:UseForwardedHeaders` is `true`. Example: `10.0.0.0/8,172.16.0.0/12` |
| EnableApplicationResetEndpoint   | When `true`, enables the `/v3/applications/{id}/reset-credential` endpoint in the Configuration Service, allowing application credentials to be reset via API. When `false`, the endpoint is not registered and will return a 404 (Not Found) response. <br>**Recommended:** Set to `false` if you need to support multiple API clients per application, as enabling this endpoint may interfere with multi-client scenarios. Default: `false` |

## MappingPacks

DMS can load precompiled mapping packs (`.mpack`) instead of compiling mapping sets at
runtime. **Mapping packs are not available yet** — with the default settings (`Enabled` is
`false`) mapping sets are compiled at runtime today, but the configuration surface exists
and is validated. These settings are bound for both the PostgreSQL and SQL Server datastores.
Because mapping-set resolution runs at startup, setting `Enabled` to `true` with no pack
present makes DMS fail to start when `Required` is `true` or `AllowRuntimeCompileFallback`
is `false`. See the
[Relational Backend Developer Guide](./RELATIONAL-BACKEND.md#5-mapping-packs-optional).

| Parameter                   | Description                                                                                                                                  |
| --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| Enabled                     | When `true`, load mapping packs. When `false` (default), mapping sets are compiled at runtime.                                               |
| Required                    | When `true`, fail fast if a pack is missing or invalid. Only meaningful when `Enabled` is `true`; cannot be `true` while `Enabled` is `false`. Default: `false`. |
| RootPath                    | Filesystem root directory for `.mpack` files. Used only when `Enabled` is `true`. Default: none.                                            |
| AllowRuntimeCompileFallback | When `true` (default), allow runtime compilation when a pack is enabled but not found.                                                      |
| FailureCooldownSeconds      | Seconds a faulted cache entry is retained before eviction. `0` (default) evicts immediately.                                                |
| CacheMode                   | Cache strategy for compiled mapping sets. Currently only `InMemory` (default).                                                              |

## DataManagement:DocumentCache

`DataManagement:DocumentCache` configures optional DocumentCache projection and
cache-backed read acceleration. The fixed database inventory is always provisioned, but
runtime cache reads are opt-in and target-gated: DMS considers cached bodies only when
`ReadAcceleration:Enabled` is `true`, the request is an external resource or descriptor
GET/read, and the request's tenant/data-store pair has an exact `Targets` entry.

The relational read path remains the correctness path. Cache misses, stale cache rows,
lifecycle fences, target ineligibility, expected cache-read availability failures, and
direct-fill failures fall back to relational reads without changing the public response.
For the authoritative behavior, see the
[cache-backed read story](../reference/design/backend-redesign/epics/18-document-cache/05-cache-backed-read-path.md),
[cache-backed reads and lifecycle](../reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle),
and
[configuration and projection target selection](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection).
For operational workflows, use the
[DocumentCache operations runbook](../reference/document-cache-documentation/operations-runbook.md).

| Parameter                               | Description                                                                                                                                                  |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Targets                                 | Explicit cache target list. Each entry contains `TenantKey` and positive `DataStoreId`; duplicate entries after tenant normalization are invalid.             |
| ReadAcceleration:Enabled                | When `true`, eligible external GET-by-id and GET-many responses may use fresh `dms.DocumentCache` rows. Default: `false`.                                    |
| ReadAcceleration:DirectFillTimeout      | Positive duration that bounds optional best-effort direct fill after a successful relational fallback. Default: `00:00:00.250`.                              |
| Projector:PollInterval                  | Positive projector polling interval. Default: `00:00:05`.                                                                                                    |
| Projector:PageSize                      | Positive projector page size. Default: `100`.                                                                                                                |
| Projector:MaxConcurrentTargets          | Positive maximum number of cache targets a process may project concurrently. Default: `2`.                                                                    |
| Projector:FailureBackoff                | Positive delay before retrying projector work after a target-level failure. Default: `00:00:30`.                                                             |
| Projector:BaselineHighWaterMark         | Positive high-water mark used during baseline projection. Must be less than `int.MaxValue`. Default: `1000`.                                                  |
| Administration:WorkflowTimeout          | Positive timeout for cache administrative workflows. Default: `1.00:00:00`.                                                                                   |
| Status:StatusObservationTimeout         | Positive per-target timeout for observing durable DocumentCache status facts. Default: `00:00:05`.                                                           |
| Status:EndpointTimeout                  | Positive timeout budget for the `GET /health/document-cache` status endpoint. Default: `00:00:30`.                                                           |
| Status:RequiredRole                     | Single literal role token required to map and authorize `GET /health/document-cache`. Empty by default, leaving the endpoint unmapped. Recommended: `dms-document-cache-operator`. |

Direct fill uses the shared cache materializer/writer with purpose `DirectFill`; it does
not build cache rows from the shaped API response and it does not replace the response
already selected by relational fallback. Snapshot and read-replica requests are read-only
for this purpose, so direct fill is skipped for those targets when derivative routing is
available.

`Status:RequiredRole` must be one untrimmed token no longer than 256 characters. Values
containing ASCII whitespace, commas, semicolons, quotes, brackets, braces, or control
characters are invalid and leave the DocumentCache status endpoint unmapped.

## DataManagement:DocumentCache:Cdc

These settings configure the deployment-owned `dms-document-cache cdc` control plane;
their presence does not enable CDC. Start with the
[CDC runbook](../reference/cdc-documentation/operations-runbook.md#prerequisites).
Installation, syntax, confirmations, and exit codes remain in the
[CLI reference](../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md).
Design owners: [target selection](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection),
[binding identity](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding),
[readiness](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness),
and [record size](../reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size).

### CDC configuration sources and targets

For the shipped CLI, increasing precedence is: optional `appsettings.json` in the starting
directory, optional `appsettings.{environment}.json`, explicit `--settings` file,
environment variables, then supplied CLI overrides. Environment selection is
`--environment`, then `DOTNET_ENVIRONMENT`, then `ASPNETCORE_ENVIRONMENT`. The CLI's
configuration builder does not itself load user secrets or remote secret providers;
deployment automation must supply their resolved values through the loaded sources.
Use `__` for environment section separators, for example
`DataManagement__DocumentCache__Cdc__LagThreshold=00:00:30`. Dictionary keys retain dots.
Omitted CDC flags leave configuration intact; request-only flags such as the previous
generation and provisioning assertions are not configuration keys.

Every CDC target must be an explicit `DataManagement:DocumentCache:Targets` entry on a
running DMS projector host and in the control plane's configuration. Initial-enable
preflight reads that membership before the CLI's invocation target override;
`--data-store-id` alone does not establish projector configuration. `TenantKey` is empty
for the default tenant and `DataStoreId` is positive. A CDC binding record spells that
tenant `default`; translate it back to the empty key (or omit `--tenant-key`) for the CLI.
Literal `--tenant-key default` names a different, nonexistent deployment tenant. Target
configuration neither changes durable lifecycle nor opens writer admission; read
acceleration is independently optional.

### CDC identity, endpoints, and policy

Names below are relative to `DataManagement:DocumentCache:Cdc`. Required text defaults to
empty and must be nonblank. Required positive integers default to zero and must be supplied.
Values supplied by local wrappers are not defaults of the CLI options object.

| Parameter | Requirement and shipped default |
| --- | --- |
| DeploymentKey, InstanceKey, TopicPrefix | Required text contributing to governed artifact names; generated-name validation also applies. |
| Generation | Required positive `Int64` binding generation; the CLI does not select the latest generation automatically. |
| PartitionCount | Required positive `Int32` public-topic partition count. |
| KafkaBootstrapServers | Required broker bootstrap addresses. Control-plane and connector clients must reach the same Kafka cluster. |
| ConnectBaseUri | Required absolute HTTP(S) Kafka Connect REST URI. |
| ConnectMetricsBaseUri | Optional absolute HTTP(S) base URI for the worker's Jolokia bridge. Blank derives the scheme and host from `ConnectBaseUri`, port `8778`, root path `/`. The reader appends `jolokia/read/...`; supply the bridge base, not the read URL. The image enables its bridge with `ENABLE_JOLOKIA=true`. |
| ConnectWorkerKey | Required worker-group identifier used for offset-store validation. |
| ConnectOffsetStorageTopic | Required shared worker offset-topic name; never a per-binding teardown artifact. |
| DurabilityProfile | Required `local` or `production`, case-insensitive; no default. Selects governed replication/in-sync-replica expectations from the [topic contract](../reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic). |
| MaxRecordBytes | Required positive `Int32` byte budget; no default. Drives producer/topic configuration and broker-limit verification. Consumers must support the same end-to-end budget. |
| ProducerBufferBytes | Optional byte override, at least `max(33554432, MaxRecordBytes)`. The renderer uses that minimum when omitted. |
| HeartbeatInterval | Optional positive `TimeSpan`; omitted renders `5000` milliseconds. |
| SqlServerPollInterval | Optional at generic options binding, but required by the SQL Server renderer: positive and no greater than the effective heartbeat interval. No default; not required for PostgreSQL. |
| LagThreshold | Positive `TimeSpan` for connector-lag assessment; default `00:00:30`. |
| DmsBaseUrl | Default empty; supplied values must be absolute HTTP(S) URIs. Workflows collecting projection evidence require the running DMS URL. |
| DmsBearerToken | Default empty; workflows collecting projection evidence require a bearer token with the configured DocumentCache status role. Excluded from options JSON serialization. Adoption and retirement do not require projection-status credentials. |

Configuration intervals use `TimeSpan` notation, for example `00:00:05`; CLI timeout
options ending in `-seconds` take positive numeric seconds. Connector heartbeat/poll
intervals render as milliseconds rounded upward. Lag observations use milliseconds;
missing metrics do not prove zero lag.

The binding contract owns identity. Timeouts, lag thresholds, credentials, and buffer/record
budgets are operational policy, not additional binding identity. Policy changes do not
authorize source or artifact changes. Record-budget changes follow the
[coordinated increase procedure](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase).

### CDC principals and credentials

| Parameter | Requirement and shipped default |
| --- | --- |
| SetupPrincipal | Required database setup principal name on every CDC verb, including validate-only operations. Separately supply a source connection with the necessary setup authority. |
| ConnectorDatabasePrincipal | Required database login/role, such as `cdc_reader`, on every CDC verb; distinct from the Kafka identity. |
| AclsEnabled | Default `false`. For a broker without an authorizer, evidence is not applicable, not proof of production isolation. |
| ConnectorKafkaPrincipal, ConnectWorkerPrincipal | Default empty; required with `AclsEnabled=true`, in broker `<type>:<name>` form such as `User:cdc_reader`. |
| Consumers | Default empty list. Each entry pairs nonblank `Principal` and `ConsumerGroup`. Both are individually unique using ordinal comparison: no principal gets multiple groups and no group is shared. With ACLs enabled, principals must use broker typed form. |
| ProviderConnectionProperties | Default empty dictionary. Renderer requires `database.hostname`, `database.user`, `database.password`, and PostgreSQL `database.dbname` or SQL Server `database.names` (exactly one database). Only provider-allowed connection keys are accepted. This configures connector access, separate from CMS-resolved setup access. |
| KafkaClientSecurityProperties | Default empty dictionary for the connector's Java client. Allow-listed properties become `producer.override.*` and SQL Server schema-history client settings. Secret-bearing properties require externalized Connect references. |
| KafkaAdminClientSecurityProperties | Default empty dictionary for the control plane's librdkafka client. Only `CdcControlOptions.AdminClientSecurityPropertyNames` are accepted, with nonblank values. Supply resolved secrets through protected configuration; Java JAAS/keystore property names are not interchangeable with librdkafka names. |

For example, connector `database.password` can use `${env:CDC_SOURCE_PASSWORD}` when the
worker has that named secret and its environment config provider enabled. Keep passwords,
tokens, connection values, and JAAS secrets out of invocations and evidence artifacts.
The [renderer input validator](../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcConnectorTemplateInputValidation.cs)
owns provider/Java allow-lists, externalized-secret checks, and reserved generated keys;
operator dictionaries cannot override generated capture, transform, or topic configuration.
The [control options and validator](../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control/CdcControlOptions.cs)
own the separate admin allow-list. See [security ownership](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

### CDC timeouts and durable state

These positive `TimeSpan` values are under `DataManagement:DocumentCache:Cdc:Timeouts`.
Timeout does not substitute for evidence or establish that a mutation rolled back.

| Parameter | Default |
| --- | --- |
| EligibilityProbe, KafkaAdmin, ConnectRequest, StatusEndpoint | `00:00:30` each |
| ProviderSetup | `00:05:00` |
| ProjectionCaughtUp, ProviderBarrier | `00:10:00` each |
| PollInterval | `00:00:02` between evidence reads |

`DataManagement:DocumentCache:Cdc:BindingStateStore:RootPath` defaults to
`eng/docker-compose/.cdc-state`, resolved against the CLI starting directory.
`--cdc-binding-state-path` overrides it. The shipped filesystem store is single-controller;
no remote state adapter is shipped. Preserve binding, incident, and retirement records
on durable restricted storage; see the [state handoff](../reference/cdc-documentation/operations-runbook.md#deployment-state).

Local wrappers resolve the host mount separately: explicit `-CdcBindingStatePath` where
supported, ambient `DMS_CDC_BINDING_STATE_PATH`, that key in the environment file, then
`./.cdc-state`. An explicit relative path uses the caller's directory; environment/default
relative paths use `eng/docker-compose`. Wrappers export the resolved path for Compose,
mount it at `/state`, and pass `/state` to the CLI. `DMS_CDC_BINDING_STATE_PATH` is a
wrapper/Compose input, not a direct CLI alias. Setup, status, stop, and retirement must
resolve the same store.

## Configuration Service AppSettings

The following parameters apply to the DMS Configuration Service (`appsettings.json`).

| Parameter                    | Description                                                                                                                                                                                               |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Datastore                    | The primary datastore used by the Configuration Service. Valid values are `postgresql` and `mssql`                                                                                                        |
| DeployDatabaseOnStartup      | When `true` the database will be created and initialized on startup.                                                                                                                                      |
| IdentityProvider             | Specifies the authentication provider. Valid values are `keycloak` and `self-contained`. Default: `self-contained`                                                                                        |
| MultiTenancy                 | When `true`, enables multi-tenancy support in the Configuration Service. Default: `false`                                                                                                                 |
| PathBase                     | Segment of the URL to use as base for all requests.                                                                                                                                                       |
| TokenRequestTimeoutSeconds   | Timeout in seconds for token requests. Default: `30`                                                                                                                                                      |
| ReverseProxy:UseForwardedHeaders | When `true`, the application respects reverse proxy `X-Forwarded-*` headers for URL generation, but only from trusted sources configured in `ReverseProxy`. Default: `false`. See [Reverse Proxy and Forwarded Headers](#reverse-proxy-and-forwarded-headers). |
| ReverseProxy:KnownProxies    | Comma-separated list of exact trusted reverse-proxy IP addresses (IPv4 or IPv6) whose `X-Forwarded-*` headers are honored. Used only when `ReverseProxy:UseForwardedHeaders` is `true`. Example: `10.0.0.5,10.0.0.6`                                                  |
| ReverseProxy:KnownNetworks   | Comma-separated list of trusted reverse-proxy networks in CIDR notation whose `X-Forwarded-*` headers are honored. Used only when `ReverseProxy:UseForwardedHeaders` is `true`. Example: `10.0.0.0/8,172.16.0.0/12`                                                  |

## Reverse Proxy and Forwarded Headers

When the DMS API or Configuration Service runs behind a reverse proxy or load balancer
(for TLS termination, host-based routing, etc.), the proxy forwards the original request
details in the `X-Forwarded-For`, `X-Forwarded-Host`, and `X-Forwarded-Proto` headers.
These are used to generate correct absolute URLs (for example, the Discovery API `urls`
and the Configuration Service information endpoint).

Set `AppSettings:ReverseProxy:UseForwardedHeaders` to `true` to process these headers. To prevent
spoofing, forwarded headers are honored **only** when the immediate client (the proxy) is
a trusted source. Configure trusted sources with:

- `AppSettings:ReverseProxy:KnownProxies` — comma-separated exact proxy IPs (IPv4/IPv6).
- `AppSettings:ReverseProxy:KnownNetworks` — comma-separated proxy networks in CIDR notation.

Behavior:

- `ReverseProxy:UseForwardedHeaders=false` (default): forwarded headers are ignored entirely.
- `ReverseProxy:UseForwardedHeaders=true` with no trusted sources configured: only loopback addresses
  are trusted (the ASP.NET Core default). This is safe, but a proxy container running on
  another host is not trusted unless you configure its address explicitly.
- `ReverseProxy:UseForwardedHeaders=true` with trusted sources: forwarded headers are honored only
  when the connecting proxy matches `KnownProxies` or `KnownNetworks`; otherwise they are
  ignored.

Invalid IP or CIDR values cause the service to fail startup with a configuration error.

### Examples by environment

**Local (no proxy)** — leave reverse proxy support disabled (the default):

```
USE_REVERSE_PROXY_HEADERS=false
```

**Local Docker (proxy container on the Docker network)** — trust the Docker bridge network
so the proxy container is recognized:

```
USE_REVERSE_PROXY_HEADERS=true
REVERSE_PROXY_KNOWN_NETWORKS=172.16.0.0/12
```

**Production (known load balancer/ingress)** — trust only the specific proxy addresses or
their subnet:

```
USE_REVERSE_PROXY_HEADERS=true
REVERSE_PROXY_KNOWN_PROXIES=10.20.30.40,10.20.30.41
# or a subnet:
REVERSE_PROXY_KNOWN_NETWORKS=10.20.30.0/24
```

For the Configuration Service, use the `DMS_CONFIG_`-prefixed variables
(`DMS_CONFIG_USE_REVERSE_PROXY_HEADERS`, `DMS_CONFIG_REVERSE_PROXY_KNOWN_PROXIES`,
`DMS_CONFIG_REVERSE_PROXY_KNOWN_NETWORKS`).

## DatabaseOptions

| Parameter      | Description                                                                                                                                                              |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| IsolationLevel | The `System.Data.IsolationLevel` to use for transaction locks. See [documentation](https://learn.microsoft.com/en-us/dotnet/api/system.data.isolationlevel?view=net-8.0) |

## ConfigurationServiceSettings

These settings configure how the DMS API connects to the Configuration Service to retrieve claim sets, data stores, and other metadata. `EncryptionKey` must match CMS `DatabaseSettings:EncryptionKey`.

| Parameter              | Description                                                                                                                                                              |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| BaseUrl                | The base URL of the Configuration Service. Example: `http://ed-fi-api-config:8081`                                                                                     |
| ClientId               | The client identifier (client ID) used to access the Configuration Service endpoints.                                                                                    |
| ClientSecret           | The client secret associated with the client ID for accessing the Configuration Service endpoints. Set via the `CONFIG_SERVICE_CLIENT_SECRET` environment variable. Must satisfy the CMS client-secret rules described in [IdentitySettings.ClientSecretValidation](#identitysettingsclientsecretvalidation). |
| EncryptionKey         | Key used to encrypt and decrypt Configuration Service connection strings. Set via the `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` environment variable and must match CMS `DatabaseSettings:EncryptionKey`. Used by `provision-dms-schema.ps1` to decrypt protected CMS datastore connection strings. DMS requires only a non-empty value; CMS rejects its `DatabaseSettings:EncryptionKey` at startup unless the value is at least 32 characters, ASCII, and does not derive the same key as the former shipped `appsettings.json` default. See the note below for valid-value semantics. |
| Scope                  | The authorization scope required for accessing the Configuration Service endpoints. Example: `edfi_admin_api/authMetadata_readonly_access`                               |

> [!NOTE]
> **Shared key.** In the provided Docker Compose files, a single
> `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` value feeds both the CMS
> `DatabaseSettings__EncryptionKey` and the DMS
> `ConfigurationServiceSettings__EncryptionKey`. CMS encrypts datastore connection
> strings with this key and DMS decrypts them with the same key (and
> `provision-dms-schema.ps1` decrypts with it too), so all three must be configured
> with an identical value.
>
> **Valid values.** The Configuration Service validates its `DatabaseSettings` at
> startup and refuses to start when `DatabaseConnection` is blank, or when
> `EncryptionKey` is blank, shorter than 32 characters, contains a non-ASCII
> character within the first 32 characters, or derives the same key as the former
> shipped `appsettings.json` default — that is, its first 32 characters match the
> default's first 32 characters, whatever follows them. DMS enforces only that
> its `ConfigurationServiceSettings:EncryptionKey` is non-empty, so it must be
> given the same value the Configuration Service accepted.
>
> The AES-256 key is derived from the UTF-8 bytes of the configured text,
> right-padded with `0` to 32 characters and then truncated to the first 32
> characters (see CMS `ConnectionStringEncryptionService`, DMS
> `ConnectionStringDecryptionService`, and `provision-dms-schema.ps1`). That
> derivation is unchanged, so its consequences still apply wherever the startup
> rules are not enforced — a DMS configured on its own, or one reading connection
> strings written by a Configuration Service that predates this validation:
> - Only the first 32 characters are significant; any characters beyond 32 are ignored.
> - Values shorter than 32 characters are zero-padded, which weakens the key. A
>   weak value that both sides share still decrypts successfully, with no warning.
> - Multi-byte (non-ASCII) characters within the first 32 push the UTF-8 length
>   past 32 bytes and break AES key initialization.
>
> Required by the Configuration Service, and therefore the value to use
> everywhere: a 32-character ASCII string.
>
> **Changing the encryption key.** Connection strings already stored by the
> Configuration Service were encrypted with the previous key and are not
> re-encrypted automatically. After setting a new key, re-submit each data store
> and data store derivative connection string through the Admin API; an update
> stores the value encrypted under the currently configured key. Until a
> connection string has been re-submitted, DMS cannot decrypt it and reports a
> decryption failure.
>
> This applies to local Docker Compose stacks as well, where the environment
> files under `eng/docker-compose/` supply the key. Picking up an updated
> environment file changes the derived key, so a database volume created before
> the change still holds connection strings encrypted under the previous one.
> `provision-dms-schema.ps1` then fails with a decryption error even though CMS
> and DMS agree on the new value — the mismatch is with the stored data, not
> between the services. Recreate the database volume, or apply the re-submission
> procedure above.

## CacheSettings

These settings configure DMS in-memory cache behavior. Expiration values are in seconds.

| Parameter                                | Description                                                                                                                |
| ---------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| ClaimSetsCacheExpirationSeconds          | The duration before cached claim sets expire and are refreshed from the Configuration Service. Default: `600`              |
| ApplicationContextCacheExpirationSeconds | The duration before cached application context metadata expires and is refreshed from the Configuration Service. Default: `600` |
| TokenCacheExpirationSeconds              | The duration before cached Configuration Service OAuth tokens expire. Default: `1500`                                      |
| ProfileCacheExpirationSeconds            | The duration before cached profile metadata expires and is refreshed from the Configuration Service. Default: `1800`       |
| DataStoreCacheRefreshEnabled             | When `true`, enables TTL-based refresh of cached data store configuration from the Configuration Service. Default: `true`  |
| DataStoreCacheExpirationSeconds          | The duration between automatic refreshes of cached data store configuration. Default: `600`                                |
| DerivativeValidationCacheExpirationSeconds | The duration a validation verdict for a read replica or snapshot database stays cached. Default: `600`, accepted range `1`–`3600` |

### DerivativeValidationCacheExpirationSeconds

A read replica or snapshot is validated the first time a request is routed to it, and that verdict is
cached for this duration.

- **Default `600`.** Used when the setting is absent from configuration.
- **Accepted range `1` to `3600`.** A value inside the range is used as configured, with no log entry.
- **A value above `3600` is clamped to `3600`**, and the clamp is logged as a warning naming both the
  configured and the effective value.
- **A value of `0` or below falls back to the default `600`**, not to the minimum `1`, and that
  fallback is logged as a warning naming both the configured and the effective value. This is not a
  clamp: clamping to the accepted range would give `1`, which would re-validate on nearly every
  request.
- **A non-positive value means "use the default", not "never expire."** This deliberately inverts the
  convention of `DataStoreCacheExpirationSeconds`, where `0` or a negative value keeps the cached
  configuration until an explicit reload. There is no way to ask for a derivative verdict that never
  expires: a derivative is a database an operator can rebuild or repoint without telling DMS, so a
  verdict about one that never expired would outlive the database it describes.
- **It is further bounded by the data store cache TTL.** A derivative's connection string comes from
  the cached data store configuration, so when `DataStoreCacheRefreshEnabled` is `true` and
  `DataStoreCacheExpirationSeconds` is positive, the effective expiration is the smaller of the two —
  a verdict never outlives the connection string it was reached for. When refresh is disabled, or
  `DataStoreCacheExpirationSeconds` is `0` or negative, that configuration is held until an explicit
  reload, so there is no shorter lifetime to bound by and the resolved value is used as is. The result
  is bounded either way.

Set it through the environment as
`CacheSettings__DerivativeValidationCacheExpirationSeconds`, or in Docker Compose through
`DMS_DERIVATIVE_VALIDATION_CACHE_EXPIRATION_SECONDS`.

## IdentitySettings.ClientSecretValidation

These settings configure the allowed client-secret length range used by CMS registration validation and by CMS startup validation for configured client secrets.

| Parameter       | Description                                                                                                           |
| --------------- | --------------------------------------------------------------------------------------------------------------------- |
| MinimumLength   | Minimum allowed client-secret length. Default: `32`                                                                   |
| MaximumLength   | Maximum allowed client-secret length. Default: `128`                                                                  |

`IdentitySettings.ClientSecretValidation` controls the accepted size range used by CMS registration, generated secrets, and startup validation. CMS startup also requires configured client secrets to satisfy the same lowercase/uppercase/number/special-character complexity rules enforced by registration, where supported special characters are `!@#$%^&*()-_=+[]{}:;,.?`. The bounds are set from the `DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH` / `DMS_CONFIG_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH` environment variables.

> [!IMPORTANT]
> Two different secrets are validated on two different paths — do not conflate them:
>
> - **`DMS_CONFIG_IDENTITY_CLIENT_SECRET`** is the CMS's own client secret
>   (`IdentitySettings:ClientSecret`, client `DmsConfigurationService`). CMS validates it at
>   **startup** via `IdentitySettingsValidator`; when it is invalid (for example, shorter than
>   the configured minimum), `ReportInvalidConfiguration` in `Program.cs` returns true,
>   `InitializeDatabase` is skipped, and the DbUp migrations that create the OpenIddict tables
>   never run — causing `start-local-dms.ps1` to fail.
> - **`CONFIG_SERVICE_CLIENT_SECRET`** is the DMS-to-CMS client secret
>   (`ConfigurationServiceSettings:ClientSecret`, client `CMSReadOnlyAccess`) that DMS uses at
>   runtime to obtain CMS tokens. It is validated when that client is **registered** by the
>   setup scripts, not by the CMS startup validator.
>
> Both secrets must satisfy the length and complexity rules above. During initial identity
> provisioning the local startup scripts register each client from its env-file secret and pass
> the env-file length bounds (`DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH` /
> `_MAXIMUM_LENGTH`) to `setup-keycloak.ps1` / `setup-openiddict.ps1`, so a secret that is valid
> for CMS is not rejected by the setup scripts' own default 32/128 bounds.
>
> Registration applies only to clients that do not yet exist: `setup-keycloak.ps1` warns and
> skips a client that is already present, and `setup-openiddict.ps1` inserts with
> `ON CONFLICT (ClientId) DO NOTHING`. Changing one of these secrets therefore does **not**
> update an already-registered client. To apply a new value, recreate the identity state first —
> run `teardown-local-dms.ps1` and set up again, or drop the Keycloak realm / `dmscs` OpenIddict
> tables — then start with the new secret.

## RateLimit

Basic rate limiting can be applied by supplying a `RateLimit` object in the
`appsettings.json` file. If no `RateLimit` object is supplied, rate limiting is
not configured for the application. Rate limiting (when applied) will be set
globally and apply to all application endpoints.

The shipped default is `PermitLimit: 20000`, `Window: 10`, `QueueLimit: 0`,
an average of 2,000 requests per second; a fixed window permits the full
20,000 as a burst at any point within each 10-second window, then rejects
until the window resets. `RateLimit__*` environment variables
shadow `appsettings.json`; the Docker Compose stacks set these from
`DMS_RATE_LIMIT_PERMIT_LIMIT`, `DMS_RATE_LIMIT_QUEUE_LIMIT`, and
`DMS_RATE_LIMIT_WINDOW` (see `.env.example`).

Omitting the `RateLimit` object disables limiting entirely, but that only
applies to a deployment configured purely through `appsettings.json`. The
provided compose stacks always set the `RateLimit__*` variables, and their
`${VAR:-default}` fallback supplies the shipped default even when the
variable is set but empty, so in those stacks the limiter is always on - it
can be raised, but not disabled.

Bulk and seed loads driven by `BulkLoadClient` are bursty, and a
`PermitLimit` set below the load's peak 10-second demand fails the load
outright with a storm of `429` responses rather than merely slowing it
down. Operators running a load larger than the `Populated` seed template
should raise `DMS_RATE_LIMIT_PERMIT_LIMIT` for the duration of the load.

The limiter partitions on the raw value of the request's `Host` header, so
every client sending the same hostname shares one bucket. It caps load per
distinct `Host` value, not per client, and because the default
`AllowedHosts` is `*`, a client that varies the `Host` header obtains
additional buckets, so it is not a strict cap on total backend load either.
Treat it as a coarse backstop; per-client isolation and DoS protection
belong at the application gateway.

The `RateLimit` object should have the following parameters.

| Parameter   | Description                                                                                                                                                                                                                                                                |
| ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| PermitLimit | The number of requests permitted within the window time span. This will be the number of requests, per hostname permitted per timeframe (`Window`). Must be > 0.                                                                                                           |
| Window      | The number of seconds before the `PermitLimit` is reset. Must be > 0.                                                                                                                                                                                                      |
| QueueLimit  | The maximum number of requests that can be Queued once `PermitLimit`s are exhausted. These requests will wait until the `Window` expires and will be processed FIFO. When the queue is exhausted, clients will receive a `429` `Too Many Requests` response. Must be >= 0. |

### Rejection response

A rejected request receives a `429 Too Many Requests` response with a
`Retry-After` header and an Ed-Fi problem-details body served as
`application/problem+json`. `Retry-After` carries the limiter-supplied
recommended retry delay, rounded up to whole seconds. It is a suggested
wait, not an exact countdown to the current window's reset — queue depth
can make the recommended delay longer than one configured `Window`. The
header is omitted in the unusual case that the limiter supplies no
retry-after metadata, and the body is served either way. The
`correlationId` is taken from the request header named by
`AppSettings:CorrelationIdHeader` when that setting and header are
present, otherwise from the server-generated trace identifier — the same
selection the API's other error responses use.

```json
{
  "detail": "The number of allowed requests has been exceeded. Retry the request later.",
  "type": "urn:ed-fi:api:too-many-requests",
  "title": "Too Many Requests",
  "status": 429,
  "correlationId": "0HNCTN1IRQMDG:00000001",
  "validationErrors": {},
  "errors": []
}
```

## CircuitBreaker

DMS routes its document CRUD calls - GET by id, query, POST, PUT, DELETE, and
tracked-change queries - through a Polly resilience pipeline whose outermost
strategy is a circuit breaker.
Endpoints that do not read or write documents, such as
`availableChangeVersions`, bypass the pipeline and keep answering while the
breaker is open.
It counts backend calls that *return* an unknown-failure result - the outcome
DMS uses for a failure it cannot turn into a specific answer for the client.
Recognized outcomes such as a deadlock victim, a constraint violation or a
validation failure never count toward it, because each maps to its own result.
Two consequences are worth knowing:

- On SQL Server, a write failure whose outcome is indeterminate - a command
  timeout, which expires on the client and leaves it unknown whether the server
  applied the write - is recognized by the classifier yet still reported as an
  unknown failure, so it does count toward the breaker.
  That is deliberate: a sustained run of them is the signal that the backend is
  unhealthy.
  The PostgreSQL classifier does not currently recognize this case; an Npgsql
  client-side timeout is not a `PostgresException`, so it escapes as an
  exception instead and falls under the next point.
  Either way it is never retried and never answered as a client error.
- A failure that escapes as an exception rather than a result does not count.
  The breaker's predicate inspects returned results only.

When the breaker opens, every request that reaches the pipeline is refused for
`BreakDurationSeconds`.
That includes reads: the pipeline is shared by GET, query, POST, PUT and
DELETE alike, so an open breaker pauses document reads as well as writes.

| Parameter               | Description                                                                                                                                                                  |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| FailureRatio            | Fraction of sampled calls that must fail before the breaker opens. Greater than 0 and at most 1; `0.1` means 10%.                                                             |
| SamplingDurationSeconds | Length of the rolling window over which the failure ratio is assessed. From 0.5 to 86400 (one day), inclusive.                                                                |
| MinimumThroughput       | Minimum number of calls that must occur inside the sampling window before the breaker may open at all. At least 2.                                                            |
| BreakDurationSeconds    | How long the breaker stays open before it admits a trial call. From 0.5 to 86400 (one day), inclusive.                                                                        |

DMS refuses to start when a value falls outside those bounds, naming the
offending setting rather than letting the resilience pipeline fail later.
The duration bounds are inclusive at both ends, matching the range the pipeline
enforces rather than its prose, which describes the lower bound as exclusive.
`FailureRatio` is the one place DMS is stricter than the pipeline: a ratio of
exactly `0` is accepted there but rejected here, because it asks the breaker to
open on a window containing no failures at all.

Beyond those hard bounds, two independent rules bound these values, and the
shipped defaults (`0.1` / `120` / `20` / `30`) satisfy both.
A configuration that violates either still starts and still works, but the
breaker will not behave usefully:

- `FailureRatio * MinimumThroughput` must exceed 1.
  Otherwise a single anomalous failure satisfies the ratio and opens the
  breaker on its own.
- `MinimumThroughput / SamplingDurationSeconds` must sit below the
  deployment's quietest sustained request rate.
  The throughput floor is a hard gate: until the window holds
  `MinimumThroughput` calls the breaker cannot open no matter how many of
  them failed, so a value set too high silently disables load shedding for a
  low-traffic deployment even when its database is completely down.
  At the defaults that floor is 20 / 120 = 0.17 requests per second.

DMS logs a startup warning when the first rule is violated, and when the
throughput floor exceeds one request per second.
It cannot check the second rule properly: only the operator knows the
deployment's quietest sustained rate, so the warning fires on an obviously
high floor rather than on a genuinely unreachable one.
Compare the floor against your own traffic.

### Rejection response

A request refused by an open breaker receives a `503 Service Unavailable`
response with a `Retry-After` header and an Ed-Fi problem-details body served
as `application/problem+json`.
`Retry-After` carries the configured `BreakDurationSeconds`, rounded up to
whole seconds.
It is the full break duration rather than the time remaining in the current
break, so a client that is refused partway through a break is told to wait
longer than it strictly needs to.
That errs deliberately: the value can never send a client back early, and
retrying early is what turns one break into a queue of retries arriving the
moment it lifts.
The request never reached the backend, so it is safe for a client to reissue
it unchanged.

```json
{
  "detail": "The service is temporarily unable to handle the request. Retry the request later.",
  "type": "urn:ed-fi:api:service-unavailable",
  "title": "Service Unavailable",
  "status": 503,
  "correlationId": "0HNCTN1IRQMDG:00000001",
  "validationErrors": {},
  "errors": []
}
```

### Unclassified backend failure response

A document request whose backend failure DMS could not turn into a specific
answer receives a `500 Internal Server Error` carrying the standard Ed-Fi
problem-details envelope as `application/problem+json`, on every verb:

```json
{
  "detail": "An unexpected problem has occurred.",
  "type": "urn:ed-fi:api:system",
  "title": "System Error",
  "status": 500,
  "correlationId": "0HNCTN1IRQMDG:00000001",
  "validationErrors": {},
  "errors": []
}
```

Earlier releases answered this case with `{"error": "...", "correlationId": "..."}`
as `application/json`, where the `error` value was an internal diagnostic
message naming DMS components.
That message is now written to the log instead of the response, so a client
parsing the old `error` field will no longer find it.
Failures that escape as exceptions rather than results are answered by a
separate generic 500 that still uses a `{"message", "traceId"}` body.

## OtlpLogging

CMS and DMS compile in `Serilog.Sinks.OpenTelemetry` as a single
vendor-neutral OTLP log exporter. It is configured through the top-level
`OtlpLogging` section, disabled by default, and can be enabled without
recompilation using the environment-variable convention described in the note
above (for example, `OtlpLogging__Enabled=true`). See
[LOGGING.md](./LOGGING.md#otlp-export) for the full description of supported
log routing paths, including OTLP export and deployment recipes.

| Parameter              | Description                                                                                                                                                              |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Enabled                | When `true`, log events are also exported over OTLP. Default: `false`.                                                                                                   |
| Endpoint                | The OTLP collector endpoint, as an absolute `http://` or `https://` URL. Required when `Enabled` is `true`: if omitted or not such a URL, OTLP export is not applied and a warning is written to stderr. Prefer `https://` for any endpoint outside a trusted network boundary (see [LOGGING.md](./LOGGING.md#security-considerations-for-otlp-export)). Example: `http://collector:4318`. |
| Protocol                | The OTLP wire protocol. Valid values are `Grpc` and `HttpProtobuf` (case-insensitive); OTLP-convention spellings such as `http/protobuf` are rejected at startup. Default: `HttpProtobuf`. |
| ServiceName             | The `service.name` resource attribute. Default: `EdFi.DataManagementService` (DMS) or `EdFi.DmsConfigurationService` (CMS).                                              |
| ServiceVersion          | The `service.version` resource attribute. Default: the application's informational version.                                                                             |
| DeploymentEnvironment   | Optional deployment environment, emitted as both the `deployment.environment` and `deployment.environment.name` resource attributes. Omitted when unset.                 |
| ServiceInstanceId       | Optional `service.instance.id` resource attribute. Omitted when unset.                                                                                                   |
| Headers                 | Optional headers sent with every export request, e.g. `OtlpLogging__Headers__Authorization` for an authenticated collector receiver. Header values are secrets: source them from a secret store or environment variable, never a committed configuration file. An invalid header name or value (e.g. a trailing newline in the value) means OTLP export is not applied: a warning is written to stderr that deliberately omits the offending value. |

## Identity Provider Configuration

For most deployments, environment variables and the setup script are sufficient,
but for custom scenarios you may edit these files directly.

By default, the configuration uses the self-contained (OpenIddict) identity
provider. The `appsettings.json` files are pre-configured for self-contained
endpoints, and the setup scripts will use self-contained unless you explicitly
specify `keycloak` as the identity provider.

If you wish to use Keycloak as the identity provider, you must update the
relevant environment variables or appsettings to set `IdentityProvider` to
`keycloak` and configure the appropriate Keycloak endpoints.

### Relevant parameters in `appsettings.json` (Configuration Service)

| Parameter        | Description                                                      | Example (Keycloak)                                   | Example (Self-contained)                      |
|------------------|------------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `AppSettings.IdentityProvider` | Selects the identity provider                                    | `keycloak`                                           | `self-contained`                              |
| `IdentitySettings.Authority`        | URL of the identity provider's authority (issuer)                | `http://dms-keycloak:8080/realms/edfi`              | `http://ed-fi-api-config:8081`              |
| `IdentitySettings.EncryptionKey`    | Key used for token encryption (self-contained only)              | _(not used)_                                         | `QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIz` |
| `IdentitySettings.TokenCleanupEnabled` | Enables the background sweep that deletes expired OpenIddict access tokens (self-contained only) | _(not used)_                                         | `true`              |
| `IdentitySettings.TokenCleanupIntervalMinutes` | Interval, in minutes, between expired-token cleanup sweeps (self-contained only)           | _(not used)_                                         | `30`              |

### JwtAuthentication parameters in `appsettings.json` (DMS API Service)

| Parameter         | Description                                         | Example (Keycloak)                                   | Example (Self-contained)                      |
|-------------------|-----------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `AppSettings.AuthenticationService`       | URL of the identity provider's authority (issuer)   | `http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token`              | `http://ed-fi-api-config:8081/connect/token`              |
| `JwtAuthentication.Authority`       | URL of the identity provider's authority (issuer)   | `http://dms-keycloak:8080/realms/edfi`              | `http://ed-fi-api-config:8081`              |
| `JwtAuthentication.MetadataAddress` | OpenID Connect metadata endpoint                    | `http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration` | `http://ed-fi-api-config:8081/.well-known/openid-configuration` |
| `JwtAuthentication.RoleClaimType` | Exact inbound claim type used by endpoints that require a specifically configured role | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` |

Refer to the API service's `appsettings.json` for additional options and defaults.

DMS preserves JWT claim types as emitted by the identity provider. Configure
`JwtAuthentication.RoleClaimType` to that exact claim type for endpoints that require an explicitly
configured role claim. The ordinary `JwtAuthentication.ClientRole` gate remains backward compatible
with the configured type and the standard `role`, `roles`, and
`http://schemas.microsoft.com/ws/2008/06/identity/claims/role` representations.
