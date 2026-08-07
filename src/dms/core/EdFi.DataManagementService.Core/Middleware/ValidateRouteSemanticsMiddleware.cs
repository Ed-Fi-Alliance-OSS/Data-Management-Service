// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that a resolved resource route is being used with the correct write semantics.
/// Collection routes allow POST, while item routes allow PUT and DELETE.
/// </summary>
internal class ValidateRouteSemanticsMiddleware(ILogger _logger) : IPipelineStep
{
    /// <summary>
    /// The method sets this middleware enforces, expressed for the Allow response header. They
    /// live here because the rejection table below is what makes them true, and
    /// MethodNotAllowedMiddleware advertises the same sets for a wholly unsupported verb -
    /// referencing these keeps the two 405 producers in agreement by construction.
    /// </summary>
    internal const string CollectionMethods = "GET, POST";

    internal const string ItemMethods = "GET, PUT, DELETE";

    /// <summary>
    /// The partitions operation is a read-only sibling of the GET-many endpoint, so the write
    /// methods this middleware rejects on it leave GET as the whole Allow set. Advertised even
    /// though no partitions pipeline exists yet and ParsePathMiddleware answers the GET itself as
    /// an invalid identifier: the header has to name the methods the route will serve, and naming
    /// the collection's set instead would advertise the very POST being rejected.
    /// </summary>
    internal const string PartitionsMethods = "GET";

    /// <summary>
    /// The Allow set for an operation, for both this middleware and MethodNotAllowedMiddleware.
    /// </summary>
    /// <remarks>
    /// Exhaustive rather than an item-or-else choice, so an operation added to the hierarchy has to
    /// name its own set instead of silently inheriting the collection's.
    /// </remarks>
    internal static string AllowedMethodsFor(ResourcePathOperation operation) =>
        operation switch
        {
            ResourcePathOperation.ById => ItemMethods,
            ResourcePathOperation.Collection => CollectionMethods,
            ResourcePathOperation.Partitions => PartitionsMethods,
            _ => throw new InvalidOperationException(
                $"Unhandled resource path operation '{operation.GetType().Name}'. A new operation "
                    + "must name the methods it allows."
            ),
        };

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateRouteSemanticsMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Every arm names the operation its method requires and rejects everything else: DELETE and
        // PUT require an item, POST requires the collection. Phrased the other way round — as the one
        // operation each method rejects — an operation added to the hierarchy would fall through to
        // the rest of the pipeline under a method that was never meant to reach it.
        string? error = (requestInfo.Method, requestInfo.PathComponents.Operation) switch
        {
            (RequestMethod.DELETE, not ResourcePathOperation.ById) =>
                "Resource collections cannot be deleted. To delete a specific item, use DELETE and include the 'id' in the route.",
            (RequestMethod.PUT, not ResourcePathOperation.ById) =>
                "Resource collections cannot be replaced. To 'upsert' an item in the collection, use POST. To update a specific item, use PUT and include the 'id' in the route.",
            (RequestMethod.POST, not ResourcePathOperation.Collection) =>
                "Resource items can only be updated using PUT. To 'upsert' an item in the resource collection using POST, remove the 'id' from the route.",
            _ => null,
        };

        if (error is null)
        {
            await next();
            return;
        }

        _logger.LogDebug(
            "ValidateRouteSemanticsMiddleware: Invalid route semantics for request method {Method} - {TraceId}",
            requestInfo.Method,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForMethodNotAllowed([error], requestInfo.FrontendRequest.TraceId),
            // RFC 9110 section 15.5.6 requires Allow on a 405. Sent here as well as from
            // MethodNotAllowedMiddleware so both urn:ed-fi:api:method-not-allowed responses carry
            // it, rather than only the one for a wholly unsupported verb. The profile-scoped 405
            // in CachedProfileService is a different contract
            // (urn:ed-fi:api:profile:method-usage) whose allowed set depends on the profile's
            // read/write content types, and is deliberately left alone.
            Headers: new Dictionary<string, string>
            {
                ["Allow"] = AllowedMethodsFor(requestInfo.PathComponents.Operation),
            },
            ContentType: "application/json; charset=utf-8"
        );
    }
}
