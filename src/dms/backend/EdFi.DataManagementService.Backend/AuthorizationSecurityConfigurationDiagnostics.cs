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
    public const string CustomViewAuth1PayloadMappingFailed =
        "CustomViewAuthorization.Auth1.PayloadMappingFailed";
    public const string CustomViewProposedValueExtractionInvalid =
        "CustomViewAuthorization.ProposedValueExtractionInvalid";
    public const string OwnershipTokenCapExceeded = "OwnershipAuthorization.TokenCapExceeded";
    public const string OwnershipAuth1PayloadMappingFailed =
        "OwnershipAuthorization.Auth1.PayloadMappingFailed";
    public const string OwnershipInvalidStaleTargetPayload =
        "OwnershipAuthorization.Auth1.InvalidStaleTargetPayload";

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

    /// <summary>
    /// Diagnostics for an ownership-token parameterization failure — today only the provider-independent
    /// defensive token limit.
    /// </summary>
    /// <remarks>
    /// No physical path is reported: the ownership check addresses <c>dms.Document</c> by <c>DocumentId</c>
    /// regardless of resource, so there is no resource-specific column to name. No token value is reported
    /// either; the message carries only a count.
    /// </remarks>
    public static SecurityConfigurationFailureDiagnostic[] ForOwnershipTokenParameterization(
        string providerOrPlannerFailureKind
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: providerOrPlannerFailureKind,
                ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.OwnershipBased]
            ),
        ];

    /// <summary>
    /// Diagnostics for an ownership AUTH1 payload that could not be attributed to the request's planned
    /// ownership check.
    /// </summary>
    /// <param name="providerOrPlannerFailureKind">
    /// <see cref="OwnershipAuth1PayloadMappingFailed"/> for an unparseable payload or a configured-index
    /// mismatch, or <see cref="OwnershipInvalidStaleTargetPayload"/> for a stale-target payload on a path
    /// that planned no stored ownership check.
    /// </param>
    /// <param name="configuredStrategyIndex">
    /// The configured index the request's planned ownership check carried, or <see langword="null"/> when no
    /// ownership check was planned. Reported so an operator can see which configured position the request
    /// expected, against the index the payload actually carried.
    /// </param>
    public static SecurityConfigurationFailureDiagnostic[] ForOwnershipAuthorizationAuth1(
        string providerOrPlannerFailureKind,
        int? configuredStrategyIndex
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: providerOrPlannerFailureKind,
                ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.OwnershipBased],
                ConfiguredStrategyIndexes: configuredStrategyIndex is { } index ? [index] : []
            ),
        ];

    /// <summary>
    /// Diagnostics for a custom-view AUTH1 payload that could not be mapped to a response. Names every
    /// configured custom view in the batch and the physical basis paths they authorize against, since the
    /// payload itself no longer identifies which check it came from.
    /// </summary>
    public static SecurityConfigurationFailureDiagnostic[] ForCustomViewAuthorizationAuth1(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: CustomViewAuth1PayloadMappingFailed,
                ConfiguredStrategyNames: DistinctInFirstOccurrenceOrder(
                    checks.Select(static check => check.ConfiguredStrategy.StrategyName)
                ),
                PhysicalPath: FormatCustomViewPhysicalPath(checks)
            ),
        ];

    /// <summary>
    /// Diagnostics for proposed custom-view checks that could not be reconciled with the finalized root row.
    /// </summary>
    public static SecurityConfigurationFailureDiagnostic[] ForCustomViewProposedValueExtraction(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks
    ) =>
        [
            new SecurityConfigurationFailureDiagnostic(
                ProviderOrPlannerFailureKind: CustomViewProposedValueExtractionInvalid,
                ConfiguredStrategyNames: DistinctInFirstOccurrenceOrder(
                    checks.Select(static check => check.ConfiguredStrategy.StrategyName)
                ),
                PhysicalPath: FormatCustomViewPhysicalPath(checks)
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

    private static string? FormatCustomViewPhysicalPath(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks
    )
    {
        var physicalPaths = DistinctInFirstOccurrenceOrder(
            checks.Select(static check =>
                $"{check.AuthView}.{check.AuthViewDocumentIdColumn.Value} <- {check.PathToBasisResource[^1].SourceTable}.{check.PathToBasisResource[^1].SourceColumnName.Value}"
            )
        );

        return physicalPaths.Length switch
        {
            0 => null,
            1 => physicalPaths[0],
            _ => string.Join(", ", physicalPaths),
        };
    }

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
