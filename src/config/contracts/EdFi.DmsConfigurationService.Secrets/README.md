# Ed-Fi API Secrets

This package defines the two contracts a Configuration Service plugin implements to take over its
secrets: `ISecretResolver`, which supplies the value of a secret a stored connection string names,
and `IClientSecretHasher`, which replaces how client secrets are hashed. It is the implementer's
guide to both, and to serving the hosts' configuration secrets from a vault.

What a plugin is, how to publish and package one, how an operator delivers and allowlists it, and
the trust model it runs under are in
[PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md),
the guide to `EdFi.Api.Plugins`, and are not restated here. What an operator gains and gives up by
installing a secrets plugin, and how a rotation propagates, is in
[SECRETS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/SECRETS.md).

> **Links out of this readme point at the current documentation on `main`**, not at the
> documentation for the package version you resolved. Where the two could differ, the copy in the
> Configuration Service release you are targeting is the authority.

## Two jobs, two mechanisms

A secrets plugin does one of two things, or both in the Configuration Service only, and they use
different hooks.

- **Supply configuration values from a vault.** Override `EdFiApiPlugin.ContributeConfiguration`
  and add a configuration source. This needs nothing from this package: the hosts' configuration
  secrets, such as `IdentitySettings:ClientSecret` or `ConfigurationServiceSettings:EncryptionKey`,
  are ordinary configuration keys, and the vendor's own configuration source serves them. It works
  in both the Data Management Service and the Configuration Service. See
  [Contributing configuration](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#contributing-configuration)
  and the two worked examples below.
- **Resolve secret references in stored connection strings.** Override `ContributeServices` and
  register an `ISecretResolver`. A data store or derivative connection string an API client stores
  in the Configuration Service may carry `${secret:<name>}` in place of a value, and the
  Configuration Service calls your resolver for it when it returns that connection string. Only the
  Configuration Service declares this contract, as it alone declares `IClientSecretHasher`. The
  Data Management Service treats both as types it owns, so a plugin allowlisted there that
  registers either one fails its startup, whatever else the plugin contributes, a configuration
  source included.

So a plugin that registers a resolver or a hasher is allowlisted in the Configuration Service only.
A deployment that also serves the Data Management Service's configuration secrets from a vault does
that with a second plugin, which only adds a configuration source, allowlisted in the Data
Management Service.

## What is here

Three public types in the `EdFi.DmsConfigurationService.Secrets` namespace.

**`SecretReference`**, a record of the two things a resolver is asked for:

- `Name`, the name inside `${secret:<name>}`: one or more of `A`–`Z`, `a`–`z`, `0`–`9`, and
  `_ - . / : @ +`, exactly as the operator wrote it. Matching is ordinal and case-sensitive.
- `Tenant`, the tenant the connection string is being read for: `null` in a single-tenant
  deployment, and the tenant's name in a multi-tenant one. A tenant name is made of `A`–`Z`,
  `a`–`z`, `0`–`9`, `_` and `-`.

**`ISecretResolver`**, with one member:

- `ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)`
  returns the current value of the named secret for the reference's tenant, fetched by this call.

**`IClientSecretHasher`**, with three members, which replace the host's default PBKDF2-SHA256
hasher on the self-contained identity provider:

- `Task<string> HashSecretAsync(string plainTextSecret)` returns the stored form of a new secret.
- `Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret)` reports whether a
  presented secret matches a stored one.
- `bool IsSecretHashed(string secret)` reports whether a stored value is already in hashed form
  rather than plain text.

## Registering either contract

Both contracts are **replace** contracts with zero or one implementation, and both have the same
three rules. Each rule's failure is stated beside it, because some of those failures are silent.

**Register with a plain `Add`, never a `TryAdd`.**

```csharp
services.AddSingleton<ISecretResolver>(new MyResolver(client));
```

A replace contract has one claimant, so there is nothing to try, and a `TryAdd` defeats the host's
claim detection. The host works out what each plugin registered by comparing the service collection
before and after its hook, and the collection your hook is handed hides nothing: a `TryAdd` sees
every registration already there, including the host's own default hasher, which is always
registered, and an earlier plugin's resolver. A `TryAdd` that declines adds nothing, and what
follows depends on what else your plugin contributed:

- If your plugin registered nothing else the host declares and added no configuration source,
  startup fails naming you, because nothing you registered is something the host calls. Your own
  types, such as a vault client or options you register for your resolver, do not count.
- If your plugin also registered another contract the host declares, or added a configuration source
  in `ContributeConfiguration`, nothing fails at all: your plugin loads and the implementation you
  meant to install never runs.

The host counts only what is still registered once every allowlisted plugin has run. A plugin whose
every declared registration was removed by a later plugin fails startup naming it, whatever else it
contributed.

`TryAdd` for `IClientSecretHasher` always declines, so it always lands in one of those two cases.
`TryAdd` for `ISecretResolver` succeeds when yours is the only resolver and declines behind another
plugin's. A plain `Add` in that position fails startup naming both plugins, which is the outcome an
operator can act on.

Two plugins that each register a plain `Add` fail startup naming both, and one plugin that
registers twice fails naming that plugin and its count. Which of two vendors is live is the
operator's decision.

**Register a singleton, unkeyed.** The host resolves each contract once, unkeyed, from its root
container, and holds the instance for the life of the process. It refuses anything else at startup,
naming your plugin and the contract: a keyed registration, which would never reach the host; a
scoped or transient one; an `IEnumerable<T>` registration over either contract; and a factory that
returns null, which the host finds by resolving it before it serves traffic.

**Register in `ContributeServices`.** `ContributeConfiguration` reaches configuration and nothing
else.

## Writing a resolver

### The tenant is an argument

Your resolver is constructed once per process and outlives every tenant, and tenants are created
and removed while the Configuration Service runs. So the tenant arrives on each call, as
`SecretReference.Tenant`. Read it from the reference every time, and never from static state, an
injected request-scoped service, or a value captured at construction: none of those is the tenant
of the read you are serving. A deployment that keeps one set of secrets for every tenant ignores
the tenant; one that keeps a vault subtree per tenant maps it into the path, as the
[resolver example](#a-resolver-over-parameter-store) does.

### Be safe to call concurrently

The host calls your resolver from many requests at once, for different names and tenants. It
collapses concurrent requests for the same `(tenant, name)` pair into one call, and makes no other
promise. Hold no mutable state that is not thread-safe.

### Cache the client, never the value

**You may cache your vault client, its connection, and its ambient-credential token, and you
should**: they are the expensive things to build, and none of them affects how old a secret value
is.

**You must not return a secret value you did not just fetch.** The Configuration Service caches
each resolved value for `SecretsSettings:CacheExpirationSeconds` and re-asks you when it expires.
The rotation window an operator reads, configures and plans revocations around is the host's, and
it holds only if each re-ask returns what the vault holds now. A value you cached as well adds your
age to the host's, invisibly, and an operator who revokes the old credential on the documented
schedule would revoke it while DMS is still being handed it.

### What the host does with your answer

| Your resolver | What the host does |
| --- | --- |
| Returns a non-empty value | Substitutes it into the connection string through the database engine's own connection string builder, re-encrypts, and caches it. |
| Returns null or an empty string | Treats the reference as unresolved. |
| Throws, including `OperationCanceledException` | Treats the reference as unresolved, and logs your exception's **type**, never its message or inner exceptions, which may describe the secret you were fetching. Make the type say what went wrong. |
| Does not return within the read's time allowance | Treats the reference as unresolved; see below. |

An unresolved reference fails the read: the data store collection or single row, or a derivative
read on its own through `/v3/dataStoreDerivatives/`, returns HTTP 500 and the log names the row, the
tenant and the reference. The one exception is a derivative read **as part of its data store**,
which is returned with a null connection string and logged as a warning, so the data store and its
other derivatives are unaffected. An unresolved reference is never passed through as text. How the
cache and the time allowance shape these outcomes is in
[SecretsSettings](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CONFIGURATION.md#secretssettings).

### What the host bounds, and what it cannot

**The timeout is the read's, not the call's.** `SecretsSettings:ResolveTimeoutSeconds`, ten by
default, bounds the time one read spends waiting on your resolver across all the calls it makes. It
starts at the first reference the read resolves. Once it has passed, the read reports every
remaining reference it has no cached value for as unresolved, without calling you again. A data
store read includes one derivative read, so it waits at most two allowances whatever its row count.

**A hung resolver fails a read, not a request thread.** The host invokes your resolver on a task it
owns and races that task against its deadline, so even a resolver that blocks before it returns its
`ValueTask` cannot hold the request: the read fails on time either way. At the deadline the host
cancels the token it passed you.

**It cannot reclaim a thread you are blocking.** A thread your resolver blocks is released only when
your call returns, and a call that never completes keeps the host's continuation and the request
state it captured reachable for as long as you keep the work pending. That is the residual, and the
only thing that removes it is your code. **Honour the cancellation token**: pass it to every vault
call you make, so a call nobody is waiting for any more stops.

**Nothing enforces that you do not log what you resolved.** The Configuration Service's own logs
carry the reference's name and never its value. Your resolver runs at full process trust inside the
same process, holding the value, so not writing it to any log, exception message, or telemetry is
your obligation, and nothing checks it.

## Replacing the client secret hasher

Register an `IClientSecretHasher` only to change how client secrets are stored, for example to a
different algorithm or work factor. The host's default stays registered and yours replaces it. Every
client secret already in the database was hashed by the host's default, so a replacement must still
verify those stored values, or every existing client must be issued a new secret once it is
installed. `IsSecretHashed` should recognise your own stored form as well as any form you still
verify.

## Worked examples

Each example below is complete and compiles against this package, `EdFi.Api.Plugins` 1.1.0 or
later (1.1.0 is the first version with `ContributeConfiguration`), and the vendor SDK versions named
beside it; a check in the Ed-Fi repository compiles these exact blocks.
None of them is a published plugin, and none has been run against a vault.

### Azure Key Vault configuration source

Compiled against `Azure.Extensions.AspNetCore.Configuration.Secrets` 1.5.2 and `Azure.Identity`
1.21.0.

<!-- embed: eng/verification/SecretsPluginExamples/KeyVaultConfigurationPlugin.cs#plugin -->
```csharp
using Azure.Identity;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;

namespace Acme.KeyVaultConfiguration;

public sealed class KeyVaultConfigurationPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.KeyVaultConfiguration";

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        // The vault address is ordinary operator configuration, read from what is already layered.
        // Throwing here fails startup naming this plugin, which is the right outcome for a plugin
        // allowlisted without the one setting it needs.
        string vaultUri =
            bootstrapConfiguration["Acme:KeyVault:VaultUri"]
            ?? throw new InvalidOperationException(
                "Set Acme:KeyVault:VaultUri to the vault's address, such as https://my-vault.vault.azure.net/."
            );

        // One source. Where it lands among the host's sources is the host's decision, not the order
        // it is added in. The credential is ambient: on Azure, DefaultAzureCredential finds the
        // workload's managed identity and no secret enters configuration.
        configurationBuilder.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
    }
}
```

- **The vault address comes from `bootstrapConfiguration`**, the operator configuration already
  layered when the hook runs, as `Acme:KeyVault:VaultUri`.
- **It adds one source, and the host places it.** Where the source ranks against the host's own is
  decided after the hook returns: below the operator's unprefixed environment variables and
  command-line arguments, above every JSON file. See
  [Contributing configuration](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#contributing-configuration).
- **Secret names map to keys through `--`.** Key Vault names allow only letters, digits and `-`,
  and the default secret manager reads `--` as the `:` separator, so the operator stores
  `IdentitySettings:ClientSecret` as a secret named `IdentitySettings--ClientSecret`.
- **Ambient credential.** On Azure, `DefaultAzureCredential` finds the workload's managed identity,
  and no secret enters configuration. It tries environment credentials first, so an
  `AZURE_CLIENT_SECRET` in the environment is used before the managed identity: that is a static
  credential, which reduces the deployment's secrets to one rather than to none. Microsoft
  recommends a specific credential over `DefaultAzureCredential` in production; constructing
  `ManagedIdentityCredential` instead keeps the ambient identity and drops that fallback.

### AWS Systems Manager Parameter Store configuration source

Compiled against `Amazon.Extensions.Configuration.SystemsManager` 7.1.1 and
`AWSSDK.Extensions.NETCore.Setup` 4.0.102.1.

<!-- embed: eng/verification/SecretsPluginExamples/ParameterStoreConfigurationPlugin.cs#plugin -->
```csharp
using Amazon.Extensions.NETCore.Setup;
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;

namespace Acme.ParameterStoreConfiguration;

public sealed class ParameterStoreConfigurationPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.ParameterStoreConfiguration";

    public override void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder,
        IConfiguration bootstrapConfiguration
    )
    {
        // The parameter path is ordinary operator configuration, read from what is already layered.
        string path =
            bootstrapConfiguration["Acme:ParameterStore:ConfigurationPath"]
            ?? throw new InvalidOperationException(
                "Set Acme:ParameterStore:ConfigurationPath to the parameter path to load, such as /edfi/cms."
            );

        // The AWS options are read from bootstrapConfiguration's AWS section and passed explicitly.
        // Without them the source builds the builder it is added to in order to find them, and the
        // builder a plugin is handed is not the one its sources are loaded with. The credential is
        // ambient: the SDK's default chain finds the workload's IAM role and no secret enters
        // configuration.
        AWSOptions awsOptions = bootstrapConfiguration.GetAWSOptions();

        // One source. Where it lands among the host's sources is the host's decision, not the order
        // it is added in.
        configurationBuilder.AddSystemsManager(path, awsOptions);
    }
}
```

- **The parameter path comes from `bootstrapConfiguration`**, as
  `Acme:ParameterStore:ConfigurationPath`, and so do the AWS options, from its `AWS` section.
- **It adds one source, and the host places it**, as above.
- **Parameter names map to keys through `/`.** The source loads every parameter under the path,
  drops the path, and reads each remaining `/` as the `:` separator, so the operator stores
  `IdentitySettings:ClientSecret` as `/edfi/cms/IdentitySettings/ClientSecret` when the path is
  `/edfi/cms`.
- **Pass the AWS options explicitly.** Without them this source builds the builder it is added to
  in order to find them, and the builder a plugin is handed is not the one its sources are loaded
  with.
- **Ambient credential.** The SDK's default credential chain finds the workload's IAM role, and no
  secret enters configuration. An access key in the environment is found first if one is set: that
  is a static credential, which reduces the deployment's secrets to one rather than to none.

### A resolver over Parameter Store

Compiled against `AWSSDK.SimpleSystemsManagement` 4.0.104.1 and `AWSSDK.Extensions.NETCore.Setup`
4.0.102.1. It uses the same vault, and the same client type, as the configuration source above,
but it is a plugin of its own: it registers a resolver, so it is allowlisted in the Configuration
Service only, while the configuration source may be allowlisted in either host or both.

<!-- embed: eng/verification/SecretsPluginExamples/ParameterStoreSecretResolver.cs#resolver -->
```csharp
using Amazon.Extensions.NETCore.Setup;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using EdFi.Api.Plugins;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Cms.ParameterStoreSecrets;

public sealed class ParameterStoreSecretsPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Cms.ParameterStoreSecrets";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        string root =
            configuration["Acme:ParameterStore:SecretsRoot"]
            ?? throw new InvalidOperationException(
                "Set Acme:ParameterStore:SecretsRoot to the path secret references resolve under, such as /edfi/cms/secrets."
            );
        bool perTenant = string.Equals(
            configuration["Acme:ParameterStore:PerTenant"],
            "true",
            StringComparison.OrdinalIgnoreCase
        );

        // The client is the expensive thing to build, so it is built once and kept for the life of
        // the process; it caches its connection and its ambient credential itself. The host never
        // disposes it.
        IAmazonSimpleSystemsManagement client = configuration
            .GetAWSOptions()
            .CreateServiceClient<IAmazonSimpleSystemsManagement>();

        // A plain Add of one singleton, unkeyed instance. ISecretResolver is a replace contract, so
        // there is nothing to try, and a TryAdd would hide a second claimant instead of failing.
        services.AddSingleton<ISecretResolver>(new ParameterStoreSecretResolver(client, root, perTenant));
    }
}

/// <summary>
/// Maps a secret reference to a Parameter Store name and fetches it on every call. It caches the
/// client and never a value: the Configuration Service caches values in front of it, and the
/// rotation window an operator configures is the host's.
/// </summary>
public sealed class ParameterStoreSecretResolver(
    IAmazonSimpleSystemsManagement client,
    string root,
    bool perTenant
) : ISecretResolver
{
    private readonly string _root = root.TrimEnd('/');

    // Safe to call concurrently: it holds no mutable state, and the SDK client is thread-safe.
    public async ValueTask<string> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        GetParameterResponse response = await client.GetParameterAsync(
            new GetParameterRequest { Name = ParameterName(reference), WithDecryption = true },
            cancellationToken
        );

        // The host logs the type of an exception a resolver throws and never its message, so the
        // type is the diagnostic.
        return response.Parameter?.Value ?? throw new ParameterHasNoValueException();
    }

    // ${secret:prod/dms/ds-2026} resolves to <root>/<tenant>/prod/dms/ds-2026 when the deployment
    // keeps one subtree per tenant, and to <root>/prod/dms/ds-2026 otherwise. A tenant-agnostic
    // deployment, and every read in a single-tenant one, where the tenant is null, ignores the tenant.
    private string ParameterName(SecretReference reference)
    {
        string name = reference.Name.TrimStart('/');

        // Parameter Store reads name:version and name:label as a selector, so ${secret:prod/dms/ds:1}
        // would fetch version 1 and keep returning it after the parameter is rotated. Refused here
        // rather than passed through.
        if (name.Contains(':'))
        {
            throw new ParameterSelectorNotAllowedException();
        }

        return perTenant && reference.Tenant is { } tenant ? $"{_root}/{tenant}/{name}" : $"{_root}/{name}";
    }
}

public sealed class ParameterHasNoValueException() : Exception("Parameter Store returned no value.");

public sealed class ParameterSelectorNotAllowedException()
    : Exception("A secret reference may not name a Parameter Store version or label.");
```

- **The `(name, tenant)` pair becomes a vault path.** With `Acme:ParameterStore:SecretsRoot` set to
  `/edfi/cms/secrets`, `${secret:prod/dms/ds-2026}` read for tenant `district-a` resolves to
  `/edfi/cms/secrets/district-a/prod/dms/ds-2026` when `Acme:ParameterStore:PerTenant` is `true`.
- **A tenant-agnostic deployment ignores the tenant.** With `PerTenant` unset, every tenant's
  reference resolves to `/edfi/cms/secrets/prod/dms/ds-2026`, and so does every reference in a
  single-tenant deployment, where the tenant is null.
- **It caches the client and not the value.** The client is built once, in `ContributeServices`,
  and every call fetches. The cancellation token goes to the vault call.
- **It is registered with a plain `Add`, as one singleton, unkeyed instance.**
- **It refuses a `:` before calling the vault.** Parameter Store reads `name:version` and
  `name:label` as a selector rather than as part of the name, so `${secret:prod/dms/ds:1}` would
  fetch version 1 and keep returning it after the parameter is rotated. The example throws for any
  reference containing `:`, and the reference is unresolved.
- **A name Parameter Store does not allow fails the call.** Parameter Store names allow letters,
  digits, `_ . - /`, so a reference using `@` or `+` throws from the SDK and is unresolved.
