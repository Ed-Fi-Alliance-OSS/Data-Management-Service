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
                ["Allow"] = requestInfo.PathComponents.HasDocumentUuidSegment
                    ? ItemMethods
                    : CollectionMethods,
            },
            ContentType: "application/json; charset=utf-8"
        );
    }
}
