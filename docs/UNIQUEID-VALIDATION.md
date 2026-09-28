# UniqueId Validation

DMS ships no UniqueIdValidation feature of its own.
This page is a how-to for building one as a custom validator plugin: a validator that checks a `Student`, `Staff`, or `Contact` write's UniqueId against an external system your deployment operates, delivered the same way any other custom validator is.
It assumes familiarity with [CUSTOM-VALIDATION.md](../src/dms/core/EdFi.DataManagementService.CustomValidation/CUSTOM-VALIDATION.md), the validator contract this plugin is built against, and [PLUGINS.md](../src/plugins/EdFi.Api.Plugins/PLUGINS.md), how a plugin is packaged and delivered.
This page does not restate either.

**What it does.**
Rejects a `POST` or `PUT` to `Student`, `Staff`, or `Contact` whose UniqueId does not resolve against an external lookup you configure, checking on both writes rather than on `POST` alone.

**What it does not do.**
It does not populate DMS's own document id from an external identifier, maintain an id-mapping table, or supply a cache of its own; DMS assigns its own document ids regardless of any plugin.
It also does not need to implement "a person's UniqueId cannot be modified": DMS already rejects that change natively for `Student`, `Staff`, and `Contact`, with no validator involved, unless the deployment lists the resource in `AppSettings:AllowIdentityUpdateOverrides`.
See [How this differs from ODS/API UniqueIdValidation, and why](#how-this-differs-from-odsapi-uniqueidvalidation-and-why) for the reasoning behind each difference.

## Worked example

Three files, the same shape as the sample in CUSTOM-VALIDATION.md: an options type, a validator, and the plugin that registers it.
All three are compiled, and run over real HTTP against a stub of the external system, by this repository's own integration suite before this page ships.

### The options type

Ordinary implementer code, naming no Ed-Fi type.
Both files below depend on it.

<!-- embed: eng/fixtures/plugins/Acme.UniqueIdValidation/UniqueIdValidationOptions.cs#options -->
```csharp
namespace Acme.UniqueIdValidation;

public sealed class UniqueIdValidationOptions
{
    /// <summary>
    /// The absolute base address of the external unique id system, for example
    /// "https://identity.example.org/uniqueids/". A trailing slash is not required; the plugin
    /// normalizes it when it builds the HttpClient from this value.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// How long the validator's own HttpClient waits before giving up on a request.
    /// This is the only timeout the write path enforces; DMS itself imposes none.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
```

### The validator

Checks on both `Upsert` (POST) and `Update` (PUT), unlike ODS/API's own feature, because an upstream unique-id system can retire an id after a document was first created.
The submitted value is never quoted back in a failure message, so a deployment can log these messages safely.

<!-- embed: eng/fixtures/plugins/Acme.UniqueIdValidation/UniqueIdValidator.cs#validator -->
```csharp
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.CustomValidation;

namespace Acme.UniqueIdValidation;

public sealed class UniqueIdValidator : ICustomResourceValidator
{
    /// <summary>
    /// The name this validator asks <see cref="IHttpClientFactory"/> for, and the same name the
    /// plugin below registers it under, so the two sides of that agreement are one string rather
    /// than two that can drift apart.
    /// </summary>
    public const string HttpClientName = "Acme.UniqueIdValidation";

    private readonly IHttpClientFactory _httpClientFactory;

    // Trivial by obligation, not by taste: DMS resolves every registered validator on every write
    // request before it reads any AppliesTo, so this constructor runs for writes to resources this
    // validator has nothing to say about. Storing the factory is the whole of it; the HttpClient
    // itself is built per call in ValidateAsync below, which is exactly as cheap and keeps this
    // validator from holding a client past the request that created it.
    public UniqueIdValidator(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    // Built once and handed back by reference. This getter is read on every write request for
    // every registered validator, before any filtering, so it must stay this cheap.
    public IReadOnlyList<ValidatedResource> AppliesTo { get; } =
    [
        new ValidatedResource("Ed-Fi", "Student"),
        new ValidatedResource("Ed-Fi", "Staff"),
        new ValidatedResource("Ed-Fi", "Contact"),
    ];

    public async Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    )
    {
        // Observed and allowed to propagate rather than caught and turned into a validation
        // failure: on a request the client has already abandoned, DMS rethrows this instead of
        // answering a 500, and reporting a failure here would answer 400 to a caller who is gone.
        cancellationToken.ThrowIfCancellationRequested();

        // This validator does not special-case operation: it checks on both Upsert (POST) and
        // Update (PUT), because an upstream unique-id system can retire an id after the document
        // was first created, so a write that was valid once is not proof it still is. ODS/API's own
        // UniqueIdValidation feature does not re-check on PUT; this is a deliberate difference.
        string member = MemberFor(resource.ResourceName);

        // Read as a JSON string rather than through GetValue<string>(), which throws when the
        // member holds a number, and treated as nothing to check when it is absent, not a string,
        // or empty: a validator reads a document it did not construct, so it defends against its
        // own rule's input being missing rather than assuming that input is there.
        if (
            document[member] is not JsonValue submitted
            || !submitted.TryGetValue(out string? uniqueId)
            || uniqueId.Length == 0
        )
        {
            return NoFailures;
        }

        // "." and ".." cannot be sent as one path segment: EscapeDataString leaves dots alone, and
        // URI resolution removes dot segments even when they are percent-encoded, so the lookup
        // would land on the upstream's collection or root instead. No upstream can hold such an
        // id under this contract, so it is answered as not found without a call.
        if (uniqueId is "." or "..")
        {
            return NotFound(resource.ResourceName, member);
        }

        HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
        string requestUri = $"{resource.ResourceName}/{Uri.EscapeDataString(uniqueId)}";

        // Only the status code is read, so the call completes once the headers arrive rather than
        // after buffering a body this validator never looks at.
        using HttpResponseMessage response = await httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return NoFailures;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(resource.ResourceName, member);
        }

        // Any other answer, a different 2xx included, is outside the contract this validator
        // speaks, so it is treated as a fault in the dependency rather than a fact about the
        // document: reporting it as a validation failure would tell a client something false about
        // their own data. Thrown instead, it reaches the host's catch-all and becomes a logged 500
        // that persists nothing; the only timeout on this call is the one this validator's own
        // HttpClient was configured with.
        throw new HttpRequestException(
            $"The external unique id system answered {(int)response.StatusCode} for a {resource.ResourceName} lookup.",
            inner: null,
            statusCode: response.StatusCode
        );
    }

    // The submitted value is deliberately not quoted back: a failure message reaches the 400
    // body, and keeping submitted data out of it is what lets a deployment log these messages if
    // it chooses to.
    private static IReadOnlyList<CustomValidationFailure> NotFound(
        string resourceName,
        string member
    ) =>
        [
            new CustomValidationFailure.OnPath(
                $"$.{member}",
                $"The {resourceName} unique id was not found in the external unique id system."
            ),
        ];

    private static string MemberFor(string resourceName) =>
        $"{char.ToLowerInvariant(resourceName[0])}{resourceName[1..]}UniqueId";

    // An empty list, never null. A null return is not a substitute for one and DMS treats it as a
    // hard failure.
    private static readonly IReadOnlyList<CustomValidationFailure> NoFailures = [];
}
```

### The plugin that registers it

Binds the options from configuration, builds a named `HttpClient` from them, and registers the validator in the one shape DMS's startup guard accepts.

<!-- embed: eng/fixtures/plugins/Acme.UniqueIdValidation/UniqueIdValidationPlugin.cs#plugin -->
```csharp
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.UniqueIdValidation;

public sealed class UniqueIdValidationPlugin : EdFiApiPlugin
{
    // Must equal the name of the directory this plugin is published into and named by in
    // Plugins:Allowed. The host treats a mismatch as fatal.
    public override string Name => "Acme.UniqueIdValidation";

    public override void ContributeServices(
        IServiceCollection services,
        IConfiguration configuration
    )
    {
        // The section-binding overload, from Microsoft.Extensions.Options.ConfigurationExtensions.
        // There is no Action<TOptions> default registered ahead of it, unlike the sample in
        // CUSTOM-VALIDATION.md: every default here already lives on the options type itself
        // (Timeout), and BaseAddress deliberately has none, so a deployment that configures nothing
        // fails clearly rather than silently calling an address nobody chose.
        services.Configure<UniqueIdValidationOptions>(
            configuration.GetSection("UniqueIdValidation")
        );

        services
            .AddHttpClient(UniqueIdValidator.HttpClientName)
            .ConfigureHttpClient(
                (serviceProvider, client) =>
                {
                    UniqueIdValidationOptions options = serviceProvider
                        .GetRequiredService<IOptions<UniqueIdValidationOptions>>()
                        .Value;

                    // This action runs each time the validator asks the factory for a client,
                    // which is once per matching write. So a deployment that has not set the
                    // address answers each matching write with a logged 500 naming the setting,
                    // and every other write keeps working. A deployment that would rather refuse
                    // to start can validate the options at startup instead.
                    if (
                        options.BaseAddress
                        is not {
                            IsAbsoluteUri: true,
                            Scheme: "http" or "https",
                            Query: "",
                            Fragment: "",
                        } baseAddress
                    )
                    {
                        throw new InvalidOperationException(
                            "UniqueIdValidation:BaseAddress must be configured as an absolute http "
                                + "or https URI with no query or fragment."
                        );
                    }

                    // A base address without a trailing slash would have HttpClient replace its
                    // last path segment instead of appending to it, following the ordinary rules
                    // for combining a base URI with a relative one. Normalizing here, rather than
                    // asking every deployment to remember the trailing slash, is what makes both
                    // forms of a configured address work.
                    UriBuilder normalized = new(baseAddress);
                    if (!normalized.Path.EndsWith('/'))
                    {
                        normalized.Path += "/";
                    }
                    client.BaseAddress = normalized.Uri;
                    client.Timeout = options.Timeout;
                }
            )
            // Redirects are not followed. The default handler follows them, and a lookup that
            // is redirected to a sign-in or landing page answering 200 would then read as "this
            // UniqueId exists". With this off, a redirect is an answer outside the contract and
            // faults like any other.
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false }
            )
            // The factory's default logging writes each request URI at Information, and the
            // UniqueId is in that URI's path, which its redaction leaves intact. A UniqueId is
            // student or staff data taken from the request body, so it must not reach the host's
            // logs; removing this client's loggers keeps it out while other clients keep theirs.
            .RemoveAllLoggers();

        // The registration shape DMS's startup guard accepts: TryAddEnumerable, Transient,
        // unkeyed, and an implementation type. TryAddEnumerable is required because it adds to the
        // collection rather than replacing it, so an earlier plugin's validator survives this call
        // and this one survives a later plugin's.
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, UniqueIdValidator>()
        );
    }
}
```

### Package references

| Package | Why |
| --- | --- |
| `EdFi.Api.CustomValidation` | Declares `ICustomResourceValidator`. |
| `EdFi.Api.Plugins` | Declares `EdFiApiPlugin`, and brings the configuration and dependency-injection abstractions the two hook parameters need. |
| `Microsoft.Extensions.Options` | `IOptions<T>`. |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | The section-binding form of `Configure` this plugin uses. |
| `Microsoft.Extensions.Http` | `IHttpClientFactory` and `AddHttpClient`. Pin to the 10.0.x line; the host serves its own copy from the ASP.NET Core shared framework, so this is one you declare rather than one that changes what loads. |

Pin the two Ed-Fi contracts exactly, in brackets, as CUSTOM-VALIDATION.md's "Getting the package" describes; a bare version is a floor rather than a pin.
Keep each `Microsoft.Extensions.*` reference at or below the version the host carries, since the host serves its own copy and refuses a plugin that requires a newer assembly version.

## The upstream contract, and adapting it

The sample validator above assumes a lookup service reachable at:

```text
GET {BaseAddress}/{ResourceName}/{uniqueId}
```

with the UniqueId URI-escaped into one path segment and `ResourceName` exactly `Student`, `Staff`, or `Contact`.
A UniqueId of `.` or `..` cannot be sent as one path segment, because URI resolution removes dot segments even when they are percent-encoded, so the validator answers it as not found without calling the upstream.
A `200` response means the UniqueId exists; a `404` means it does not.
Any other response, including a different `2xx` or a redirect, or a transport failure, is treated as a fault rather than an answer: the validator throws, and the write answers a logged `500` and persists nothing.
The plugin turns off redirect following on its client, so a redirect to a page that answers `200` is never read as "exists".
It also removes the client's default request logging, which would otherwise write every request URI, and so every submitted UniqueId, to the host's log at `Information`.
Keep that property in any adaptation: a UniqueId is personal data taken from the request body, and it belongs in neither a DMS log line nor a failure message.
Because this contract carries the UniqueId in the URL path, the upstream's own access logs, and any proxy or monitoring tool between the two, can still record it.
If that matters for your deployment, adapt the lookup to send the UniqueId in a `POST` body instead.

This is this sample's own contract, not an Ed-Fi standard.
Adapt it to what you actually operate:

- **Auth headers.**
  Register a `DelegatingHandler` as a transient service and chain `.AddHttpMessageHandler<THandler>()` after `AddHttpClient(UniqueIdValidator.HttpClientName)` in the plugin.
  Have it attach whatever bearer token, API key, or signature your upstream requires.
- **A different lookup shape.**
  If your system takes a query string, a request body, or a different path layout, change how `ValidateAsync` builds its request; nothing about the contract or the plugin depends on the shape shown here.
- **An extension person resource.**
  Add another entry to `AppliesTo`, naming the project and the resource, for a project extension person type your deployment defines, and extend `MemberFor` if that resource's UniqueId member is not named `{resource}UniqueId`.
- **Caching, as your own choice.**
  This contract gives a validator no store access and supplies no cache of its own, so a cache is something you add, typically an `IMemoryCache` or `IDistributedCache` injected alongside `IHttpClientFactory`.
  ODS/API's own 10-minute sliding cache (`IPersonUniqueIdToIdCache`) is a reasonable reference point for how long to hold an answer.
  A cache trades freshness for write latency: every applicable validator is awaited before a write proceeds, so an uncached slow upstream is the slowest thing this validator can do, while a cache can serve a UniqueId the upstream has since retired.

## Deploying it

Follow PLUGINS.md's publishing and packaging steps, and [CONFIGURATION.md](./CONFIGURATION.md#plugins) for `Plugins:Allowed` and `Plugins:Directory`.
This section states only what is specific to this plugin.

```shell
dotnet publish --no-self-contained -o out/Acme.UniqueIdValidation
```

```text
Plugins__Directory=/app/plugins
Plugins__Allowed=Acme.UniqueIdValidation
UniqueIdValidation__BaseAddress=https://identity.example.org/uniqueids/
UniqueIdValidation__Timeout=00:00:05
```

The plugin directory name, the entry assembly's file name (`Acme.UniqueIdValidation.dll`), the loaded assembly's own `AssemblyName`, and `UniqueIdValidationPlugin.Name` must all read `Acme.UniqueIdValidation`; the host treats each mismatch as fatal.
See PLUGINS.md's "Names: four of them, and they must all match".

`UniqueIdValidation:BaseAddress` and `UniqueIdValidation:Timeout` bind from the `UniqueIdValidation` section, `appsettings.json` or environment alike.
A deployment that does not set `BaseAddress`, or sets one that is not an absolute `http` or `https` URI without a query or fragment, gets a validator whose `HttpClient` cannot be created, answered as a logged `500` on every matching write, because `BaseAddress` deliberately has no default.

## What a client sees

### A rejected UniqueId

```json
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "0HN7C4NQEXAMPLE",
  "validationErrors": {
    "$.studentUniqueId": [
      "The Student unique id was not found in the external unique id system."
    ]
  },
  "errors": []
}
```

Unlike ODS/API's own message, this one does not echo the submitted value back to the caller.

### A faulted upstream

Any response other than `200` or `404`, a transport failure, a timeout, and a missing `BaseAddress` all reach the caller the same way: a logged `500`, and the write persists nothing.
The `Timeout` option is the only timeout the write path enforces; DMS itself imposes none, there is no retry, and there is no fail-open.

### Ordering consequences

The validator runs after claim-set and resource authorization and after DMS's own core document validation, and before relationship, namespace, and ownership authorization, and before DMS's identity-immutability check.
Two things follow:

- The validator's lookup, and its cost, still happens for a write the caller will ultimately be refused with a `403`.
- A `PUT` that changes a person's UniqueId to a value the upstream does not know gets this validator's `400` first.
  A `PUT` that changes it to a value the upstream does know passes the validator and reaches DMS's own identity-immutability check, which answers `key-change-not-supported`.

## How this differs from ODS/API UniqueIdValidation, and why

| ODS/API mechanism | This DMS plugin | Why |
| --- | --- | --- |
| `FeatureManagement:UniqueIdValidation` (default `false`) enables the feature. | No dedicated flag. `Plugins:Allowed` (ships empty) is the switch. | Allowlisting a plugin is already how a DMS deployment opts in to optional behavior, so a second switch for the same thing would add nothing. |
| A host-supplied `IUniqueIdToIdValueMapper` (ODS registers none) resolves the id, fronted by a 10-minute sliding `IPersonUniqueIdToIdCache`; the mapper's GUID becomes the person's resource `id`. | No mapping table, no external GUID reuse, no populate-id step, no built-in cache. DMS assigns its own document ids. | Deliberate. A DMS resource id belongs to DMS and is never an external value, so validating a UniqueId stays independent of how DMS assigns identity, and an upstream outage or remapping cannot change a resource's id. |
| Existence is checked on `POST` only; `PUT` is not re-checked. | Existence is checked on both `POST` and `PUT`. | An upstream system can retire a UniqueId after the document was first created, so a write that was valid once is not proof it still is. |
| A validator compares the submitted UniqueId to the persisted one, to reject a change. | Not expressible as a custom validator, and unnecessary anyway. | This contract offers no store read and no persisted-document identity, so the rule cannot be built as a validator; see CUSTOM-VALIDATION.md's "The ODS UniqueId rule, and both of its causes". Student, Staff, and Contact identity members are immutable in DMS unless a deployment lists the resource in `AppSettings:AllowIdentityUpdateOverrides`, so DMS's own identity check enforces the rule; that check runs after custom validators, as [Ordering consequences](#ordering-consequences) describes. |
| The failure message echoes the submitted value: `"The supplied UniqueId value '{0}' was not resolved."` | The failure message does not echo the submitted value. | Keeping submitted data out of a failure message is a contract-wide convention, so a deployment can log these messages safely. |
| Person types are derived from the model: Student, Staff, Contact (Parent in Data Standard 4.x). | The same three resources by default, in `AppliesTo`. | This plugin targets the same conceptual person types; a deployment on a different Data Standard, or with an extension person resource, extends `AppliesTo` and `MemberFor` accordingly. |
| The lookup is whatever the deployment's own mapper implementation calls. | `GET {BaseAddress}/{ResourceName}/{uniqueId}`, this sample's own choice. | DMS ships no built-in mapper, so the contract is defined by the sample and is meant to be adapted; see [The upstream contract, and adapting it](#the-upstream-contract-and-adapting-it) above. |

Taken together, the two behaviors a client of ODS/API's feature observes are both present: this plugin rejects a UniqueId the upstream does not know, and DMS itself rejects a UniqueId change.
What is deliberately absent is the coupling between the two systems' identifiers.

## Migrating from ODS/API UniqueIdValidation

1. Identify what your `IUniqueIdToIdValueMapper` implementation actually does to resolve a UniqueId, and expose that lookup, or a thin wrapper around it, as an HTTP `GET` endpoint that answers `200` when the UniqueId resolves and `404` when it does not.
   If that lookup is already reachable in-process from a plugin, adapt this validator to call it directly instead, and skip the HTTP hop entirely.
2. Publish a plugin built from the worked example above, pointing `UniqueIdValidation:BaseAddress` at that endpoint, and add its directory name to `Plugins:Allowed`.
3. Stop relying on the external GUID as a resource id.
   DMS assigns its own document ids independent of any upstream identifier, so a client that assumed a person's resource id equaled the upstream mapper's GUID must not carry that assumption into DMS; give it the DMS-issued id instead.
4. Remove `FeatureManagement:UniqueIdValidation` from your configuration.
   DMS does not read it; enabling the plugin through `Plugins:Allowed` is what replaces it.
5. Write nothing for the "UniqueId cannot be modified" rule, and keep `Student`, `Staff`, and `Contact` out of `AppSettings:AllowIdentityUpdateOverrides`.
   With those resources absent from that setting, DMS rejects a UniqueId change as a `400` `key-change-not-supported`, independent of any plugin.
