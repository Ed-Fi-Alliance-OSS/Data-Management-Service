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
/// <param name="_isTrackedChangeRoute">
/// True in the tracked-change pipeline (/deletes, /keyChanges), false in the data-route pipeline.
/// ApiService fixes this when it builds the two pipelines, because the request cannot be asked:
/// both parse steps leave HasDocumentUuidSegment false, so that flag alone cannot tell a
/// tracked-change route from a data collection route.
/// </param>
internal class MethodNotAllowedMiddleware(ILogger _logger, bool _isTrackedChangeRoute) : IPipelineStep
{
    /// <summary>
    /// Tracked-change routes are read-only: TrackedChangesEndpointModule maps GET alone. The
    /// data-route sets come from ValidateRouteSemanticsMiddleware, which is their authority
    /// because its rejection table is what makes them true.
    /// </summary>
    private const string TrackedChangeMethods = "GET";

    public Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering MethodNotAllowedMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        string dataRouteMethods = requestInfo.PathComponents.HasDocumentUuidSegment
            ? ValidateRouteSemanticsMiddleware.ItemMethods
            : ValidateRouteSemanticsMiddleware.CollectionMethods;

        string allowed = _isTrackedChangeRoute ? TrackedChangeMethods : dataRouteMethods;

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForMethodNotAllowed(
                [$"The endpoint of the request does not support the '{requestInfo.MethodName}' method."],
                requestInfo.FrontendRequest.TraceId
            ),
            Headers: new Dictionary<string, string> { ["Allow"] = allowed },
            ContentType: "application/json; charset=utf-8"
        );

        return Task.CompletedTask;
    }
}
