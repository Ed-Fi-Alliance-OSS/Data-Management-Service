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
    /// <summary>
    /// Descriptors authorize against the shared <c>dms.Descriptor</c> root-table Namespace column, so the
    /// only effective strategies they can evaluate are <c>NamespaceBased</c> and
    /// <c>NoFurtherAuthorizationRequired</c>. Any other strategy (relationship, ownership, custom view)
    /// has no descriptor-applicable filter and must fail closed before any database work.
    /// </summary>
    public static bool HasOnlyNamespaceBasedOrNoFurtherAuthorizationRequired(
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        return authorizationStrategyEvaluators.All(static evaluator =>
            string.Equals(
                evaluator.AuthorizationStrategyName,
                AuthorizationStrategyNameConstants.NamespaceBased,
                StringComparison.Ordinal
            )
            || string.Equals(
                evaluator.AuthorizationStrategyName,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                StringComparison.Ordinal
            )
        );
    }

    /// <summary>
    /// Whether <c>NamespaceBased</c> is among the effective strategies, indicating that backend-planned
    /// namespace authorization must run for the request.
    /// </summary>
    public static bool ContainsNamespaceBased(
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        return authorizationStrategyEvaluators.Any(static evaluator =>
            string.Equals(
                evaluator.AuthorizationStrategyName,
                AuthorizationStrategyNameConstants.NamespaceBased,
                StringComparison.Ordinal
            )
        );
    }

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

    public static string[] BuildSecurityConfigurationErrors(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(nonNamespaceConfiguredStrategies);

        var classification = RelationshipAuthorizationStrategyClassifier.Classify(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies
        );

        string[] unavailableStrategyNames =
        [
            .. classification
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

        return
        [
            SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies(unavailableStrategyNames),
        ];
    }

    public static SecurityConfigurationFailureDiagnostic[] BuildSecurityConfigurationDiagnostics(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(nonNamespaceConfiguredStrategies);

        var classification = RelationshipAuthorizationStrategyClassifier.Classify(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies
        );

        if (classification.SecurityConfigurationFailures.Count > 0)
        {
            return
            [
                .. classification.SecurityConfigurationFailures.Select(
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
