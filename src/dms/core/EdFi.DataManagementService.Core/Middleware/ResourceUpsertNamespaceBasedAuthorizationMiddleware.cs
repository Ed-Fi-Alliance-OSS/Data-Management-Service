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
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Authorize the request bodies based on the client's authorization information.
/// </summary>
internal class ResourceUpsertNamespaceBasedAuthorizationMiddleware(
    IAuthorizationServiceFactory _authorizationServiceFactory,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ResourceUpsertNamespaceBasedAuthorizationMiddleware - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );

            string authorizationStrategy = "NamespaceBased";

            if (context.ResourceActionAuthStrategies.Contains(authorizationStrategy))
            {
                List<AuthorizationResult> authResultsAcrossAuthStrategies = [];

                var authStrategyHandler = _authorizationServiceFactory.GetByName<IAuthorizationValidator>(
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
                    context.FrontendRequest.ClientAuthorizations
                );
                authResultsAcrossAuthStrategies.Add(authorizationResult);

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
