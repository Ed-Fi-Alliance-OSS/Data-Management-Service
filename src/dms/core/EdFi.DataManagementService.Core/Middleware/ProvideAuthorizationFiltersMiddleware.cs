// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Provides authorization filters
/// </summary>
internal class ProvideAuthorizationFiltersMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ProvideAuthorizationFiltersMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            // Check if ClientAuthorizations has been populated by JWT middleware
            if (requestInfo.ClientAuthorizations == No.ClientAuthorizations)
            {
                _logger.LogWarning(
                    "ProvideAuthorizationFiltersMiddleware: No ClientAuthorizations found - JWT authentication may have failed - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Unauthorized,
                    Body: FailureResponse.ForAuthenticationFailure(
                        requestInfo.FrontendRequest.TraceId,
                        ["No authorization information found. Ensure valid JWT token is provided."]
                    ),
                    Headers: new Dictionary<string, string>
                    {
                        ["WWW-Authenticate"] = "Bearer error=\"invalid_token\"",
                    },
                    ContentType: "application/problem+json"
                );
                return;
            }

            if (requestInfo.UpsertActionPolicies is { } upsertActionPolicies)
            {
                requestInfo.UpsertActionAuthorization = new UpsertActionAuthorization(
                    ToUpsertActionPolicy(upsertActionPolicies.Create),
                    ToUpsertActionPolicy(upsertActionPolicies.Update)
                );
            }
            else
            {
                requestInfo.AuthorizationStrategyEvaluators = ToEvaluators(
                    requestInfo.ResourceActionAuthStrategies
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ProvideAuthorizationFiltersMiddleware: Error while authorizing the request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForServerErrorMessageBody(
                    $"Error while authorizing the request.{ex.Message}",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );

            return;
        }

        await next();
    }

    /// <summary>
    /// A denied action and one granted with no strategies both leave the backend nothing to apply; the
    /// evidence kept on the request is what tells their responses apart.
    /// </summary>
    private static UpsertActionPolicy ToUpsertActionPolicy(UpsertActionPolicyEvidence evidence) =>
        evidence is UpsertActionPolicyEvidence.Permitted permitted
            ? new UpsertActionPolicy.Permitted(ToEvaluators(permitted.StrategyNames))
            : UpsertActionPolicy.NotPermitted.Instance;

    private static AuthorizationStrategyEvaluator[] ToEvaluators(IReadOnlyList<string> strategyNames) =>
        [
            .. strategyNames.Select(static authorizationStrategy => new AuthorizationStrategyEvaluator(
                authorizationStrategy,
                [],
                FilterOperator.Or
            )),
        ];
}
