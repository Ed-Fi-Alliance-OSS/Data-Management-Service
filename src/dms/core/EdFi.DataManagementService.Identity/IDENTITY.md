# Ed-Fi API Identity Contract

This package defines `IIdentityService`, the contract a district or vendor implements to back the Identity Management operations of the Ed-Fi Data Management Service, and this document is the implementer guide for it.
The service exposes five identity operations under `/identity/v2/identities`: create, get by UniqueId, find, search, and polling for the results of an asynchronous find or search.
It owns the routes, authentication, tenant checks and every problem response, and it calls your provider for the identity work behind each one.

> **A provider is registered from a plugin.**
>
> A provider reaches the Data Management Service through a plugin's `ContributeServices` hook.
> How a plugin is built, published, packaged, and delivered into a host is
> [PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md).
> That guide also covers the allowlist and the trust model, and this one does not restate them.
> The host assembly manifest attached to a Data Management Service release states which contract versions that release carries, which is what tells you the version of this package to build against for a given host.
>
> **Links out of this readme point at the current documentation on `main`**, not at the documentation for the package version you resolved.
> Where the two could differ, the copy in the Data Management Service release you are targeting is the authority.

## What is here

- `IIdentityService` - the provider contract: a `Capabilities` getter and the five operations `CreateAsync`, `GetByIdAsync`, `FindAsync`, `SearchAsync` and `ResultsAsync`.
- `IdentityCapabilities` - the flags a provider uses to declare which operations it supports.
- `IdentityRequestContext` - the tenant, route qualifiers, authenticated client and trace id of the request.
- `IdentityResult` and `IdentityAsyncResult` - the results of the operations, `IdentityAsyncResult` being the one that can carry a request token for an asynchronous job.
- `IdentityResultStatus` and `IdentityError` - the status a provider reports and the per-property failures it reports with `InvalidProperties`.

Each type carries its rules in its XML documentation, which ships with this package, so an IDE shows them at the point of use.
Where this guide and that documentation overlap, they are written to agree.
The XML documentation is the most precise statement of each obligation.

## Package and version

Reference the package and the plugin base class it is registered through, each pinned exactly, in brackets:

```xml
<PackageReference Include="EdFi.Api.Identity" Version="[1.0.0]" />
<PackageReference Include="EdFi.Api.Plugins" Version="[1.1.0]" />
```

