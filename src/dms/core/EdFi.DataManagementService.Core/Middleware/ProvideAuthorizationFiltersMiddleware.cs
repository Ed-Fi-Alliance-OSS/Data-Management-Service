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
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Provides authorization filters
/// </summary>
internal class ProvideAuthorizationFiltersMiddleware(
    IClaimSetCacheService _claimSetCacheService,
    IAuthorizationStrategiesProvider _authorizationStrategiesProvider,
    IAuthorizationServiceFactory _authorizationFiltersProvider,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        try
        {
            // Common authorization steps will be moved to common middleware
            _logger.LogDebug(
                "Entering ProvideAuthorizationFiltersMiddleware - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );
            ApiClientDetails? apiClientDetails = context.FrontendRequest.ApiClientDetails;
            if (apiClientDetails == null)
            {
                _logger.LogDebug(
                    "ProvideAuthorizationFiltersMiddleware: No ApiClientDetails - {TraceId}",
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

            ClaimSet? claimSet = claimsList.SingleOrDefault(c =>
                string.Equals(c.Name, claimSetName, StringComparison.InvariantCultureIgnoreCase)
            );

            if (claimSet == null)
            {
                _logger.LogInformation(
                    "ProvideAuthorizationFiltersMiddleware: No ClaimSet matching Scope {Scope} - {TraceId}",
                    claimSetName,
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            Debug.Assert(
                context.PathComponents != null,
                "ProvideAuthorizationFiltersMiddleware: There should be PathComponents"
            );

            if (claimSet.ResourceClaims.Count == 0)
            {
                _logger.LogDebug("ProvideAuthorizationFiltersMiddleware: No ResourceClaims found");
                RespondAuthorizationError();
                return;
            }

            ResourceClaim? resourceClaim = claimSet.ResourceClaims.SingleOrDefault(r =>
                string.Equals(
                    r.Name,
                    context.PathComponents.EndpointName.Value,
                    StringComparison.InvariantCultureIgnoreCase
                )
            );

            if (resourceClaim == null)
            {
                _logger.LogDebug(
                    "ProvideAuthorizationFiltersMiddleware: No ResourceClaim matching Endpoint {Endpoint} - {TraceId}",
                    context.PathComponents.EndpointName.Value,
                    context.FrontendRequest.TraceId.Value
                );
                RespondAuthorizationError();
                return;
            }

            List<ResourceClaimAction>? resourceActions = resourceClaim.Actions;
            if (resourceActions == null)
            {
                _logger.LogDebug(
                    "ProvideAuthorizationFiltersMiddleware: No actions on the resource claim {ResourceClaim} - {TraceId}",
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
                    "ProvideAuthorizationFiltersMiddleware: Can not perform {RequestMethod} on the resource {ResourceName} - {TraceId}",
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

            IList<string> resourceActionAuthStrategies =
                _authorizationStrategiesProvider.GetAuthorizationStrategies(resourceClaim, actionName);

            if (resourceActionAuthStrategies.Count == 0)
            {
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Forbidden,
                    Body: FailureResponse.ForForbidden(
                        traceId: context.FrontendRequest.TraceId,
                        errors:
                        [
                            $"No authorization strategies were defined for the requested action '{actionName}' against resource ['{resourceClaim.Name}'] matched by the caller's claim '{claimSetName}'.",
                        ]
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            List<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators = [];
            foreach (string authorizationStrategy in resourceActionAuthStrategies)
            {
                var authFiltersProvider =
                    _authorizationFiltersProvider.GetByName<IAuthorizationFiltersProvider>(
                        authorizationStrategy
                    );
                if (authFiltersProvider == null)
                {
                    context.FrontendResponse = new FrontendResponse(
                        StatusCode: (int)HttpStatusCode.Forbidden,
                        Body: FailureResponse.ForForbidden(
                            traceId: context.FrontendRequest.TraceId,
                            errors:
                            [
                                $"Could not find authorization filters implementation for the following strategy: '{authorizationStrategy}'.",
                            ]
                        ),
                        Headers: [],
                        ContentType: "application/problem+json"
                    );
                    return;
                }

                authorizationStrategyEvaluators.Add(authFiltersProvider.GetFilters(apiClientDetails));
            }

            context.AuthorizationStrategyEvaluators = [.. authorizationStrategyEvaluators];

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
