// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalReadGuardrails
{
    public static bool HasOnlyNoFurtherAuthorizationRequired(
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        return authorizationStrategyEvaluators.All(static evaluator =>
            string.Equals(
                evaluator.AuthorizationStrategyName,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                StringComparison.Ordinal
            )
        );
    }

    public static string BuildAuthorizationNotImplementedMessage(
        QualifiedResourceName resource,
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators,
        string operationLabel,
        string effectiveAuthorizationActionLabel
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        var strategyNames = authorizationStrategyEvaluators
            .Select(static evaluator => evaluator.AuthorizationStrategyName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(static name => $"'{name}'");

        return $"Relational {operationLabel} authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + $"when effective {effectiveAuthorizationActionLabel} authorization requires filtering. Effective strategies: "
            + $"[{string.Join(", ", strategyNames)}]. Only requests with no authorization strategies or only "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' are currently supported.";
    }

    public static int ConvertTotalCountOrThrow(
        QualifiedResourceName resource,
        long? totalCount,
        string operationLabel
    )
    {
        if (totalCount is null)
        {
            throw new InvalidOperationException(
                $"Relational {operationLabel} for resource '{RelationalWriteSupport.FormatResource(resource)}' did not return a total count "
                    + "even though the request asked for totalCount=true."
            );
        }

        if (totalCount < 0 || totalCount > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Relational {operationLabel} returned total count {totalCount.Value} for resource "
                    + $"'{RelationalWriteSupport.FormatResource(resource)}', but only values in the range [0, {int.MaxValue}] are supported."
            );
        }

        return (int)totalCount.Value;
    }
}
