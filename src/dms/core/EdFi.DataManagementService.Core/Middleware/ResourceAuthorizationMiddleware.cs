// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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

            var claim = claimsList.SingleOrDefault(c => string.Equals(c.Name, claimSetName));

            if (claim == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: No Claim matching Scope {Scope} - {TraceId}",
                    claimSetName,
                    context.FrontendRequest.TraceId.Value
                );
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(
                        context.FrontendRequest.TraceId,
                        "Forbidden",
                        "Access to the resource is forbidden"
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            Debug.Assert(context.PathComponents != null, "context.PathComponents != null");
            ResourceClaim? resourceClaim = (claim.ResourceClaims ?? []).SingleOrDefault(r =>
                r.Name == context.PathComponents.EndpointName.Value
            );

            if (resourceClaim == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: No ResourceClaim matching Endpoint {Endpoint} - {TraceId}",
                    context.PathComponents.EndpointName.Value,
                    context.FrontendRequest.TraceId.Value
                );
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(
                        context.FrontendRequest.TraceId,
                        "Forbidden",
                        "Access to the resource is forbidden"
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            await next();
        }
        catch (ConfigurationServiceException ex)
        {
            _logger.LogError(ex, "Error while retrieving claim sets");
            context.FrontendResponse = new FrontendResponse(
                StatusCode: (int)ex.StatusCode,
                Body: ex.ErrorContent,
                Headers: []
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while retrieving claim sets");
        }
    }
}
