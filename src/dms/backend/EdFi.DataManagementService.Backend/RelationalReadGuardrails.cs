// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalReadGuardrails
{
    public static bool HasDescriptorUnsupportedNonNamespaceStrategies(
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(nonNamespaceConfiguredStrategies);

        return nonNamespaceConfiguredStrategies.Any(static strategy =>
            !string.Equals(
                strategy.StrategyName,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                StringComparison.Ordinal
            )
        );
    }

    public static RelationalReadSecurityConfigurationFailure BuildSecurityConfigurationFailure(
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationshipAuthorizationClassification relationshipClassification
    )
    {
        ArgumentNullException.ThrowIfNull(nonNamespaceConfiguredStrategies);
        ArgumentNullException.ThrowIfNull(relationshipClassification);

        string[] unavailableStrategyNames =
        [
            .. relationshipClassification
                .SecurityConfigurationFailures.Select(static failure =>
                    failure.ConfiguredStrategy?.StrategyName
                )
                .Where(static strategyName => strategyName is not null)
                .Cast<string>(),
        ];

        if (unavailableStrategyNames.Length == 0)
        {
            unavailableStrategyNames =
            [
                .. nonNamespaceConfiguredStrategies
                    .Where(static strategy =>
                        !string.Equals(
                            strategy.StrategyName,
                            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(static strategy => strategy.StrategyName),
            ];
        }

        string[] errors =
        [
            SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies(unavailableStrategyNames),
        ];

        return new RelationalReadSecurityConfigurationFailure(
            errors,
            BuildSecurityConfigurationDiagnostics(
                resource,
                nonNamespaceConfiguredStrategies,
                relationshipClassification
            )
        );
    }

    private static SecurityConfigurationFailureDiagnostic[] BuildSecurityConfigurationDiagnostics(
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationshipAuthorizationClassification relationshipClassification
    )
    {
        if (relationshipClassification.SecurityConfigurationFailures.Count > 0)
        {
            return
            [
                .. relationshipClassification.SecurityConfigurationFailures.Select(
                    static failure => new SecurityConfigurationFailureDiagnostic(
                        ProviderOrPlannerFailureKind: $"RelationshipAuthorization.{failure.FailureKind}",
                        ResourceFullName: RelationalWriteSupport.FormatResource(failure.Resource),
                        ConfiguredStrategyNames: failure.ConfiguredStrategy is null
                            ? null
                            : [failure.ConfiguredStrategy.StrategyName],
                        ConfiguredStrategyIndexes: failure.ConfiguredStrategy is null
                            ? null
                            : [failure.ConfiguredStrategy.RawConfiguredIndex],
                        TargetResourceFullName: failure.FailureKind
                        is RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource
                            ? RelationalWriteSupport.FormatResource(failure.Resource)
                            : null
                    )
                ),
            ];
        }

        return
        [
            .. nonNamespaceConfiguredStrategies
                .Where(static strategy =>
                    !string.Equals(
                        strategy.StrategyName,
                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                        StringComparison.Ordinal
                    )
                )
                .Select(strategy => new SecurityConfigurationFailureDiagnostic(
                    ProviderOrPlannerFailureKind: "RelationshipAuthorization.UnavailableStrategy",
                    ResourceFullName: RelationalWriteSupport.FormatResource(resource),
                    ConfiguredStrategyNames: [strategy.StrategyName],
                    ConfiguredStrategyIndexes: [strategy.RawConfiguredIndex]
                )),
        ];
    }

    public static SecurityConfigurationFailureDiagnostic[] BuildNoUsableRootColumnDiagnostics(
        QualifiedResourceName resource
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: "NamespaceAuthorization.NoUsableRootColumn",
                ResourceFullName: RelationalWriteSupport.FormatResource(resource),
                ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.NamespaceBased]
            ),
        ];

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
            + $"[{string.Join(", ", strategyNames)}]. Only requests with no authorization strategies or with "
            + $"'{AuthorizationStrategyNameConstants.NamespaceBased}' and/or "
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

internal sealed record RelationalReadSecurityConfigurationFailure(
    string[] Errors,
    SecurityConfigurationFailureDiagnostic[] Diagnostics
);
