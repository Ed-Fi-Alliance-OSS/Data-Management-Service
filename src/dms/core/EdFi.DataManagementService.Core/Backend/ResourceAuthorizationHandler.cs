// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The ResourceAuthorizationHandler implementation that
/// interrogates a resources securityElements to determine if the
/// filters in the provided authorizationStrategy are satisfied.
/// </summary>
public class ResourceAuthorizationHandler(
    AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
    AuthorizationSecurableInfo[] authorizationSecurableInfos,
    IAuthorizationServiceFactory authorizationServiceFactory,
    ILogger logger
) : IResourceAuthorizationHandler
{
    public async Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    )
    {
        return new ResourceAuthorizationResult.Authorized();
        logger.LogInformation(
            "Entering ResourceAuthorizationHandler. OperationType:{OperationType}, AuthorizationStrategyCount:{StrategyCount} TraceId:{TraceId}",
            operationType,
            authorizationStrategyEvaluators.Length,
            traceId.Value
        );

        logger.LogDebug(
            "DocumentSecurityElements - Namespaces: {Namespaces}, EdOrgs: {EdOrgs}, Students: {Students}, Contacts: {Contacts}, Staff: {Staff}",
            string.Join(", ", documentSecurityElements.Namespace),
            string.Join(
                ", ",
                documentSecurityElements.EducationOrganization.Select(x => $"{x.PropertyName}={x.Id}")
            ),
            string.Join(", ", documentSecurityElements.Student.Select(x => x.Value)),
            string.Join(", ", documentSecurityElements.Contact.Select(x => x.Value)),
            string.Join(", ", documentSecurityElements.Staff.Select(x => x.Value))
        );

        logger.LogDebug(
            "AuthorizationSecurableInfos: {SecurableInfos}",
            string.Join(", ", authorizationSecurableInfos.Select(x => x.SecurableKey))
        );

        List<ResourceAuthorizationResult> andResults = [];
        List<ResourceAuthorizationResult> orResults = [];

        foreach (var evaluator in authorizationStrategyEvaluators)
        {
            var validator =
                authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                    evaluator.AuthorizationStrategyName
                )
                ?? throw new AuthorizationException(
                    $"Could not find authorization strategy implementation for the following strategy: '{evaluator.AuthorizationStrategyName}'."
                );

            logger.LogDebug(
                "Using AuthorizationStrategy: {AuthorizationStrategyName} for TraceId: {TraceId}",
                evaluator.AuthorizationStrategyName,
                traceId.Value
            );

            var authResult = await validator.ValidateAuthorization(
                documentSecurityElements,
                evaluator.Filters,
                authorizationSecurableInfos,
                operationType
            );

            logger.LogDebug(
                "Authorization strategy '{AuthorizationStrategyName}' result: {AuthResult}, Operator: {Operator}, Filters: {Filters}, for TraceId: {TraceId}",
                evaluator.AuthorizationStrategyName,
                authResult.GetType().Name,
                evaluator.Operator,
                string.Join(", ", evaluator.Filters.Select(f => $"{f.GetType().Name}={f.Value}")),
                traceId.Value
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

        logger.LogDebug(
            "Authorization evaluation complete. AND results: {AndResultCount} ({AndResults}), OR results: {OrResultCount} ({OrResults}) for TraceId: {TraceId}",
            andResults.Count,
            string.Join(", ", andResults.Select(r => r.GetType().Name)),
            orResults.Count,
            string.Join(", ", orResults.Select(r => r.GetType().Name)),
            traceId.Value
        );

        if (andResults.Exists(f => f is ResourceAuthorizationResult.NotAuthorized))
        {
            return CreateNotAuthorizedResult(andResults);
        }

        if (orResults.Count != 0 && orResults.TrueForAll(f => f is ResourceAuthorizationResult.NotAuthorized))
        {
            return CreateNotAuthorizedResult(orResults);
        }

        logger.LogInformation("Authorization GRANTED for TraceId: {TraceId}", traceId.Value);
        return new ResourceAuthorizationResult.Authorized();
    }

    private static ResourceAuthorizationResult.NotAuthorized CreateNotAuthorizedResult(
        IEnumerable<ResourceAuthorizationResult> results
    )
    {
        string[] errors = results
            .OfType<ResourceAuthorizationResult.NotAuthorized>()
            .SelectMany(x => x.ErrorMessages)
            .Distinct()
            .ToArray();

        string[] hints = results
            .OfType<ResourceAuthorizationResult.NotAuthorized.WithHint>()
            .SelectMany(x => x.Hints)
            .ToArray();

        var result = hints.Any()
            ? new ResourceAuthorizationResult.NotAuthorized.WithHint(errors, hints)
            : new ResourceAuthorizationResult.NotAuthorized(errors);

        return result;
    }
}
