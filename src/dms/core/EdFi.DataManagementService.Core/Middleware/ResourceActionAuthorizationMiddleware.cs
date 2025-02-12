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
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
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
            IList<ClaimSet> claimsList = await _claimSetCacheService.GetClaimSets();

            ClaimSet? claim = claimsList.SingleOrDefault(c =>
                string.Equals(c.Name, claimSetName, StringComparison.InvariantCultureIgnoreCase)
            );

            if (claim == null)
            {
                _logger.LogInformation(
                    "ResourceAuthorizationMiddleware: No ClaimSet matching Scope {Scope} - {TraceId}",
                    claimSetName,
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            Debug.Assert(
                context.PathComponents != null,
                "ResourceAuthorizationMiddleware: There should be PathComponents"
            );

            if (claim.ResourceClaims.Count == 0)
            {
                _logger.LogDebug("ResourceAuthorizationMiddleware: No ResourceClaims found");
                RespondAuthorizationError();
                return;
            }

            ResourceClaim? resourceClaim = claim.ResourceClaims.SingleOrDefault(r =>
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

            context.ResourceClaim = resourceClaim;

            List<ResourceClaimAction>? resourceActions = resourceClaim.Actions;
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
            var actionName = ActionResolver.Resolve(context.Method).ToString();
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

            // passes authorization
            context.ClientAuthorizations = new(
                apiClientDetails.EducationOrganizationIds,
                apiClientDetails.NamespacePrefixes
            );
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

/// <summary>
/// Authorize the request bodies based on the client's authorization information.
/// </summary>
internal class ResourceUpsertAuthorizationMiddleware(
    IAuthorizationStrategiesProvider _authorizationStrategiesProvider,
    IAuthorizationValidatorProvider _authorizationStrategyHandlerProvider,
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

            Debug.Assert(
                context.FrontendRequest.ApiClientDetails != null,
                "context.FrontendRequest.ApiClientDetails != null"
            );

            string claimSetName = context.FrontendRequest.ApiClientDetails.ClaimSetName;
            string actionName = ActionResolver.Translate(context.Method).ToString();

            IList<string> resourceActionAuthStrategies =
                _authorizationStrategiesProvider.GetAuthorizationStrategies(
                    context.ResourceClaim,
                    actionName
                );

            if (resourceActionAuthStrategies.Count == 0)
            {
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: context.FrontendRequest.TraceId,
                        errors:
                        [
                            $"No authorization strategies were defined for the requested action '{actionName}' against resource ['{context.ResourceClaim.Name}'] matched by the caller's claim '{claimSetName}'.",
                        ]
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            List<AuthorizationResult> authResultsAcrossAuthStrategies = [];

            foreach (string authorizationStrategy in resourceActionAuthStrategies)
            {
                var authStrategyHandler = _authorizationStrategyHandlerProvider.GetByName(
                    authorizationStrategy
                );
                if (authStrategyHandler == null)
                {
                    context.FrontendResponse = new FrontendResponse(
                        StatusCode: (int)HttpStatusCode.Forbidden,
                        Body: FailureResponse.ForForbidden(
                            traceId: context.FrontendRequest.TraceId,
                            errors:
                            [
                                $"Could not find authorization strategy implementation for the following strategy: '{authorizationStrategy}'.",
                            ]
                        ),
                        Headers: [],
                        ContentType: "application/problem+json"
                    );
                    return;
                }

                AuthorizationResult authorizationResult = authStrategyHandler.ValidateAuthorization(
                    context.DocumentSecurityElements,
                    context.FrontendRequest.ApiClientDetails
                );
                authResultsAcrossAuthStrategies.Add(authorizationResult);
            }

            if (!authResultsAcrossAuthStrategies.TrueForAll(x => x.IsAuthorized))
            {
                string[] errors = authResultsAcrossAuthStrategies
                    .Where(x => !string.IsNullOrEmpty(x.ErrorMessage))
                    .Select(x => x.ErrorMessage)
                    .ToArray();
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: context.FrontendRequest.TraceId,
                        errors: errors
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            await next();
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
