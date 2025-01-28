// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ResourceAuthorizationMiddleware(
    ISecurityMetadataService _securityMetadataService,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ResourceAuthorizationMiddleware - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );
            ApiClientDetails? apiClientDetails = context.FrontendRequest.ApiClientDetails;
            if (apiClientDetails == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: No ApiClientDetails - {TraceId}",
                    context.FrontendRequest.TraceId.Value
                );
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 401,
                    Body: FailureResponse.ForUnauthorized(
                        context.FrontendRequest.TraceId,
                        "Unauthorized",
                        "No Api Client Details"
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            Debug.Assert(
                context.FrontendRequest.ApiClientDetails != null,
                "context.FrontendRequest.ApiClientDetails != null"
            );
            string claimSetName = context.FrontendRequest.ApiClientDetails.ClaimSetName;
            _logger.LogInformation("Claim set name from token scope - {ClaimSetName}", claimSetName);

            _logger.LogInformation("Retrieving claim set list");
            var claimsList = await _securityMetadataService.GetClaimSets();

            var claim = claimsList.SingleOrDefault(c =>
                string.Equals(c.Name, claimSetName, StringComparison.InvariantCultureIgnoreCase)
            );

            if (claim == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: No Claim matching Scope {Scope} - {TraceId}",
                    claimSetName,
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            Debug.Assert(context.PathComponents != null, "context.PathComponents != null");
            ResourceClaim? resourceClaim = (claim.ResourceClaims ?? []).SingleOrDefault(r =>
                string.Equals(
                    r.Name,
                    context.PathComponents.EndpointName.Value,
                    StringComparison.InvariantCultureIgnoreCase
                )
            );

            if (resourceClaim == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: No ResourceClaim matching Endpoint {Endpoint} - {TraceId}",
                    context.PathComponents.EndpointName.Value,
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            var resourceActions = resourceClaim.Actions;
            if (resourceActions == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: No actions on the resource claim {ResourceClaim} - {TraceId}",
                    resourceClaim.Name,
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }
            var actionName = ActionResolver.Translate(context.Method).ToString();
            var isActionAuthorized =
                resourceActions.SingleOrDefault(x =>
                    string.Equals(x.Name, actionName, StringComparison.InvariantCultureIgnoreCase)
                    && x.Enabled
                ) != null;

            if (!isActionAuthorized)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: Can not perform {RequestMethod} on the resource {ResourceName} - {TraceId}",
                    context.Method.ToString(),
                    resourceClaim.Name,
                    context.FrontendRequest.TraceId.Value
                );
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: context.FrontendRequest.TraceId,
                        errors:
                        [
                            $"The API client's assigned claim set (currently '{claimSetName}') must grant permission of the '{actionName}' action on one of the following resource claims: {resourceClaim.Name}",
                        ],
                        typeExtension: "access-denied:action"
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            await next();

            void RespondAuthorizationError()
            {
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(traceId: context.FrontendRequest.TraceId, errors: []),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error while authorizing the request - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );
            context.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: new JsonObject
                {
                    ["message"] = "Error while authorizing the request.",
                    ["traceId"] = context.FrontendRequest.TraceId.Value,
                },
                Headers: []
            );
        }
    }
}
