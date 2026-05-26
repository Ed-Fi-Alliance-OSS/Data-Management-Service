// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

public sealed class RelationshipAuthorizationPlanner
{
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
            CreateStoredCheckSpec
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
            CreateProposedCheckSpecFactory(resource, writePlan, ProposedValueOperationKind.CreateNew)
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
                    createProposedCheckSpec
                ),
            RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError =>
                PlanSecurityConfigurationUpdateFailures(
                    mappingSet,
                    resource,
                    classification,
                    authorizationContext,
                    createProposedCheckSpec
                ),
            RelationshipAuthorizationClassificationOutcome.SupportedStrategies =>
                PlanSupportedUpdateStrategies(
                    mappingSet,
                    resource,
                    classification.SupportedStrategies,
                    authorizationContext,
                    createProposedCheckSpec
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
        CreateCheckSpec createCheckSpec
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
                    createCheckSpec
                ),
            RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError =>
                PlanSecurityConfigurationFailures(
                    mappingSet,
                    resource,
                    classification,
                    authorizationContext,
                    valueSource,
                    createCheckSpec
                ),
            RelationshipAuthorizationClassificationOutcome.SupportedStrategies => PlanSupportedStrategies(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                valueSource,
                createCheckSpec
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
        CreateCheckSpec createCheckSpec
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
                            createCheckSpec
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
        CreateCheckSpec createCheckSpec
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
                            createCheckSpec
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
        CreateCheckSpec createProposedCheckSpec
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
                            createProposedCheckSpec
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
        CreateCheckSpec createProposedCheckSpec
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
                            createProposedCheckSpec
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
        CreateCheckSpec createProposedCheckSpec
    )
    {
        var selectedSubjects = SelectSupportedStrategySubjects(mappingSet, resource, supportedStrategies);
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

        return CreateUpdatePlan(
            PlanSelectedSupportedStrategies(
                mappingSet,
                resource,
                authorizationContext,
                CreateStoredCheckSpec,
                selectedSubjects.StrategySubjects
            ),
            PlanSelectedSupportedStrategies(
                mappingSet,
                resource,
                authorizationContext,
                createProposedCheckSpec,
                selectedSubjects.StrategySubjects
            )
        );
    }

    private RelationshipAuthorizationResult PlanSupportedStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec
    )
    {
        var selectedSubjects = SelectSupportedStrategySubjects(mappingSet, resource, supportedStrategies);

        if (selectedSubjects.Failures.Count > 0)
        {
            var missingPeopleAuthViewFailures = CreateMissingPeopleAuthViewAssociationFailures(
                mappingSet,
                resource,
                selectedSubjects.StrategySubjects,
                valueSource
            );

            return new RelationshipAuthorizationResult.SecurityConfigurationError([
                .. OrderFailures([
                    .. ApplyExecutionMetadata(selectedSubjects.Failures, supportedStrategies, valueSource),
                    .. missingPeopleAuthViewFailures,
                ]),
            ]);
        }

        return PlanSelectedSupportedStrategies(
            mappingSet,
            resource,
            authorizationContext,
            createCheckSpec,
            selectedSubjects.StrategySubjects
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

        foreach (var supportedStrategy in supportedStrategies)
        {
            var edOrgSelection = SelectEdOrgSubjects(mappingSet, resource, supportedStrategy);
            var peopleSelection = SelectPeopleSubjects(mappingSet, resource, supportedStrategy);
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
                    new SupportedRelationshipAuthorizationStrategySubjects(supportedStrategy, subjects)
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
            return new RelationshipAuthorizationStrategySubjectSelection([], []);
        }

        var selection = _edOrgAuthorizationSubjectSelector.Select(mappingSet, resource, supportedStrategy);

        return selection.Outcome switch
        {
            RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success =>
                new RelationshipAuthorizationStrategySubjectSelection(selection.Subjects, []),
            RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError =>
                new RelationshipAuthorizationStrategySubjectSelection(
                    [],
                    selection.SecurityConfigurationFailures
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported EdOrg relationship authorization subject selection outcome '{selection.Outcome}'."
            ),
        };
    }

    private static RelationshipAuthorizationStrategySubjectSelection SelectPeopleSubjects(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    )
    {
        if (!SelectsPeopleSubject(supportedStrategy))
        {
            return new RelationshipAuthorizationStrategySubjectSelection([], []);
        }

        var selection = RelationalPeopleAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            [supportedStrategy]
        );

        return selection.Outcome switch
        {
            RelationalPeopleAuthorizationSubjectSelectionOutcome.Success =>
                new RelationshipAuthorizationStrategySubjectSelection(
                    selection
                        .StrategySubjectSelections.SelectMany(static strategy => strategy.Subjects)
                        .ToArray(),
                    []
                ),
            RelationalPeopleAuthorizationSubjectSelectionOutcome.SecurityConfigurationError =>
                new RelationshipAuthorizationStrategySubjectSelection(
                    [],
                    selection.SecurityConfigurationFailures
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported People relationship authorization subject selection outcome '{selection.Outcome}'."
            ),
        };
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
        CreateCheckSpec createCheckSpec,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> strategySubjects
    )
    {
        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var rootTableModel = concreteResourceModel.RelationalModel.Root;
        var rootTable = rootTableModel.Table;
        var rootDocumentIdColumn = GetRootDocumentIdColumn(rootTableModel);

        List<RelationshipAuthorizationCheckSpec> checkSpecs = [];
        List<RelationshipAuthorizationFailureMetadata> planningFailures = [];

        foreach (var selectedStrategySubjects in strategySubjects)
        {
            var supportedStrategy = selectedStrategySubjects.Strategy;
            var checkSpecResult = createCheckSpec(
                rootTable,
                rootDocumentIdColumn,
                selectedStrategySubjects.Subjects,
                supportedStrategy
            );

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

        if (planningFailures.Count > 0)
        {
            return new RelationshipAuthorizationResult.SecurityConfigurationError([
                .. OrderFailures(planningFailures),
            ]);
        }

        var missingPeopleAuthViewFailures = CreateMissingPeopleAuthViewAssociationFailures(
            mappingSet,
            resource,
            checkSpecs
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
                checkSpecs,
                CreateNoClaimsFailures(resource, checkSpecs)
            );
        }

        return new RelationshipAuthorizationResult.Authorized(
            checkSpecs,
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                mappingSet.Key.Dialect,
                authorizationContext.ClaimEducationOrganizationIds,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static CheckSpecCreationResult CreateStoredCheckSpec(
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    ) =>
        new(
            new RelationshipAuthorizationCheckSpec(
                supportedStrategy.ConfiguredStrategy,
                supportedStrategy.RelationshipLocalOrder,
                supportedStrategy.Direction,
                RelationshipAuthorizationValueSource.Stored,
                subjects,
                new RelationshipAuthorizationCheckTarget.Stored(rootTable, rootDocumentIdColumn)
            ),
            []
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

        return (_, rootDocumentIdColumn, subjects, supportedStrategy) =>
        {
            List<RelationshipAuthorizationFailureMetadata> failures = [];
            List<RelationshipAuthorizationSubject> executableSubjects = [];
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

                var anchor = CreateProposedAnchor(subject, rootTable, rootDocumentIdColumn);

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
                return new CheckSpecCreationResult(null, failures);
            }

            if (executableSubjects.Count == 0 && ineligibleSubjects.Count > 0)
            {
                return new CheckSpecCreationResult(
                    null,
                    [CreateNoExecutableSubjectsFailure(resource, supportedStrategy, ineligibleSubjects)]
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
                },
                []
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
        DbColumnName rootDocumentIdColumn
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
                RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
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
        IReadOnlyList<RelationshipAuthorizationIneligibleSubject> ineligibleSubjects
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
    ) => [.. OrderFailures(checkSpecs.SelectMany(checkSpec => CreateNoClaimsFailures(resource, checkSpec)))];

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
        var missingAssociationResourceNames =
            AuthObjectDefinitions.GetMissingPeopleAuthAssociationResourceNames(
                mappingSet.Model.ConcreteResourcesInNameOrder
            );

        if (missingAssociationResourceNames.Count == 0)
        {
            return [];
        }

        if (selectedPeopleAuthViews.Count == 0)
        {
            return [];
        }

        var missingAssociationResourceNamesText = string.Join(", ", missingAssociationResourceNames);

        return
        [
            .. OrderFailures(
                selectedPeopleAuthViews.Select(selectedPeopleAuthView =>
                    CreateMissingPeopleAuthViewAssociationFailure(
                        resource,
                        selectedPeopleAuthView,
                        missingAssociationResourceNamesText
                    )
                )
            ),
        ];
    }

    private static RelationshipAuthorizationFailureMetadata CreateMissingPeopleAuthViewAssociationFailure(
        QualifiedResourceName resource,
        SelectedStrategyPersonAuthView selectedPeopleAuthView,
        string missingAssociationResourceNamesText
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
            Hint: $"Strategy '{selectedPeopleAuthView.ConfiguredStrategy.StrategyName}' selects {selectedPeopleAuthView.PersonKind} relationship authorization through auth view '{selectedPeopleAuthView.AuthObject.Name}', but the people auth views were not emitted for resource '{resource.ProjectName}.{resource.ResourceName}'. Missing required association resources: [{missingAssociationResourceNamesText}]."
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
                strategySubject
                    .Subjects.Where(static subject => subject.PersonMetadata is not null)
                    .Select(subject =>
                    {
                        var personMetadata = subject.PersonMetadata!;
                        return new SelectedStrategyPersonAuthView(
                            strategySubject.Strategy.ConfiguredStrategy,
                            strategySubject.Strategy.RelationshipLocalOrder,
                            valueSource,
                            SelectPersonSecurableElementKind(subject, personMetadata.PersonKind),
                            personMetadata.PersonKind,
                            subject.AuthObject,
                            subject.Contributors
                        );
                    })
            )
        );

    private static IReadOnlyList<SelectedStrategyPersonAuthView> SelectStrategyPersonAuthViews(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    ) =>
        MergeSelectedStrategyPersonAuthViews(
            checkSpecs.SelectMany(checkSpec =>
                checkSpec
                    .Subjects.Where(static subject => subject.PersonMetadata is not null)
                    .Select(subject =>
                    {
                        var personMetadata = subject.PersonMetadata!;
                        return new SelectedStrategyPersonAuthView(
                            checkSpec.ConfiguredStrategy,
                            checkSpec.RelationshipLocalOrder,
                            checkSpec.ValueSource,
                            SelectPersonSecurableElementKind(subject, personMetadata.PersonKind),
                            personMetadata.PersonKind,
                            subject.AuthObject,
                            subject.Contributors
                        );
                    })
            )
        );

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

    private static RelationshipAuthorizationPersonKind MapPersonKind(SecurableElementKind kind) =>
        kind switch
        {
            SecurableElementKind.Student => RelationshipAuthorizationPersonKind.Student,
            SecurableElementKind.Contact => RelationshipAuthorizationPersonKind.Contact,
            SecurableElementKind.Staff => RelationshipAuthorizationPersonKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported relationship authorization person securable element kind."
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

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> OrderFailures(
        IEnumerable<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        failures
            .OrderBy(static failure => failure.ConfiguredStrategy?.RawConfiguredIndex ?? int.MaxValue)
            .ThenBy(static failure => failure.RelationshipLocalOrder ?? int.MaxValue)
            .ThenBy(static failure => failure.Location?.JsonPath, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.ReadableName, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.Table?.ToString(), StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.Column?.Value, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.AuthorizationObjectName, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Hint, StringComparer.Ordinal);

    private static DbColumnName GetRootDocumentIdColumn(DbTableModel rootTableModel)
    {
        var rootScopeLocatorColumns = rootTableModel.IdentityMetadata.RootScopeLocatorColumns;

        return rootScopeLocatorColumns.Count switch
        {
            1 => rootScopeLocatorColumns[0],
            0 => throw new InvalidOperationException(
                $"Root table '{rootTableModel.Table}' does not expose a root-scope locator column for stored relationship authorization planning."
            ),
            _ => throw new InvalidOperationException(
                $"Root table '{rootTableModel.Table}' exposes multiple root-scope locator columns, which is not supported for stored relationship authorization planning."
            ),
        };
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
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    );

    private sealed record CheckSpecCreationResult(
        RelationshipAuthorizationCheckSpec? CheckSpec,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
    );

    private sealed record RelationshipAuthorizationSubjectSelectionResult(
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySubjects> StrategySubjects,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
    );

    private sealed record SupportedRelationshipAuthorizationStrategySubjects(
        SupportedRelationshipAuthorizationStrategy Strategy,
        IReadOnlyList<RelationshipAuthorizationSubject> Subjects
    );

    private sealed record RelationshipAuthorizationStrategySubjectSelection(
        IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
    );

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
