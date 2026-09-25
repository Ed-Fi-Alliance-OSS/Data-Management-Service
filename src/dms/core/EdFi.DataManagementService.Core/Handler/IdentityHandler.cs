// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// The terminal step of both identity pipelines (design.md "Pipeline" and "Async Token Rule", D9).
/// Runs only once <see cref="Middleware.IdentityOperationCapabilityMiddleware" /> has gated the
/// requested operation and populated <see cref="RequestInfo.IdentityProvider" />: rejects a
/// present-but-blank route value, validates the parsed body's top-level shape for the three JSON-body
/// operations, builds <see cref="IdentityRequestContext" />, invokes the operation through
/// <see cref="IdentityProviderBoundary" />, and maps the result per design.md:825-871.
/// </summary>
/// <param name="_boundary">
/// The sanitized provider-execution boundary. Shared with
/// <see cref="Middleware.IdentityOperationCapabilityMiddleware" /> only in the sense that both use the
/// same boundary shape; this handler only ever calls <see cref="IdentityProviderBoundary.InvokeAsync{T}" />.
/// </param>
/// <param name="_maxRequestLineSize">
/// The fallback maximum HTTP request-line size used when the current request's
/// <see cref="FrontendRequest.MaxRequestLineSize" /> is null - a request-line budget the frontend did
/// not report. <see cref="IdentityRequestTokenRule.Evaluate" /> prefers the per-request value read from
/// the running server (for example Kestrel's <c>KestrelServerLimits.MaxRequestLineSize</c>) whenever the
/// frontend supplies one, and falls back to this constructor value - the documented .NET default of
/// 8,192 bytes - only when it does not.
/// </param>
internal sealed class IdentityHandler(
    IdentityProviderBoundary _boundary,
    int _maxRequestLineSize,
    ILogger<IdentityHandler> _logger
) : IPipelineStep
{
    /// <summary>
    /// The fallback maximum HTTP request-line size passed to every <see cref="IdentityHandler"/>
    /// instance, used only when a request's <see cref="FrontendRequest.MaxRequestLineSize"/> is null.
    /// This is the documented default for Kestrel's <c>KestrelServerLimits.MaxRequestLineSize</c>,
    /// re-verified at runtime against a live host rather than assumed.
    /// </summary>
    internal const int DefaultMaxRequestLineSize = 8192;

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug("Entering IdentityHandler - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        TraceId traceId = requestInfo.FrontendRequest.TraceId;

        if (requestInfo.IdentityRouteValue is not null && requestInfo.IdentityRouteValue.Trim().Length == 0)
        {
            requestInfo.FrontendResponse = BadRequest(
                "The route value for this operation was present but blank.",
                traceId
            );
            return;
        }

        if (!TryValidateBodyShape(requestInfo, traceId, out FrontendResponse? shapeRejection))
        {
            requestInfo.FrontendResponse = shapeRejection!;
            return;
        }

        // BuildContext throws InvalidOperationException on a route-qualifier name collision under
        // case-insensitive comparison - a host configuration defect. The frontend already guards
        // against this at module-mapping time, so reaching it here is a genuine host bug rather than
        // a client or provider fault. It is deliberately left uncaught here, not routed through the
        // provider boundary, so CoreExceptionLoggingMiddleware's general handler logs it as an
        // unexpected condition.
        IdentityRequestContext context = BuildContext(requestInfo);

        // Populated by IdentityOperationCapabilityMiddleware, which returns operation-unsupported
        // 404 before this handler runs if activation failed, so this is never null here.
        IIdentityService provider = requestInfo.IdentityProvider!;

        FrontendResponse? mapped = requestInfo.IdentityOperation switch
        {
            IdentityOperation.Create => await ExecuteCreate(provider, context, requestInfo),
            IdentityOperation.GetById => await ExecuteGetById(provider, context, requestInfo),
            IdentityOperation.Find => await ExecuteFind(provider, context, requestInfo),
            IdentityOperation.Search => await ExecuteSearch(provider, context, requestInfo),
            IdentityOperation.Results => await ExecuteResults(provider, context, requestInfo),
            _ => throw new InvalidOperationException(
                $"Unsupported identity operation '{requestInfo.IdentityOperation}'."
            ),
        };

        if (mapped is not null)
        {
            requestInfo.FrontendResponse = mapped;
        }
        // A null mapped response means the provider boundary already set a sanitized
        // upstream-failure 502 on requestInfo.FrontendResponse; a provider returning null without the
        // boundary failing is mapped to a provider-contract-violation 502 in each Execute* method
        // above, so it never reaches this branch as a null mapped response.
    }

    private async Task<FrontendResponse?> ExecuteCreate(
        IIdentityService provider,
        IdentityRequestContext context,
        RequestInfo requestInfo
    )
    {
        JsonObject body = (JsonObject)requestInfo.ParsedBody;
        IdentityInvocation<IdentityResult> invocation = await _boundary.InvokeAsync(
            () => provider.CreateAsync(body, context, requestInfo.RequestCancellationToken),
            requestInfo
        );
        return MapInvocation(
            requestInfo,
            invocation,
            result => MapResult(requestInfo, result.Status, result.Payload, requestToken: null, result.Errors)
        );
    }

    private async Task<FrontendResponse?> ExecuteGetById(
        IIdentityService provider,
        IdentityRequestContext context,
        RequestInfo requestInfo
    )
    {
        IdentityInvocation<IdentityResult> invocation = await _boundary.InvokeAsync(
            () =>
                provider.GetByIdAsync(
                    requestInfo.IdentityRouteValue!,
                    context,
                    requestInfo.RequestCancellationToken
                ),
            requestInfo
        );
        return MapInvocation(
            requestInfo,
            invocation,
            result => MapResult(requestInfo, result.Status, result.Payload, requestToken: null, result.Errors)
        );
    }

    private async Task<FrontendResponse?> ExecuteFind(
        IIdentityService provider,
        IdentityRequestContext context,
        RequestInfo requestInfo
    )
    {
        JsonArray body = (JsonArray)requestInfo.ParsedBody;
        IReadOnlyList<string> uniqueIds = [.. body.Select(item => ((JsonValue)item!).GetValue<string>())];

        IdentityInvocation<IdentityAsyncResult> invocation = await _boundary.InvokeAsync(
            () => provider.FindAsync(uniqueIds, context, requestInfo.RequestCancellationToken),
            requestInfo
        );
        return MapInvocation(
            requestInfo,
            invocation,
            result =>
                MapResult(requestInfo, result.Status, result.Payload, result.RequestToken, result.Errors)
        );
    }

    private async Task<FrontendResponse?> ExecuteSearch(
        IIdentityService provider,
        IdentityRequestContext context,
        RequestInfo requestInfo
    )
    {
        JsonArray body = (JsonArray)requestInfo.ParsedBody;
        IReadOnlyList<JsonObject> requests = [.. body.Select(item => (JsonObject)item!)];

        IdentityInvocation<IdentityAsyncResult> invocation = await _boundary.InvokeAsync(
            () => provider.SearchAsync(requests, context, requestInfo.RequestCancellationToken),
            requestInfo
        );
        return MapInvocation(
            requestInfo,
            invocation,
            result =>
                MapResult(requestInfo, result.Status, result.Payload, result.RequestToken, result.Errors)
        );
    }

    private async Task<FrontendResponse?> ExecuteResults(
        IIdentityService provider,
        IdentityRequestContext context,
        RequestInfo requestInfo
    )
    {
        IdentityInvocation<IdentityResult> invocation = await _boundary.InvokeAsync(
            () =>
                provider.ResultsAsync(
                    requestInfo.IdentityRouteValue!,
                    context,
                    requestInfo.RequestCancellationToken
                ),
            requestInfo
        );
        return MapInvocation(
            requestInfo,
            invocation,
            result => MapResult(requestInfo, result.Status, result.Payload, requestToken: null, result.Errors)
        );
    }

    /// <summary>
    /// Distinguishes the boundary's two null-adjacent outcomes for every one of the five identity
    /// operations: when <see cref="IdentityInvocation{T}.BoundaryFailed" /> is true, the boundary has
    /// already set a sanitized upstream-failure 502 and this returns null so the caller sets no
    /// response of its own; otherwise a null <see cref="IdentityInvocation{T}.Result" /> means the
    /// provider itself returned null - breaking the non-nullable <see cref="IIdentityService" />
    /// contract - and is mapped to the existing provider-contract-violation 502, never to the default
    /// bodyless 503.
    /// </summary>
    private static FrontendResponse? MapInvocation<T>(
        RequestInfo requestInfo,
        IdentityInvocation<T> invocation,
        Func<T, FrontendResponse> mapResult
    )
        where T : class
    {
        if (invocation.BoundaryFailed)
        {
            return null;
        }

        if (invocation.Result is null)
        {
            return ContractViolation(
                requestInfo.FrontendRequest.TraceId,
                "The identity provider returned no result."
            );
        }

        return mapResult(invocation.Result);
    }

    /// <summary>
    /// Maps a provider outcome to an HTTP response per design.md:825-871. <paramref name="requestToken" />
    /// is always null for create, get-by-id, and results, which never carry one on the wire.
    /// </summary>
    private FrontendResponse MapResult(
        RequestInfo requestInfo,
        IdentityResultStatus status,
        JsonNode? payload,
        string? requestToken,
        IReadOnlyList<IdentityError> errors
    )
    {
        TraceId traceId = requestInfo.FrontendRequest.TraceId;
        bool isResults = requestInfo.IdentityOperation == IdentityOperation.Results;

        switch (status)
        {
            case IdentityResultStatus.NotFound:
                return new FrontendResponse(
                    StatusCode: 404,
                    Body: IdentityFailureResponse.ForIdentityNotFound(traceId),
                    Headers: [],
                    ContentType: "application/problem+json"
                );

            case IdentityResultStatus.InvalidProperties:
                return new FrontendResponse(
                    StatusCode: 400,
                    Body: IdentityErrorProjection.Project(errors, traceId),
                    Headers: [],
                    ContentType: "application/problem+json"
                );

            case IdentityResultStatus.JobFailed:
                return isResults
                    ? new FrontendResponse(
                        StatusCode: 502,
                        Body: IdentityFailureResponse.ForIdentityJobFailed(traceId),
                        Headers: [],
                        ContentType: "application/problem+json"
                    )
                    : ContractViolation(
                        traceId,
                        "The identity provider returned JobFailed from an operation other than results polling."
                    );

            case IdentityResultStatus.Incomplete:
                if (!isResults)
                {
                    return ContractViolation(
                        traceId,
                        "The identity provider returned Incomplete from an operation other than results polling."
                    );
                }
                if (payload is null)
                {
                    return ContractViolation(
                        traceId,
                        "The identity provider returned Incomplete with no payload."
                    );
                }
                return new FrontendResponse(
                    StatusCode: 200,
                    Body: payload,
                    Headers: [],
                    LocationHeaderPath: ComposePollPath(
                        requestInfo.IdentityPollPathPrefix,
                        Uri.EscapeDataString(requestInfo.IdentityRouteValue!)
                    )
                );

            case IdentityResultStatus.Success:
                return MapSuccess(requestInfo, payload, requestToken, traceId);

            default:
                return ContractViolation(
                    traceId,
                    $"The identity provider returned an unrecognized status '{status}'."
                );
        }
    }

    private FrontendResponse MapSuccess(
        RequestInfo requestInfo,
        JsonNode? payload,
        string? requestToken,
        TraceId traceId
    )
    {
        switch (requestInfo.IdentityOperation)
        {
            case IdentityOperation.Create:
                return IsStringPayload(payload)
                    ? new FrontendResponse(StatusCode: 200, Body: payload, Headers: [])
                    : ContractViolation(
                        traceId,
                        "The identity provider returned Success from create with no unique-id string payload."
                    );

            case IdentityOperation.GetById:
                return payload is not null
                    ? new FrontendResponse(StatusCode: 200, Body: payload, Headers: [])
                    : ContractViolation(
                        traceId,
                        "The identity provider returned Success from get-by-id with no payload."
                    );

            case IdentityOperation.Results:
                return payload is not null
                    ? new FrontendResponse(StatusCode: 200, Body: payload, Headers: [])
                    : ContractViolation(
                        traceId,
                        "The identity provider returned Success from results with no payload."
                    );

            case IdentityOperation.Find:
            case IdentityOperation.Search:
                return MapFindOrSearchSuccess(requestInfo, payload, requestToken, traceId);

            default:
                return ContractViolation(traceId, "The identity provider returned Success unexpectedly.");
        }
    }

    private FrontendResponse MapFindOrSearchSuccess(
        RequestInfo requestInfo,
        JsonNode? payload,
        string? requestToken,
        TraceId traceId
    )
    {
        bool hasPayload = payload is not null;
        bool hasToken = requestToken is not null;

        if (hasPayload && hasToken)
        {
            return ContractViolation(
                traceId,
                "The identity provider returned Success with both a payload and a request token."
            );
        }

        if (!hasPayload && !hasToken)
        {
            return ContractViolation(
                traceId,
                "The identity provider returned Success with neither a payload nor a request token."
            );
        }

        if (hasPayload)
        {
            return new FrontendResponse(StatusCode: 200, Body: payload, Headers: []);
        }

        if (!requestInfo.IdentityCapabilities.HasFlag(IdentityCapabilities.Results))
        {
            return ContractViolation(
                traceId,
                "The identity provider returned a request token but does not advertise the Results capability."
            );
        }

        IdentityRequestTokenEvaluation evaluation = IdentityRequestTokenRule.Evaluate(
            requestToken,
            requestInfo.IdentityPollPathPrefix,
            requestInfo.FrontendRequest.MaxRequestLineSize ?? _maxRequestLineSize
        );

        if (!evaluation.IsUsable)
        {
            return ContractViolation(
                traceId,
                "The identity provider returned a request token that cannot be safely composed into a poll path."
            );
        }

        return new FrontendResponse(
            StatusCode: 202,
            Body: null,
            Headers: [],
            LocationHeaderPath: ComposePollPath(requestInfo.IdentityPollPathPrefix, evaluation.EscapedToken)
        );
    }

    private static string ComposePollPath(string pollPathPrefix, string escapedToken) =>
        $"{pollPathPrefix}/{escapedToken}";

    private static bool IsStringPayload(JsonNode? payload) =>
        payload is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    private static FrontendResponse ContractViolation(TraceId traceId, string detail) =>
        new(
            StatusCode: 502,
            Body: IdentityFailureResponse.ForIdentityProviderContractViolation(traceId, detail),
            Headers: [],
            ContentType: "application/problem+json"
        );

    private static FrontendResponse BadRequest(string errorDetail, TraceId traceId) =>
        new(
            StatusCode: 400,
            Body: FailureResponse.ForBadRequest(FailureResponse.ErrorsArmDetail, traceId, [], [errorDetail]),
            Headers: [],
            ContentType: "application/problem+json"
        );

    /// <summary>
    /// Validates the parsed body's top-level shape for the three JSON-body operations
    /// (design.md "The four inbound protocol checks"): create needs a JSON object, find needs a JSON
    /// array of strings, and search needs a JSON array of objects. Get-by-id and results carry no
    /// body and are not checked here. Malformed JSON, an empty body, and a duplicate property are
    /// already rejected upstream by ParseBodyMiddleware/DuplicatePropertiesMiddleware before this
    /// handler runs, so only the shape - not the JSON grammar - is checked here.
    /// </summary>
    private static bool TryValidateBodyShape(
        RequestInfo requestInfo,
        TraceId traceId,
        out FrontendResponse? rejection
    )
    {
        rejection = null;

        switch (requestInfo.IdentityOperation)
        {
            case IdentityOperation.Create:
                if (requestInfo.ParsedBody is JsonObject)
                {
                    return true;
                }
                rejection = BadRequest("The request body must be a JSON object.", traceId);
                return false;

            case IdentityOperation.Find:
                if (
                    requestInfo.ParsedBody is JsonArray findArray
                    && findArray.All(item =>
                        item is JsonValue value && value.GetValueKind() == JsonValueKind.String
                    )
                )
                {
                    return true;
                }
                rejection = BadRequest("The request body must be a JSON array of strings.", traceId);
                return false;

            case IdentityOperation.Search:
                if (
                    requestInfo.ParsedBody is JsonArray searchArray
                    && searchArray.All(item => item is JsonObject)
                )
                {
                    return true;
                }
                rejection = BadRequest("The request body must be a JSON array of objects.", traceId);
                return false;

            default:
                // GetById and Results carry no body.
                return true;
        }
    }

    private static IdentityRequestContext BuildContext(RequestInfo requestInfo)
    {
        Dictionary<string, string> routeQualifiers = new(StringComparer.OrdinalIgnoreCase);

        foreach (var qualifier in requestInfo.FrontendRequest.RouteQualifiers)
        {
            if (!routeQualifiers.TryAdd(qualifier.Key.Value, qualifier.Value.Value))
            {
                throw new InvalidOperationException(
                    $"Route qualifier name '{qualifier.Key.Value}' collides with another configured "
                        + "qualifier name under a case-insensitive comparison."
                );
            }
        }

        return new IdentityRequestContext
        {
            Tenant = requestInfo.FrontendRequest.Tenant,
            RouteQualifiers = routeQualifiers,
            ClientId = requestInfo.ClientAuthorizations.ClientId,
            TraceId = requestInfo.FrontendRequest.TraceId.Value,
        };
    }
}
