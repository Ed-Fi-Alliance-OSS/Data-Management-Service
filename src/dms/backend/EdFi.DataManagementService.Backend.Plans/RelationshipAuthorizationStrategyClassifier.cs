// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class RelationshipAuthorizationStrategyClassifier
{
    private const string CustomViewConvention = "{BasisResource}With...";
    private const string NamespaceBased = "NamespaceBased";
    private const string NoFurtherAuthorizationRequired = "NoFurtherAuthorizationRequired";
    private const string OwnershipBased = "OwnershipBased";
    private const string RelationshipsWithEdOrgsAndPeople = "RelationshipsWithEdOrgsAndPeople";
    private const string RelationshipsWithEdOrgsAndPeopleInverted =
        "RelationshipsWithEdOrgsAndPeopleInverted";
    private const string RelationshipsWithEdOrgsOnly = "RelationshipsWithEdOrgsOnly";
    private const string RelationshipsWithEdOrgsOnlyInverted = "RelationshipsWithEdOrgsOnlyInverted";
    private const string RelationshipsWithPeopleOnly = "RelationshipsWithPeopleOnly";
    private const string RelationshipsWithStudentsOnly = "RelationshipsWithStudentsOnly";
    private const string RelationshipsWithStudentsOnlyThroughResponsibility =
        "RelationshipsWithStudentsOnlyThroughResponsibility";
    private const string StandardProjectName = "Ed-Fi";

    private static readonly IReadOnlyDictionary<
        string,
        RelationshipAuthorizationStrategyKind
    > _knownButNotEnabledStrategyKindsByName = new Dictionary<string, RelationshipAuthorizationStrategyKind>(
        StringComparer.Ordinal
    )
    {
        [NamespaceBased] = RelationshipAuthorizationStrategyKind.NamespaceBased,
        [OwnershipBased] = RelationshipAuthorizationStrategyKind.OwnershipBased,
        [RelationshipsWithEdOrgsAndPeople] =
            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeople,
        [RelationshipsWithEdOrgsAndPeopleInverted] =
            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeopleInverted,
        [RelationshipsWithPeopleOnly] = RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
        [RelationshipsWithStudentsOnly] = RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnly,
        [RelationshipsWithStudentsOnlyThroughResponsibility] =
            RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility,
    };

    public static RelationshipAuthorizationClassification Classify(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);

        List<SupportedRelationshipAuthorizationStrategy> supportedStrategies = [];
        List<ConfiguredAuthorizationStrategy> noFurtherAuthorizationRequiredStrategies = [];
        List<KnownButNotEnabledRelationshipAuthorizationStrategy> knownButNotEnabledStrategies = [];
        List<RelationshipAuthorizationFailureMetadata> securityConfigurationFailures = [];
        var relationshipLocalOrder = 0;

        foreach (var configuredStrategy in configuredAuthorizationStrategies)
        {
            var strategyName = configuredStrategy.StrategyName;

            if (string.Equals(strategyName, NoFurtherAuthorizationRequired, StringComparison.Ordinal))
            {
                noFurtherAuthorizationRequiredStrategies.Add(configuredStrategy);
                continue;
            }

            var currentRelationshipLocalOrder = relationshipLocalOrder++;

            switch (strategyName)
            {
                case RelationshipsWithEdOrgsOnly:
                    supportedStrategies.Add(
                        new SupportedRelationshipAuthorizationStrategy(
                            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                            RelationshipAuthorizationHierarchyDirection.Normal,
                            configuredStrategy,
                            currentRelationshipLocalOrder
                        )
                    );
                    continue;

                case RelationshipsWithEdOrgsOnlyInverted:
                    supportedStrategies.Add(
                        new SupportedRelationshipAuthorizationStrategy(
                            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted,
                            RelationshipAuthorizationHierarchyDirection.Inverted,
                            configuredStrategy,
                            currentRelationshipLocalOrder
                        )
                    );
                    continue;
            }

            if (
                _knownButNotEnabledStrategyKindsByName.TryGetValue(
                    strategyName,
                    out var knownButNotEnabledKind
                )
            )
            {
                knownButNotEnabledStrategies.Add(
                    new KnownButNotEnabledRelationshipAuthorizationStrategy(
                        knownButNotEnabledKind,
                        configuredStrategy,
                        currentRelationshipLocalOrder
                    )
                );

                continue;
            }

            var customViewStrategyResolution = ResolveCustomViewStrategy(mappingSet, strategyName);

            if (customViewStrategyResolution.Outcome is CustomViewStrategyResolutionOutcome.Resolved)
            {
                knownButNotEnabledStrategies.Add(
                    new KnownButNotEnabledRelationshipAuthorizationStrategy(
                        RelationshipAuthorizationStrategyKind.CustomViewBased,
                        configuredStrategy,
                        currentRelationshipLocalOrder,
                        customViewStrategyResolution.BasisResource
                    )
                );

                continue;
            }

            securityConfigurationFailures.Add(
                BuildSecurityConfigurationFailure(
                    resource,
                    configuredStrategy,
                    currentRelationshipLocalOrder,
                    customViewStrategyResolution
                )
            );
        }

        return new RelationshipAuthorizationClassification(
            DetermineOutcome(
                supportedStrategies,
                noFurtherAuthorizationRequiredStrategies,
                knownButNotEnabledStrategies,
                securityConfigurationFailures
            ),
            supportedStrategies,
            noFurtherAuthorizationRequiredStrategies,
            knownButNotEnabledStrategies,
            securityConfigurationFailures
        );
    }

    private static RelationshipAuthorizationClassificationOutcome DetermineOutcome(
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        IReadOnlyList<ConfiguredAuthorizationStrategy> noFurtherAuthorizationRequiredStrategies,
        IReadOnlyList<KnownButNotEnabledRelationshipAuthorizationStrategy> knownButNotEnabledStrategies,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> securityConfigurationFailures
    )
    {
        if (securityConfigurationFailures.Count > 0)
        {
            return RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError;
        }

        if (knownButNotEnabledStrategies.Count > 0)
        {
            return RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled;
        }

        if (supportedStrategies.Count > 0)
        {
            return RelationshipAuthorizationClassificationOutcome.SupportedStrategies;
        }

        if (noFurtherAuthorizationRequiredStrategies.Count > 0)
        {
            return RelationshipAuthorizationClassificationOutcome.NoFurtherAuthorizationRequired;
        }

        return RelationshipAuthorizationClassificationOutcome.NoAuthorizationRequired;
    }

    private static RelationshipAuthorizationFailureMetadata BuildSecurityConfigurationFailure(
        QualifiedResourceName resource,
        ConfiguredAuthorizationStrategy configuredStrategy,
        int relationshipLocalOrder,
        CustomViewStrategyResolution resolution
    ) =>
        resolution.Outcome switch
        {
            CustomViewStrategyResolutionOutcome.UnknownBasisResource =>
                new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource,
                    resource,
                    configuredStrategy,
                    relationshipLocalOrder,
                    new RelationshipAuthorizationFailureLocation(
                        AuthorizationObjectName: resolution.BasisResourceName
                    ),
                    $"Strategy '{configuredStrategy.StrategyName}' matches the {CustomViewConvention} custom-view convention, but its basis resource could not be resolved."
                ),
            _ => new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy,
                resource,
                configuredStrategy,
                relationshipLocalOrder,
                null,
                $"Strategy '{configuredStrategy.StrategyName}' is not a recognized built-in strategy and does not match the {CustomViewConvention} custom-view convention."
            ),
        };

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
            return new CustomViewStrategyResolution(
                CustomViewStrategyResolutionOutcome.UnknownBasisResource,
                strategyName[..withDelimiterIndexes.Max()],
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
