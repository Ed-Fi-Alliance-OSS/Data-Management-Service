// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Options;

namespace Acme.IdentityFixture;

/// <summary>
/// The conforming reference provider the <see cref="IIdentityService"/> documentation describes,
/// backed by an in-memory store. Every operation selects the namespace from the context and checks
/// the client's grant before any identity work. It logs nothing, because identifiers and person data
/// are all it handles.
/// </summary>
public class FixtureIdentityService(
    IOptions<IdentityFixtureOptions> options,
    FixtureRequestScope requestScope,
    FixtureState state,
    FixturePolicySource policy,
    FixtureControlEvents events
) : IIdentityService
{
    /// <summary>A find array element that selects an async job. No issued UniqueId can equal it.</summary>
    public const string AsyncFindTrigger = "~fixture:async";

    /// <summary>A search object property that selects an async job when it is the JSON value true.</summary>
    public const string AsyncSearchProperty = "~FixtureAsync";

    /// <summary>A find element prefix; the rest is the variant the whole find returns.</summary>
    public const string FindReturnPrefix = "~fixture:return:";

    /// <summary>A find element prefix; the rest is the variant every poll of the accepted job answers.</summary>
    public const string FindResultsPrefix = "~fixture:results:";

    /// <summary>A find element prefix; the rest is the exact request token the accepted job is issued under.</summary>
    public const string FindTokenPrefix = "~fixture:token:";

    /// <summary>A create or search property naming the variant the whole call returns.</summary>
    public const string ReturnProperty = "~FixtureReturn";

    /// <summary>A search property naming the variant every poll of the accepted job answers.</summary>
    public const string ResultsProperty = "~FixtureResults";

    /// <summary>A search property holding the exact request token the accepted job is issued under.</summary>
    public const string TokenProperty = "~FixtureToken";

    /// <summary>A get-by-id prefix; the rest is the variant the call returns.</summary>
    public const string GetByIdReturnPrefix = "~fixture-return-";

    /// <summary>The custom property a create records, and the one property of a reconciliation lookup.</summary>
    public const string UpstreamKeyProperty = "upstreamKey";

    /// <summary>The prefix of every reserved name; no real identifying attribute starts with it.</summary>
    public const string ReservedPrefix = "~";

    private const double MinimumMatchScore = 50;

    private static readonly string[] StandardAttributes =
    [
        "LastSurname",
        "FirstName",
        "MiddleName",
        "GenerationCodeSuffix",
        "SexType",
        "BirthDate",
        "BirthOrder",
    ];

    private static readonly string[] LocationAttributes =
    [
        "City",
        "StateAbbreviation",
        "InternationalProvince",
        "Country",
    ];

    private readonly Guid _providerId = Guid.NewGuid();
    private int _capabilitiesReads;

    // Read from options, so it is inexpensive and does no I/O. The count is only a probe for the
    // once-per-request obligation.
    public IdentityCapabilities Capabilities
    {
        get
        {
            Interlocked.Increment(ref _capabilitiesReads);

            return options.Value.ThrowAt == FixtureThrowAt.Capabilities
                ? throw FixtureFailures.Nested("The capabilities getter failed.")
                : options.Value.Capabilities;
        }
    }

    public async Task<IdentityResult> CreateAsync(
        JsonObject request,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        if (await BeginAsync("create", context, token: null, cancellationToken) is not { } namespaceName)
        {
            return NotFound();
        }

        string? variant = (request[ReturnProperty] as JsonValue)?.GetValue<string>();
        if (variant is not null && FixtureVariants.Is(variant, FixtureVariants.CancelPersonText))
        {
            return await AwaitCancellationAsync("create", cancellationToken);
        }

        if (variant is not null && !FixtureVariants.Is(variant, FixtureVariants.LostCreate))
        {
            return FixtureVariants.Result(variant);
        }

        string uniqueId = state.AddPerson(namespaceName, request);
        await events.ReportAsync("create", FixtureControlEvents.Issuance, cancellationToken);

        // The upstream acted on the create and its answer never arrived, so the caller cannot know the
        // id. The issuance above stays, which is what a reconciliation lookup later finds.
        return variant is not null
            ? throw FixtureFailures.LostResponse()
            : new IdentityResult
            {
                Status = IdentityResultStatus.Success,
                Payload = JsonValue.Create(uniqueId),
            };
    }

    public async Task<IdentityResult> GetByIdAsync(
        string uniqueId,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        if (await BeginAsync("getById", context, token: null, cancellationToken) is not { } namespaceName)
        {
            return NotFound();
        }

        if (uniqueId.StartsWith(GetByIdReturnPrefix, StringComparison.Ordinal))
        {
            return FixtureVariants.Result(uniqueId[GetByIdReturnPrefix.Length..]);
        }

        if (state.FindPerson(namespaceName, uniqueId) is not { } person)
        {
            return NotFound();
        }

        JsonObject response = ToResponse(uniqueId, person, score: null);

        // The lifetime probe: which provider and scoped dependency instances served this request, and
        // how often this provider's capabilities were read.
        response["FixtureProviderId"] = _providerId.ToString();
        response["FixtureScopeId"] = requestScope.Id.ToString();
        response["FixtureCapabilitiesReadCount"] = Volatile.Read(ref _capabilitiesReads);

        return new IdentityResult { Status = IdentityResultStatus.Success, Payload = response };
    }

    public async Task<IdentityAsyncResult> FindAsync(
        IReadOnlyList<string> uniqueIds,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        if (await BeginAsync("find", context, token: null, cancellationToken) is not { } namespaceName)
        {
            return new IdentityAsyncResult { Status = IdentityResultStatus.NotFound };
        }

        string? returnVariant = null;
        string? resultsVariant = null;
        string? requestedToken = null;
        bool isAsync = false;
        JsonArray groups = [];

        foreach (string uniqueId in uniqueIds)
        {
            JsonArray responses = [];

            if (uniqueId == AsyncFindTrigger)
            {
                isAsync = true;
            }
            else if (uniqueId.StartsWith(FindReturnPrefix, StringComparison.Ordinal))
            {
                returnVariant ??= uniqueId[FindReturnPrefix.Length..];
            }
            else if (uniqueId.StartsWith(FindResultsPrefix, StringComparison.Ordinal))
            {
                isAsync = true;
                resultsVariant ??= uniqueId[FindResultsPrefix.Length..];
            }
            else if (uniqueId.StartsWith(FindTokenPrefix, StringComparison.Ordinal))
            {
                isAsync = true;
                requestedToken ??= uniqueId[FindTokenPrefix.Length..];
            }
            else if (state.FindPerson(namespaceName, uniqueId) is { } person)
            {
                responses.Add(ToResponse(uniqueId, person, score: null));
            }

            groups.Add(new JsonObject { ["Responses"] = responses });
        }

        return returnVariant is not null
            ? FixtureVariants.AsyncResult(returnVariant)
            : await CompleteAsync(
                "find",
                namespaceName,
                context,
                groups,
                isAsync,
                requestedToken,
                resultsVariant,
                cancellationToken
            );
    }

    public async Task<IdentityAsyncResult> SearchAsync(
        IReadOnlyList<JsonObject> requests,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        if (await BeginAsync("search", context, token: null, cancellationToken) is not { } namespaceName)
        {
            return new IdentityAsyncResult { Status = IdentityResultStatus.NotFound };
        }

        JsonArray groups = [];
        string? returnVariant = null;
        string? resultsVariant = null;
        string? requestedToken = null;
        bool isAsync = false;
        IReadOnlyList<KeyValuePair<string, JsonObject>> persons = state.Persons(namespaceName);

        foreach (JsonObject criteria in requests)
        {
            isAsync |= criteria[AsyncSearchProperty] is JsonValue flag && flag.TryGetValue(out bool on) && on;
            returnVariant ??= ReadText(criteria, ReturnProperty);
            resultsVariant ??= ReadText(criteria, ResultsProperty);
            requestedToken ??= ReadText(criteria, TokenProperty);
            groups.Add(new JsonObject { ["Responses"] = Search(criteria, persons) });
        }

        isAsync |= resultsVariant is not null || requestedToken is not null;

        return returnVariant is not null
            ? FixtureVariants.AsyncResult(returnVariant)
            : await CompleteAsync(
                "search",
                namespaceName,
                context,
                groups,
                isAsync,
                requestedToken,
                resultsVariant,
                cancellationToken
            );
    }

    public async Task<IdentityResult> ResultsAsync(
        string requestToken,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        if (await BeginAsync("results", context, requestToken, cancellationToken) is not { } namespaceName)
        {
            return NotFound();
        }

        // A grant never waives ownership: the job must have been issued under this exact context and
        // client, whatever the caller is granted.
        if (
            state.FindJob(requestToken) is not { } job
            || job.Namespace != namespaceName
            || job.Owner != FixtureJobOwner.From(context)
        )
        {
            return NotFound();
        }

        if (await policy.IsJobExpiredAsync(requestToken, cancellationToken))
        {
            state.RemoveJob(requestToken);
            return NotFound();
        }

        // A transient failure to learn the job's state is a thrown failure; a terminal failure is a
        // definitive status, answered on every authorized poll while the job is retained.
        (bool failed, bool failNextPoll) = await policy.GetJobStateAsync(requestToken, cancellationToken);
        if (failNextPoll)
        {
            throw FixtureFailures.TransientPoll();
        }

        if (failed)
        {
            return new IdentityResult { Status = IdentityResultStatus.JobFailed };
        }

        if (job.ResultsVariant is not null)
        {
            return FixtureVariants.Result(job.ResultsVariant);
        }

        if (job.RecordPollAndIsIncomplete(options.Value.PollsUntilComplete))
        {
            return new IdentityResult
            {
                Status = IdentityResultStatus.Incomplete,
                Payload = new JsonObject { ["Status"] = "Incomplete", ["SearchResponses"] = new JsonArray() },
            };
        }

        return new IdentityResult
        {
            Status = IdentityResultStatus.Success,
            Payload = job.CompletePayload.DeepClone(),
        };
    }

    private static IdentityResult NotFound() => new() { Status = IdentityResultStatus.NotFound };

    // Reports that the operation is waiting, waits until its token is cancelled and then throws a
    // cancellation of that token whose message and inner exception carry person-shaped text. It never
    // returns. The report is sent without the token: a test cancels as soon as the report arrives, and
    // a cancelled report would replace this exception with the control client's own. The wait
    // suppresses the delay's own cancellation exception rather than catching it, so the only
    // exception that leaves is the person-shaped one.
    private async Task<IdentityResult> AwaitCancellationAsync(
        string operation,
        CancellationToken cancellationToken
    )
    {
        await events.ReportAsync(
            operation,
            FixtureControlEvents.AwaitingCancellation,
            CancellationToken.None
        );
        await Task.Delay(Timeout.Infinite, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        throw FixtureFailures.PersonTextCancellation(cancellationToken);
    }

    // Reports the invocation, then selects the namespace the context selects when the client holds a
    // grant on it; null for an unknown namespace or a missing grant, with no lookup, issuance or job
    // creation having happened. A policy-source failure propagates, so it is never read as a grant.
    // ThrowAt=Operation fires only for an authorized call, after the grant check.
    private async Task<string?> BeginAsync(
        string operation,
        IdentityRequestContext context,
        string? token,
        CancellationToken cancellationToken
    )
    {
        await events.ReportAsync(operation, FixtureControlEvents.Invocation, cancellationToken, token);

        if (await AuthorizeAsync(context, cancellationToken) is not { } namespaceName)
        {
            return null;
        }

        await events.ReportAsync(operation, FixtureControlEvents.Lookup, cancellationToken, token);

        return options.Value.ThrowAt == FixtureThrowAt.Operation
            ? throw FixtureFailures.Nested("The operation failed.")
            : namespaceName;
    }

    private async Task<string?> AuthorizeAsync(
        IdentityRequestContext context,
        CancellationToken cancellationToken
    )
    {
        if (state.SelectNamespace(FixtureContextKey.From(context)) is not { } namespaceName)
        {
            return null;
        }

        return await policy.IsGrantedAsync(context.ClientId, context.Tenant, namespaceName, cancellationToken)
            ? namespaceName
            : null;
    }

    private async Task<IdentityAsyncResult> CompleteAsync(
        string operation,
        string namespaceName,
        IdentityRequestContext context,
        JsonArray groups,
        bool isAsync,
        string? requestedToken,
        string? resultsVariant,
        CancellationToken cancellationToken
    )
    {
        JsonObject payload = new() { ["Status"] = "Complete", ["SearchResponses"] = groups };

        if (!isAsync)
        {
            return new IdentityAsyncResult { Status = IdentityResultStatus.Success, Payload = payload };
        }

        // The caller-chosen token reaches the result byte for byte; otherwise the fixture mints one.
        string token = state.AddJob(
            namespaceName,
            FixtureJobOwner.From(context),
            payload,
            requestedToken,
            resultsVariant
        );
        await events.ReportAsync(operation, FixtureControlEvents.Job, cancellationToken);

        return new IdentityAsyncResult { Status = IdentityResultStatus.Success, RequestToken = token };
    }

    private static string? ReadText(JsonObject criteria, string property) =>
        criteria[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // The exact reconciliation lookup when the object carries only upstreamKey; any other object is a
    // scored match.
    private JsonArray Search(JsonObject criteria, IReadOnlyList<KeyValuePair<string, JsonObject>> persons)
    {
        string? upstreamKey = ReadUpstreamKeyLookup(criteria);
        if (upstreamKey is null)
        {
            return Match(criteria, persons);
        }

        JsonArray responses = [];
        if (!options.Value.ReconciliationLookup)
        {
            // No reliable reconciliation: the empty answer cannot establish that nothing was issued.
            return responses;
        }

        KeyValuePair<string, JsonObject>? original = persons
            .Where(pair =>
                pair.Value[UpstreamKeyProperty] is JsonValue stored
                && stored.TryGetValue(out string? key)
                && string.Equals(key, upstreamKey, StringComparison.Ordinal)
            )
            .OrderBy(pair =>
                pair.Value[FixtureState.IssuanceOrderProperty]?.GetValue<long>() ?? long.MaxValue
            )
            .Select(pair => (KeyValuePair<string, JsonObject>?)pair)
            .FirstOrDefault();

        if (original is { } issuance)
        {
            responses.Add(ToResponse(issuance.Key, issuance.Value, score: 100));
        }

        return responses;
    }

    private static string? ReadUpstreamKeyLookup(JsonObject criteria)
    {
        List<KeyValuePair<string, JsonNode?>> supplied =
        [
            .. criteria.Where(pair =>
                pair.Value is not null && !pair.Key.StartsWith(ReservedPrefix, StringComparison.Ordinal)
            ),
        ];

        return
            supplied is [{ Key: UpstreamKeyProperty, Value: JsonValue value }]
            && value.TryGetValue(out string? key)
            ? key
            : null;
    }

    // A match is a stored person agreeing with at least half of the supplied attributes. The score is
    // the agreeing share as a percentage, so an exact match scores 100.
    private static JsonArray Match(
        JsonObject criteria,
        IReadOnlyList<KeyValuePair<string, JsonObject>> persons
    )
    {
        List<KeyValuePair<string, JsonNode?>> supplied =
        [
            .. criteria.Where(pair =>
                pair.Value is not null && !pair.Key.StartsWith(ReservedPrefix, StringComparison.Ordinal)
            ),
        ];

        List<(string UniqueId, JsonObject Person, double Score)> matches = [];
        if (supplied.Count > 0)
        {
            foreach ((string uniqueId, JsonObject person) in persons)
            {
                int agreeing = supplied.Count(pair =>
                    person.TryGetPropertyValue(pair.Key, out JsonNode? stored)
                    && stored is not null
                    && Agrees(pair.Value!, stored)
                );
                double score = Math.Round(100.0 * agreeing / supplied.Count, 2);

                if (score >= MinimumMatchScore)
                {
                    matches.Add((uniqueId, person, score));
                }
            }
        }

        JsonArray responses = [];
        foreach (
            (string uniqueId, JsonObject person, double score) in matches
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.UniqueId, StringComparer.Ordinal)
        )
        {
            responses.Add(ToResponse(uniqueId, person, score));
        }

        return responses;
    }

    private static bool Agrees(JsonNode supplied, JsonNode stored) =>
        (supplied, stored) switch
        {
            (JsonObject suppliedObject, JsonObject storedObject) => suppliedObject
                .Where(pair => pair.Value is not null)
                .All(pair =>
                    storedObject.TryGetPropertyValue(pair.Key, out JsonNode? child)
                    && child is not null
                    && Agrees(pair.Value!, child)
                ),
            (JsonValue suppliedValue, JsonValue storedValue)
                when suppliedValue.TryGetValue(out string? left)
                    && storedValue.TryGetValue(out string? right) => string.Equals(
                left,
                right,
                StringComparison.OrdinalIgnoreCase
            ),
            _ => JsonNode.DeepEquals(supplied, stored),
        };

    // Every standard attribute is present, null where the person has none; BirthLocation is always an
    // object with its four children; custom properties pass through; reserved names never do.
    private static JsonObject ToResponse(string uniqueId, JsonObject person, double? score)
    {
        JsonObject response = new() { ["UniqueId"] = uniqueId };

        foreach (string attribute in StandardAttributes)
        {
            response[attribute] = person[attribute]?.DeepClone();
        }

        JsonObject location = [];
        JsonObject? stored = person["BirthLocation"] as JsonObject;
        foreach (string attribute in LocationAttributes)
        {
            location[attribute] = stored?[attribute]?.DeepClone();
        }

        foreach ((string name, JsonNode? value) in stored ?? [])
        {
            if (!location.ContainsKey(name))
            {
                location[name] = value?.DeepClone();
            }
        }

        response["BirthLocation"] = location;
        response["Score"] = score is { } scoreValue ? JsonValue.Create(scoreValue) : null;

        foreach ((string name, JsonNode? value) in person)
        {
            if (
                !response.ContainsKey(name)
                && !name.StartsWith(ReservedPrefix, StringComparison.Ordinal)
                && name is not ("UniqueId" or "Score")
            )
            {
                response[name] = value?.DeepClone();
            }
        }

        return response;
    }
}
