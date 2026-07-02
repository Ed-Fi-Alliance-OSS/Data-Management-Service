// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles GET /changeQueries/v1/availableChangeVersions, returning the ODS-compatible contract
/// { "oldestChangeVersion": 0, "newestChangeVersion": &lt;backend high-water mark&gt; }.
/// oldestChangeVersion is always 0; newestChangeVersion is read from the relational backend.
/// </summary>
internal sealed class AvailableChangeVersionsHandler(ILogger<AvailableChangeVersionsHandler> logger)
    : IPipelineStep
{
    async Task IPipelineStep.Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            $"Entering {nameof(AvailableChangeVersionsHandler)} - {{TraceId}}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        var changeQueryRepository = requestInfo.ScopedServiceProvider.GetService<IChangeQueryRepository>();

        // Change Queries are exposed only when the selected datastore registers support.
        // Otherwise the endpoint is treated as not routed and returns the standard 404 not-found
        // response, matching the Change Queries contract (the same shape Program.cs MapFallback
        // emits for any unrouted path).
        if (changeQueryRepository is null)
        {
            logger.LogWarning(
                "IChangeQueryRepository is not registered; Change Queries are unsupported for this datastore configuration - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: FailureResponse.ForNotFound(
                    "The specified data could not be found.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        long newestChangeVersion = await changeQueryRepository.GetNewestChangeVersion();

        JsonObject body = new()
        {
            ["oldestChangeVersion"] = 0L,
            ["newestChangeVersion"] = newestChangeVersion,
        };

        requestInfo.FrontendResponse = new FrontendResponse(StatusCode: 200, Body: body, Headers: []);
    }
}
