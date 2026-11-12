// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// What a third party writes: an IIdentityService implementation, compiled against the packed
// EdFi.Api.Identity nupkg rather than against anything in this repository. Dropping any interface
// member from the provider below fails this project with CS0535.
//
// The regions below are mirrored verbatim into the published guide, and a check compares the two, so
// the sample an implementer copies is one that has been compiled. Each carries its own usings and
// namespace, and every type it names, so a reader can paste it into a new project unchanged.

// embed-region: provider
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

    // embed-region-end: provider

    // embed-region: provider-create
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

    // embed-region-end: provider-create

    // embed-region: provider-get
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

    // embed-region-end: provider-get

    // embed-region: provider-find-search
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

    // embed-region-end: provider-find-search

    // embed-region: provider-results
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

// embed-region-end: provider-results

// embed-region: provider-store
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
// embed-region-end: provider-store
