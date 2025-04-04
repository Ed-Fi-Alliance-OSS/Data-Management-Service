// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The ResourceAuthorizationHandler implementation that
/// interrogates a resources securityElements to determine if the
/// filters in the provided authorizationStrategy are satisfied.
/// </summary>
public class ResourceAuthorizationHandler(
    AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
    IAuthorizationServiceFactory authorizationServiceFactory,
    ILogger logger
) : IResourceAuthorizationHandler
{
    public async Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        TraceId traceId
    )
    {
        logger.LogInformation("Entering ResourceAuthorizationHandler. TraceId:{TraceId}", traceId.Value);
        List<AuthorizationResult> andResults = [];
        List<AuthorizationResult> orResults = [];

        foreach (var evaluator in authorizationStrategyEvaluators)
        {
            var validator =
                authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                    evaluator.AuthorizationStrategyName
                )
                ?? throw new Exception(
                    $"Could not find authorization strategy implementation for the following strategy: '{evaluator.AuthorizationStrategyName}'."
                );

            var authResult = await validator.ValidateAuthorization(
                documentSecurityElements,
                evaluator.Filters,
                traceId
            );

            if (evaluator.Operator == FilterOperator.And)
            {
                andResults.Add(authResult);
            }
            else
            {
                orResults.Add(authResult);
            }
        }

        if (andResults.Exists(f => !f.IsAuthorized))
        {
            return CreateNotAuthorizedResult(andResults);
        }

        if (orResults.Any() && Enumerable.All(orResults, f => !f.IsAuthorized))
        {
            return CreateNotAuthorizedResult(orResults);
        }

        return new ResourceAuthorizationResult.Authorized();
    }

    private static ResourceAuthorizationResult.NotAuthorized CreateNotAuthorizedResult(
        IEnumerable<AuthorizationResult> results
    )
    {
        string[] errors = results
            .Where(x => !string.IsNullOrEmpty(x.ErrorMessage))
            .Select(x => x.ErrorMessage)
            .ToArray();
        return new ResourceAuthorizationResult.NotAuthorized(errors);
    }
}
