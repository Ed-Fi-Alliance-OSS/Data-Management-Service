// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class RelationshipAuthorizationStrategyClassifier
{
    private const string CustomViewConvention = "{BasisResource}With...";
    private const string StandardProjectName = "Ed-Fi";

    private static readonly IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> _edOrgOnlySubjects =
    [
        new(SecurableElementKind.EducationOrganization),
    ];

    private static readonly IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> _edOrgAndPeopleSubjects =
    [
        new(SecurableElementKind.EducationOrganization),
        new(SecurableElementKind.Student, RelationshipAuthorizationPersonAuthViewKind.Student),
        new(SecurableElementKind.Contact, RelationshipAuthorizationPersonAuthViewKind.Contact),
        new(SecurableElementKind.Staff, RelationshipAuthorizationPersonAuthViewKind.Staff),
    ];

    private static readonly IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> _peopleOnlySubjects =
    [
        new(SecurableElementKind.Student, RelationshipAuthorizationPersonAuthViewKind.Student),
        new(SecurableElementKind.Contact, RelationshipAuthorizationPersonAuthViewKind.Contact),
        new(SecurableElementKind.Staff, RelationshipAuthorizationPersonAuthViewKind.Staff),
    ];

    private static readonly IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> _studentsOnlySubjects =
    [
        new(SecurableElementKind.Student, RelationshipAuthorizationPersonAuthViewKind.Student),
    ];

    private static readonly IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> _studentsOnlyThroughResponsibilitySubjects =
    [
        new(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
        ),
    ];

    private static readonly IReadOnlyDictionary<
        string,
        SupportedRelationshipAuthorizationStrategyDefinition
    > _supportedStrategyDefinitionsByName = new Dictionary<
        string,
        SupportedRelationshipAuthorizationStrategyDefinition
    >(StringComparer.Ordinal)
    {
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                _edOrgOnlySubjects
            ),
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted,
                RelationshipAuthorizationHierarchyDirection.Inverted,
                _edOrgOnlySubjects
            ),
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeople,
                RelationshipAuthorizationHierarchyDirection.Normal,
                _edOrgAndPeopleSubjects
            ),
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeopleInverted,
                RelationshipAuthorizationHierarchyDirection.Inverted,
                _edOrgAndPeopleSubjects
            ),
        [AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                _peopleOnlySubjects
            ),
        [AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                _studentsOnlySubjects
            ),
        [AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility] =
            new SupportedRelationshipAuthorizationStrategyDefinition(
                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility,
                RelationshipAuthorizationHierarchyDirection.Normal,
                _studentsOnlyThroughResponsibilitySubjects
            ),
    };

    private static readonly IReadOnlyDictionary<
        string,
        RelationshipAuthorizationStrategyKind
    > _knownButNotEnabledStrategyKindsByName = new Dictionary<string, RelationshipAuthorizationStrategyKind>(
        StringComparer.Ordinal
    )
    {
        [AuthorizationStrategyNameConstants.OwnershipBased] =
            RelationshipAuthorizationStrategyKind.OwnershipBased,
    };

    public static bool IsPeopleRelationshipStrategy(string strategyName) =>
        _supportedStrategyDefinitionsByName.TryGetValue(strategyName, out var definition)
        && definition.IncludesPeopleSubjects;

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

            if (IsNoFurtherAuthorizationRequired(strategyName))
            {
                noFurtherAuthorizationRequiredStrategies.Add(configuredStrategy);
                continue;
            }

            var currentRelationshipLocalOrder = relationshipLocalOrder++;

            if (
                TryCreateSupportedRelationshipStrategy(
                    configuredStrategy,
                    currentRelationshipLocalOrder,
                    out var supportedStrategy
                )
            )
            {
                supportedStrategies.Add(supportedStrategy);
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
                    Location: new RelationshipAuthorizationFailureLocation(
                        AuthorizationObjectName: resolution.BasisResourceName
                    ),
                    Hint: $"Strategy '{configuredStrategy.StrategyName}' matches the {CustomViewConvention} custom-view convention, but its basis resource could not be resolved."
                ),
            _ => new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy,
                resource,
                configuredStrategy,
                relationshipLocalOrder,
                Hint: $"Strategy '{configuredStrategy.StrategyName}' is not a recognized built-in strategy and does not match the {CustomViewConvention} custom-view convention."
            ),
        };

    private static bool IsNoFurtherAuthorizationRequired(string strategyName) =>
        string.Equals(
            strategyName,
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
            StringComparison.Ordinal
        );

    private static bool TryCreateSupportedRelationshipStrategy(
        ConfiguredAuthorizationStrategy configuredStrategy,
        int relationshipLocalOrder,
        out SupportedRelationshipAuthorizationStrategy supportedStrategy
    )
    {
        if (
            !_supportedStrategyDefinitionsByName.TryGetValue(
                configuredStrategy.StrategyName,
                out var definition
            )
        )
        {
            supportedStrategy = null!;
            return false;
        }

        supportedStrategy = new SupportedRelationshipAuthorizationStrategy(
            definition.Kind,
            definition.Direction,
            configuredStrategy,
            relationshipLocalOrder,
            definition.EligibleSubjects
        );
        return true;
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

    private sealed record SupportedRelationshipAuthorizationStrategyDefinition(
        RelationshipAuthorizationStrategyKind Kind,
        RelationshipAuthorizationHierarchyDirection Direction,
        IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> EligibleSubjects
    )
    {
        public bool IncludesPeopleSubjects { get; } =
            EligibleSubjects.Any(static subject =>
                subject.Kind
                    is SecurableElementKind.Student
                        or SecurableElementKind.Contact
                        or SecurableElementKind.Staff
            );
    }
}
