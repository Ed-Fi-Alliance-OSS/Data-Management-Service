# Secrets

This chapter is for the operator who wants the secrets the Ed-Fi API and the
Configuration Service use to live in a vault rather than in a configuration file or
an environment variable. It says what a secrets plugin can and cannot serve, how a
rotation propagates, and what adopting one does and does not protect.

How a plugin is packaged, delivered, allowlisted, and trusted is not restated here:
see [PLUGINS.md](../src/plugins/EdFi.Api.Plugins/PLUGINS.md) for the implementer's
side and the [Plugins chapter of OPERATIONS.md](./OPERATIONS.md#plugins) for the
operator's. How to write a resolver is in the `EdFi.Api.Secrets` package's readme.

## What a secrets plugin is

A secrets plugin is an ordinary plugin, delivered and allowlisted like any other,
that does one or both of two things.

1. **It supplies configuration values from a vault (Phase A).** Its
   `ContributeConfiguration` hook adds a configuration source, such as Azure Key
   Vault or AWS Systems Manager Parameter Store, and every configuration key that
   source holds resolves from it. This serves the **process-global** secrets each
   host reads from its configuration at startup. It works in both DMS and the
   Configuration Service, and the plugin must be allowlisted in each host whose
   values it supplies.
2. **It resolves secret references in stored connection strings (Phase B).** It
   registers an `ISecretResolver` in the Configuration Service, which the
   Configuration Service calls when a data store or derivative connection string it
   is returning names a secret instead of carrying one. This serves the
   **runtime-data** secrets an API client creates through `/v3/dataStores/` and
   `/v3/dataStoreDerivatives/`. Only the Configuration Service resolves references;
   DMS receives connection strings that are already resolved.

The two are separate because the values are. A process-global secret is fixed by
the deployment before the process starts; a connection string is data an API client
writes while the process runs, and it never reaches either host's configuration.
Phase A is therefore not a way to supply a connection string: a vault key named
`ConnectionStrings:Anything` is a key nothing reads.

## The process-global secrets Phase A serves

Each of these is an ordinary configuration key, so a Phase A source serves it with
no special handling, provided nothing that outranks plugin sources also supplies it.
An unprefixed environment variable or a command-line argument does; see
[Configuration precedence](./CONFIGURATION.md#configuration-precedence). An operator
moving a value into a vault removes it from the environment.

**DMS, two keys:**

| Key | What it is |
| --- | --- |
| `ConfigurationServiceSettings:ClientSecret` | The client secret DMS presents to the Configuration Service. |
| `ConfigurationServiceSettings:EncryptionKey` | The key DMS decrypts the Configuration Service's stored connection strings with. |

**The Configuration Service, six keys:**

| Key | What it is |
| --- | --- |
| `DatabaseSettings:DatabaseConnection` | The Configuration Service's own database connection string, password included. See the stock-image note below. |
| `DatabaseSettings:EncryptionKey` | The key the Configuration Service encrypts stored connection strings with. |
| `IdentitySettings:ClientSecret` | The Configuration Service's own client secret. |
| `IdentitySettings:EncryptionKey` | The key protecting the OpenIddict signing keys held in the database. |
| `IdentitySettings:CertificatePassword` | The production signing certificate's password. |
| `IdentitySettings:DevCertificatePassword` | The development signing certificate's password, which defaults to the literal `password`. |

**`IdentitySettings:CertificatePassword` and `IdentitySettings:DevCertificatePassword`
are absent from `appsettings.json`.** The Configuration Service still reads both
(`src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs`),
so a vault key with either name is served exactly as the other four are. Their
absence from the shipped file is not a sign that they are unused.

**`OtlpLogging:Headers` in each host.** Both hosts send the headers in this section
with every OTLP log export, typically an `Authorization` value for an authenticated
collector, and both hosts' sources already say to keep the values out of committed
configuration. It is a section of arbitrarily many values rather than one key, and
each header is an ordinary key: `OtlpLogging:Headers:Authorization` is served by a
vault key of that name like any other.

**On the stock Configuration Service image, keep `DatabaseSettings__DatabaseConnection`
in the environment.** The container entry point, `src/config/run.sh`, reads that
variable before .NET starts, to wait for the database, and the environment value
then outranks any plugin source for .NET as well. A deployment with its own entry
point, or outside a container, has neither constraint. See
[Configuration Service plugins](./CONFIGURATION.md#configuration-service-plugins).

### The one deployment credential Phase A cannot serve

**`DATABASE_CONNECTION_STRING_ADMIN` is out of reach, structurally.** It carries
elevated database credentials, and its only consumer is the DMS container entry
point: `src/dms/run.sh:14-16` parses the host, port, and username out of it in the
shell, to wait for PostgreSQL, before the .NET host exists. No configuration source
of any origin can serve a value read before there is a configuration to read it
from, so it stays a deployment-environment value, supplied the way the deployment
already supplies its other secret environment variables.

### Rotating a process-global secret takes a restart

**Each of these values is captured once and never re-read.** DMS reads its two into
singletons when it registers services; the Configuration Service reads its six
through options computed on first use and kept for the life of the process; both
hosts bind `OtlpLogging` once when they configure logging. A vault source that
reloads on an interval does not change that, and neither does
`SecretsSettings:CacheExpirationSeconds`, which applies only to Phase B. Rotating
any of them takes a restart of the host that reads it.

## Secret references in stored connection strings

A data store or derivative connection string may name a secret instead of carrying
it:

```text
Host=db;Port=5432;Username=edfi;Database=edfi_datastore_2026;Password=${secret:prod/dms/ds-2026}
```

The same shape on SQL Server, in that engine's own keywords:

```text
Server=db;Database=edfi_datastore_2026;User ID=edfi;Password=${secret:prod/dms/ds-2026};Encrypt=False
```

**The syntax is `${secret:<name>}`**, where `<name>` is one or more of `A`–`Z`,
`a`–`z`, `0`–`9`, and `_ - . / : @ +`. Matching is ordinal and case-sensitive,
because vault names are.

- **Where it may appear:** inside the value of any keyword in a connection string
  submitted to `POST` or `PUT` on `/v3/dataStores/` or `/v3/dataStoreDerivatives/`.
  It is not limited to `Password`; every string keyword's value is resolved. A
  keyword the engine types, such as `Port` or `Connect Timeout`, refuses a reference
  when the connection string is written, because the engine cannot parse it. A
  reference does not appear in, and is not resolved from, either host's
  configuration.
- A value may carry more than one reference, and each resolves independently. A
  resolved value is substituted as opaque text and is never scanned again.
- Text that opens `${` without forming a well-formed reference is not one and is
  left exactly as written, so an existing password containing `${` is unaffected.
  There is no escape sequence.
- The resolved value is substituted through the configured engine's own connection
  string builder, which applies that engine's quoting, so a secret containing `;`,
  `=`, quotes, or leading spaces reaches the driver exactly as the vault holds it.
  The connection string a reader receives is the builder's rendering, so its keyword
  names, casing, order, and quoting may differ from what was submitted.

The Configuration Service stores the reference, encrypted, exactly as it stores any
other connection string, and never writes the resolved value to its database. It
resolves on every read, re-encrypts, and returns cipher text in the shape DMS
already expects. A reference with no resolver installed, or one the resolver cannot
answer, fails the read rather than reaching DMS as a password; see
[SecretsSettings](./CONFIGURATION.md#secretssettings) for each failure's result.

## How a rotation propagates

**A rotated secret reaches a running DMS within the sum of two windows:**
`SecretsSettings:CacheExpirationSeconds` in the Configuration Service, which is how
long it reuses a resolved value before asking the resolver again, plus DMS's
`CacheSettings:DataStoreCacheExpirationSeconds`, which is how long DMS keeps the data
stores it fetched from the Configuration Service. On stock settings that is
300 + 600 seconds, **about fifteen minutes**, and not the Configuration Service's
five minutes alone.

That sum assumes a resolver that fetches each value when asked, which the
implementer guide requires. Both windows are measured on the system clock, so allow
longer if a host's clock may have been stepped back during one.

### Rotation rules

- **Rotate first, revoke after the propagation window.** For the whole window DMS
  keeps using the value it had, so write the new credential to the vault and accept
  it at the database, then revoke the old one only once the full sum has elapsed.
  Revoking at the Configuration Service's window alone leaves DMS presenting a
  revoked credential for up to DMS's window, and the result is authentication
  failures.
- **An immediate rotation takes a restart of both hosts.** Nothing pushes a rotation
  sooner. Restarting the Configuration Service clears only its own cache; DMS keeps
  the data stores it already fetched until its own window passes. When DMS's
  `CacheSettings:DataStoreCacheRefreshEnabled` is `false`, or its
  `DataStoreCacheExpirationSeconds` is not positive, DMS never refreshes and keeps the
  pre-rotation value for the life of the process
  (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:155-165`).
  Restart the Configuration Service first, then DMS.
- **A rotation retires and rebuilds DMS's connection pool for that data store.** DMS
  keys pool ownership on the connection string it received, so a changed resolved
  value is a new connection string: the old pool is retired and a new one is built.
  This is the same churn editing a connection string through the API causes, and it
  is expected rather than a fault.

While diagnosing a rotation that appears not to have taken effect, setting
`SecretsSettings:CacheExpirationSeconds` to `0` makes the Configuration Service ask
the resolver on every read.

## What adopting a secrets plugin does and does not protect

Read both halves of each point before adopting one.

**A secrets plugin runs with full process trust, and the allowlist is the operator's
control.** It is handed each host's configuration builder in Phase A and the name of
every secret the Configuration Service resolves in Phase B, and nothing contains it.
What the operator controls is whether it runs at all: it runs only when its name is
in that host's `Plugins:Allowed`, which no plugin can influence. The general trust
model, including what write access to the plugin root means, is in
[Trust](./OPERATIONS.md#trust) and [PLUGINS.md](../src/plugins/EdFi.Api.Plugins/PLUGINS.md#trust).

**The vault credential is not a secret this mechanism can hold.** A plugin needs
something to authenticate to its vault with, and it can read that only from the
configuration already present or from the ambient environment. A static vault
credential, such as a client secret or an access key in the environment, reduces the
problem from many secrets to one, which is a real improvement and not the same as
none. An ambient workload identity, such as an Azure managed identity or an AWS IAM
role, puts no secret in configuration at all, and reduces it to none.

**The Configuration Service can still produce the resolved value.** The value is no
longer at rest in the Configuration Service's database or its backups, and that is
the gain. It is not that the Configuration Service becomes unable to produce it. The
resolved value is held in Configuration Service process memory in its cache, and it
is returned, re-encrypted under `DatabaseSettings:EncryptionKey`, on every
limited-access read of the four endpoints that return connection strings:
`GET /v3/dataStores/` and `GET /v3/dataStores/{id}`
(`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/DataStoreModule.cs:23-24`),
and `GET /v3/dataStoreDerivatives/` and `GET /v3/dataStoreDerivatives/{id}`
(`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/DataStoreDerivativeModule.cs:21-22`).
Anyone holding a token those endpoints admit and the encryption key obtains the
value the vault currently holds. That path is how DMS obtains connection strings, so
it is not new; what it now leads to is.

**An operator whose requirement is that nothing but the vault can produce the
secret is not served by this mechanism.** The vault's presence does not imply that
property, and adopting a secrets plugin does not provide it.

**Write access to data stores is now access to every secret the plugin's vault
identity can read.** Nothing restricts which name a reference uses, and every string
keyword is resolved, not only `Password`. A client allowed to create or update data
stores or derivatives (`edfi_admin_api/full_access`), in any existing tenant, since a
request names its tenant in the `Tenant` header, can store a connection string such
as `Host=<a server it controls>;Username=${secret:<any name>}`. The Configuration
Service resolves it, and DMS sends the resolved value to that server when it
connects, which it does at startup for every data store it loads, whether or not an
application is bound to it; nothing checks the host. A value resolved into a keyword
other than `Password` can also appear in DMS's own error logs, because the database
driver repeats it in a failed-login or unknown-database message. Without secret
resolution that client could replace a stored password but never read one. Grant
write access to data stores and derivatives accordingly. Narrowing the vault identity
to the secrets the Configuration Service is meant to serve reduces what such a client
can reach; it does not close the path.

**The Configuration Service's own logs never carry a resolved value; that is the
whole of the guarantee.** A resolution failure is logged with the data store, the
tenant, and the reference's name, none of which is the secret. The guarantee does
not extend to DMS: as above, a database driver that repeats a resolved username or
database name in its error message puts it in DMS's error log. Nor does it extend to
the resolver, which runs at full process trust inside the Configuration Service, so
not logging what it fetched is an obligation on its implementer that nothing
enforces.

## Changing the client-secret hashing work factor

**Changing `IdentitySettings:ClientSecretHashingIterations` invalidates every client
secret hashed at the old count.** The self-contained identity provider stores a
client secret as a PBKDF2 hash without the iteration count, and verifies at whatever
count is configured now, so raising the work factor (or lowering it) makes every
existing client secret fail verification. The remedy is to re-issue those client
secrets. In the provided Docker Compose files this value is set from
`DMS_CONFIG_IDENTITY_HASHING_ITERATIONS`, which also sets the count the bootstrap
script hashes its own client secrets with. See
[Identity Provider Configuration](./CONFIGURATION.md#identity-provider-configuration).
