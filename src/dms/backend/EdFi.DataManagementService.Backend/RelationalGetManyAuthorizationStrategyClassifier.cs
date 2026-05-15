// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;

namespace EdFi.DataManagementService.Backend;

internal enum RelationalGetManyAuthorizationStrategyClassificationOutcome
{
    NoAuthorizationRequired,
    SupportedRelationshipStrategies,
    KnownButNotImplemented,
    SecurityConfigurationError,
}

internal enum RelationalGetManyAuthorizationStrategyKind
{
    RelationshipsWithEdOrgsOnly,
    RelationshipsWithEdOrgsOnlyInverted,
    NamespaceBased,
    OwnershipBased,
    RelationshipsWithEdOrgsAndPeople,
    RelationshipsWithEdOrgsAndPeopleInverted,
    RelationshipsWithPeopleOnly,
    RelationshipsWithStudentsOnly,
    RelationshipsWithStudentsOnlyThroughResponsibility,
    CustomViewBased,
}

internal sealed record RelationalGetManySupportedAuthorizationStrategy(
    RelationalGetManyAuthorizationStrategyKind Kind,
    AuthorizationStrategyEvaluator Evaluator
);

internal sealed record RelationalGetManyKnownButNotImplementedStrategy(
    string StrategyName,
    RelationalGetManyAuthorizationStrategyKind Kind,
    QualifiedResourceName? BasisResource = null
);

internal sealed record RelationalGetManyAuthorizationStrategyClassification(
    RelationalGetManyAuthorizationStrategyClassificationOutcome Outcome,
    IReadOnlyList<RelationalGetManySupportedAuthorizationStrategy> SupportedStrategies,
    IReadOnlyList<RelationalGetManyKnownButNotImplementedStrategy> KnownButNotImplementedStrategies,
    string? FailureMessage
);

