// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using static EdFi.DataManagementService.Backend.Plans.RelationshipAuthorizationPlanningHelpers;

namespace EdFi.DataManagementService.Backend.Plans;

public sealed class RelationshipAuthorizationPlanner
{
    private const int MaxCachedSupportedStrategyPlans = 4096;

    private static readonly ConditionalWeakTable<
        MappingSet,
        SupportedStrategyPlanCache
    > _supportedStrategyPlanCachesByMappingSet = new();

    private readonly RelationalEdOrgAuthorizationSubjectSelector _edOrgAuthorizationSubjectSelector;

    public RelationshipAuthorizationPlanner(
        RelationalEdOrgAuthorizationSubjectSelector edOrgAuthorizationSubjectSelector
    )
    {
        _edOrgAuthorizationSubjectSelector =
            edOrgAuthorizationSubjectSelector
            ?? throw new ArgumentNullException(nameof(edOrgAuthorizationSubjectSelector));
    }

    public RelationshipAuthorizationResult PlanStoredValues(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext
    ) =>
        PlanValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            authorizationContext,
            RelationshipAuthorizationValueSource.Stored,
            CreateStoredCheckSpec,
            SupportedStrategyPlanCacheContext.Stored
        );

    public RelationshipAuthorizationResult PlanProposedValues(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        ResourceWritePlan writePlan
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        return PlanValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            authorizationContext,
            RelationshipAuthorizationValueSource.Proposed,
            CreateProposedCheckSpecFactory(resource, writePlan, ProposedValueOperationKind.CreateNew),
            SupportedStrategyPlanCacheContext.CreateProposed(ProposedValueOperationKind.CreateNew, writePlan)
        );
    }

    public RelationshipAuthorizationUpdatePlan PlanUpdateValues(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        ResourceWritePlan writePlan
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);
        ArgumentNullException.ThrowIfNull(authorizationContext);
        ArgumentNullException.ThrowIfNull(writePlan);

        var classification = RelationshipAuthorizationStrategyClassifier.Classify(
            mappingSet,
            resource,
            configuredAuthorizationStrategies
        );
        var createProposedCheckSpec = CreateProposedCheckSpecFactory(
            resource,
            writePlan,
            ProposedValueOperationKind.ExistingResource
        );
        var proposedCacheContext = SupportedStrategyPlanCacheContext.CreateProposed(
            ProposedValueOperationKind.ExistingResource,
            writePlan
        );

        return classification.Outcome switch
        {
            RelationshipAuthorizationClassificationOutcome.NoAuthorizationRequired => CreateUpdatePlan(
                new RelationshipAuthorizationResult.NoAuthorizationRequired(
                    configuredAuthorizationStrategies
                ),
                new RelationshipAuthorizationResult.NoAuthorizationRequired(configuredAuthorizationStrategies)
            ),
            RelationshipAuthorizationClassificationOutcome.NoFurtherAuthorizationRequired => CreateUpdatePlan(
                new RelationshipAuthorizationResult.NoFurtherAuthorizationRequired(
                    classification.NoFurtherAuthorizationRequiredStrategies
                ),
                new RelationshipAuthorizationResult.NoFurtherAuthorizationRequired(
                    classification.NoFurtherAuthorizationRequiredStrategies
                )
            ),
            RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled =>
                PlanKnownButNotEnabledUpdateStrategies(
                    mappingSet,
                    resource,
                    classification,
                    authorizationContext,
                    createProposedCheckSpec,
                    proposedCacheContext
                ),
            RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError =>
                PlanSecurityConfigurationUpdateFailures(
                    mappingSet,
                    resource,
                    classification,
                    authorizationContext,
                    createProposedCheckSpec,
                    proposedCacheContext
                ),
            RelationshipAuthorizationClassificationOutcome.SupportedStrategies =>
                PlanSupportedUpdateStrategies(
                    mappingSet,
                    resource,
                    classification.SupportedStrategies,
                    authorizationContext,
                    createProposedCheckSpec,
                    proposedCacheContext
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported relationship authorization classification outcome '{classification.Outcome}'."
            ),
        };
    }

    private RelationshipAuthorizationResult PlanValues(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        SupportedStrategyPlanCacheContext cacheContext
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);
        ArgumentNullException.ThrowIfNull(authorizationContext);
        ArgumentNullException.ThrowIfNull(createCheckSpec);

        var classification = RelationshipAuthorizationStrategyClassifier.Classify(
            mappingSet,
            resource,
            configuredAuthorizationStrategies
        );

        return PlanValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            authorizationContext,
            valueSource,
            createCheckSpec,
            cacheContext,
            classification
        );
    }

    private RelationshipAuthorizationResult PlanValues(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        SupportedStrategyPlanCacheContext cacheContext,
        RelationshipAuthorizationClassification classification
    )
    {
        ArgumentNullException.ThrowIfNull(classification);

        return classification.Outcome switch
        {
            RelationshipAuthorizationClassificationOutcome.NoAuthorizationRequired =>
                new RelationshipAuthorizationResult.NoAuthorizationRequired(
                    configuredAuthorizationStrategies
                ),
            RelationshipAuthorizationClassificationOutcome.NoFurtherAuthorizationRequired =>
                new RelationshipAuthorizationResult.NoFurtherAuthorizationRequired(
                    classification.NoFurtherAuthorizationRequiredStrategies
                ),
            RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled =>
                PlanKnownButNotEnabledStrategies(
                    mappingSet,
                    resource,
                    classification,
                    authorizationContext,
                    valueSource,
                    createCheckSpec,
                    cacheContext
                ),
            RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError =>
                PlanSecurityConfigurationFailures(
                    mappingSet,
                    resource,
                    classification,
                    authorizationContext,
                    valueSource,
                    createCheckSpec,
                    cacheContext
                ),
            RelationshipAuthorizationClassificationOutcome.SupportedStrategies => PlanSupportedStrategies(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                valueSource,
                createCheckSpec,
                cacheContext
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relationship authorization classification outcome '{classification.Outcome}'."
            ),
        };
    }

    private RelationshipAuthorizationResult PlanKnownButNotEnabledStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        SupportedStrategyPlanCacheContext cacheContext
    ) =>
        PlanKnownButNotEnabledClassification(
            resource,
            classification,
            additionalFailures =>
                TryPlanSupportedStrategySecurityConfigurationFailures(
                    classification.SupportedStrategies,
                    additionalFailures,
                    supportedStrategies =>
                        PlanSupportedStrategies(
                            mappingSet,
                            resource,
                            supportedStrategies,
                            authorizationContext,
                            valueSource,
                            createCheckSpec,
                            cacheContext
                        ),
                    GetSecurityConfigurationFailures,
                    CreateSecurityConfigurationResult
                ),
            static knownButNotEnabledFailures => new RelationshipAuthorizationResult.KnownButNotEnabled(
                knownButNotEnabledFailures
            )
        );

    private RelationshipAuthorizationResult PlanSecurityConfigurationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        SupportedStrategyPlanCacheContext cacheContext
    ) =>
        PlanSecurityConfigurationClassification(
            resource,
            classification,
            additionalFailures =>
                TryPlanSupportedStrategySecurityConfigurationFailures(
                    classification.SupportedStrategies,
                    additionalFailures,
                    supportedStrategies =>
                        PlanSupportedStrategies(
                            mappingSet,
                            resource,
                            supportedStrategies,
                            authorizationContext,
                            valueSource,
                            createCheckSpec,
                            cacheContext
                        ),
                    GetSecurityConfigurationFailures,
                    CreateSecurityConfigurationResult
                ),
            CreateSecurityConfigurationResult
        );

    private RelationshipAuthorizationUpdatePlan PlanKnownButNotEnabledUpdateStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createProposedCheckSpec,
        SupportedStrategyPlanCacheContext proposedCacheContext
    ) =>
        PlanKnownButNotEnabledClassification(
            resource,
            classification,
            additionalFailures =>
                TryPlanSupportedStrategySecurityConfigurationFailures(
                    classification.SupportedStrategies,
                    additionalFailures,
                    supportedStrategies =>
                        PlanSupportedUpdateStrategies(
                            mappingSet,
                            resource,
                            supportedStrategies,
                            authorizationContext,
                            createProposedCheckSpec,
                            proposedCacheContext
                        ),
                    static updatePlan => updatePlan.SecurityConfigurationFailures,
                    CreateSecurityConfigurationUpdatePlan
                ),
            CreateKnownButNotEnabledUpdatePlan
        );

    private RelationshipAuthorizationUpdatePlan PlanSecurityConfigurationUpdateFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createProposedCheckSpec,
        SupportedStrategyPlanCacheContext proposedCacheContext
    ) =>
        PlanSecurityConfigurationClassification(
            resource,
            classification,
            additionalFailures =>
                TryPlanSupportedStrategySecurityConfigurationFailures(
                    classification.SupportedStrategies,
                    additionalFailures,
                    supportedStrategies =>
                        PlanSupportedUpdateStrategies(
                            mappingSet,
                            resource,
                            supportedStrategies,
                            authorizationContext,
                            createProposedCheckSpec,
                            proposedCacheContext
                        ),
                    static updatePlan => updatePlan.SecurityConfigurationFailures,
                    CreateSecurityConfigurationUpdatePlan
                ),
            CreateSecurityConfigurationUpdatePlan
        );

    private static TPlan PlanKnownButNotEnabledClassification<TPlan>(
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        Func<
            IReadOnlyList<RelationshipAuthorizationFailureMetadata>,
            TPlan?
        > tryPlanSupportedSecurityConfigurationFailures,
        Func<IReadOnlyList<RelationshipAuthorizationFailureMetadata>, TPlan> createKnownButNotEnabledPlan
    )
        where TPlan : class
    {
        var knownButNotEnabledFailures = CreateKnownButNotEnabledFailures(
            resource,
            classification.KnownButNotEnabledStrategies
        );

        return tryPlanSupportedSecurityConfigurationFailures(knownButNotEnabledFailures)
            ?? createKnownButNotEnabledPlan(knownButNotEnabledFailures);
    }

    private static TPlan PlanSecurityConfigurationClassification<TPlan>(
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        Func<
            IReadOnlyList<RelationshipAuthorizationFailureMetadata>,
            TPlan?
        > tryPlanSupportedSecurityConfigurationFailures,
        Func<IReadOnlyList<RelationshipAuthorizationFailureMetadata>, TPlan> createSecurityConfigurationPlan
    )
        where TPlan : class
    {
        var classificationFailures = CombineAndOrderFailures(
            classification.SecurityConfigurationFailures,
            CreateKnownButNotEnabledFailures(resource, classification.KnownButNotEnabledStrategies)
        );

        return tryPlanSupportedSecurityConfigurationFailures(classificationFailures)
            ?? createSecurityConfigurationPlan(classificationFailures);
    }

    private static TPlan? TryPlanSupportedStrategySecurityConfigurationFailures<TPlan>(
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> additionalFailures,
        Func<IReadOnlyList<SupportedRelationshipAuthorizationStrategy>, TPlan> planSupportedStrategies,
        Func<TPlan, IReadOnlyList<RelationshipAuthorizationFailureMetadata>> getSecurityConfigurationFailures,
        Func<IReadOnlyList<RelationshipAuthorizationFailureMetadata>, TPlan> createSecurityConfigurationPlan
    )
        where TPlan : class
    {
        if (supportedStrategies.Count == 0)
        {
            return null;
        }

        var supportedPlanningResult = planSupportedStrategies(supportedStrategies);
        var securityConfigurationFailures = getSecurityConfigurationFailures(supportedPlanningResult);

        return securityConfigurationFailures.Count == 0
            ? null
            : createSecurityConfigurationPlan(
                CombineAndOrderFailures(securityConfigurationFailures, additionalFailures)
            );
    }

    private RelationshipAuthorizationUpdatePlan PlanSupportedUpdateStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createProposedCheckSpec,
        SupportedStrategyPlanCacheContext proposedCacheContext
    )
    {
        var storedPlanningResult = GetSupportedStrategyPlan(
            mappingSet,
            resource,
            supportedStrategies,
            RelationshipAuthorizationValueSource.Stored,
            CreateStoredCheckSpec,
            SupportedStrategyPlanCacheContext.Stored
        );
        var selectedSubjects = storedPlanningResult.SubjectSelectionResult;
        var missingPeopleAuthViewFailures = CreateMissingPeopleAuthViewAssociationFailures(
            mappingSet,
            resource,
            selectedSubjects.StrategySubjects,
            null
        );

        if (selectedSubjects.Failures.Count > 0)
        {
            return CreateSecurityConfigurationUpdatePlan([
                .. OrderFailures([
                    .. ApplyExecutionMetadata(selectedSubjects.Failures, supportedStrategies, null),
                    .. missingPeopleAuthViewFailures,
                ]),
            ]);
        }

        if (missingPeopleAuthViewFailures.Count > 0)
        {
            return CreateSecurityConfigurationUpdatePlan(missingPeopleAuthViewFailures);
        }

        var proposedPlanningResult = GetSupportedStrategyPlan(
            mappingSet,
            resource,
            supportedStrategies,
            RelationshipAuthorizationValueSource.Proposed,
            createProposedCheckSpec,
            proposedCacheContext
        );

        return CreateUpdatePlan(
            PlanSelectedSupportedStrategies(
                mappingSet,
                resource,
                authorizationContext,
                RelationshipAuthorizationValueSource.Stored,
                storedPlanningResult
            ),
            PlanSelectedSupportedStrategies(
                mappingSet,
                resource,
                authorizationContext,
                RelationshipAuthorizationValueSource.Proposed,
                proposedPlanningResult
            )
        );
    }

    private RelationshipAuthorizationResult PlanSupportedStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        SupportedStrategyPlanCacheContext cacheContext
    )
    {
        var supportedStrategyPlan = GetSupportedStrategyPlan(
            mappingSet,
            resource,
            supportedStrategies,
            valueSource,
            createCheckSpec,
            cacheContext
        );
        var selectedSubjects = supportedStrategyPlan.SubjectSelectionResult;

        if (selectedSubjects.Failures.Count > 0)
        {
            var additionalPlanningFailures =
                valueSource is RelationshipAuthorizationValueSource.Proposed
                    ? CreateSelectedSupportedStrategySecurityConfigurationFailures(
                        mappingSet,
                        resource,
                        valueSource,
                        supportedStrategyPlan.PlanningResult
                    )
                    : CreateMissingPeopleAuthViewAssociationFailures(
                        mappingSet,
                        resource,
                        selectedSubjects.StrategySubjects,
                        valueSource
                    );

            return new RelationshipAuthorizationResult.SecurityConfigurationError([
                .. OrderFailures([
                    .. ApplyExecutionMetadata(selectedSubjects.Failures, supportedStrategies, valueSource),
                    .. additionalPlanningFailures,
                ]),
            ]);
        }

        return PlanSelectedSupportedStrategies(
            mappingSet,
            resource,
            authorizationContext,
            valueSource,
            supportedStrategyPlan
        );
    }

    private RelationshipAuthorizationSubjectSelectionResult SelectSupportedStrategySubjects(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies
    )
    {
        List<SupportedRelationshipAuthorizationStrategySubjects> strategySubjects = [];
        List<RelationshipAuthorizationFailureMetadata> failures = [];
        var peopleSubjectsByStrategy = SelectPeopleSubjectsByStrategy(
            mappingSet,
            resource,
            supportedStrategies
        );

        foreach (var supportedStrategy in supportedStrategies)
        {
            var edOrgSelection = SelectEdOrgSubjects(mappingSet, resource, supportedStrategy);
            var peopleSelection = SelectPeopleSubjects(supportedStrategy, peopleSubjectsByStrategy);
            List<RelationshipAuthorizationSubject> subjects =
            [
                .. edOrgSelection.Subjects,
                .. peopleSelection.Subjects,
            ];

            failures.AddRange(
                SelectHardSubjectSelectionFailures(edOrgSelection.Failures, peopleSelection.Subjects.Count)
            );
            failures.AddRange(
                SelectHardSubjectSelectionFailures(peopleSelection.Failures, edOrgSelection.Subjects.Count)
            );

            if (
                subjects.Count == 0
                && edOrgSelection.Failures.Count == 0
                && peopleSelection.Failures.Count == 0
            )
            {
                failures.AddRange(CreateNoApplicableSubjectFailures(resource, supportedStrategy));
            }

            if (subjects.Count > 0)
            {
                strategySubjects.Add(
                    new SupportedRelationshipAuthorizationStrategySubjects(
                        supportedStrategy,
                        subjects,
                        peopleSelection.SkippedContributors
                    )
                );
            }
        }

        return new RelationshipAuthorizationSubjectSelectionResult(
            strategySubjects,
            [.. OrderFailures(failures)]
        );
    }

    private RelationshipAuthorizationStrategySubjectSelection SelectEdOrgSubjects(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    )
    {
        if (!SelectsSubjectKind(supportedStrategy, SecurableElementKind.EducationOrganization))
        {
            return new RelationshipAuthorizationStrategySubjectSelection([], [], []);
        }

        var selection = _edOrgAuthorizationSubjectSelector.Select(mappingSet, resource, supportedStrategy);

        return selection.Outcome switch
        {
            RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success =>
                new RelationshipAuthorizationStrategySubjectSelection(selection.Subjects, [], []),
            RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError =>
                new RelationshipAuthorizationStrategySubjectSelection(
                    [],
                    selection.SecurityConfigurationFailures,
                    []
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported EdOrg relationship authorization subject selection outcome '{selection.Outcome}'."
            ),
        };
    }

    private static PeopleSubjectSelectionsByStrategy SelectPeopleSubjectsByStrategy(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies
    )
    {
        var peopleStrategies = supportedStrategies.Where(SelectsPeopleSubject).ToArray();

        if (peopleStrategies.Length == 0)
        {
            return PeopleSubjectSelectionsByStrategy.Empty;
        }

        var selection = RelationalPeopleAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            peopleStrategies
        );

        var subjectsByStrategy = selection
            .StrategySubjectSelections.GroupBy(static strategySelection =>
                CreateStrategyIdentity(
                    strategySelection.ConfiguredStrategy,
                    strategySelection.RelationshipLocalOrder
                )
            )
            .ToDictionary(
                static group => group.Key,
                static group =>
                    (IReadOnlyList<RelationshipAuthorizationSubject>)
                        group.SelectMany(static strategySelection => strategySelection.Subjects).ToArray()
            );
        var skippedContributorsByStrategy = selection
            .StrategySkippedContributors.Select(strategySkippedContributors => new
            {
                Identity = CreateStrategyIdentity(
                    strategySkippedContributors.ConfiguredStrategy,
                    strategySkippedContributors.RelationshipLocalOrder
                ),
                strategySkippedContributors.SkippedContributors,
            })
            .GroupBy(static strategySelection => strategySelection.Identity)
            .ToDictionary(
                static group => group.Key,
                static group =>
                    (IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor>)
                        group
                            .SelectMany(static strategySelection => strategySelection.SkippedContributors)
                            .Distinct()
                            .ToArray()
            );

        var failuresByStrategy = selection
            .SecurityConfigurationFailures.GroupBy(CreateStrategyIdentity)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<RelationshipAuthorizationFailureMetadata>)group.ToArray()
            );

        return new PeopleSubjectSelectionsByStrategy(
            subjectsByStrategy,
            failuresByStrategy,
            skippedContributorsByStrategy
        );
    }

    private static RelationshipAuthorizationStrategySubjectSelection SelectPeopleSubjects(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        PeopleSubjectSelectionsByStrategy peopleSubjectsByStrategy
    )
    {
        if (!SelectsPeopleSubject(supportedStrategy))
        {
            return new RelationshipAuthorizationStrategySubjectSelection([], [], []);
        }

        var strategyIdentity = CreateStrategyIdentity(
            supportedStrategy.ConfiguredStrategy,
            supportedStrategy.RelationshipLocalOrder
        );

        return new RelationshipAuthorizationStrategySubjectSelection(
            peopleSubjectsByStrategy.SubjectsByStrategy.GetValueOrDefault(strategyIdentity) ?? [],
            peopleSubjectsByStrategy.FailuresByStrategy.GetValueOrDefault(strategyIdentity) ?? [],
            peopleSubjectsByStrategy.SkippedContributorsByStrategy.GetValueOrDefault(strategyIdentity) ?? []
        );
    }

    private static RelationshipAuthorizationStrategyIdentity CreateStrategyIdentity(
        ConfiguredAuthorizationStrategy configuredStrategy,
        int relationshipLocalOrder
    ) => new(configuredStrategy.RawConfiguredIndex, relationshipLocalOrder);

    private static RelationshipAuthorizationStrategyIdentity CreateStrategyIdentity(
        RelationshipAuthorizationFailureMetadata failure
    )
    {
        if (
            failure.ConfiguredStrategy is not { } configuredStrategy
            || failure.RelationshipLocalOrder is not { } relationshipLocalOrder
        )
        {
            throw new InvalidOperationException(
                "People relationship authorization subject selection failures must identify their configured strategy."
            );
        }

        return CreateStrategyIdentity(configuredStrategy, relationshipLocalOrder);
    }

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> SelectHardSubjectSelectionFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures,
        int alternateSubjectCount
    ) =>
        failures.Where(failure =>
            failure.FailureKind is not RelationshipAuthorizationFailureKind.NoApplicableRootSubject
            || alternateSubjectCount == 0
        );

    private static RelationshipAuthorizationResult PlanSelectedSupportedStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        SupportedStrategyPlan supportedStrategyPlan
    )
    {
        var planningResult = supportedStrategyPlan.PlanningResult;

        if (planningResult.Failures.Count > 0)
        {
            var missingPeopleAuthViewPlanningFailures = CreateMissingPeopleAuthViewAssociationFailures(
                mappingSet,
                resource,
                planningResult.PeopleAuthViewCandidateStrategySubjects,
                valueSource
            );

            return new RelationshipAuthorizationResult.SecurityConfigurationError([
                .. OrderFailures([.. planningResult.Failures, .. missingPeopleAuthViewPlanningFailures]),
            ]);
        }

        var missingPeopleAuthViewFailures = CreateMissingPeopleAuthViewAssociationFailures(
            mappingSet,
            resource,
            planningResult.CheckSpecs
        );

        if (missingPeopleAuthViewFailures.Count > 0)
        {
            return new RelationshipAuthorizationResult.SecurityConfigurationError(
                missingPeopleAuthViewFailures
            );
        }

        if (authorizationContext.ClaimEducationOrganizationIds.Count == 0)
        {
            return new RelationshipAuthorizationResult.NoClaims(
                planningResult.CheckSpecs,
                CreateNoClaimsFailures(resource, planningResult.CheckSpecs)
            );
        }

        return new RelationshipAuthorizationResult.Authorized(
            planningResult.CheckSpecs,
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                mappingSet.Key.Dialect,
                authorizationContext.ClaimEducationOrganizationIds,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            supportedStrategyPlan.ExecutableShape
        );
    }

    private SupportedStrategyPlan GetSupportedStrategyPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        SupportedStrategyPlanCacheContext cacheContext
    )
    {
        var cache = _supportedStrategyPlanCachesByMappingSet.GetValue(
            mappingSet,
            static _ => new SupportedStrategyPlanCache()
        );
        var cacheKey = SupportedStrategyPlanCacheKey.Create(
            resource,
            supportedStrategies,
            valueSource,
            cacheContext
        );

        return cache.GetOrAdd(
            cacheKey,
            () => CreateSupportedStrategyPlan(mappingSet, resource, createCheckSpec, supportedStrategies)
        );
    }

    private SupportedStrategyPlan CreateSupportedStrategyPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        CreateCheckSpec createCheckSpec,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies
    )
    {
        var subjectSelectionResult = SelectSupportedStrategySubjects(
            mappingSet,
            resource,
            supportedStrategies
        );
        var planningResult = CreateSelectedSupportedStrategyPlanningResult(
            mappingSet,
            resource,
            createCheckSpec,
            subjectSelectionResult.StrategySubjects
        );

        return new SupportedStrategyPlan(
            subjectSelectionResult,
            planningResult,
            RelationshipAuthorizationExecutableShape.Create(planningResult.CheckSpecs)
        );
    }

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateSelectedSupportedStrategySecurityConfigurationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationValueSource valueSource,
        SelectedSupportedStrategyPlanningResult planningResult
    )
    {
        if (planningResult.Failures.Count > 0)
        {
            var missingPeopleAuthViewPlanningFailures = CreateMissingPeopleAuthViewAssociationFailures(
                mappingSet,
                resource,
                planningResult.PeopleAuthViewCandidateStrategySubjects,
                valueSource
            );

            return [.. OrderFailures([.. planningResult.Failures, .. missingPeopleAuthViewPlanningFailures])];
        }

        return CreateMissingPeopleAuthViewAssociationFailures(
            mappingSet,
            resource,
            planningResult.CheckSpecs
        );
    }

    private static SelectedSupportedStrategyPlanningResult CreateSelectedSupportedStrategyPlanningResult(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        CreateCheckSpec createCheckSpec,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> strategySubjects
    )
    {
        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var rootTableModel = concreteResourceModel.RelationalModel.Root;
        var rootTable = rootTableModel.Table;
        var rootDocumentIdColumn = GetRootDocumentIdColumn(
            rootTableModel,
            "stored relationship authorization planning"
        );

        List<RelationshipAuthorizationCheckSpec> checkSpecs = [];
        List<RelationshipAuthorizationFailureMetadata> planningFailures = [];
        List<SupportedRelationshipAuthorizationStrategySubjects> peopleAuthViewCandidateStrategySubjects = [];

        foreach (var selectedStrategySubjects in strategySubjects)
        {
            var supportedStrategy = selectedStrategySubjects.Strategy;
            var checkSpecResult = createCheckSpec(
                rootTable,
                rootDocumentIdColumn,
                selectedStrategySubjects.Subjects,
                supportedStrategy,
                selectedStrategySubjects.SkippedContributors
            );

            if (checkSpecResult.PeopleAuthViewCandidateSubjects.Count > 0)
            {
                peopleAuthViewCandidateStrategySubjects.Add(
                    new SupportedRelationshipAuthorizationStrategySubjects(
                        supportedStrategy,
                        checkSpecResult.PeopleAuthViewCandidateSubjects,
                        []
                    )
                );
            }

            if (checkSpecResult.Failures.Count > 0)
            {
                planningFailures.AddRange(checkSpecResult.Failures);
                continue;
            }

            if (checkSpecResult.CheckSpec is not null)
            {
                checkSpecs.Add(checkSpecResult.CheckSpec);
            }
        }

        return new SelectedSupportedStrategyPlanningResult(
            checkSpecs,
            planningFailures,
            peopleAuthViewCandidateStrategySubjects
        );
    }

    private static CheckSpecCreationResult CreateStoredCheckSpec(
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects,
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> skippedContributors
    ) =>
        new(
            new RelationshipAuthorizationCheckSpec(
                supportedStrategy.ConfiguredStrategy,
                supportedStrategy.RelationshipLocalOrder,
                supportedStrategy.Direction,
                RelationshipAuthorizationValueSource.Stored,
                subjects,
                new RelationshipAuthorizationCheckTarget.Stored(rootTable, rootDocumentIdColumn)
            )
            {
                SkippedContributors = skippedContributors,
            },
            [],
            subjects
        );

    private static CreateCheckSpec CreateProposedCheckSpecFactory(
        QualifiedResourceName resource,
        ResourceWritePlan writePlan,
        ProposedValueOperationKind operationKind
    )
    {
        if (writePlan.Model.Resource != resource)
        {
            throw new InvalidOperationException(
                $"Write plan resource '{writePlan.Model.Resource.ProjectName}.{writePlan.Model.Resource.ResourceName}' does not match requested resource '{resource.ProjectName}.{resource.ResourceName}'."
            );
        }

        var rootTablePlan = GetRootTableWritePlan(writePlan);
        var rootTable = rootTablePlan.TableModel.Table;
        var bindingIndexByColumn = rootTablePlan
            .ColumnBindings.Select(static (binding, index) => (binding, index))
            .ToDictionary(
                static entry => entry.binding.Column.ColumnName,
                static entry => (entry.index, entry.binding),
                EqualityComparer<DbColumnName>.Default
            );

        return (_, rootDocumentIdColumn, subjects, supportedStrategy, skippedContributors) =>
        {
            List<RelationshipAuthorizationFailureMetadata> failures = [];
            List<RelationshipAuthorizationSubject> executableSubjects = [];
            List<RelationshipAuthorizationSubject> peopleAuthViewCandidateSubjects = [];
            List<RelationshipAuthorizationProposedValueBinding> proposedBindings = [];
            List<RelationshipAuthorizationIneligibleSubject> ineligibleSubjects = [];

            foreach (var subject in subjects)
            {
                if (
                    IsSelfPersonCreateNewSubject(subject, operationKind) && subject.PersonMetadata is not null
                )
                {
                    ineligibleSubjects.Add(CreateSelfPersonCreateNewIneligibleSubject(subject));
                    continue;
                }

                if (subject.PersonMetadata is not null)
                {
                    peopleAuthViewCandidateSubjects.Add(subject);
                }

                var anchor = CreateProposedAnchor(subject, rootTable, rootDocumentIdColumn, operationKind);

                if (!anchor.Table.Equals(rootTable))
                {
                    failures.AddRange(
                        CreateMissingProposedRootBindingFailures(
                            subject,
                            resource,
                            supportedStrategy,
                            anchor.Table,
                            anchor.Column
                        )
                    );
                    continue;
                }

                if (!bindingIndexByColumn.TryGetValue(anchor.Column, out var bindingEntry))
                {
                    failures.AddRange(
                        CreateMissingProposedRootBindingFailures(
                            subject,
                            resource,
                            supportedStrategy,
                            anchor.Table,
                            anchor.Column
                        )
                    );
                    continue;
                }

                var proposedBinding = new RelationshipAuthorizationProposedValueBinding(
                    anchor.Table,
                    anchor.Column,
                    bindingEntry.index,
                    anchor.Column.Value,
                    bindingEntry.binding.ParameterName
                );

                proposedBindings.Add(proposedBinding);
                executableSubjects.Add(
                    ApplyProposedAnchorToPersonSubject(subject, anchor.Kind, proposedBinding)
                );
            }

            if (failures.Count > 0)
            {
                return new CheckSpecCreationResult(null, failures, peopleAuthViewCandidateSubjects);
            }

            if (executableSubjects.Count == 0 && ineligibleSubjects.Count > 0)
            {
                return new CheckSpecCreationResult(
                    null,
                    [
                        CreateNoExecutableSubjectsFailure(
                            resource,
                            supportedStrategy,
                            ineligibleSubjects,
                            skippedContributors
                        ),
                    ],
                    peopleAuthViewCandidateSubjects
                );
            }

            return new CheckSpecCreationResult(
                new RelationshipAuthorizationCheckSpec(
                    supportedStrategy.ConfiguredStrategy,
                    supportedStrategy.RelationshipLocalOrder,
                    supportedStrategy.Direction,
                    RelationshipAuthorizationValueSource.Proposed,
                    executableSubjects,
                    new RelationshipAuthorizationCheckTarget.Proposed(rootTable, proposedBindings)
                )
                {
                    IneligibleSubjects = ineligibleSubjects,
                    SkippedContributors = skippedContributors,
                },
                [],
                peopleAuthViewCandidateSubjects
            );
        };
    }

    private static bool IsSelfPersonCreateNewSubject(
        RelationshipAuthorizationSubject subject,
        ProposedValueOperationKind operationKind
    ) =>
        operationKind is ProposedValueOperationKind.CreateNew
        && subject.PersonMetadata?.Path.Kind
            is RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId;

    private static RelationshipAuthorizationIneligibleSubject CreateSelfPersonCreateNewIneligibleSubject(
        RelationshipAuthorizationSubject subject
    ) =>
        new(
            subject,
            RelationshipAuthorizationSubjectIneligibilityReason.SelfPersonDocumentIdUnavailableForCreateNew,
            "Self person DocumentId is unavailable to People relationship auth views during POST create-new."
        );

    private static ProposedAnchor CreateProposedAnchor(
        RelationshipAuthorizationSubject subject,
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        ProposedValueOperationKind operationKind
    )
    {
        var personMetadata = subject.PersonMetadata;

        if (personMetadata is null)
        {
            return new ProposedAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                subject.Table,
                subject.Column
            );
        }

        return personMetadata.Path.Kind switch
        {
            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn => CreatePathStepAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                personMetadata.Path.Steps[0]
            ),
            RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath => CreatePathStepAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                personMetadata.Path.Steps[0]
            ),
            RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId => new ProposedAnchor(
                operationKind is ProposedValueOperationKind.ExistingResource
                    ? RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId
                    : RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                rootTable,
                rootDocumentIdColumn
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(subject),
                personMetadata.Path.Kind,
                "Unsupported people relationship authorization proposed path kind."
            ),
        };
    }

    private static ProposedAnchor CreatePathStepAnchor(
        RelationshipAuthorizationPersonProposedAnchorKind anchorKind,
        ColumnPathStep step
    ) => new(anchorKind, step.SourceTable, step.SourceColumnName);

    private static RelationshipAuthorizationSubject ApplyProposedAnchorToPersonSubject(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonProposedAnchorKind anchorKind,
        RelationshipAuthorizationProposedValueBinding binding
    )
    {
        if (subject.PersonMetadata is not { } personMetadata)
        {
            return subject;
        }

        return subject with
        {
            PersonMetadata = personMetadata with
            {
                ProposedAnchor = new RelationshipAuthorizationPersonProposedAnchor(anchorKind, binding),
            },
        };
    }

    private static RelationshipAuthorizationFailureMetadata CreateNoExecutableSubjectsFailure(
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        IReadOnlyList<RelationshipAuthorizationIneligibleSubject> ineligibleSubjects,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> skippedContributors
    )
    {
        var firstSubject = ineligibleSubjects[0].Subject;
        var firstContributor = firstSubject.Contributors.FirstOrDefault();
        var authObject = firstSubject.AuthObject;
        var reasons = string.Join(
            ", ",
            ineligibleSubjects
                .Select(static ineligibleSubject => ineligibleSubject.Reason.ToString())
                .Distinct()
                .OrderBy(static reason => reason, StringComparer.Ordinal)
        );

        return new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.NoExecutableSubjects,
            resource,
            supportedStrategy.ConfiguredStrategy,
            supportedStrategy.RelationshipLocalOrder,
            ValueSource: RelationshipAuthorizationValueSource.Proposed,
            AuthObject: authObject,
            Location: new RelationshipAuthorizationFailureLocation(
                firstContributor?.Kind,
                firstContributor?.JsonPath,
                firstContributor?.ReadableName,
                firstSubject.Table,
                firstSubject.Column,
                authObject.Name.ToString()
            ),
            Hint: $"No executable relationship authorization subjects remain for strategy '{supportedStrategy.ConfiguredStrategy.StrategyName}' after operation eligibility filtering. Reasons: [{reasons}]."
        )
        {
            PersonMetadata = firstSubject.PersonMetadata is null
                ? null
                : CreatePersonFailureMetadata(firstSubject),
            Contributors =
            [
                .. ineligibleSubjects.SelectMany(static ineligible => ineligible.Subject.Contributors),
            ],
            IneligibleSubjects = ineligibleSubjects,
            SkippedContributors = skippedContributors,
        };
    }

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateKnownButNotEnabledFailures(
        QualifiedResourceName resource,
        IReadOnlyList<KnownButNotEnabledRelationshipAuthorizationStrategy> knownButNotEnabledStrategies
    ) =>
        [
            .. OrderFailures(
                knownButNotEnabledStrategies.Select(strategy =>
                {
                    var basisResource = strategy.BasisResource;

                    if (basisResource is null)
                    {
                        return new RelationshipAuthorizationFailureMetadata(
                            RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy,
                            resource,
                            strategy.ConfiguredStrategy,
                            strategy.RelationshipLocalOrder,
                            Hint: BuildKnownButNotEnabledHint(strategy)
                        );
                    }

                    var nonNullBasisResource = basisResource.Value;

                    return new RelationshipAuthorizationFailureMetadata(
                        RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy,
                        resource,
                        strategy.ConfiguredStrategy,
                        strategy.RelationshipLocalOrder,
                        Location: new RelationshipAuthorizationFailureLocation(
                            AuthorizationObjectName: $"{nonNullBasisResource.ProjectName}.{nonNullBasisResource.ResourceName}"
                        ),
                        Hint: BuildKnownButNotEnabledHint(strategy)
                    );
                })
            ),
        ];

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateNoClaimsFailures(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) => [.. checkSpecs.SelectMany(checkSpec => CreateNoClaimsFailures(resource, checkSpec))];

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateNoClaimsFailures(
        QualifiedResourceName resource,
        RelationshipAuthorizationCheckSpec checkSpec
    ) =>
        checkSpec
            .Subjects.GroupBy(static subject => subject.AuthObject)
            .Select(subjectGroup =>
                CreateNoClaimsFailure(resource, checkSpec, subjectGroup.Key, subjectGroup.ToArray())
            );

    private static RelationshipAuthorizationFailureMetadata CreateNoClaimsFailure(
        QualifiedResourceName resource,
        RelationshipAuthorizationCheckSpec checkSpec,
        RelationshipAuthorizationAuthObject authObject,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects
    )
    {
        var personSubject = subjects.FirstOrDefault(static subject => subject.PersonMetadata is not null);

        if (personSubject is null)
        {
            return new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                resource,
                checkSpec.ConfiguredStrategy,
                checkSpec.RelationshipLocalOrder,
                ValueSource: checkSpec.ValueSource,
                AuthObject: authObject,
                Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
            );
        }

        var personMetadata = personSubject.PersonMetadata!;
        var firstContributor = subjects.SelectMany(static subject => subject.Contributors).FirstOrDefault();

        return new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
            resource,
            checkSpec.ConfiguredStrategy,
            checkSpec.RelationshipLocalOrder,
            ValueSource: checkSpec.ValueSource,
            AuthObject: authObject,
            Location: new RelationshipAuthorizationFailureLocation(
                Kind: firstContributor?.Kind ?? MapPersonSecurableElementKind(personMetadata.PersonKind),
                JsonPath: firstContributor?.JsonPath,
                ReadableName: firstContributor?.ReadableName,
                Table: personSubject.Table,
                Column: personSubject.Column,
                AuthorizationObjectName: authObject.Name.ToString()
            ),
            Hint: BuildNoClaimsPeopleHint(authObject)
        )
        {
            PersonMetadata = CreatePersonFailureMetadata(personSubject),
            Contributors = [.. subjects.SelectMany(static subject => subject.Contributors)],
        };
    }

    private static string BuildNoClaimsPeopleHint(RelationshipAuthorizationAuthObject authObject) =>
        authObject.FailureHint is null
            ? $"Relationship authorization through auth view '{authObject.Name}' requires at least one claim EducationOrganizationId."
            : $"Relationship authorization through auth view '{authObject.Name}' requires at least one claim EducationOrganizationId. {authObject.FailureHint}";

    private static RelationshipAuthorizationPersonFailureMetadata CreatePersonFailureMetadata(
        RelationshipAuthorizationSubject subject
    )
    {
        var personMetadata =
            subject.PersonMetadata
            ?? throw new ArgumentException("Subject must include person metadata.", nameof(subject));

        return new RelationshipAuthorizationPersonFailureMetadata(
            personMetadata.PersonKind,
            subject.AuthObject,
            personMetadata.Path
        );
    }

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateMissingPeopleAuthViewAssociationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> strategySubjects,
        RelationshipAuthorizationValueSource? valueSource
    ) =>
        CreateMissingPeopleAuthViewAssociationFailures(
            mappingSet,
            resource,
            SelectStrategyPersonAuthViews(strategySubjects, valueSource)
        );

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateMissingPeopleAuthViewAssociationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) =>
        CreateMissingPeopleAuthViewAssociationFailures(
            mappingSet,
            resource,
            SelectStrategyPersonAuthViews(checkSpecs)
        );

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateMissingPeopleAuthViewAssociationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SelectedStrategyPersonAuthView> selectedPeopleAuthViews
    )
    {
        var peopleAuthViewAvailability = AuthObjectDefinitions.GetPeopleAuthViewAvailability(
            mappingSet.Model.AuthEdOrgHierarchy,
            mappingSet.Model.ConcreteResourcesInNameOrder
        );

        if (peopleAuthViewAvailability.IsAvailable)
        {
            return [];
        }

        if (selectedPeopleAuthViews.Count == 0)
        {
            return [];
        }

        var peopleAuthViewUnavailableReason = FormatPeopleAuthViewUnavailableReason(
            peopleAuthViewAvailability
        );

        return
        [
            .. OrderFailures(
                selectedPeopleAuthViews.Select(selectedPeopleAuthView =>
                    CreateMissingPeopleAuthViewAssociationFailure(
                        resource,
                        selectedPeopleAuthView,
                        peopleAuthViewUnavailableReason
                    )
                )
            ),
        ];
    }

    private static string FormatPeopleAuthViewUnavailableReason(
        PeopleAuthViewAvailability peopleAuthViewAvailability
    )
    {
        List<string> reasons = [];

        if (!peopleAuthViewAvailability.HasAuthEdOrgHierarchy)
        {
            reasons.Add("the auth EducationOrganization hierarchy was not emitted");
        }

        if (peopleAuthViewAvailability.MissingAssociationResourceNames.Count > 0)
        {
            reasons.Add(
                "missing required association resources: ["
                    + string.Join(", ", peopleAuthViewAvailability.MissingAssociationResourceNames)
                    + "]"
            );
        }

        return string.Join("; ", reasons);
    }

    private static RelationshipAuthorizationFailureMetadata CreateMissingPeopleAuthViewAssociationFailure(
        QualifiedResourceName resource,
        SelectedStrategyPersonAuthView selectedPeopleAuthView,
        string peopleAuthViewUnavailableReason
    )
    {
        var firstContributor = selectedPeopleAuthView.Contributors.FirstOrDefault();

        return new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations,
            resource,
            selectedPeopleAuthView.ConfiguredStrategy,
            selectedPeopleAuthView.RelationshipLocalOrder,
            ValueSource: selectedPeopleAuthView.ValueSource,
            AuthObject: selectedPeopleAuthView.AuthObject,
            Location: new RelationshipAuthorizationFailureLocation(
                Kind: firstContributor?.Kind ?? selectedPeopleAuthView.SecurableElementKind,
                JsonPath: firstContributor?.JsonPath,
                ReadableName: firstContributor?.ReadableName,
                AuthorizationObjectName: selectedPeopleAuthView.AuthObject.Name.ToString()
            ),
            Hint: $"Strategy '{selectedPeopleAuthView.ConfiguredStrategy.StrategyName}' selects {selectedPeopleAuthView.PersonKind} relationship authorization through auth view '{selectedPeopleAuthView.AuthObject.Name}', but the people auth views were not emitted for resource '{resource.ProjectName}.{resource.ResourceName}'. Reason: {peopleAuthViewUnavailableReason}."
        )
        {
            PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                selectedPeopleAuthView.PersonKind,
                selectedPeopleAuthView.AuthObject
            ),
            Contributors = selectedPeopleAuthView.Contributors,
        };
    }

    private static IReadOnlyList<SelectedStrategyPersonAuthView> SelectStrategyPersonAuthViews(
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> strategySubjects,
        RelationshipAuthorizationValueSource? valueSource
    ) =>
        MergeSelectedStrategyPersonAuthViews(
            strategySubjects.SelectMany(strategySubject =>
                SelectStrategyPersonAuthViews(
                    strategySubject.Subjects,
                    strategySubject.Strategy.ConfiguredStrategy,
                    strategySubject.Strategy.RelationshipLocalOrder,
                    valueSource
                )
            )
        );

    private static IReadOnlyList<SelectedStrategyPersonAuthView> SelectStrategyPersonAuthViews(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) =>
        MergeSelectedStrategyPersonAuthViews(
            checkSpecs.SelectMany(checkSpec =>
                SelectStrategyPersonAuthViews(
                    checkSpec.Subjects,
                    checkSpec.ConfiguredStrategy,
                    checkSpec.RelationshipLocalOrder,
                    checkSpec.ValueSource
                )
            )
        );

    private static IEnumerable<SelectedStrategyPersonAuthView> SelectStrategyPersonAuthViews(
        IEnumerable<RelationshipAuthorizationSubject> subjects,
        ConfiguredAuthorizationStrategy configuredStrategy,
        int relationshipLocalOrder,
        RelationshipAuthorizationValueSource? valueSource
    ) =>
        subjects
            .Where(static subject => subject.PersonMetadata is not null)
            .Select(subject =>
                CreateSelectedStrategyPersonAuthView(
                    subject,
                    configuredStrategy,
                    relationshipLocalOrder,
                    valueSource
                )
            );

    private static SelectedStrategyPersonAuthView CreateSelectedStrategyPersonAuthView(
        RelationshipAuthorizationSubject subject,
        ConfiguredAuthorizationStrategy configuredStrategy,
        int relationshipLocalOrder,
        RelationshipAuthorizationValueSource? valueSource
    )
    {
        var personMetadata =
            subject.PersonMetadata
            ?? throw new ArgumentException("Subject must include person metadata.", nameof(subject));

        return new SelectedStrategyPersonAuthView(
            configuredStrategy,
            relationshipLocalOrder,
            valueSource,
            SelectPersonSecurableElementKind(subject, personMetadata.PersonKind),
            personMetadata.PersonKind,
            subject.AuthObject,
            subject.Contributors
        );
    }

    private static IReadOnlyList<SelectedStrategyPersonAuthView> MergeSelectedStrategyPersonAuthViews(
        IEnumerable<SelectedStrategyPersonAuthView> selectedPeopleAuthViews
    ) =>
        [
            .. selectedPeopleAuthViews
                .GroupBy(CreateSelectedStrategyPersonAuthViewKey)
                .Select(static group =>
                {
                    var selected = group.First();
                    return selected with
                    {
                        Contributors =
                        [
                            .. group
                                .SelectMany(static groupedSelected => groupedSelected.Contributors)
                                .OrderBy(static contributor => contributor.ContributionOrder)
                                .ThenBy(static contributor => contributor.JsonPath, StringComparer.Ordinal)
                                .ThenBy(
                                    static contributor => contributor.ReadableName,
                                    StringComparer.Ordinal
                                ),
                        ],
                    };
                }),
        ];

    private static (
        int RawConfiguredIndex,
        int RelationshipLocalOrder,
        RelationshipAuthorizationValueSource? ValueSource,
        SecurableElementKind SecurableElementKind,
        RelationshipAuthorizationPersonKind PersonKind,
        DbTableName AuthObjectName,
        DbColumnName SubjectValueColumn
    ) CreateSelectedStrategyPersonAuthViewKey(SelectedStrategyPersonAuthView selected) =>
        (
            selected.ConfiguredStrategy.RawConfiguredIndex,
            selected.RelationshipLocalOrder,
            selected.ValueSource,
            selected.SecurableElementKind,
            selected.PersonKind,
            selected.AuthObject.Name,
            selected.AuthObject.SubjectValueColumn
        );

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateNoApplicableSubjectFailures(
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    ) =>
        supportedStrategy.EligibleSubjects.Select(eligibleSubject =>
        {
            var authObject = CreateEligibleSubjectAuthObject(supportedStrategy, eligibleSubject);

            return new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                resource,
                supportedStrategy.ConfiguredStrategy,
                supportedStrategy.RelationshipLocalOrder,
                AuthObject: authObject,
                Location: new RelationshipAuthorizationFailureLocation(
                    Kind: eligibleSubject.Kind,
                    AuthorizationObjectName: authObject?.Name.ToString()
                ),
                Hint: $"No applicable {eligibleSubject.Kind} relationship authorization subject was selected for strategy '{supportedStrategy.ConfiguredStrategy.StrategyName}'."
            )
            {
                PersonMetadata =
                    eligibleSubject.PersonAuthViewKind is null || authObject is null
                        ? null
                        : new RelationshipAuthorizationPersonFailureMetadata(
                            MapPersonKind(eligibleSubject.Kind),
                            authObject
                        ),
            };
        });

    private static RelationshipAuthorizationAuthObject? CreateEligibleSubjectAuthObject(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        RelationshipAuthorizationStrategySubjectEligibility eligibleSubject
    )
    {
        if (eligibleSubject.PersonAuthViewKind is { } personAuthViewKind)
        {
            return RelationshipAuthorizationAuthObject.CreatePerson(personAuthViewKind);
        }

        return eligibleSubject.Kind is SecurableElementKind.EducationOrganization
            ? RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(supportedStrategy.Direction)
            : null;
    }

    private static bool SelectsSubjectKind(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        SecurableElementKind kind
    ) => supportedStrategy.EligibleSubjects.Any(subject => subject.Kind == kind);

    private static bool SelectsPeopleSubject(SupportedRelationshipAuthorizationStrategy supportedStrategy) =>
        supportedStrategy.EligibleSubjects.Any(static subject => subject.PersonAuthViewKind is not null);

    private static bool IsPeopleSubjectKind(SecurableElementKind kind) =>
        kind is SecurableElementKind.Student or SecurableElementKind.Contact or SecurableElementKind.Staff;

    private static SecurableElementKind SelectPersonSecurableElementKind(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonKind personKind
    ) =>
        subject.Contributors.FirstOrDefault(contributor => IsPeopleSubjectKind(contributor.Kind))?.Kind
        ?? MapPersonSecurableElementKind(personKind);

    private static SecurableElementKind MapPersonSecurableElementKind(
        RelationshipAuthorizationPersonKind personKind
    ) =>
        personKind switch
        {
            RelationshipAuthorizationPersonKind.Student => SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Contact => SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Staff => SecurableElementKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(personKind),
                personKind,
                "Unsupported relationship authorization person kind."
            ),
        };

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CombineAndOrderFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> primaryFailures,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> secondaryFailures
    ) => [.. OrderFailures([.. primaryFailures, .. secondaryFailures])];

    private static RelationshipAuthorizationUpdatePlan CreateUpdatePlan(
        RelationshipAuthorizationResult storedValues,
        RelationshipAuthorizationResult proposedValues
    ) =>
        new(
            storedValues,
            proposedValues,
            CollectSecurityConfigurationFailures(storedValues, proposedValues),
            CollectKnownButNotEnabledFailures(storedValues, proposedValues)
        );

    private static RelationshipAuthorizationResult CreateSecurityConfigurationResult(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    ) => new RelationshipAuthorizationResult.SecurityConfigurationError(failures);

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> GetSecurityConfigurationFailures(
        RelationshipAuthorizationResult result
    ) =>
        result is RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError
            ? securityConfigurationError.Failures
            : [];

    private static RelationshipAuthorizationUpdatePlan CreateSecurityConfigurationUpdatePlan(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> orderedFailures =
        [
            .. OrderFailures(failures.DistinctBy(CreateUpdateFailureIdentity)),
        ];
        var result = new RelationshipAuthorizationResult.SecurityConfigurationError(orderedFailures);

        return new RelationshipAuthorizationUpdatePlan(result, result, orderedFailures, []);
    }

    private static RelationshipAuthorizationUpdatePlan CreateKnownButNotEnabledUpdatePlan(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> orderedFailures =
        [
            .. OrderFailures(failures.DistinctBy(CreateUpdateFailureIdentity)),
        ];
        var result = new RelationshipAuthorizationResult.KnownButNotEnabled(orderedFailures);

        return new RelationshipAuthorizationUpdatePlan(result, result, [], orderedFailures);
    }

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CollectSecurityConfigurationFailures(
        RelationshipAuthorizationResult storedValues,
        RelationshipAuthorizationResult proposedValues
    ) =>
        [
            .. OrderFailures(
                new[] { storedValues, proposedValues }
                    .OfType<RelationshipAuthorizationResult.SecurityConfigurationError>()
                    .SelectMany(static result => result.Failures)
                    .DistinctBy(CreateUpdateFailureIdentity)
            ),
        ];

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CollectKnownButNotEnabledFailures(
        RelationshipAuthorizationResult storedValues,
        RelationshipAuthorizationResult proposedValues
    ) =>
        [
            .. OrderFailures(
                new[] { storedValues, proposedValues }
                    .OfType<RelationshipAuthorizationResult.KnownButNotEnabled>()
                    .SelectMany(static result => result.Failures)
                    .DistinctBy(CreateUpdateFailureIdentity)
            ),
        ];

    private static (
        RelationshipAuthorizationFailureKind FailureKind,
        QualifiedResourceName Resource,
        ConfiguredAuthorizationStrategy? ConfiguredStrategy,
        int? RelationshipLocalOrder,
        RelationshipAuthorizationValueSource? ValueSource,
        RelationshipAuthorizationAuthObject? AuthObject,
        RelationshipAuthorizationPersonFailureMetadata? PersonMetadata,
        RelationshipAuthorizationFailureLocation? Location,
        string? Hint
    ) CreateUpdateFailureIdentity(RelationshipAuthorizationFailureMetadata failure) =>
        (
            failure.FailureKind,
            failure.Resource,
            failure.ConfiguredStrategy,
            failure.RelationshipLocalOrder,
            failure.ValueSource,
            failure.AuthObject,
            failure.PersonMetadata,
            failure.Location,
            failure.Hint
        );

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateMissingProposedRootBindingFailures(
        RelationshipAuthorizationSubject subject,
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        DbTableName? table = null,
        DbColumnName? column = null
    ) =>
        subject.Contributors.Select(contributor => new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.MissingProposedRootBinding,
            resource,
            supportedStrategy.ConfiguredStrategy,
            supportedStrategy.RelationshipLocalOrder,
            ValueSource: RelationshipAuthorizationValueSource.Proposed,
            AuthObject: subject.AuthObject,
            Location: new RelationshipAuthorizationFailureLocation(
                contributor.Kind,
                contributor.JsonPath,
                contributor.ReadableName,
                table ?? subject.Table,
                column ?? subject.Column
            ),
            Hint: "Selected root-table authorization subject does not have a matching root write binding."
        )
        {
            PersonMetadata = subject.PersonMetadata is null ? null : CreatePersonFailureMetadata(subject),
            Contributors = [contributor],
        });

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> ApplyExecutionMetadata(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationshipAuthorizationValueSource? valueSource
    )
    {
        var strategyByIdentity = supportedStrategies.ToDictionary(
            static strategy =>
                (strategy.ConfiguredStrategy.RawConfiguredIndex, strategy.RelationshipLocalOrder),
            static strategy => strategy
        );

        return
        [
            .. failures.Select(failure =>
            {
                var rawConfiguredIndex = failure.ConfiguredStrategy?.RawConfiguredIndex;
                var relationshipLocalOrder = failure.RelationshipLocalOrder;

                if (
                    rawConfiguredIndex is null
                    || relationshipLocalOrder is null
                    || !strategyByIdentity.TryGetValue(
                        (rawConfiguredIndex.Value, relationshipLocalOrder.Value),
                        out var strategy
                    )
                )
                {
                    return failure with { ValueSource = failure.ValueSource ?? valueSource };
                }

                return failure with
                {
                    ValueSource = failure.ValueSource ?? valueSource,
                    AuthObject = failure.AuthObject ?? CreateFailureAuthObject(failure, strategy),
                };
            }),
        ];
    }

    private static RelationshipAuthorizationAuthObject CreateFailureAuthObject(
        RelationshipAuthorizationFailureMetadata failure,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    )
    {
        var failureKind = failure.Location?.Kind;

        if (failureKind is not null && IsPeopleSubjectKind(failureKind.Value))
        {
            var eligibleSubject = supportedStrategy.EligibleSubjects.FirstOrDefault(subject =>
                subject.Kind == failureKind.Value && subject.PersonAuthViewKind is not null
            );

            if (eligibleSubject?.PersonAuthViewKind is { } personAuthViewKind)
            {
                return RelationshipAuthorizationAuthObject.CreatePerson(personAuthViewKind);
            }
        }

        return RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(supportedStrategy.Direction);
    }

    private static string BuildKnownButNotEnabledHint(
        KnownButNotEnabledRelationshipAuthorizationStrategy strategy
    )
    {
        var basisResource = strategy.BasisResource;

        if (basisResource is null)
        {
            return $"Strategy '{strategy.ConfiguredStrategy.StrategyName}' is known but not enabled by the shared relationship authorization core.";
        }

        var nonNullBasisResource = basisResource.Value;

        return $"Strategy '{strategy.ConfiguredStrategy.StrategyName}' is known but not enabled by the shared relationship authorization core. Basis resource: '{nonNullBasisResource.ProjectName}.{nonNullBasisResource.ResourceName}'.";
    }

    private static TableWritePlan GetRootTableWritePlan(ResourceWritePlan writePlan)
    {
        var rootPlans = writePlan
            .TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind is DbTableKind.Root
            )
            .Take(2)
            .ToArray();

        return rootPlans.Length switch
        {
            1 => rootPlans[0],
            0 => throw new InvalidOperationException(
                $"Write plan for resource '{writePlan.Model.Resource.ProjectName}.{writePlan.Model.Resource.ResourceName}' does not contain a root table plan."
            ),
            _ => throw new InvalidOperationException(
                $"Write plan for resource '{writePlan.Model.Resource.ProjectName}.{writePlan.Model.Resource.ResourceName}' contains multiple root table plans."
            ),
        };
    }

    private enum ProposedValueOperationKind
    {
        CreateNew,
        ExistingResource,
    }

    private sealed record ProposedAnchor(
        RelationshipAuthorizationPersonProposedAnchorKind Kind,
        DbTableName Table,
        DbColumnName Column
    );

    private delegate CheckSpecCreationResult CreateCheckSpec(
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects,
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> skippedContributors
    );

    private sealed record CheckSpecCreationResult(
        RelationshipAuthorizationCheckSpec? CheckSpec,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<RelationshipAuthorizationSubject> PeopleAuthViewCandidateSubjects
    );

    private sealed record SelectedSupportedStrategyPlanningResult(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> PeopleAuthViewCandidateStrategySubjects
    );

    private sealed record SupportedStrategyPlan(
        RelationshipAuthorizationSubjectSelectionResult SubjectSelectionResult,
        SelectedSupportedStrategyPlanningResult PlanningResult,
        RelationshipAuthorizationExecutableShape ExecutableShape
    );

    private sealed class SupportedStrategyPlanCache
    {
        private readonly ConcurrentDictionary<
            SupportedStrategyPlanCacheKey,
            Lazy<SupportedStrategyPlan>
        > _plansByKey = new();
        private int _planCount;

        public SupportedStrategyPlan GetOrAdd(
            SupportedStrategyPlanCacheKey cacheKey,
            Func<SupportedStrategyPlan> createPlan
        )
        {
            if (_plansByKey.TryGetValue(cacheKey, out var cachedPlan))
            {
                return GetCachedPlan(cacheKey, cachedPlan);
            }

            // The cache is already scoped by MappingSet, but a MappingSet can still see many
            // one-off claim-set or write-plan shapes. Keep hot plans bounded and compile rare
            // overflow shapes uncached so they do not live for the MappingSet lifetime.
            if (!TryReserveCacheSlot())
            {
                return createPlan();
            }

            var lazyPlan = new Lazy<SupportedStrategyPlan>(
                createPlan,
                LazyThreadSafetyMode.ExecutionAndPublication
            );
            cachedPlan = _plansByKey.GetOrAdd(cacheKey, lazyPlan);

            if (!ReferenceEquals(cachedPlan, lazyPlan))
            {
                ReleaseCacheSlot();
            }

            return GetCachedPlan(cacheKey, cachedPlan);
        }

        private bool TryReserveCacheSlot()
        {
            while (true)
            {
                var planCount = Volatile.Read(ref _planCount);

                if (planCount >= MaxCachedSupportedStrategyPlans)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _planCount, planCount + 1, planCount) == planCount)
                {
                    return true;
                }
            }
        }

        private void ReleaseCacheSlot()
        {
            Interlocked.Decrement(ref _planCount);
        }

        private SupportedStrategyPlan GetCachedPlan(
            SupportedStrategyPlanCacheKey cacheKey,
            Lazy<SupportedStrategyPlan> cachedPlan
        )
        {
            try
            {
                return cachedPlan.Value;
            }
            catch
            {
                Remove(cacheKey, cachedPlan);
                throw;
            }
        }

        private void Remove(SupportedStrategyPlanCacheKey cacheKey, Lazy<SupportedStrategyPlan> cachedPlan)
        {
            if (
                _plansByKey.TryRemove(
                    new KeyValuePair<SupportedStrategyPlanCacheKey, Lazy<SupportedStrategyPlan>>(
                        cacheKey,
                        cachedPlan
                    )
                )
            )
            {
                ReleaseCacheSlot();
            }
        }
    }

    private sealed class SupportedStrategyPlanCacheContext
    {
        private SupportedStrategyPlanCacheContext(
            ProposedValueOperationKind? proposedOperationKind,
            ResourceWritePlan? writePlan
        )
        {
            ProposedOperationKind = proposedOperationKind;
            WritePlan = writePlan;
        }

        public static SupportedStrategyPlanCacheContext Stored { get; } = new(null, null);

        public ProposedValueOperationKind? ProposedOperationKind { get; }

        public ResourceWritePlan? WritePlan { get; }

        public static SupportedStrategyPlanCacheContext CreateProposed(
            ProposedValueOperationKind operationKind,
            ResourceWritePlan writePlan
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);

            return new SupportedStrategyPlanCacheContext(operationKind, writePlan);
        }
    }

    private sealed class SupportedStrategyPlanCacheKey : IEquatable<SupportedStrategyPlanCacheKey>
    {
        private readonly SupportedStrategySignature[] _supportedStrategySignatures;
        private readonly ResourceWritePlan? _writePlan;
        private readonly int _writePlanIdentityHashCode;
        private readonly int _hashCode;

        private SupportedStrategyPlanCacheKey(
            QualifiedResourceName resource,
            RelationshipAuthorizationValueSource valueSource,
            ProposedValueOperationKind? proposedOperationKind,
            ResourceWritePlan? writePlan,
            SupportedStrategySignature[] supportedStrategySignatures
        )
        {
            Resource = resource;
            ValueSource = valueSource;
            ProposedOperationKind = proposedOperationKind;
            _writePlan = writePlan;
            _writePlanIdentityHashCode = writePlan is null ? 0 : RuntimeHelpers.GetHashCode(writePlan);
            _supportedStrategySignatures = supportedStrategySignatures;
            _hashCode = BuildHashCode();
        }

        private QualifiedResourceName Resource { get; }

        private RelationshipAuthorizationValueSource ValueSource { get; }

        private ProposedValueOperationKind? ProposedOperationKind { get; }

        public static SupportedStrategyPlanCacheKey Create(
            QualifiedResourceName resource,
            IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
            RelationshipAuthorizationValueSource valueSource,
            SupportedStrategyPlanCacheContext cacheContext
        )
        {
            ArgumentNullException.ThrowIfNull(supportedStrategies);
            ArgumentNullException.ThrowIfNull(cacheContext);

            return new SupportedStrategyPlanCacheKey(
                resource,
                valueSource,
                cacheContext.ProposedOperationKind,
                cacheContext.WritePlan,
                [.. supportedStrategies.Select(SupportedStrategySignature.Create)]
            );
        }

        public bool Equals(SupportedStrategyPlanCacheKey? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null
                && _hashCode == other._hashCode
                && Resource.Equals(other.Resource)
                && ValueSource == other.ValueSource
                && ProposedOperationKind == other.ProposedOperationKind
                && ReferenceEquals(_writePlan, other._writePlan)
                && SupportedStrategySignaturesEqual(
                    _supportedStrategySignatures,
                    other._supportedStrategySignatures
                );
        }

        public override bool Equals(object? obj) =>
            obj is SupportedStrategyPlanCacheKey other && Equals(other);

        public override int GetHashCode() => _hashCode;

        private int BuildHashCode()
        {
            HashCode hashCode = new();
            hashCode.Add(Resource);
            hashCode.Add(ValueSource);
            hashCode.Add(ProposedOperationKind);
            hashCode.Add(_writePlanIdentityHashCode);
            hashCode.Add(_supportedStrategySignatures.Length);

            foreach (var signature in _supportedStrategySignatures)
            {
                hashCode.Add(signature);
            }

            return hashCode.ToHashCode();
        }

        private static bool SupportedStrategySignaturesEqual(
            IReadOnlyList<SupportedStrategySignature> first,
            IReadOnlyList<SupportedStrategySignature> second
        )
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            for (var index = 0; index < first.Count; index++)
            {
                if (!first[index].Equals(second[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class SupportedStrategySignature : IEquatable<SupportedStrategySignature>
    {
        private readonly EligibleSubjectSignature[] _eligibleSubjectSignatures;
        private readonly int _hashCode;

        private SupportedStrategySignature(
            RelationshipAuthorizationStrategyKind kind,
            RelationshipAuthorizationHierarchyDirection direction,
            string strategyName,
            int rawConfiguredIndex,
            int relationshipLocalOrder,
            EligibleSubjectSignature[] eligibleSubjectSignatures
        )
        {
            Kind = kind;
            Direction = direction;
            StrategyName = strategyName;
            RawConfiguredIndex = rawConfiguredIndex;
            RelationshipLocalOrder = relationshipLocalOrder;
            _eligibleSubjectSignatures = eligibleSubjectSignatures;
            _hashCode = BuildHashCode();
        }

        private RelationshipAuthorizationStrategyKind Kind { get; }

        private RelationshipAuthorizationHierarchyDirection Direction { get; }

        private string StrategyName { get; }

        private int RawConfiguredIndex { get; }

        private int RelationshipLocalOrder { get; }

        public static SupportedStrategySignature Create(
            SupportedRelationshipAuthorizationStrategy supportedStrategy
        )
        {
            ArgumentNullException.ThrowIfNull(supportedStrategy);

            return new SupportedStrategySignature(
                supportedStrategy.Kind,
                supportedStrategy.Direction,
                supportedStrategy.ConfiguredStrategy.StrategyName,
                supportedStrategy.ConfiguredStrategy.RawConfiguredIndex,
                supportedStrategy.RelationshipLocalOrder,
                [.. supportedStrategy.EligibleSubjects.Select(EligibleSubjectSignature.Create)]
            );
        }

        public bool Equals(SupportedStrategySignature? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null
                && _hashCode == other._hashCode
                && Kind == other.Kind
                && Direction == other.Direction
                && string.Equals(StrategyName, other.StrategyName, StringComparison.Ordinal)
                && RawConfiguredIndex == other.RawConfiguredIndex
                && RelationshipLocalOrder == other.RelationshipLocalOrder
                && EligibleSubjectSignaturesEqual(
                    _eligibleSubjectSignatures,
                    other._eligibleSubjectSignatures
                );
        }

        public override bool Equals(object? obj) => obj is SupportedStrategySignature other && Equals(other);

        public override int GetHashCode() => _hashCode;

        private int BuildHashCode()
        {
            HashCode hashCode = new();
            hashCode.Add(Kind);
            hashCode.Add(Direction);
            hashCode.Add(StrategyName, StringComparer.Ordinal);
            hashCode.Add(RawConfiguredIndex);
            hashCode.Add(RelationshipLocalOrder);
            hashCode.Add(_eligibleSubjectSignatures.Length);

            foreach (var signature in _eligibleSubjectSignatures)
            {
                hashCode.Add(signature);
            }

            return hashCode.ToHashCode();
        }

        private static bool EligibleSubjectSignaturesEqual(
            IReadOnlyList<EligibleSubjectSignature> first,
            IReadOnlyList<EligibleSubjectSignature> second
        )
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            for (var index = 0; index < first.Count; index++)
            {
                if (!first[index].Equals(second[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly record struct EligibleSubjectSignature(
        SecurableElementKind Kind,
        RelationshipAuthorizationPersonAuthViewKind? PersonAuthViewKind
    )
    {
        public static EligibleSubjectSignature Create(
            RelationshipAuthorizationStrategySubjectEligibility subjectEligibility
        ) => new(subjectEligibility.Kind, subjectEligibility.PersonAuthViewKind);
    }

    private sealed record RelationshipAuthorizationSubjectSelectionResult(
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> StrategySubjects,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
    );

    private sealed record SupportedRelationshipAuthorizationStrategySubjects(
        SupportedRelationshipAuthorizationStrategy Strategy,
        IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors
    );

    private sealed record RelationshipAuthorizationStrategySubjectSelection(
        IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors
    );

    private readonly record struct RelationshipAuthorizationStrategyIdentity(
        int RawConfiguredIndex,
        int RelationshipLocalOrder
    );

    private sealed record PeopleSubjectSelectionsByStrategy(
        IReadOnlyDictionary<
            RelationshipAuthorizationStrategyIdentity,
            IReadOnlyList<RelationshipAuthorizationSubject>
        > SubjectsByStrategy,
        IReadOnlyDictionary<
            RelationshipAuthorizationStrategyIdentity,
            IReadOnlyList<RelationshipAuthorizationFailureMetadata>
        > FailuresByStrategy,
        IReadOnlyDictionary<
            RelationshipAuthorizationStrategyIdentity,
            IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor>
        > SkippedContributorsByStrategy
    )
    {
        public static PeopleSubjectSelectionsByStrategy Empty { get; } =
            new(
                new Dictionary<
                    RelationshipAuthorizationStrategyIdentity,
                    IReadOnlyList<RelationshipAuthorizationSubject>
                >(),
                new Dictionary<
                    RelationshipAuthorizationStrategyIdentity,
                    IReadOnlyList<RelationshipAuthorizationFailureMetadata>
                >(),
                new Dictionary<
                    RelationshipAuthorizationStrategyIdentity,
                    IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor>
                >()
            );
    }

    private sealed record SelectedStrategyPersonAuthView(
        ConfiguredAuthorizationStrategy ConfiguredStrategy,
        int RelationshipLocalOrder,
        RelationshipAuthorizationValueSource? ValueSource,
        SecurableElementKind SecurableElementKind,
        RelationshipAuthorizationPersonKind PersonKind,
        RelationshipAuthorizationAuthObject AuthObject,
        IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors
    );
}
