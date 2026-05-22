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
            CreateProposedCheckSpecFactory(resource, writePlan)
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
        var createProposedCheckSpec = CreateProposedCheckSpecFactory(resource, writePlan);

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
    )
    {
        var knownButNotEnabledFailures = CreateKnownButNotEnabledFailures(
            resource,
            classification.KnownButNotEnabledStrategies
        );

        var securityConfigurationFailures = TryPlanSupportedStrategySecurityConfigurationFailures(
            mappingSet,
            resource,
            classification.SupportedStrategies,
            authorizationContext,
            valueSource,
            createCheckSpec,
            knownButNotEnabledFailures
        );

        return securityConfigurationFailures is not null
            ? securityConfigurationFailures
            : new RelationshipAuthorizationResult.KnownButNotEnabled(knownButNotEnabledFailures);
    }

    private RelationshipAuthorizationResult PlanSecurityConfigurationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec
    )
    {
        var classificationFailures = CombineAndOrderFailures(
            classification.SecurityConfigurationFailures,
            CreateKnownButNotEnabledFailures(resource, classification.KnownButNotEnabledStrategies)
        );

        var supportedStrategySecurityConfigurationFailures =
            TryPlanSupportedStrategySecurityConfigurationFailures(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                valueSource,
                createCheckSpec,
                classificationFailures
            );

        return supportedStrategySecurityConfigurationFailures is not null
            ? supportedStrategySecurityConfigurationFailures
            : new RelationshipAuthorizationResult.SecurityConfigurationError(classificationFailures);
    }

    private RelationshipAuthorizationResult.SecurityConfigurationError? TryPlanSupportedStrategySecurityConfigurationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> additionalFailures
    )
    {
        if (supportedStrategies.Count == 0)
        {
            return null;
        }

        var supportedPlanningResult = PlanSupportedStrategies(
            mappingSet,
            resource,
            supportedStrategies,
            authorizationContext,
            valueSource,
            createCheckSpec
        );

        if (
            supportedPlanningResult
            is not RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError
        )
        {
            return null;
        }

        return new RelationshipAuthorizationResult.SecurityConfigurationError(
            CombineAndOrderFailures(securityConfigurationError.Failures, additionalFailures)
        );
    }

    private RelationshipAuthorizationUpdatePlan PlanKnownButNotEnabledUpdateStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createProposedCheckSpec
    )
    {
        var knownButNotEnabledFailures = CreateKnownButNotEnabledFailures(
            resource,
            classification.KnownButNotEnabledStrategies
        );

        var supportedStrategySecurityConfigurationFailures =
            TryPlanSupportedStrategyUpdateSecurityConfigurationFailures(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                createProposedCheckSpec,
                knownButNotEnabledFailures
            );

        return supportedStrategySecurityConfigurationFailures
            ?? CreateKnownButNotEnabledUpdatePlan(knownButNotEnabledFailures);
    }

    private RelationshipAuthorizationUpdatePlan PlanSecurityConfigurationUpdateFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationshipAuthorizationClassification classification,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createProposedCheckSpec
    )
    {
        var classificationFailures = CombineAndOrderFailures(
            classification.SecurityConfigurationFailures,
            CreateKnownButNotEnabledFailures(resource, classification.KnownButNotEnabledStrategies)
        );

        var supportedStrategySecurityConfigurationFailures =
            TryPlanSupportedStrategyUpdateSecurityConfigurationFailures(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                createProposedCheckSpec,
                classificationFailures
            );

        return supportedStrategySecurityConfigurationFailures
            ?? CreateSecurityConfigurationUpdatePlan(classificationFailures);
    }

    private RelationshipAuthorizationUpdatePlan? TryPlanSupportedStrategyUpdateSecurityConfigurationFailures(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createProposedCheckSpec,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> additionalFailures
    )
    {
        if (supportedStrategies.Count == 0)
        {
            return null;
        }

        var supportedPlanningResult = PlanSupportedUpdateStrategies(
            mappingSet,
            resource,
            supportedStrategies,
            authorizationContext,
            createProposedCheckSpec
        );

        if (supportedPlanningResult.SecurityConfigurationFailures.Count == 0)
        {
            return null;
        }

        return CreateSecurityConfigurationUpdatePlan(
            CombineAndOrderFailures(supportedPlanningResult.SecurityConfigurationFailures, additionalFailures)
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
        var selectedEdOrgSubjects = _edOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            supportedStrategies
        );

        if (
            selectedEdOrgSubjects.Outcome
            is RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError
        )
        {
            return CreateSecurityConfigurationUpdatePlan([
                .. OrderFailures(
                    ApplyExecutionMetadata(
                        selectedEdOrgSubjects.SecurityConfigurationFailures,
                        supportedStrategies,
                        null
                    )
                ),
            ]);
        }

        return CreateUpdatePlan(
            PlanSelectedSupportedStrategies(
                mappingSet,
                resource,
                supportedStrategies,
                authorizationContext,
                RelationshipAuthorizationValueSource.Stored,
                CreateStoredCheckSpec,
                selectedEdOrgSubjects.Subjects
            ),
            PlanSelectedSupportedStrategies(
                mappingSet,
                resource,
                supportedStrategies,
                authorizationContext,
                RelationshipAuthorizationValueSource.Proposed,
                createProposedCheckSpec,
                selectedEdOrgSubjects.Subjects
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
        var selectedEdOrgSubjects = _edOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            supportedStrategies
        );

        if (
            selectedEdOrgSubjects.Outcome
            is RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError
        )
        {
            return new RelationshipAuthorizationResult.SecurityConfigurationError([
                .. OrderFailures(
                    ApplyExecutionMetadata(
                        selectedEdOrgSubjects.SecurityConfigurationFailures,
                        supportedStrategies,
                        valueSource
                    )
                ),
            ]);
        }

        return PlanSelectedSupportedStrategies(
            mappingSet,
            resource,
            supportedStrategies,
            authorizationContext,
            valueSource,
            createCheckSpec,
            selectedEdOrgSubjects.Subjects
        );
    }

    private static RelationshipAuthorizationResult PlanSelectedSupportedStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationshipAuthorizationValueSource valueSource,
        CreateCheckSpec createCheckSpec,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects
    )
    {
        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var rootTableModel = concreteResourceModel.RelationalModel.Root;
        var rootTable = rootTableModel.Table;
        var rootDocumentIdColumn = GetRootDocumentIdColumn(rootTableModel);

        List<RelationshipAuthorizationCheckSpec> checkSpecs = [];
        List<RelationshipAuthorizationFailureMetadata> planningFailures = [];

        foreach (var supportedStrategy in supportedStrategies)
        {
            var checkSpecResult = createCheckSpec(
                rootTable,
                rootDocumentIdColumn,
                subjects,
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

        if (authorizationContext.ClaimEducationOrganizationIds.Count == 0)
        {
            return new RelationshipAuthorizationResult.NoClaims(
                checkSpecs,
                CreateNoClaimsFailures(resource, supportedStrategies, valueSource)
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
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(supportedStrategy.Direction),
                subjects,
                new RelationshipAuthorizationCheckTarget.Stored(rootTable, rootDocumentIdColumn)
            ),
            []
        );

    private static CreateCheckSpec CreateProposedCheckSpecFactory(
        QualifiedResourceName resource,
        ResourceWritePlan writePlan
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

        return (_, _, subjects, supportedStrategy) =>
        {
            List<RelationshipAuthorizationFailureMetadata> failures = [];
            List<RelationshipAuthorizationProposedValueBinding> proposedBindings = [];

            foreach (var subject in subjects)
            {
                if (
                    !subject.Table.Equals(rootTable)
                    || !bindingIndexByColumn.TryGetValue(subject.Column, out var bindingEntry)
                )
                {
                    failures.AddRange(
                        CreateMissingProposedRootBindingFailures(subject, resource, supportedStrategy)
                    );
                    continue;
                }

                proposedBindings.Add(
                    new RelationshipAuthorizationProposedValueBinding(
                        subject.Table,
                        subject.Column,
                        bindingEntry.index,
                        subject.Column.Value,
                        bindingEntry.binding.ParameterName
                    )
                );
            }

            return failures.Count > 0
                ? new CheckSpecCreationResult(null, failures)
                : new CheckSpecCreationResult(
                    new RelationshipAuthorizationCheckSpec(
                        supportedStrategy.ConfiguredStrategy,
                        supportedStrategy.RelationshipLocalOrder,
                        supportedStrategy.Direction,
                        RelationshipAuthorizationValueSource.Proposed,
                        RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(supportedStrategy.Direction),
                        subjects,
                        new RelationshipAuthorizationCheckTarget.Proposed(rootTable, proposedBindings)
                    ),
                    []
                );
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
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationshipAuthorizationValueSource valueSource
    ) =>
        [
            .. OrderFailures(
                supportedStrategies.Select(strategy => new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                    resource,
                    strategy.ConfiguredStrategy,
                    strategy.RelationshipLocalOrder,
                    ValueSource: valueSource,
                    AuthObject: RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(strategy.Direction),
                    Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                ))
            ),
        ];

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
        RelationshipAuthorizationAuthObject? AuthObject,
        RelationshipAuthorizationFailureLocation? Location,
        string? Hint
    ) CreateUpdateFailureIdentity(RelationshipAuthorizationFailureMetadata failure) =>
        (
            failure.FailureKind,
            failure.Resource,
            failure.ConfiguredStrategy,
            failure.RelationshipLocalOrder,
            failure.AuthObject,
            failure.Location,
            failure.Hint
        );

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateMissingProposedRootBindingFailures(
        RelationshipAuthorizationSubject subject,
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    ) =>
        subject.Contributors.Select(contributor => new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.MissingProposedRootBinding,
            resource,
            supportedStrategy.ConfiguredStrategy,
            supportedStrategy.RelationshipLocalOrder,
            ValueSource: RelationshipAuthorizationValueSource.Proposed,
            AuthObject: RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(supportedStrategy.Direction),
            Location: new RelationshipAuthorizationFailureLocation(
                contributor.Kind,
                contributor.JsonPath,
                contributor.ReadableName,
                subject.Table,
                subject.Column
            ),
            Hint: "Selected root-table authorization subject does not have a matching root write binding."
        ));

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> ApplyExecutionMetadata(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationshipAuthorizationValueSource? valueSource
    )
    {
        var authObjectByStrategyIdentity = supportedStrategies.ToDictionary(
            static strategy =>
                (strategy.ConfiguredStrategy.RawConfiguredIndex, strategy.RelationshipLocalOrder),
            static strategy => RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(strategy.Direction)
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
                    || !authObjectByStrategyIdentity.TryGetValue(
                        (rawConfiguredIndex.Value, relationshipLocalOrder.Value),
                        out var authObject
                    )
                )
                {
                    return failure with { ValueSource = failure.ValueSource ?? valueSource };
                }

                return failure with
                {
                    ValueSource = failure.ValueSource ?? valueSource,
                    AuthObject = failure.AuthObject ?? authObject,
                };
            }),
        ];
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
}