A bare version is a minimum rather than a pin, and the host assembly manifest of the release you target names the contract versions it carries.
`EdFi.Api.Identity` has its own version, independent of the Data Management Service release version.
The same is true of `EdFi.Api.Plugins`, which supplies the plugin base class.
A Data Management Service release carries whichever contract version it was built against, so an 8.x host and an 8.y host can carry the same identity contract.
The served OpenAPI document states the contract version it describes in its `x-edfi-identity-contract-version` property.
The package has no dependencies, because `JsonNode` and `JsonObject` come from the shared framework.
[Versioning and compatibility](#versioning-and-compatibility) says what moves the version.

## Implementing and registering a provider

You write two things: a class that implements `IIdentityService`, and a plugin that registers it.
The Data Management Service owns the HTTP surface.
It maps the five routes, authenticates the caller, checks the tenant, authorizes the identity service claim, validates the request body's shape, and turns what your provider returns into the HTTP response.
A plugin maps no identity endpoints and cannot add or change one.
The `AppSettings:EnableIdentityManagement` setting, off by default, decides whether the host maps the routes at all, as [CONFIGURATION.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CONFIGURATION.md) describes.

The plugin is a small class.
This is the complete plugin of the sample provider below:

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityPlugin.cs#plugin -->
```csharp
using EdFi.Api.Plugins;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Dms.Identity;

public sealed class AcmeIdentityPlugin : EdFiApiPlugin
{
    // The plugin directory, the entry assembly's file name and its AssemblyName must all be exactly
    // this name.
    public override string Name => "Acme.Dms.Identity";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // The clients allowed to use this provider, read from the plugin's own configuration section.
        string[] authorizedClients =
        [
            .. configuration
                .GetSection("Acme:Identity:AuthorizedClients")
                .GetChildren()
                .Select(client => client.Value)
                .OfType<string>(),
        ];

        // One store serves every request, so it is a singleton.
        services.AddSingleton(new AcmeIdentityStore(authorizedClients));

        // A plain Add, unkeyed. The host registers its own default identity service, so a TryAdd would
        // be declined and the default would keep serving; a keyed registration is never resolved.
        // Scoped is one of the three supported lifetimes: this provider holds no state of its own.
        services.AddScoped<IIdentityService, AcmeIdentityService>();
    }
}
```

Two rules in that registration are the ones that go wrong.

**Register with `Add`, never `TryAdd`.**
This is a replace contract: zero or one plugin implementation replaces the host default, `NoIdentityService`, which answers every operation as unsupported.
The host default is always registered, so a `TryAdd` is declined and the replacement never happens.
Startup catches that only when the plugin is left with no declared registration and added no configuration source.
Beside a surviving declared registration, such as a custom validator, or beside a configuration source, a declined `TryAdd` goes undetected and the host default silently keeps serving.
`TryAddEnumerable` happens to add here, but it is the form for fan-in contracts, so use `Add`.
Never call `services.Replace` or `RemoveAll` for this contract either: they remove the host's own registration, which the host refuses at startup.
[What you may register, and how](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#what-you-may-register-and-how) and
[The registration form per cardinality is a rule, not a suggestion](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#the-registration-form-per-cardinality-is-a-rule-not-a-suggestion)
in PLUGINS.md state the registration rules in full.

**Register unkeyed.**
The host resolves this contract unkeyed, from the request scope.
A keyed registration is accepted at startup and never reached by a request, so the host default keeps serving.

Singleton, scoped and transient lifetimes are all supported, and [Lifetimes and concurrency](#lifetimes-and-concurrency) says what each one means for your class.

## A provider, end to end

The sample provider is a real implementation of every member of `IIdentityService`, against an in-memory store that stands in for an identity system.
It is shown in six consecutive parts, and the parts together are the whole of the compiled file.
This repository's verification lane compiles the sample against the packed `EdFi.Api.Identity` package and runs assertions against what it returns, and a check holds each block in this guide to the file it came from.
So these are samples that have been compiled and run rather than samples that look right.

Everything the sample does that is specific to its in-memory store is incidental.
What it demonstrates is the shape of a provider: a namespace-access check before any identity work, request values checked against the served schema before they are stored, payloads shaped as the contract defines them, a job and a token for asynchronous lookups, cancellation, and `InvalidProperties` errors that name a property and never its value.

### The class and its capabilities

The provider declares which operations it supports in `Capabilities`.
It supports all five here, `Results` included, because it can accept an asynchronous find.

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityService.cs#provider -->
```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;

namespace Acme.Dms.Identity;

/// <summary>
/// A sample provider that issues and resolves person identities against an in-memory store standing
/// in for Acme's real identity system. Everything above the store is what a real provider needs: the
/// namespace-access check on every operation, payloads shaped as the contract defines, token and job
/// rules for asynchronous lookups, cancellation, and failures that never carry person data.
/// </summary>
internal sealed class AcmeIdentityService(AcmeIdentityStore store) : IIdentityService
{
    // A lookup of more than this many UniqueIds is queued as a job instead of answered inline.
    private const int MaxInlineLookups = 5;

    private static readonly string[] RequiredProperties = ["LastSurname", "FirstName", "BirthDate"];

    // Read once per request, before any operation runs, so it must be cheap and must not do I/O.
    public IdentityCapabilities Capabilities =>
        IdentityCapabilities.Create
        | IdentityCapabilities.GetById
        | IdentityCapabilities.Find
        | IdentityCapabilities.Search
        | IdentityCapabilities.Results;
```

### Create

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityService.cs#provider-create -->
```csharp
    public Task<IdentityResult> CreateAsync(
        JsonObject request,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        // A real provider passes the token to its upstream call; this one has nothing to await.
        cancellationToken.ThrowIfCancellationRequested();

        if (store.ResolveNamespace(context) is not { } identityNamespace)
        {
            return Task.FromResult(new IdentityResult { Status = IdentityResultStatus.NotFound });
        }

        // Errors name the property, never its value: names and birth dates are person data.
        List<IdentityError> errors = [.. CheckAttributes(request, "$", RequiredProperties)];

        if (errors.Count > 0)
        {
            return Task.FromResult(
                new IdentityResult { Status = IdentityResultStatus.InvalidProperties, Errors = errors }
            );
        }

        string uniqueId = store.Issue(identityNamespace, request);

        // The create payload is the bare UniqueId string, not an object wrapping it.
        return Task.FromResult(
            new IdentityResult { Status = IdentityResultStatus.Success, Payload = JsonValue.Create(uniqueId) }
        );
    }

    // DMS checks no property values, so the provider does: each standard property must have the type
    // the served schema gives it, or a later answer would return it in the wrong shape. A missing or
    // null property is an unknown value, refused only when it is required. Search uses this too, with
    // each item's own path.
    private static IEnumerable<IdentityError> CheckAttributes(
        JsonObject request,
        string path,
        string[] required
    )
    {
        foreach (string name in AcmeIdentityStore.TextProperties)
        {
            if (request[name] is { } value && !AcmeIdentityStore.IsText(value))
            {
                yield return Error(path, name, $"{name} must be a string.");
            }
            else if (required.Contains(name) && !AcmeIdentityStore.HasText(request[name]))
            {
                yield return Error(path, name, $"{name} is required.");
            }
        }

        if (request["BirthDate"] is { } birthDate && AcmeIdentityStore.NormalizeBirthDate(birthDate) is null)
        {
            yield return Error(path, "BirthDate", "BirthDate must be a date-time with a UTC offset.");
        }
        else if (required.Contains("BirthDate") && request["BirthDate"] is null)
        {
            yield return Error(path, "BirthDate", "BirthDate is required.");
        }

        if (
            request["BirthOrder"] is { } birthOrder
            && !(birthOrder is JsonValue order && order.TryGetValue(out int _))
        )
        {
            yield return Error(path, "BirthOrder", "BirthOrder must be an integer.");
        }

        if (request["BirthLocation"] is JsonObject location)
        {
            foreach (string name in AcmeIdentityStore.LocationProperties)
            {
                if (location[name] is { } value && !AcmeIdentityStore.IsText(value))
                {
                    yield return Error(
                        path,
                        $"BirthLocation.{name}",
                        $"BirthLocation.{name} must be a string."
                    );
                }
            }
        }
        else if (request["BirthLocation"] is not null)
        {
            yield return Error(path, "BirthLocation", "BirthLocation must be an object.");
        }
    }

    private static IdentityError Error(string path, string property, string message) =>
        new() { Message = message, Path = $"{path}.{property}" };
```

DMS checks no property values, so the sample checks each standard property against the type the served schema gives it before it stores anything.
A value it stored unchecked would come back from a later get in a shape the schema does not allow, such as a `BirthDate` that is a date and not a `date-time`.
The create payload is the issued UniqueId as a bare JSON string.
[Payloads](#payloads) and [Issuing UniqueIds](#issuing-uniqueids) say what a created UniqueId must satisfy.

### Get by UniqueId

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityService.cs#provider-get -->
```csharp
    public Task<IdentityResult> GetByIdAsync(
        string uniqueId,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An unauthorized caller and an unknown UniqueId get the same answer, so the response never
        // reveals whether an identity exists.
        if (
            store.ResolveNamespace(context) is not { } identityNamespace
            || store.Find(identityNamespace, uniqueId, score: null) is not { } identity
        )
        {
            return Task.FromResult(new IdentityResult { Status = IdentityResultStatus.NotFound });
        }

        return Task.FromResult(
            new IdentityResult { Status = IdentityResultStatus.Success, Payload = identity }
        );
    }
```

### Find and search

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityService.cs#provider-find-search -->
```csharp
    public Task<IdentityAsyncResult> FindAsync(
        IReadOnlyList<string> uniqueIds,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (store.ResolveNamespace(context) is not { } identityNamespace)
        {
            return Task.FromResult(new IdentityAsyncResult { Status = IdentityResultStatus.NotFound });
        }

        // One entry per requested UniqueId, in request order. An id that matches nothing is an entry
        // with no responses, not a NotFound and not a missing entry.
        IEnumerable<JsonObject> Match(string uniqueId) =>
            store.Find(identityNamespace, uniqueId, score: null) is { } identity ? [identity] : [];

        JsonObject Resolve() => AcmeIdentityStore.Complete(uniqueIds.Select(Match));

        return Task.FromResult(
            uniqueIds.Count > MaxInlineLookups
                ? Accept(context, Resolve())
                : new IdentityAsyncResult { Status = IdentityResultStatus.Success, Payload = Resolve() }
        );
    }

    public Task<IdentityAsyncResult> SearchAsync(
        IReadOnlyList<JsonObject> requests,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (store.ResolveNamespace(context) is not { } identityNamespace)
        {
            return Task.FromResult(new IdentityAsyncResult { Status = IdentityResultStatus.NotFound });
        }

        // The search needs a surname and a first name to match on, and every property it carries is
        // checked as a create's is. The error names only the item's position and the property, never
        // the values supplied.
        List<IdentityError> errors =
        [
            .. requests.SelectMany(
                (request, index) => CheckAttributes(request, $"$[{index}]", ["LastSurname", "FirstName"])
            ),
        ];

        if (errors.Count > 0)
        {
            return Task.FromResult(
                new IdentityAsyncResult { Status = IdentityResultStatus.InvalidProperties, Errors = errors }
            );
        }

        // One entry per search request, in request order, each holding zero or more scored matches.
        return Task.FromResult(
            new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = AcmeIdentityStore.Complete(
                    requests.Select(r => store.Search(identityNamespace, r))
                ),
            }
        );
    }
```

### Results, and the class's last member

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityService.cs#provider-results -->
```csharp
    public Task<IdentityResult> ResultsAsync(
        string requestToken,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Authorize the client before revealing anything about a job. A job issued to another client,
        // tenant, or route, an expired job, and an unknown token all answer NotFound.
        if (store.ResolveNamespace(context) is null || store.FindJob(requestToken, context) is not { } job)
        {
            return Task.FromResult(new IdentityResult { Status = IdentityResultStatus.NotFound });
        }

        // The object is required even while the job runs; it simply carries no results yet.
        // Once complete, every authorized poll returns the same payload.
        return Task.FromResult(
            job.IsReady
                ? new IdentityResult
                {
                    Status = IdentityResultStatus.Success,
                    Payload = job.Result.DeepClone(),
                }
                : new IdentityResult
                {
                    Status = IdentityResultStatus.Incomplete,
                    Payload = new JsonObject { ["Status"] = "Incomplete" },
                }
        );
    }

    // Accepts the lookup as a job owned by the calling context and returns its poll token. The token
    // is URL-safe, carries no person data, and is only meaningful because Results is advertised above.
    private IdentityAsyncResult Accept(IdentityRequestContext context, JsonObject result) =>
        new() { Status = IdentityResultStatus.Success, RequestToken = store.AcceptJob(context, result) };
}
```

### The store the sample stands on

<!-- embed: eng/verification/IdentityConsumer/AcmeIdentityService.cs#provider-store -->
```csharp
/// <summary>
/// The in-memory stand-in for Acme's upstream system: identities per namespace, jobs per owner, and
/// the clients granted access. Safe for concurrent use, because one instance serves every request.
/// Each listed client is granted every namespace, which is the explicit broad grant; a real provider
/// maps each client to the namespaces it may use. Jobs live in this process only, so this sample
/// supports a single replica and loses accepted jobs on restart; a real provider keeps jobs where
/// every replica can read them and documents their retention.
/// </summary>
internal sealed class AcmeIdentityStore(IReadOnlyCollection<string> authorizedClients)
{
    private static readonly TimeSpan JobDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JobRetention = TimeSpan.FromHours(1);

    // An RFC 3339 date-time with an offset, as the served schema's date-time format requires: a date
    // alone, or a date-time with no offset, names no single instant.
    private static readonly string[] BirthDateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    public static readonly string[] TextProperties =
    [
        "LastSurname",
        "FirstName",
        "MiddleName",
        "GenerationCodeSuffix",
        "SexType",
    ];

    public static readonly string[] LocationProperties =
    [
        "City",
        "StateAbbreviation",
        "InternationalProvince",
        "Country",
    ];

    private readonly HashSet<string> authorizedClients = new(authorizedClients, StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonObject>> identities =
        new();

    private readonly ConcurrentDictionary<string, AcmeJob> jobs = new(StringComparer.Ordinal);

    // Returns the identity namespace for the request, or null when the client has no grant. Tenant and
    // qualifier names and values compare case-insensitively and the client id exactly, so the key is
    // built from upper-cased text in an encoding that cannot be confused by a delimiter in a value.
    public string? ResolveNamespace(IdentityRequestContext context) =>
        authorizedClients.Contains(context.ClientId)
            ? JsonSerializer.Serialize(
                new[] { context.Tenant?.ToUpperInvariant() }.Concat(
                    context
                        .RouteQualifiers.OrderBy(q => q.Key.ToUpperInvariant(), StringComparer.Ordinal)
                        .SelectMany(q => new[] { q.Key.ToUpperInvariant(), q.Value.ToUpperInvariant() })
                )
            )
            : null;

    public static bool IsText(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? _);

    public static bool HasText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text);

    // The birth date in one UTC form, so a stored date and a searched one compare as the same
    // instant however each was written; null when the value is not a date-time with an offset.
    public static string? NormalizeBirthDate(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue(out string? text)
        && DateTimeOffset.TryParseExact(
            text,
            BirthDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed
        )
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
            : null;

    // A GUID without hyphens: 32 characters from the guaranteed repertoire, one URL path segment.
    public string Issue(string identityNamespace, JsonObject request)
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        JsonObject birthLocation = request["BirthLocation"] as JsonObject ?? [];

        // Standard properties the request omits are stored as null, never left out. The request was
        // checked first, so each value already has its schema type; the birth date is normalized.
        JsonObject identity = new()
        {
            ["UniqueId"] = uniqueId,
            ["LastSurname"] = request["LastSurname"]?.DeepClone(),
            ["FirstName"] = request["FirstName"]?.DeepClone(),
            ["MiddleName"] = request["MiddleName"]?.DeepClone(),
            ["GenerationCodeSuffix"] = request["GenerationCodeSuffix"]?.DeepClone(),
            ["SexType"] = request["SexType"]?.DeepClone(),
            ["BirthDate"] = NormalizeBirthDate(request["BirthDate"]),
            ["BirthOrder"] = request["BirthOrder"]?.DeepClone(),
            ["BirthLocation"] = new JsonObject
            {
                ["City"] = birthLocation["City"]?.DeepClone(),
                ["StateAbbreviation"] = birthLocation["StateAbbreviation"]?.DeepClone(),
                ["InternationalProvince"] = birthLocation["InternationalProvince"]?.DeepClone(),
                ["Country"] = birthLocation["Country"]?.DeepClone(),
            },
            ["Score"] = null,
        };

        identities
            .GetOrAdd(identityNamespace, _ => new(StringComparer.OrdinalIgnoreCase))
            .TryAdd(uniqueId, identity);

        return uniqueId;
    }

    // Returns a copy of the identity, with Score set, or null when the namespace holds no such id.
    public JsonObject? Find(string identityNamespace, string uniqueId, int? score) =>
        identities.TryGetValue(identityNamespace, out var inNamespace)
        && inNamespace.TryGetValue(uniqueId, out var identity)
            ? WithScore(identity, score)
            : null;

    // Matches on surname and first name; a supplied birth date must also agree, and raises the score.
    public IEnumerable<JsonObject> Search(string identityNamespace, JsonObject criteria) =>
        identities.TryGetValue(identityNamespace, out var inNamespace)
            ? inNamespace
                .Values.Where(identity =>
                    Same(identity["LastSurname"], criteria["LastSurname"])
                    && Same(identity["FirstName"], criteria["FirstName"])
                    && (
                        criteria["BirthDate"] is null
                        || Same(identity["BirthDate"], NormalizeBirthDate(criteria["BirthDate"]))
                    )
                )
                .Select(identity => WithScore(identity, criteria["BirthDate"] is null ? 90 : 100))
            : [];

    // The payload of a finished find or search: one entry per request, each with its responses.
    public static JsonObject Complete(IEnumerable<IEnumerable<JsonObject>> matchesPerRequest) =>
        new()
        {
            ["Status"] = "Complete",
            ["SearchResponses"] = new JsonArray(
                matchesPerRequest
                    .Select(matches =>
                        (JsonNode)new JsonObject { ["Responses"] = new JsonArray([.. matches]) }
                    )
                    .ToArray()
            ),
        };

    public string AcceptJob(IdentityRequestContext context, JsonObject result)
    {
        string token = Guid.NewGuid().ToString("N");
        DateTimeOffset readyAt = TimeProvider.System.GetUtcNow() + JobDelay;
        jobs[token] = new AcmeJob(OwnerKey(context), result, readyAt, readyAt + JobRetention);
        return token;
    }

    // A job is visible only to the complete context it was accepted under, and only until it expires.
    public AcmeJob? FindJob(string token, IdentityRequestContext context)
    {
        if (!jobs.TryGetValue(token, out var job))
        {
            return null;
        }

        if (TimeProvider.System.GetUtcNow() >= job.ExpiresAt)
        {
            jobs.TryRemove(token, out _);
            return null;
        }

        return job.Owner == OwnerKey(context) ? job : null;
    }

    private string OwnerKey(IdentityRequestContext context) =>
        JsonSerializer.Serialize(new[] { ResolveNamespace(context), context.ClientId });

    private static bool Same(JsonNode? stored, JsonNode? supplied) =>
        string.Equals(stored?.ToString(), supplied?.ToString(), StringComparison.OrdinalIgnoreCase);

    private static JsonObject WithScore(JsonObject identity, int? score)
    {
        var copy = (JsonObject)identity.DeepClone();
        copy["Score"] = score;
        return copy;
    }
}

internal sealed record AcmeJob(
    string Owner,
    JsonObject Result,
    DateTimeOffset ReadyAt,
    DateTimeOffset ExpiresAt
)
{
    public bool IsReady => TimeProvider.System.GetUtcNow() >= ReadyAt;
}
```

## Capabilities

`Capabilities` is a set of flags, `Create`, `GetById`, `Find`, `Search` and `Results`, and a provider returns the union of the operations it supports.
A provider that returns `IdentityCapabilities.None` supports nothing, which is exactly how the host default behaves.

- **Capabilities are deployment-wide in v1.**
  The first contract exposes them for the whole deployment rather than per tenant or per route qualifier.
  Which tenant or qualifier a client may use is governed by the provider's own namespace-access policy, and an unknown or unauthorized namespace answers `NotFound`, never a missing-capability response.
  A later context-aware capability method would arrive as a default interface member or a new versioned interface or package, because adding a required member to a published, plugin-implemented interface is a breaking change.
- **The getter must be inexpensive, perform no I/O, and return a stable value** across requests and across instances of the registration for the configured deployment.
  It must not depend on whether an upstream system is available: an outage does not change the capability set.
  A configuration change that needs a different set takes effect on restart.
- **DMS captures the value once per request.**
  It reads `Capabilities` immediately after activating the provider and before any operation, and uses that one captured value for both capability checks: the gate on the requested operation, and the rule that a request token from find or search may only be returned when `Results` is advertised.
  The same resolved instance then serves the call, so a provider can never observe a capability set different from the one its call was gated on.
- **`Results` is its own capability.**
  It is what the host checks before it will hand a token to `ResultsAsync`.
  A provider whose `FindAsync` or `SearchAsync` returns a request token while `Results` is absent has misused the contract, and the host answers a provider-contract-violation `502`.
- **An unsupported operation is a `404` before the request itself is validated.**
  The gate runs after authentication and the tenant and claim checks, and before the content type, the body parse and the duplicate-property check, and no `IIdentityService` method is called.
  An unauthenticated request still gets the `401`, and one without the identity claim the `403`.
  A request for an unsupported operation gets the operation-unsupported `404` even when its body is malformed or has a duplicate property.
  [The four 404 responses](#the-four-404-responses) show how a client tells that `404` apart from the others.

## Operations

The routes are relative to the host's base URL.
When the host is multi-tenant, `/{tenant}` precedes them, and any configured route-qualifier segments follow it, as for the other DMS routes outside `/data`.

| Operation | Method and route | Request body | `IIdentityService` method | Result |
| --- | --- | --- | --- | --- |
| Create | `POST /identity/v2/identities` | a JSON object | `CreateAsync` | synchronous: `200` with the new UniqueId as a JSON string |
| Get by UniqueId | `GET /identity/v2/identities/{id}` | none | `GetByIdAsync` | synchronous: `200` with an `IdentityResponse` |
| Find | `POST /identity/v2/identities/find` | a JSON array of strings | `FindAsync` | synchronous `200` with a complete `IdentitySearchResponse`, or `202` with a `Location` and no body |
| Search | `POST /identity/v2/identities/search` | a JSON array of objects | `SearchAsync` | synchronous `200` with a complete `IdentitySearchResponse`, or `202` with a `Location` and no body |
| Results | `GET /identity/v2/identities/results/{token}` | none | `ResultsAsync` | `200` with a complete `IdentitySearchResponse`, or `200` with an incomplete one and a `Location` |

Two route details follow from the fixed routes.
A `GET` to `.../identities/results` or `.../identities/find` with no further segment is a get-by-id for the UniqueId `results` or `find`, because the results route needs a token segment and find accepts only `POST`.
`{id}` and `{token}` reach your provider as the framework-decoded route value, unchanged: the host performs no second unescape, and it never trims or normalizes either value.

### Request bodies

- **Accepted media types.**
  `application/json` and `text/json`, with or without parameters such as a charset, and a request with no `Content-Type` header at all.
  Any other value, including an Ed-Fi profile media type, is rejected with `415` before the body is parsed.
- **Top-level shapes.**
  Create takes a JSON object.
  Find takes a JSON array, and every element must be a JSON string.
  Search takes a JSON array, and every element must be a JSON object.
  A body of any other top-level shape, a find array holding a non-string, or a search array holding a non-object is rejected with `400` before the provider is called.
  An empty array is a valid shape and is passed to the provider, which decides what an empty request means.
  Get and results carry no body.
- **Malformed JSON and an empty body** are rejected with `400`.
- **Duplicate property names are rejected** with `400`, at any depth, before the provider is called.
  The problem carries a `validationErrors` entry keyed by the JSON path of the duplicate, such as `$.FirstName` or `$[1].BirthLocation.City`, with the message `An item with the same key has already been added.`
  A provider therefore never sees a request object with a duplicated key.
- A route value that is present but blank is rejected with `400`.
  The provider never receives a null, empty or whitespace-only UniqueId or token.

The `IIdentityService` methods receive the parsed body: a `JsonObject` for create, an `IReadOnlyList<string>` of UniqueIds for find, and an `IReadOnlyList<JsonObject>` for search.

## Payloads

DMS treats payloads as opaque JSON and validates almost nothing about their contents, but their shape is defined rather than provider-chosen.
The OpenAPI document that DMS serves pins every shape, so a provider returning a different shape serves a response that does not conform to the API it backs.
[Validating your payloads](#validating-your-payloads) shows how to check yours against it.

### Standard identifying attributes

Create and search request objects, and every identity a provider returns, share one set of standard identifying properties, all of them nullable.

| Property | Type |
| --- | --- |
| `LastSurname`, `FirstName`, `MiddleName`, `GenerationCodeSuffix`, `SexType` | string |
| `BirthDate` | string with format `date-time` |
| `BirthOrder` | integer |
| `BirthLocation` | object with nullable string children `City`, `StateAbbreviation`, `InternationalProvince` and `Country` |

On a request, a client expresses an unknown value either as `null` or by omitting the property, and a provider should treat the two alike unless its own validation requires the field.
A request never carries `UniqueId` or `Score`.
DMS does not require any standard property, validate any value, or reject custom properties, so the provider decides which properties its integration needs and answers `InvalidProperties` when the supplied data is insufficient.
A provider may add custom properties to request objects, to `IdentityResponse` and to `BirthLocation`, and DMS passes them through without inspecting them.

### The identity a provider returns

An `IdentityResponse` carries every standard property above, plus a required, non-empty `UniqueId` and a required `Score`.

- **Unsupported attributes are `null`, not omitted.**
  A provider represents a standard attribute its upstream system does not support as `null`, and the served schema lists every one of these properties as required.
  When birth-location data is unsupported, the provider still sends `BirthLocation` itself, with `null` children.
- **`BirthDate` is a `date-time` string**, not a date.
- **`Score` is a JSON number, `number` with format `double`.**
  It is `null` for an identity returned outside a search, and a number from 0 through 100 for every search match.
  DMS neither reads `Score` nor applies a threshold to it.

### What each operation returns

| Operation and status | Payload |
| --- | --- |
| Create, `Success` | a JSON string holding the newly issued UniqueId, a bare string and not an object wrapping one |
| Get by UniqueId, `Success` | one `IdentityResponse` object |
| Find or search, `Success`, complete | an `IdentitySearchResponse` object (below) and no request token |
| Find or search, `Success`, pending | a usable request token and no payload |
| Results, `Success` | an `IdentitySearchResponse` with `Status` `"Complete"` |
| Results, `Incomplete` | an `IdentitySearchResponse` with `Status` `"Incomplete"` |
| `InvalidProperties`, `NotFound`, `JobFailed` | no payload |

An `IdentitySearchResponse` is a JSON object:

```json
{
  "Status": "Complete",
  "SearchResponses": [
    {
      "Responses": [
        {
          "UniqueId": "604700123",
          "LastSurname": "Rivera",
          "FirstName": "Ana",
          "MiddleName": null,
          "GenerationCodeSuffix": null,
          "SexType": null,
          "BirthDate": "2012-05-14T00:00:00Z",
          "BirthOrder": null,
          "BirthLocation": {
            "City": null,
            "StateAbbreviation": null,
            "InternationalProvince": null,
            "Country": null
          },
          "Score": 100
        }
      ]
    },
    {
      "Responses": []
    }
  ]
}
```

- **A synchronous find or search is complete.**
  It returns `Success` with a payload whose `Status` is `"Complete"` and whose `SearchResponses` array is present.
  A payload that says `"Incomplete"` is never a synchronous answer.
- **`SearchResponses` is ordered and positional.**
  It holds exactly one entry per submitted UniqueId or search request, in request order, and each entry carries a required `Responses` array.
  For find, an entry holds zero or one `IdentityResponse`, and for search zero or more, each with a numeric `Score`.
- **No match is an empty `Responses` array**, in a `Success` result.
  It is not `NotFound`, and not a dropped entry, because the entries are positional.
  In the example above the second request matched nothing.
  `NotFound` is reserved for an unknown or unauthorized namespace, and for a get-by-id or poll that matches no identity or job.
- **Pending work returns a usable token and no payload.**
  A provider that accepts an asynchronous job returns `Success` with `RequestToken` set and `Payload` left null, and DMS answers `202` with a `Location` header.
  Returning both a token and a payload, or neither, is a contract violation.
  [Asynchronous requests and jobs](#asynchronous-requests-and-jobs) covers the token rules and the job obligations.
- **`Incomplete` is for a pending results poll only.**
  `ResultsAsync` returns it, with a payload whose `Status` is `"Incomplete"`, while the job is still running.
  That payload is required even then, and it may omit `SearchResponses` or send it empty.

## Results and status codes

A provider reports a definitive outcome with an `IdentityResultStatus`, and DMS maps it to an HTTP response.
The table shows what each combination produces.
Every problem response is `application/problem+json`.

| Operation | Status the provider returns | HTTP status | Problem type |
| --- | --- | --- | --- |
| any | `NotFound` | `404` | `urn:ed-fi:api:identities:not-found` |
| any | `InvalidProperties` with at least one error | `400` | `urn:ed-fi:api:bad-request:data-validation-failed`, or `urn:ed-fi:api:bad-request` when any error has no path |
| Create | `Success` with a JSON string payload | `200` with the string | |
| Get by UniqueId | `Success` with a payload | `200` with the payload | |
| Find, search | `Success` with a payload and no token | `200` with the payload | |
| Find, search | `Success` with a usable token and no payload | `202`, no body, `Location` header | |
| Results | `Success` with a payload | `200` with the payload | |
| Results | `Incomplete` with a payload | `200` with the payload and a `Location` header for the same poll | |
| Results | `JobFailed` | `502` | `urn:ed-fi:api:identities:job-failed` |
| any other combination in the next section | a contract violation | `502` | `urn:ed-fi:api:identities:provider-contract-violation` |
| any | the call throws | `502` | `urn:ed-fi:api:identities:upstream-failure` |

The `Location` of a `202` or of an incomplete poll is an absolute URL built from the request's own URL, so a tenant and route qualifiers carry through: `.../identity/v2/identities/results/{escaped token}`.

### What the host checks, and what it does not

DMS validates almost nothing about a payload's contents, and it does check the following.
Each failure is a `502` provider-contract-violation problem whose `detail` names the broken rule and never repeats provider text or payload content.

- The operation returned no result at all.
- The status is not one of the five defined values.
- `JobFailed` from any operation but results.
- `Incomplete` from any operation but results, or `Incomplete` with no payload.
- A `Success` with no payload from create, get by UniqueId or results.
- A create `Success` whose payload is not a JSON string.
- A find or search `Success` with both a payload and a token, or with neither.
- A find or search `Success` with a token while `Results` is not advertised.
- A find or search `Success` with a token that cannot be composed into a poll path.
- `InvalidProperties` with a null or empty error list, a null entry, or an entry with a null `Message`.

Everything else about a payload is served as returned.
A misshaped `IdentityResponse`, a `SearchResponses` array with the wrong number of entries, a `Score` that is a string, or a `BirthDate` that is not a `date-time` reaches the client unchanged, so it is a provider defect that the host does not detect on your behalf.
A token returned alongside `InvalidProperties` or `NotFound` is ignored.
Errors returned alongside any status but `InvalidProperties` are ignored.

### The four 404 responses

Four different situations answer `404`, and the problem body tells them apart.
Switch on the problem `type` and, where two share one, on `detail`.

| Situation | `type` | `detail` |
| --- | --- | --- |
| The provider reported `NotFound`: no matching identity or job, a mismatched or expired job, an unknown or unauthorized namespace | `urn:ed-fi:api:identities:not-found` | `The specified data could not be found.` |
| The provider does not advertise the requested operation | `urn:ed-fi:api:identities:operation-not-supported` | `The identity provider does not support this operation.` |
| The request's tenant does not exist (multi-tenant hosts, after authentication) | `urn:ed-fi:api:not-found` | `The specified tenant could not be found.` |
| Identity management is switched off (`AppSettings:EnableIdentityManagement` is `false`), so no identity route exists | `urn:ed-fi:api:not-found` | `The specified data could not be found.` |

The tenant-not-found and the feature-off responses share the general `urn:ed-fi:api:not-found` type, so `detail` is what separates them.
The `Cache-Control: no-store` header does not separate them, because the host adds it to every non-successful response, the catch-all `404` included.
An identity-not-found `404` deliberately says nothing about whether a namespace exists, a client is authorized, or a person is known.

### The three 502 problems, and the terminal job failure

Three problem types share `502`, and the `type` is how a client tells them apart.

- **`urn:ed-fi:api:identities:provider-contract-violation`**: the provider returned a result the contract does not permit, as listed above.
  Retrying does not help, and the fix is in the provider.
- **`urn:ed-fi:api:identities:upstream-failure`**: an operation call threw, so it obtained no answer.
  It is a sanitized problem with no provider detail, the host establishes no terminal job state, and the problem makes no statement about whether a retry will succeed.
- **`urn:ed-fi:api:identities:job-failed`**: a results poll returned `JobFailed`.

`JobFailed` is valid only from `ResultsAsync`, and only when the provider has established that an accepted job failed permanently.
It requires no payload, and DMS ignores any payload or errors supplied with it.
DMS answers its own fixed, sanitized problem, `urn:ed-fi:api:identities:job-failed`, with no provider payload, error message or token, and with no `Location` header.
That `type`, rather than the status code, is the portable terminal signal: a client that switches on `502` alone cannot tell a permanently failed job from a poll that simply did not reach an answer.

- The terminal problem tells the client to stop polling this job.
  It is not an instruction to resubmit the original find or search automatically.
- The provider retains the terminal state for its documented retention period and returns the same `JobFailed` on repeated authorized polls while the state is retrievable, subject to the same ownership and expiry checks as a complete job.
  After expiry, or for a different client, the answer is `NotFound`.
- A poll that throws is not a failed job.
  It answers the ordinary upstream-failure `502`, and the job, including one that is already terminal but whose record could not be read, stays retrievable and unmodified once the dependency recovers.
  A client may retry the poll under its own bounded policy.
  That problem guarantees neither that the failure is temporary nor that a later poll will succeed, and it never makes resubmitting the original find or search safe, because the job it failed to read may still be running.
- Return `JobFailed` only from a call that itself establishes permanent failure.
  When the job's state cannot be read, throw.

### The other statuses a client can see

The host produces these without calling your provider, and a client sees them whatever the provider does.
Every one of them is `application/problem+json`, except the `400` for a malformed or empty body or a duplicate property, which DMS sends as `application/json` with the same problem body.

| Status | Cause |
| --- | --- |
| `400` | a malformed or empty body, a duplicate property, a body of the wrong top-level shape, a blank route value, or `InvalidProperties` from the provider |
| `401` | no bearer token, a token that cannot be validated, or a client not bound to the URL's tenant |
| `403` | the client's claim set does not grant the identity service claim for the operation |
| `404` | the four situations above |
| `415` | a `Content-Type` other than `application/json` or `text/json` on a POST |
| `429` | the global rate limiter rejected the request, with a `Retry-After` header only when the limiter supplies one |
| `500` | `urn:ed-fi:api:identities:provider-configuration`, when the provider cannot be activated or its `Capabilities` getter throws, or `urn:ed-fi:api:system:configuration:security`, when the identity service claim is not configured as the host requires |
| `502` | the three problem types above |
| `503` | the tenant catalog or the client-to-tenant binding cannot be resolved right now |

### Cache-Control

Every response from an identity operation route carries `Cache-Control: no-store`, whatever its status: the `200` and `202` successes, the poll outcomes, the problems the host raises before the pipeline starts, and a `429` from the rate limiter.
The host sets it after routing and before the rate limiter, so a request the limiter rejects still carries it.
Metadata documents and unrelated routes keep the host's general rule instead: their non-successful responses carry `no-store`, and their successful ones do not.

This is an HTTP-cache policy only.
It stops a shared or private HTTP cache from storing a response.
It does not delete a copy a client already holds, and it is not a retention guarantee about person data on the client side.
How long a provider keeps an accepted job's result is a separate, provider-documented matter.

## Errors

A provider reports invalid request data with `IdentityResultStatus.InvalidProperties` and a list of `IdentityError` entries, each with a `Message` and an optional `Path`.
`IdentityError` entries are used only for `InvalidProperties`, and entries returned with any other status are ignored.
The list must not be null or empty, and no entry may be null or have a null `Message`, or the host answers a provider-contract-violation `502`.

DMS projects the errors into the same `400` problem body that schema validation produces elsewhere in the API.
`Path` is a JSONPath rooted at `$`, and DMS uses it verbatim, as a key of the problem's `validationErrors` object.
It never parses, splits or renumbers a path, and it does not check that the path exists in the request.

| `Path` | Meaning |
| --- | --- |
| `$` | the request as a whole, for a create body or a find or search array |
| `$.FirstName` | a property of a create request object |
| `$[2].FirstName` | a property of item 2, counting from zero, of a find or search array |
| `$.BirthLocation.City` | a nested property |
| `null` or blank | no path: the message goes to the problem's document-level `errors` array |

- **Messages for the same path are grouped** under one `validationErrors` key, in the order the provider returned them.
- **Keys appear in the order the provider first used each path.**
- **When every error has a path**, the problem is `urn:ed-fi:api:bad-request:data-validation-failed`, and `errors` is empty.
- **When any error has no path**, the problem is `urn:ed-fi:api:bad-request`, with those messages in `errors`, and any path-keyed messages still in `validationErrors`.

A create request that fails on two properties, one of them twice:

```csharp
return Task.FromResult(
    new IdentityResult
    {
        Status = IdentityResultStatus.InvalidProperties,
        Errors =
        [
            new IdentityError { Message = "FirstName is required.", Path = "$.FirstName" },
            new IdentityError { Message = "FirstName must not exceed 75 characters.", Path = "$.FirstName" },
            new IdentityError { Message = "BirthDate must be a date-time with a UTC offset.", Path = "$.BirthDate" },
        ],
    }
);
```

becomes this response:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json
Cache-Control: no-store
```

```json
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "0HNOOQ2BHB6VR",
  "validationErrors": {
    "$.FirstName": [
      "FirstName is required.",
      "FirstName must not exceed 75 characters."
    ],
    "$.BirthDate": [
      "BirthDate must be a date-time with a UTC offset."
    ]
  },
  "errors": []
}
```

A search request whose third group is missing a surname reports the group's position in the path, as the sample provider does:

```csharp
new IdentityError { Message = "LastSurname is required.", Path = "$[2].LastSurname" }
```

```json
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "0HNOOQ2BHB6VR",
  "validationErrors": {
    "$[2].LastSurname": [
      "LastSurname is required."
    ]
  },
  "errors": []
}
```

An error about the request as a whole, with no path, goes to `errors`, and changes the problem's type:

```csharp
new IdentityError { Message = "The request could not be evaluated.", Path = null }
```

```json
{
  "detail": "The request could not be processed. See 'errors' for details.",
  "type": "urn:ed-fi:api:bad-request",
  "title": "Bad Request",
  "status": 400,
  "correlationId": "0HNOOQ2BHB6VR",
  "validationErrors": {},
  "errors": [
    "The request could not be evaluated."
  ]
}
```

Write messages that name the property and never its value.
Names, birth dates and other identifying data are person data, and a message is returned to the client.

## Failures and logging

A failure of an operation call to obtain an answer is signaled by throwing, never by a result status.
DMS never retries a call on this interface and imposes no timeout of its own on one, so a timeout a provider does not handle itself surfaces as a thrown exception.

- **An operation that throws is a sanitized `502` upstream-failure.**
  The client response carries a fixed message, with no provider detail.
- **Failure to activate the provider, or a `Capabilities` getter that throws, is a sanitized `500` provider-configuration problem**, `urn:ed-fi:api:identities:provider-configuration`, and no operation is invoked.
  This covers a constructor, a registration factory, a scoped dependency, and the getter.
  A provider's constructor or factory must also succeed outside a request, because at startup the host constructs every declared plugin contract once in a discarded scope to verify the registration can be activated.
- **An exception message is recorded only at `Debug`, and never returned.**
  The host's own failure log, at the error level, carries only the exception type name, the stage (activation, capabilities or invoke), the operation, the trace id and the stack trace.
  The exception's message, and its inner exceptions, are written only at `Debug`.
  That applies to activation, the capability getter and operation invocation alike, and it includes nested exceptions: an inner exception's message is no more visible than the outer one's.
- **Keep person data out of the exception messages you raise.**
  Names, birth dates and identifiers belong in no message, because an operator who enables `Debug` logging, or a log handler beneath the host, will see it.
- **Cancellation is not a failure.**
  Request cancellation observed before or during activation, the capability read, or an invocation propagates as an `OperationCanceledException` with no replacement problem response.
  The host rethrows a fresh exception that carries only the request's token, so your own message and inner exception never reach the host's cancellation logging, and the original is logged at `Debug` only.
  Honor the `CancellationToken` you are given and pass it to your upstream calls.
  A cancellation your own timeout raises while the request is still live is an ordinary failure.
  During activation or the capability read it answers the provider-configuration `500`, and during an operation call the upstream-failure `502`.
- **The host's restriction binds only what the host writes.**
  A plugin runs fully trusted inside the host process, and this contract cannot constrain what a provider logs on its own account.
  Keeping provider detail out of operator-visible logs at the higher levels is your obligation, and the host cannot enforce it.
  The contract also does not constrain plugin startup admission, which [PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md) describes.

## Validating your payloads

The Data Management Service serves the OpenAPI document for the identity API at `/metadata/identity/v2/swagger.json`, when `AppSettings:EnableIdentityManagement` is on.
It is the artifact to validate your own request and response payloads against.
It defines `IdentityCreateRequest`, `IdentitySearchRequest`, `IdentityResponse` and `IdentitySearchResponse`, with their complete and incomplete forms, and each problem type, and it states its contract version in `x-edfi-identity-contract-version`.
It also pins example `400` bodies, for a create failure keyed by `$.FirstName`, a search item failure keyed by `$[2].FirstName`, a pathless error, and two messages under one key, and your `InvalidProperties` errors should project to bodies of those shapes.
The repository copy is
[identity-v2-openapi.json](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/dms/core/EdFi.DataManagementService.Core/OpenApi/identity-v2-openapi.json).

No conformance test package ships with this contract, and the contract package embeds no payload schemas.
You validate against the served document with your own tooling.

## Browser clients and the Location header

A client that runs in a browser and follows the asynchronous flow must read the `Location` header of a `202` and of an incomplete results poll.
A browser exposes a response header to a cross-origin script only if the server lists it, so DMS exposes `Location` through CORS for that reason.
It exposes it only to the single origin configured as `Cors:SwaggerUIOrigin`, which defaults to `http://localhost:8082`, and only when `AppSettings:EnableIdentityManagement` is on.
A browser client served from any other origin cannot read the header, so deploy such a client at the configured origin, or change that setting.
Clients that are not browsers are not affected.

## Asynchronous requests and jobs

`FindAsync` and `SearchAsync` are the only operations that can begin an asynchronous job, by returning `Success` with a `RequestToken` and no payload.
DMS answers `202` with a `Location` header built from that token, and the client polls it until `ResultsAsync` reports a complete result.
The token is opaque to DMS, which assumes no structure or meaning in it, and the rules below exist only so that it can travel safely through a URL path.

### The request token rule

**A usable token is one DMS can round-trip through a poll path.**
A token is usable when it satisfies every one of these:

- It is not null, empty or whitespace.
- It contains no `/`, no `\` and no control character.
- It is not exactly `.` or `..`, the two dot segments, which a URL path would treat as traversal.
- It compares ordinally equal to `Uri.UnescapeDataString(Uri.EscapeDataString(token))`, which excludes, for example, a lone UTF-16 surrogate.
- It escapes to at most 1024 characters under `Uri.EscapeDataString`.
- The poll path composed from it fits the deployment's request-line budget.

The 1024-character ceiling is on the escaped form, because escaping only ever expands a token.
It is chosen independently of any server's limit, so it rejects a pathologically long token even against a generously configured host.

The last rule depends on the deployment, and a provider cannot see the inputs to it.
DMS appends the escaped token as the final segment of `.../identity/v2/identities/results/{token}`, after the deployment's own path base, tenant segment and route-qualifier segments.
It then checks that the whole request line, `GET `, the path, ` HTTP/1.1` and the line terminator, fits the web server's configured maximum request line size, less any path prefix the host adds.
That maximum is 8192 bytes by default on Kestrel, and it is read at request time from the server's own configuration rather than assumed.

**An unusable token is rejected when the job is accepted, not later.**
When `FindAsync` or `SearchAsync` returns `Success` with a token that breaks any rule above, DMS answers a `502` provider-contract-violation problem to the original request, with no `Location` header.
The job has already been accepted by your provider at that point, so a client that sees this `502` holds no token for it.
A token that was merely long enough to be rejected here would otherwise yield a `Location` the client could not follow, because a web server answers a request line over its limit with `414`, which is why the host checks before it answers.
Keep a token short enough that this never arises.

- **Recommendation: keep an escaped token at or below 256 characters.**
  The rule above bounds only what DMS can observe.
  A proxy or gateway between the client and DMS may enforce a total-URL limit that DMS cannot see, and a `Location` that passes DMS's check can still be refused there.
- **Prefer a URL-safe, fixed-width token.**
  The sample provider issues a 32-character hyphen-free GUID, which escapes to itself and carries no person data.
- **Do not put person data in a token.**
  A token appears in the `Location` header, in access logs and in browser history.

### What a job owes the client

A provider that accepts jobs owns the lifecycle of each one, because DMS keeps no job state of its own.
These are obligations.
The sample provider shows each of them in its `ResultsAsync` and its store except a permanent failure, which its in-memory jobs cannot have.

- **Bind the job to the complete request context.**
  Record the tenant, the route qualifiers and the `ClientId` the job was accepted under, compared under the [equality rules](#namespaces-grants-and-context-equality) below.
  A poll whose context does not match answers `NotFound` and never returns the job's state, so a job accepted under one tenant, route or client cannot be read from another.
- **Scope the result to the issuing client, not to the tenant.**
  This is the v1 security boundary, and it is mandatory and not waivable.
  A provider whose upstream system shares results across a tenant must still gate the poll on `ClientId`, and documenting the upstream sharing does not excuse it.
- **Document the retention period, and answer `NotFound` after it.**
  A poll after expiry is indistinguishable from a poll of an unknown token, and that is intended.
- **Return the same result on every authorized poll of a complete job.**
  Polling is a `GET`, and clients and intermediaries retry a `GET`.
  A result that can be fetched once and is gone on the second poll is a defect.
- **Report a permanent failure as an answer.**
  Return `JobFailed` once the provider has established that the job failed for good, and keep returning it for the retention period.
  Never leave a dead job answering `Incomplete` forever, because the client then polls with no end.
  [The three 502 problems, and the terminal job failure](#the-three-502-problems-and-the-terminal-job-failure) says what the client sees, and warns against returning `JobFailed` for a read that merely failed: when the job's state cannot be read, throw.
- **Make the result retrievable from any replica, or say you cannot.**
  A poll reaches whichever replica the load balancer picks, so either keep jobs in storage every replica can read, or document that the integration supports a single replica only.
  The sample provider is the counter-example: its jobs live in one process's memory, so it supports a single replica only and loses its accepted jobs on restart.
- **Document what happens to accepted jobs on restart.**
  State whether a job accepted before a provider restart can still be polled afterward, and for how long.
- **A cancelled request does not cancel an accepted job.**
  Once `FindAsync` or `SearchAsync` has returned `Success` with a token, DMS has already committed to the `202`, and the client's connection is irrelevant to the job.
  Cancellation applies only to the call while it is in flight, and the `CancellationToken` you are given must never be used to tear down a job you have already accepted.

Every one of these also holds for `SearchAsync`, with the same ownership, retention and result-scoping rules.

### Polling

A client polls the `Location` it was given.
While the job runs, `ResultsAsync` returns `Incomplete` with a payload whose `Status` is `"Incomplete"`, and DMS answers `200` with a `Location` for the same poll.
When it finishes, `ResultsAsync` returns `Success` with the complete payload, and DMS answers `200` with no `Location`.
The authorization check on `ResultsAsync` comes first: resolve the namespace and authorize the client before revealing anything about a job, so that an unauthorized client and an unknown token get the same `NotFound`.

A client that resubmits a find or search does not resume the earlier job.
It starts a new one with a new token, and the earlier one stays subject to its own retention.

## Issuing UniqueIds

`CreateAsync` returns the UniqueId of a person, and that value then becomes natural-key data in person documents written to the Data Management Service.
It is the one value the contract asks you to choose rather than shape, so it carries the most constraints.
DMS does not enforce most of them when you return the id, and a violation surfaces later, in a person write or in a lookup.

A UniqueId you issue must:

- **Fit the person-resource `maxLength` of the deployment's ApiSchema.**
  That limit is 32 characters today, across the checked-in core and shipped extension schemas.
  It is a property of the ApiSchema a deployment runs, not a constant of this contract, so confirm it against the ApiSchema you deploy with.
- **Come from the guaranteed repertoire.**
  That is the ASCII digits `0` to `9` and the ASCII letters `A` to `Z` and `a` to `z`.
  Inside it, `StringComparer.OrdinalIgnoreCase` agrees exactly with the SQL Server identity collation.
- **Be non-empty and free of leading or trailing whitespace**, which DMS does not trim.
- **Be a single URL path segment**, because `GetByIdAsync` receives it as a route value.
- **Be stable for the life of the identity**, because person documents already written reference it.
- **Be unique, and distinct under `OrdinalIgnoreCase`, within the namespace you document.**
  The sections below say what that means.

**A canonical GUID does not fit.**
The canonical 36-character form, `d3b07384-d9a0-4c8e-9f5a-1b2c3d4e5f60`, exceeds the 32-character limit, and it is the likely default choice.
The same GUID without hyphens, `Guid.NewGuid().ToString("N")`, is 32 characters from the repertoire and fits.
The sample provider does exactly that.

### Case

**Case-variant ids collapse on SQL Server and stay distinct on PostgreSQL.**
Two UniqueIds that differ only by letter case, such as `ab12` and `AB12`, are one value to a database whose collation is case-insensitive and two values to one whose comparison is case-sensitive.
PostgreSQL compares text case-sensitively, so the two stay distinct there.
SQL Server's default database collation is case-insensitive, so the two collapse onto one person-resource key there.
DMS creates the SQL Server database without naming a collation, so a deployment gets whatever the server default is, and a deployment that overrides the default has its own answer.
This is a condition of the database, not something DMS enforces.

The consequence is that a provider can never rely on the datastore to tell two case-variant ids apart.
Make them distinct under `OrdinalIgnoreCase` yourself, as the list above says, and the same ids behave the same on both engines.

### Namespaces

**Uniqueness belongs to a namespace you define.**
A provider owns an identity authority namespace, and a documented mapping from each tenant and route-qualifier combination onto one.
Two independent registries may issue the same value in their own namespaces, and neither is wrong.
[Namespaces, grants and context equality](#namespaces-grants-and-context-equality) says how the context selects a namespace.

DMS does not mandate that UniqueIds be unique across the whole deployment, and it does not enforce what you document.
The obligation falls on whoever combines namespaces: identities from independent namespaces that end up in one person table of one datastore must have compatible namespaces, or be remapped by the provider or its upstream system.
Two namespaces that both issue `0042` and share a person natural-key domain collide there, and nothing in DMS will notice.
No deployment-wide namespace, host remapping or person-resource write is implied by this contract.

### Ids outside the repertoire

DMS does not reject a UniqueId outside the guaranteed repertoire, so nothing stops a provider issuing one.
It moves the uniqueness obligation onto the provider, under each backing store's own equality.
`OrdinalIgnoreCase` is an in-process approximation of the SQL Server collation, exact only inside the repertoire, so outside it the two can disagree, and this contract pins no cross-engine equivalence for such ids.
A provider that issues them must therefore prove its ids distinct under the equality of every store the deployment uses.

`GetByIdAsync` is the reverse case.
It receives a UniqueId unchanged, which may be outside the repertoire if the caller sent one, and the provider looks it up under its own equality.

## Namespaces, grants and context equality

Every operation receives an `IdentityRequestContext`, and the provider decides from it which identity namespace the call is about and whether the client may use it.
DMS gives you the context and nothing more: the namespace policy is yours.

### Where DMS's responsibility ends

**DMS validates the tenant and binds the client, and the provider authorizes the namespace.**
Before your provider is called, a multi-tenant host has checked that the URL's tenant exists, which answers `404` otherwise, and has checked that the authenticated client belongs to that tenant, which answers `401` otherwise.
It has also checked that the client's claim set grants the identity service claim for the operation.
Route qualifiers, such as a district or a school year, are passed through as context without being interpreted.
Identity does not inherit datastore authorization and does not select a datastore: the claim and tenant checks are necessary and are not sufficient.

**Authorize the client against your own namespace policy on every operation.**
Before any identity work, resolve the namespace from the tenant and route qualifiers, and check that the `ClientId` holds a grant for it.
That applies to create, get, find and search at the moment of the request, and not only to results polls.
A caller who is not authorized must not be able to issue ids, probe for identities or start a job.
The sample provider does this at the start of every operation.

Document these four things for your operators:

- the **policy source**, such as grants kept in the provider's own configuration or an upstream authorization service;
- how grants are **administered**;
- which **operations** a grant permits, since a grant may allow reads and not create;
- how the policy is **cached**, and how a revoked grant takes effect, because a cached grant keeps serving until it refreshes.

How the provider answers follows from that policy:

- **A missing or denied grant, and an unknown namespace, answer `NotFound`.**
  Return the same `NotFound` for both, without naming the namespace or any person, so the response never confirms that a namespace or an identity exists.
  `InvalidProperties` diagnoses invalid request data and is never the answer to a missing permission.
- **A policy-source failure is never an implicit grant.**
  When the policy cannot be read, throw, and DMS answers the sanitized upstream-failure `502`.
  Do not fall back to allowing the call, and do not convert the failure to `NotFound`, which would hide an outage as a missing grant.
- **A broad grant is explicit.**
  A deployment may deliberately grant a client every namespace of a tenant, as the sample provider does for each client it lists.
  That grant must be written down, and it is never inferred from a tenant existing or from the absence of client-specific rules.
- **A namespace grant never waives job ownership.**
  A client allowed to use a namespace still reads only the jobs its own `ClientId` accepted, as [What a job owes the client](#what-a-job-owes-the-client) requires.

No datastore entitlement system and no additional DMS authorization API is involved or required.

### Context equality

**Tenant and qualifier names and values compare with `OrdinalIgnoreCase`, and the client id compares with `Ordinal`.**
These are explicit contract rules, consistent with how DMS matches routes, and the provider applies them itself.

| Part of the context | Comparison | Example |
| --- | --- | --- |
| `Tenant` | `OrdinalIgnoreCase` | `North` and `north` are the same tenant |
| `Tenant` null | equals only null | null is single-tenant mode, and equals neither `""` nor a named tenant |
| `RouteQualifiers` names | `OrdinalIgnoreCase` | `districtId` and `DistrictID` are one key |
| `RouteQualifiers` values | `OrdinalIgnoreCase`, no trimming, no Unicode normalization | `ab` and `AB` match, `ab` and `ab ` do not |
| `RouteQualifiers` as a whole | the same names with equivalent values, in any order | `{districtId: 255901, schoolYear: 2026}` equals the same pairs in the other order, and a missing qualifier differs from a present one |
| `ClientId` | `Ordinal` | `App1` and `app1` are different clients |
| `TraceId` | excluded | correlation only, never an ownership, cache or idempotency key |

Four rules follow from the table.

- **Do not rely on the dictionary you receive.**
  DMS builds `RouteQualifiers` case-insensitively, but a provider must apply the name, value and set rules itself, because it cannot rely on the comparer of an arbitrary dictionary instance.
- **Equivalent contexts select one namespace.**
  `North` and `north` reach one namespace and one set of jobs.
  A provider may deliberately map other, non-equivalent contexts to a shared namespace, but a job's ownership check still compares the complete tenant, qualifier and client context under these rules, so sharing a namespace does not relax it.
- **Persist keys that cannot be confused.**
  Build a stored key from the context in an encoding with no ambiguous delimiter, so that no tenant or qualifier value containing a separator can collide with another context, and do not case-fold with culture-dependent casing.
  The sample provider serializes the upper-cased tenant and the sorted, upper-cased qualifier pairs as a JSON array, which has neither problem.
- **Do not normalize UniqueIds or tokens.**
  These rules do not case-fold or rewrite either one, and DMS passes both through unchanged, so each keeps the semantics this guide gives it.

DMS preserves tenant and qualifier spelling exactly as passed, with no trimming, culture-dependent casing or Unicode normalization of its own.

## Lifetimes and concurrency

**Singleton, scoped and transient registrations are all supported.**
Choose the one that matches the state your class holds, and register it with `Add`, unkeyed, as [Implementing and registering a provider](#implementing-and-registering-a-provider) describes.

- **DMS resolves the provider once per request, from its own per-request scope.**
  The same resolved instance serves both the `Capabilities` read and the operation call for that request, so your provider never observes a capability set different from the one its call was gated on.
- **DMS never disposes the instance itself.**
  A scoped or transient registration is disposed with the request scope by the container, and a singleton lives for the process.
  Do not depend on DMS to call `Dispose` at any particular moment, and do not hold a resource in a transient or scoped class that only a singleton owner should release.
- **A provider must be safe for concurrent calls across requests.**
  A singleton and a transient registration give DMS no per-request isolation, and even a scoped provider, which is called at most once per request, may share captured dependencies, such as a client, a cache or a store, with other requests running at the same time.
  The sample provider is scoped and holds no state, and it shares one thread-safe singleton store between requests.
- **A constructor or factory must also succeed outside a request.**
  At startup the host constructs every declared plugin contract once, in a discarded scope with no request, to verify that the registration can be activated.
  A constructor that reads the request, or one that needs a dependency only a request provides, fails at startup rather than serving.

Because `Capabilities` is read on every request, before any operation, it must be cheap and perform no I/O, as [Capabilities](#capabilities) states.

## Retries and lost creates

**DMS applies no timeout to a provider call and never retries one.**
That holds for all five operations, and the identity pipeline wraps no resilience pipeline.
A timeout your provider does not handle itself surfaces as a thrown exception, which answers the sanitized `502` upstream-failure problem.
A statement in this guide that a client may retry something means the client reissuing its own HTTP request, and never DMS calling your provider a second time on its own account.
The rule costs the most when a create response is lost.

### A lost create response

**A lost create response can duplicate the identity when the client retries.**
Suppose `CreateAsync` issues an id upstream and the response is then lost, because a connection aborts or the upstream system answers `502` after committing.
The client sees an error, the id exists, and the client does not hold it.
A client that retries the create with the same data may be issued a second id for the same person.

- **`TraceId` is not an idempotency key.**
  It is a per-request correlation value, new for every request, so a retried create arrives with a different one.
  Do not key deduplication on it.
- **The contract adds no portable idempotency key in v1.**
  A client cannot ask DMS to make a create safe to retry, and no guarantee in this contract says that a second create is harmless.
- **Each implementer documents what a repeated create does.**
  Either the create is idempotent on an upstream key the provider documents, or a repeat issues a duplicate and the provider documents the reconciliation path.
- **v1 accepts provider-specific, possibly manual, recovery.**
  Advertising `Create` does not require advertising `Search`, an authoritative recovery lookup or deduplication.
  A create-only provider documents an operator reconciliation process instead.

### Recovering a lost create

A worked example, with a provider whose create accepts a custom property carrying the caller's own exact upstream key, and whose lookup by that key is authoritative.
The key is a custom property of that provider, which DMS passes through without inspecting it, and it is not an idempotency contract of the host.

1. The client sends the create with its exact upstream key and receives an error, such as the sanitized `502`.
   The outcome is unknown: the id may or may not have been issued.
2. The client does not create again.
   It looks the key up through the provider's documented exact-key lookup, in the same namespace, meaning the same tenant, route qualifiers and client authorization.
3. If the lookup returns the identity, the client recovers the original UniqueId, and no second create happens.
4. If the lookup is unavailable, or cannot establish whether the identity was issued, the client stops.
   It resolves the outcome through the provider's documented operator or upstream reconciliation process, and does not create again.

Two results never make a retry safe.
A scored demographic match from a search says that a similar person exists, and does not say that this request's create was issued.
A lookup that finds nothing is not proof that no id was issued, unless the provider documents an authoritative guarantee of absence.
This guide makes no portable idempotency claim, and a provider that has no reliable lookup has only the operator path in step 4.

Document your provider's version of these steps, because a client of your provider will follow them.

## Authorization configuration

**The identity service claim must carry exactly the `NoFurtherAuthorizationRequired` strategy.**
Every identity operation is authorized against the claim `http://ed-fi.org/identity/claims/services/identity` in the caller's claim set.
Create requires the `Create` action on it, and get, find, search and results require `Read`.
For the action an operation needs, the claim's authorization strategies must be exactly one strategy, `NoFurtherAuthorizationRequired`.

Any other configuration fails closed.
The host answers `500` with the problem type `urn:ed-fi:api:system:configuration:security`, before your provider is called, whatever the strategy is: another strategy, several strategies, or none.
This is an invalid security configuration, and it is reported as one rather than treated as a denial, so the `403` a client receives for a claim set that does not grant the action at all stays distinct from it.

Tenant and namespace access are the provider's to enforce, as [Namespaces, grants and context equality](#namespaces-grants-and-context-equality) describes, and the claim strategy does not stand in for them.

How a deployment manages the claim set, the claim and tenant caches, how long a revoked claim or deleted client can remain effective, the claim reload routes, and the `429` the global rate limiter can return, are operator concerns.
[IDENTITY-MANAGEMENT.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/IDENTITY-MANAGEMENT.md) covers them, and
[CACHING-STRATEGY.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CACHING-STRATEGY.md) covers the caches themselves.
A provider's own policy cache is separate from all of those, and revoking a namespace grant takes effect no sooner than that cache refreshes, which is why its lifetime is something to document.

## Versioning and compatibility

**The contract version is independent of the Data Management Service release version.**
It lives in the package's own project file, `src/dms/core/EdFi.DataManagementService.Identity/EdFi.DataManagementService.Identity.csproj`, and it moves only when the contract moves.
A host release carries whichever contract version it was built against, so a provider built against one contract version runs on every host release that carries that version.
The host assembly manifest attached to a release states which contract version that host carries, and the same version is stamped into the served OpenAPI document as `x-edfi-identity-contract-version`.

### What moves the version

The contract is two things, and the version covers both.

- **The package you compile against.**
  That is the public surface of the assembly, the XML documentation that ships beside it, and its declared dependencies.
  The rules in this guide live in that documentation, so a rule rewritten there is a changed contract.
- **The HTTP contract the host serves.**
  That is the OpenAPI document served at `/metadata/identity/v2/swagger.json`: its paths, schemas, status codes, media types, headers and security.
  A provider implements the interface, but its clients depend on this document.

Publishing is automated and follows one rule for the package: **publish when absent, skip when unchanged, fail when changed.**
The first release publishes the package.
A later release finds the version already on the feed and compares the surface, the XML documentation and the dependencies of the build against it: unchanged is skipped, and a changed contract at an unchanged version is refused, naming which of the three changed.
A version on the feed is never overwritten, so the bytes you restore for a version are the bytes everyone restores for it.
This readme is not compared, so prose can be corrected without a new version.

### The wire contract

The served HTTP document is gated separately, because it can change without any public C# type changing.
The gate compares the document a host serves with a stored baseline, normalized so that ordering and the runtime server and token URLs do not count, and it covers paths, referenced schemas, status codes, media types, headers and security.

**The baseline is the one the last published version recorded, not the one in the change under review.**
The gate reads the commit recorded in the published package, and takes the baseline file as it was at that commit.
Comparing against the file in the same change would let any edit approve itself.
Once a contract has been published, a missing or unreadable published baseline fails verification and is never replaced silently.

- **An unchanged version requires an unchanged wire baseline.**
  A change to the served document at the same version fails, even when the package comparison would skip publication.
- **An incremented version requires a reviewed compatibility record.**
  Every difference from the published baseline that is not a hard failure needs an exact entry in a record in
  [the compatibility records directory](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/eng/verification/IdentityWireCompatibility/README.md),
  naming the pointer, the kind of change and the reviewer's reason it is compatible.
  The package is compared at an increment too: each change to its public surface, its XML documentation or its declared dependencies needs an entry, so a rule reworded in the documentation is reviewed like any other change.
  An entry that matches no difference is stale and fails.
  Example validation is never an approval path.
- **An increment can never carry a breaking category.**
  These cannot be waived by any entry: a change to the request body, parameters or media type of an existing operation, a change to the response schema, status code, media type, header or problem type of an existing operation, a change to security, a removed path or operation, and a `$ref` that does not point into `#/components`.
  For the package, they are a removed or changed public member and, on a type that was already published, a new interface member without a default implementation, a new abstract or required member, or a member that closes the type to outside derivers.

### Additive changes only

**Existing providers must keep working on newer hosts, so a change to this contract is additive.**
A member added to `IIdentityService` after publication must carry a default interface implementation.
Adding a member without one is a breaking change to an interface that plugins implement, and it can never ship as a new version of this package.
A new operation, a new type, a new optional member of a contract type, an interface member with a default implementation and similar additions go through the compatibility record above, and each is reviewed for whether it narrows what clients may send, widens what they may receive, adds a response alternative, or expects more of an existing provider than it already does.
The members of a new type are reviewed rather than refused, because no existing provider implements, derives from or constructs a type that did not exist.
A new standard property on the request or response of an existing operation changes that operation's schema, which is one of the categories above, so no version can carry it.
A custom property needs no change at all, because DMS already passes custom properties through.
A provider that conformed to the guide when it was written keeps its loadability, its callability, and the payloads and context behavior it relied on.

A newer package does not make a provider compatible with an older host.
A provider compiled against a contract version newer than the host carries is refused at load, by name, as the plugin guide describes, and it never fails later inside a call.
Check the host assembly manifest of the release you target, as [Package and version](#package-and-version) says, before choosing a version to build against.

### Breaking changes

**A breaking change is not a new version of this package.**
It needs a separately designed contract and API version, with its own design and review.
Bumping the version number does not make a breaking change pass: the wire gate refuses the breaking categories above whatever version is declared, and a change to the package surface that removes or requires something is refused the same way.

### Pinning and getting the package

`EdFi.Api.Identity` is published to the Ed-Fi Azure Artifacts feed, with the other contract packages:

```text
https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json
```

Pin the version exactly, in brackets, as [Package and version](#package-and-version) shows:

```xml
<PackageReference Include="EdFi.Api.Identity" Version="[1.0.0]" />
```

A bare version is a minimum and not a pin.
The package is frozen on the feed at each version it publishes, so `[1.0.0]` restores the same bytes tomorrow that it restores today.
[PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#versioning) describes the same policy for the plugin base class, and
[its package section](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#getting-the-package)
describes how the host assembly manifest states which contract versions a release carries.

## License

Licensed under the Apache License, Version 2.0.
See the LICENSE and NOTICES files in the project root for more information.
