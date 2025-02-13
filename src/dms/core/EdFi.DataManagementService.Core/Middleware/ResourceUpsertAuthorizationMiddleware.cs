// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

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
