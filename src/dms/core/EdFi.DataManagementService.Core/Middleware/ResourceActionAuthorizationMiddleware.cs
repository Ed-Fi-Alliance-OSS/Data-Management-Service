// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Authorizes requests resource and action based on the client's authorization information.
/// </summary>
internal class ResourceActionAuthorizationMiddleware(
    IClaimSetCacheService _claimSetCacheService,
    ILogger _logger,
    bool disablePersonAuthorizationStrategies = false
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

            string claimSetName = context.FrontendRequest.ClientAuthorizations.ClaimSetName;
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
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            Debug.Assert(
                context.PathComponents != null,
                "ResourceActionAuthorizationMiddleware: There should be PathComponents"
            );

            if (claimSet.ResourceClaims.Count == 0)
            {
                _logger.LogDebug("ResourceActionAuthorizationMiddleware: No ResourceClaims found");
                RespondAuthorizationError();
                return;
            }

            string resourceClaimName = context.ResourceSchema.ResourceName.Value;

            // Create resource claim URI
            string resourceClaimUri =
                $"{Conventions.EdFiOdsResourceClaimBaseUri}/{context.PathComponents.ProjectNamespace.Value}/{resourceClaimName}";

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
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            string actionName = ActionResolver.Resolve(context.Method).ToString();

            ResourceClaim? authorizedAction = matchingClaims.SingleOrDefault(x =>
                string.Equals(x.Action, actionName, StringComparison.InvariantCultureIgnoreCase)
            );

            if (authorizedAction == null)
            {
                _logger.LogDebug(
                    "ResourceAuthorizationMiddleware: Can not perform {RequestMethod} on the resource {ResourceName} - {TraceId}",
                    context.Method.ToString(),
                    resourceClaimName,
                    context.FrontendRequest.TraceId.Value
                );
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: context.FrontendRequest.TraceId,
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
                .Select(authStrategyName =>
                    disablePersonAuthorizationStrategies
                    && authStrategyName.StartsWith("RelationshipsWith")
                    && authStrategyName != "RelationshipsWithEdOrgsOnly"
                        ? "NoFurtherAuthorizationRequired"
                        : authStrategyName
                )
                .ToList();

            if (resourceActionAuthStrategies.Count == 0)
            {
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: context.FrontendRequest.TraceId,
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

            context.ResourceActionAuthStrategies = resourceActionAuthStrategies;

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
