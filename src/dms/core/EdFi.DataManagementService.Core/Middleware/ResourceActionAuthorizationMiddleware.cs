// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Authorizes requests resource and action based on the client's authorization information.
/// </summary>
internal class ResourceActionAuthorizationMiddleware(
    IClaimSetCacheService _claimSetCacheService,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ResourceAuthorizationMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            // Check if ClientAuthorizations has been populated by JWT middleware
            if (requestInfo.ClientAuthorizations == No.ClientAuthorizations)
            {
                _logger.LogWarning(
                    "ResourceActionAuthorizationMiddleware: No ClientAuthorizations found - JWT authentication may have failed - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Unauthorized,
                    Body: FailureResponse.ForUnauthorized(
                        requestInfo.FrontendRequest.TraceId,
                        error: "Unauthorized",
                        description: "No authorization information found. Ensure valid JWT token is provided."
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            string claimSetName = requestInfo.ClientAuthorizations.ClaimSetName;
            _logger.LogInformation("Claim set name from token scope - {ClaimSetName}", claimSetName);

            _logger.LogInformation("Retrieving claim set list");
            IList<ClaimSet> claimsList = await _claimSetCacheService.GetClaimSets();

            ClaimSet? claimSet = claimsList.SingleOrDefault(c =>
                string.Equals(c.Name, claimSetName, StringComparison.InvariantCultureIgnoreCase)
            );

            if (claimSet == null)
            {
                _logger.LogInformation(
                    "ResourceActionAuthorizationMiddleware: No ClaimSet matching Scope {Scope} - {TraceId}",
                    claimSetName,
                    requestInfo.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            Debug.Assert(
                requestInfo.PathComponents != null,
                "ResourceActionAuthorizationMiddleware: There should be PathComponents"
            );

            if (claimSet.ResourceClaims.Count == 0)
            {
                _logger.LogDebug("ResourceActionAuthorizationMiddleware: No ResourceClaims found");
                RespondAuthorizationError();
                return;
            }

            string resourceClaimName = requestInfo.ResourceSchema.ResourceName.Value;

            // Create resource claim URI
            string resourceClaimUri =
                $"{Conventions.EdFiOdsResourceClaimBaseUri}/{requestInfo.PathComponents.ProjectNamespace.Value}/{resourceClaimName}";

            ResourceClaim[] matchingClaims = claimSet
                .ResourceClaims.Where(r =>
                    string.Equals(r.Name, resourceClaimUri, StringComparison.InvariantCultureIgnoreCase)
                )
                .ToArray();

            if (matchingClaims.Length == 0)
            {
                _logger.LogDebug(
                    "ResourceActionAuthorizationMiddleware: No ResourceClaim matching Endpoint {Endpoint} - {TraceId}",
                    resourceClaimName,
                    requestInfo.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            string actionName = ActionResolver.Resolve(requestInfo.Method).ToString();

            ResourceClaim? authorizedAction = matchingClaims.SingleOrDefault(x =>
                string.Equals(x.Action, actionName, StringComparison.InvariantCultureIgnoreCase)
            );

            if (authorizedAction == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: Can not perform {RequestMethod} on the resource {ResourceName} - {TraceId}",
                    requestInfo.Method.ToString(),
                    resourceClaimName,
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors:
                        [
                            $"The API client's assigned claim set (currently '{claimSetName}') must grant permission of the '{actionName}' action on one of the following resource claims: {resourceClaimName}",
                        ],
                        typeExtension: "access-denied:action"
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            IReadOnlyList<string> resourceActionAuthStrategies = authorizedAction
                .AuthorizationStrategies.Select(auth => auth.Name)
                .ToList();

            if (resourceActionAuthStrategies.Count == 0)
            {
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors:
                        [
                            $"No authorization strategies were defined for the requested action '{actionName}' against resource ['{resourceClaimName}'] matched by the caller's claim '{claimSetName}'.",
                        ]
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            requestInfo.ResourceActionAuthStrategies = resourceActionAuthStrategies;

            await next();

            void RespondAuthorizationError()
            {
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors: []
                    ),
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
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: new JsonObject
                {
                    ["message"] = "Error while authorizing the request.",
                    ["traceId"] = requestInfo.FrontendRequest.TraceId.Value,
                },
                Headers: []
            );
        }
    }
}
