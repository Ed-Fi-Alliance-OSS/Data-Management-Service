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
/// Terminal step for a data-route request whose HTTP method is not one of the supported verbs.
/// Reached only after the path has been parsed and the resource resolved, so that an unknown
/// project namespace or resource still answers 404 rather than 405, matching ODS/API's
/// existence-then-method ordering.
/// </summary>
internal class MethodNotAllowedMiddleware(ILogger _logger) : IPipelineStep
{
    /// <summary>
    /// The authority for these sets is ValidateRouteSemanticsMiddleware: collection routes
    /// reject PUT and DELETE, item routes reject POST.
    /// </summary>
    private const string CollectionMethods = "GET, POST";

    private const string ItemMethods = "GET, PUT, DELETE";

    /// <summary>
    /// Tracked-change routes are read-only: TrackedChangesEndpointModule maps GET alone.
    /// </summary>
    private const string TrackedChangeMethods = "GET";

    public Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering MethodNotAllowedMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // ChangeQueryOperation is set only by ParseTrackedChangePathMiddleware, so it is what
        // distinguishes a /deletes or /keyChanges route from a data route here. Both parse steps
        // leave HasDocumentUuidSegment false, so that flag alone cannot tell them apart.
        string allowed = (
            requestInfo.ChangeQueryOperation is not null,
            requestInfo.PathComponents.HasDocumentUuidSegment
        ) switch
        {
            (true, _) => TrackedChangeMethods,
            (false, true) => ItemMethods,
            (false, false) => CollectionMethods,
        };

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForMethodNotAllowed(
                [
                    $"The endpoint of the request does not support the '{requestInfo.UnsupportedMethodName}' method.",
                ],
                requestInfo.FrontendRequest.TraceId
            ),
            Headers: new Dictionary<string, string> { ["Allow"] = allowed },
            ContentType: "application/json; charset=utf-8"
        );

        return Task.CompletedTask;
    }
}
