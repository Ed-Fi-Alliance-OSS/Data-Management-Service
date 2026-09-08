// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend;

internal static class AuthorizationSecurityConfigurationDiagnostics
{
    public const string NamespaceInvalidNamespacePrefix = "NamespaceAuthorization.InvalidNamespacePrefix";
    public const string NamespacePrefixCapExceeded = "NamespaceAuthorization.PrefixCapExceeded";
    public const string NamespaceInvalidAuthorizationMetadata =
        "NamespaceAuthorization.Auth1.InvalidAuthorizationMetadata";
    public const string NamespaceInvalidAuth1Payload = "NamespaceAuthorization.Auth1.InvalidPayload";
    public const string NamespaceAuth1PayloadMappingFailed =
        "NamespaceAuthorization.Auth1.PayloadMappingFailed";
    public const string NamespaceInvalidStaleTargetPayload =
        "NamespaceAuthorization.Auth1.InvalidStaleTargetPayload";
    public const string NamespaceProposedValueExtractionInvalid =
        "NamespaceAuthorization.ProposedValueExtractionInvalid";
    public const string AuthorizationParameterBudgetCommandParameterCapExceeded =
        "AuthorizationParameterBudget.CommandParameterCapExceeded";
    public const string RelationshipInvalidAuthorizationResult =
        "RelationshipAuthorization.InvalidAuthorizationResult";
    public const string RelationshipProposedValueExtractionInvalid =
        "RelationshipAuthorization.ProposedValueExtractionInvalid";

    public static SecurityConfigurationFailureDiagnostic[] ForNamespacePrefixParameterization(
        string providerOrPlannerFailureKind
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: providerOrPlannerFailureKind,
                ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.NamespaceBased]
            ),
        ];

    public static SecurityConfigurationFailureDiagnostic[] ForNamespaceAuthorizationAuth1(
        string providerOrPlannerFailureKind,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> checks
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: providerOrPlannerFailureKind,
                ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.NamespaceBased],
                PhysicalPath: FormatNamespacePhysicalPath(checks)
            ),
        ];

    public static SecurityConfigurationFailureDiagnostic[] ForNamespaceProposedValueExtraction(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> checks
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: NamespaceProposedValueExtractionInvalid,
                ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.NamespaceBased],
                PhysicalPath: FormatNamespacePhysicalPath(checks)
            ),
        ];

    public static SecurityConfigurationFailureDiagnostic[] ForCommandParameterCapExceeded(
        QualifiedResourceName resource
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: AuthorizationParameterBudgetCommandParameterCapExceeded,
                ResourceFullName: RelationalWriteSupport.FormatResource(resource)
            ),
        ];

    public static SecurityConfigurationFailureDiagnostic[] ForRelationshipAuthorizationAuth1(
        RelationshipAuthorizationProviderFailureDiagnostic providerDiagnostic,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: $"RelationshipAuthorization.Auth1.{providerDiagnostic.MappingFailureCategory}",
                ConfiguredStrategyNames: DistinctInFirstOccurrenceOrder(
                    checkSpecs.Select(static spec => spec.ConfiguredStrategy.StrategyName)
                ),
                ConfiguredStrategyIndexes: DistinctIndexesInFirstOccurrenceOrder(
                    checkSpecs.Select(static spec => spec.ConfiguredStrategy.RawConfiguredIndex)
                )
            ),
        ];

    public static SecurityConfigurationFailureDiagnostic[] ForRelationshipInvalidAuthorizationResult(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: RelationshipInvalidAuthorizationResult,
                ConfiguredStrategyNames: DistinctInFirstOccurrenceOrder(
                    checkSpecs.Select(static spec => spec.ConfiguredStrategy.StrategyName)
                ),
                ConfiguredStrategyIndexes: DistinctIndexesInFirstOccurrenceOrder(
                    checkSpecs.Select(static spec => spec.ConfiguredStrategy.RawConfiguredIndex)
                )
            ),
        ];

    public static SecurityConfigurationFailureDiagnostic[] ForRelationshipProposedValueExtraction(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: RelationshipProposedValueExtractionInvalid,
                ConfiguredStrategyNames: DistinctInFirstOccurrenceOrder(
                    checkSpecs.Select(static spec => spec.ConfiguredStrategy.StrategyName)
                ),
                ConfiguredStrategyIndexes: DistinctIndexesInFirstOccurrenceOrder(
                    checkSpecs.Select(static spec => spec.ConfiguredStrategy.RawConfiguredIndex)
                )
            ),
        ];

    private static string? FormatNamespacePhysicalPath(IReadOnlyList<NamespaceAuthorizationCheckSpec> checks)
    {
        var physicalPaths = DistinctInFirstOccurrenceOrder(
            checks.Select(static check => $"{check.RootTable}.{check.NamespaceColumn.Value}")
        );

        return physicalPaths.Length switch
        {
            0 => null,
            1 => physicalPaths[0],
            _ => string.Join(", ", physicalPaths),
        };
    }

    private static string[] DistinctInFirstOccurrenceOrder(IEnumerable<string> values)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        return [.. values.Where(seen.Add)];
    }

    private static int[] DistinctIndexesInFirstOccurrenceOrder(IEnumerable<int> values)
    {
        HashSet<int> seen = [];
        return [.. values.Where(seen.Add)];
    }
}