internal static class RelationalGetManyAuthorizationStrategyClassifier
{
    private const string CustomViewConvention = "{BasisResource}With...";
    private const string StandardProjectName = "Ed-Fi";
    private static readonly IReadOnlyDictionary<
        string,
        RelationalGetManyAuthorizationStrategyKind
    > _knownButNotImplementedStrategyKindsByName = new Dictionary<
        string,
        RelationalGetManyAuthorizationStrategyKind
    >(StringComparer.Ordinal)
    {
        [AuthorizationStrategyNameConstants.NamespaceBased] =
            RelationalGetManyAuthorizationStrategyKind.NamespaceBased,
        [AuthorizationStrategyNameConstants.OwnershipBased] =
            RelationalGetManyAuthorizationStrategyKind.OwnershipBased,
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople] =
            RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeople,
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted] =
            RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeopleInverted,
        [AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly] =
            RelationalGetManyAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
        [AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly] =
            RelationalGetManyAuthorizationStrategyKind.RelationshipsWithStudentsOnly,
        [AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility] =
            RelationalGetManyAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility,
    };

    public static RelationalGetManyAuthorizationStrategyClassification Classify(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        List<RelationalGetManySupportedAuthorizationStrategy> supportedStrategies = [];
        HashSet<RelationalGetManyAuthorizationStrategyKind> supportedStrategyKinds = [];
        List<RelationalGetManyKnownButNotImplementedStrategy> knownButNotImplementedStrategies = [];

        void AddSupportedStrategy(
            RelationalGetManyAuthorizationStrategyKind kind,
            AuthorizationStrategyEvaluator evaluator
        )
        {
            if (supportedStrategyKinds.Add(kind))
            {
                supportedStrategies.Add(new(kind, evaluator));
            }
        }

        foreach (var evaluator in authorizationStrategyEvaluators)
        {
            var strategyName = evaluator.AuthorizationStrategyName;

            switch (strategyName)
            {
                case AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired:
                    continue;

                case AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly:
                    AddSupportedStrategy(
                        RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                        evaluator
                    );
                    continue;

                case AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted:
                    AddSupportedStrategy(
                        RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted,
                        evaluator
                    );
                    continue;

                default:
                    if (
                        _knownButNotImplementedStrategyKindsByName.TryGetValue(
                            strategyName,
                            out var knownButNotImplementedKind
                        )
                    )
                    {
                        knownButNotImplementedStrategies.Add(
                            new RelationalGetManyKnownButNotImplementedStrategy(
                                strategyName,
                                knownButNotImplementedKind
                            )
                        );

                        continue;
                    }

                    var customViewStrategyResolution = ResolveCustomViewStrategy(mappingSet, strategyName);

                    if (customViewStrategyResolution.Outcome is CustomViewStrategyResolutionOutcome.Resolved)
                    {
                        knownButNotImplementedStrategies.Add(
                            new RelationalGetManyKnownButNotImplementedStrategy(
                                strategyName,
                                RelationalGetManyAuthorizationStrategyKind.CustomViewBased,
                                customViewStrategyResolution.BasisResource
                            )
                        );

                        continue;
                    }

                    return new RelationalGetManyAuthorizationStrategyClassification(
                        RelationalGetManyAuthorizationStrategyClassificationOutcome.SecurityConfigurationError,
                        supportedStrategies,
                        knownButNotImplementedStrategies,
                        BuildInvalidStrategyMessage(
                            mappingSet,
                            resource,
                            strategyName,
                            customViewStrategyResolution
                        )
                    );
            }
        }

        if (knownButNotImplementedStrategies.Count > 0)
        {
            return new RelationalGetManyAuthorizationStrategyClassification(
                RelationalGetManyAuthorizationStrategyClassificationOutcome.KnownButNotImplemented,
                supportedStrategies,
                knownButNotImplementedStrategies,
                BuildKnownButNotImplementedMessage(resource, knownButNotImplementedStrategies)
            );
        }

        if (supportedStrategies.Count > 0)
        {
            return new RelationalGetManyAuthorizationStrategyClassification(
                RelationalGetManyAuthorizationStrategyClassificationOutcome.SupportedRelationshipStrategies,
                supportedStrategies,
                [],
                null
            );
        }

        return new RelationalGetManyAuthorizationStrategyClassification(
            RelationalGetManyAuthorizationStrategyClassificationOutcome.NoAuthorizationRequired,
            [],
            [],
            null
        );
    }

    private static string BuildKnownButNotImplementedMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationalGetManyKnownButNotImplementedStrategy> knownButNotImplementedStrategies
    )
    {
        var unsupportedStrategyNames = knownButNotImplementedStrategies
            .Select(static strategy => strategy.StrategyName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
            .Select(static strategyName => $"'{strategyName}'");

        return $"Relational query authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + "when effective GET-many authorization includes strategies outside the current DMS-1055 EdOrg-only scope. Unsupported strategies: "
            + $"[{string.Join(", ", unsupportedStrategyNames)}]. Supported DMS-1055 strategies are "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}', "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted}', and "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' as a no-op.";
    }

    private static string BuildInvalidStrategyMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string strategyName,
        CustomViewStrategyResolution resolution
    )
    {
        if (resolution.Outcome is CustomViewStrategyResolutionOutcome.UnknownBasisResource)
        {
            return $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(resource)}'. "
                + $"Strategy '{strategyName}' matches the {CustomViewConvention} custom-view convention, "
                + $"but basis resource '{resolution.BasisResourceName}' was not found in mapping set "
                + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'.";
        }

        return $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(resource)}'. "
            + $"Strategy '{strategyName}' is not a recognized built-in strategy and does not match the "
            + $"{CustomViewConvention} custom-view convention.";
    }

    private static CustomViewStrategyResolution ResolveCustomViewStrategy(
        MappingSet mappingSet,
        string strategyName
    )
    {
        List<int> withDelimiterIndexes = [];

        for (
            var withDelimiterIndex = strategyName.IndexOf("With", StringComparison.Ordinal);
            withDelimiterIndex >= 0;
            withDelimiterIndex = strategyName.IndexOf(
                    "With",
                    withDelimiterIndex + "With".Length,
                    StringComparison.Ordinal
                )
        )
        {
            if (withDelimiterIndex > 0 && withDelimiterIndex < strategyName.Length - "With".Length)
            {
                withDelimiterIndexes.Add(withDelimiterIndex);
            }
        }

        if (withDelimiterIndexes.Count == 0)
        {
            return new CustomViewStrategyResolution(
                CustomViewStrategyResolutionOutcome.NotCustomViewConvention,
                null,
                null
            );
        }

        List<QualifiedResourceName> matchingBasisResources =
        [
            .. mappingSet
                .Model.EffectiveSchema.ResourceKeysInIdOrder.Select(static entry => entry.Resource)
                .Where(resource =>
                    strategyName.StartsWith($"{resource.ResourceName}With", StringComparison.Ordinal)
                ),
        ];

        if (matchingBasisResources.Count == 0)
        {
            var basisResourceName = strategyName[..withDelimiterIndexes.Max()];

            return new CustomViewStrategyResolution(
                CustomViewStrategyResolutionOutcome.UnknownBasisResource,
                basisResourceName,
                null
            );
        }

        var longestMatchingBasisResourceNameLength = matchingBasisResources.Max(static resource =>
            resource.ResourceName.Length
        );

        List<QualifiedResourceName> preferredBasisResourceCandidates =
        [
            .. matchingBasisResources.Where(resource =>
                resource.ResourceName.Length == longestMatchingBasisResourceNameLength
            ),
        ];

        return new CustomViewStrategyResolution(
            CustomViewStrategyResolutionOutcome.Resolved,
            preferredBasisResourceCandidates[0].ResourceName,
            ResolvePreferredBasisResource(mappingSet, preferredBasisResourceCandidates)
        );
    }

    private static QualifiedResourceName ResolvePreferredBasisResource(
        MappingSet mappingSet,
        IReadOnlyList<QualifiedResourceName> candidateResources
    )
    {
        Dictionary<string, int> projectOrderByProjectName = [];

        for (var index = 0; index < mappingSet.Model.ProjectSchemasInEndpointOrder.Count; index++)
        {
            projectOrderByProjectName.TryAdd(
                mappingSet.Model.ProjectSchemasInEndpointOrder[index].ProjectName,
                index
            );
        }

        return candidateResources
            .OrderBy(resource =>
                !string.Equals(resource.ProjectName, StandardProjectName, StringComparison.Ordinal)
            )
            .ThenBy(resource =>
                projectOrderByProjectName.TryGetValue(resource.ProjectName, out var order)
                    ? order
                    : int.MaxValue
            )
            .ThenBy(static resource => resource.ProjectName, StringComparer.Ordinal)
            .First();
    }

    private enum CustomViewStrategyResolutionOutcome
    {
        NotCustomViewConvention,
        UnknownBasisResource,
        Resolved,
    }

    private sealed record CustomViewStrategyResolution(
        CustomViewStrategyResolutionOutcome Outcome,
        string? BasisResourceName,
        QualifiedResourceName? BasisResource
    );
}
