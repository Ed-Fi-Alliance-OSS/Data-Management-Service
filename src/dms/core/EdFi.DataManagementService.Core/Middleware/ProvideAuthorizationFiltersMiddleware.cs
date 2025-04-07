// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Provides authorization filters
/// </summary>
internal class ProvideAuthorizationFiltersMiddleware(
    IAuthorizationServiceFactory _authorizationServiceFactory,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ProvideAuthorizationFiltersMiddleware - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );

            List<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators = [];
            foreach (string authorizationStrategy in context.ResourceActionAuthStrategies)
            {
                var authFiltersProvider =
                    _authorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
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

                authorizationStrategyEvaluators.Add(
                    authFiltersProvider.GetFilters(context.FrontendRequest.ClientAuthorizations)
                );
            }

            context.AuthorizationStrategyEvaluators = [.. authorizationStrategyEvaluators];

            await next();
        }
        catch (AuthorizationException ex)
        {
            context.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.Forbidden,
                Body: FailureResponse.ForForbidden(
                    traceId: context.FrontendRequest.TraceId,
                    errors: [ex.Message]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
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
                    ["message"] = $"Error while authorizing the request.{ex.Message}",
                    ["traceId"] = context.FrontendRequest.TraceId.Value,
                },
                Headers: []
            );
        }
    }
}
