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
        RelationalEdOrgAuthorizationSubjectSelector? edOrgAuthorizationSubjectSelector = null
    )
    {
        _edOrgAuthorizationSubjectSelector =
            edOrgAuthorizationSubjectSelector ?? new RelationalEdOrgAuthorizationSubjectSelector();
    }

    public RelationshipAuthorizationResult PlanStoredValues(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);
        ArgumentNullException.ThrowIfNull(authorizationContext);

        var classification = RelationshipAuthorizationStrategyClassifier.Classify(
            mappingSet,
            resource,
            configuredAuthorizationStrategies
        );

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
                new RelationshipAuthorizationResult.KnownButNotEnabled(
                    CreateKnownButNotEnabledFailures(resource, classification.KnownButNotEnabledStrategies)
                ),
            RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError =>
                new RelationshipAuthorizationResult.SecurityConfigurationError(
                    CombineAndOrderFailures(
                        classification.SecurityConfigurationFailures,
                        CreateKnownButNotEnabledFailures(
                            resource,
                            classification.KnownButNotEnabledStrategies
                        )
                    )
                ),
            RelationshipAuthorizationClassificationOutcome.SupportedStrategies => PlanSupportedStrategies(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                static (rootTable, rootDocumentIdColumn, subjects, strategy) =>
                    new CheckSpecCreationResult(
                        new RelationshipAuthorizationCheckSpec(
                            strategy.ConfiguredStrategy,
                            strategy.Direction,
                            RelationshipAuthorizationValueSource.Stored,
                            subjects,
                            new RelationshipAuthorizationCheckTarget.Stored(rootTable, rootDocumentIdColumn)
                        ),
                        []
                    )
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relationship authorization classification outcome '{classification.Outcome}'."
            ),
        };
    }

    public RelationshipAuthorizationResult PlanProposedValues(
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
                new RelationshipAuthorizationResult.KnownButNotEnabled(
                    CreateKnownButNotEnabledFailures(resource, classification.KnownButNotEnabledStrategies)
                ),
            RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError =>
                new RelationshipAuthorizationResult.SecurityConfigurationError(
                    CombineAndOrderFailures(
                        classification.SecurityConfigurationFailures,
                        CreateKnownButNotEnabledFailures(
                            resource,
                            classification.KnownButNotEnabledStrategies
                        )
                    )
                ),
            RelationshipAuthorizationClassificationOutcome.SupportedStrategies => PlanSupportedStrategies(
                mappingSet,
                resource,
                classification.SupportedStrategies,
                authorizationContext,
                CreateProposedCheckSpecFactory(resource, writePlan)
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relationship authorization classification outcome '{classification.Outcome}'."
            ),
        };
    }

    private RelationshipAuthorizationResult PlanSupportedStrategies(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        RelationalAuthorizationContext authorizationContext,
        CreateCheckSpec createCheckSpec
    )
    {
        var selectedEdOrgSubjects = _edOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            [.. supportedStrategies.Select(static strategy => strategy.ConfiguredStrategy)]
        );

        if (
            selectedEdOrgSubjects.Outcome
            is RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError
        )
        {
            return new RelationshipAuthorizationResult.SecurityConfigurationError([
                .. OrderFailures(selectedEdOrgSubjects.SecurityConfigurationFailures),
            ]);
        }

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var rootTableModel = concreteResourceModel.RelationalModel.Root;
        var rootTable = rootTableModel.Table;
        var rootDocumentIdColumn = GetRootDocumentIdColumn(rootTableModel);
        IReadOnlyList<RelationshipAuthorizationSubject> subjects = selectedEdOrgSubjects.Subjects;

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
                CreateNoClaimsFailures(resource, supportedStrategies)
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
                        supportedStrategy.Direction,
                        RelationshipAuthorizationValueSource.Proposed,
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
                            null,
                            BuildKnownButNotEnabledHint(strategy)
                        );
                    }

                    var nonNullBasisResource = basisResource.Value;

                    return new RelationshipAuthorizationFailureMetadata(
                        RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy,
                        resource,
                        strategy.ConfiguredStrategy,
                        strategy.RelationshipLocalOrder,
                        new RelationshipAuthorizationFailureLocation(
                            AuthorizationObjectName: $"{nonNullBasisResource.ProjectName}.{nonNullBasisResource.ResourceName}"
                        ),
                        BuildKnownButNotEnabledHint(strategy)
                    );
                })
            ),
        ];

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateNoClaimsFailures(
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies
    ) =>
        [
            .. OrderFailures(
                supportedStrategies.Select(strategy => new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                    resource,
                    strategy.ConfiguredStrategy,
                    strategy.RelationshipLocalOrder,
                    Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                ))
            ),
        ];

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CombineAndOrderFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> primaryFailures,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> secondaryFailures
    ) => [.. OrderFailures([.. primaryFailures, .. secondaryFailures])];

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
            new RelationshipAuthorizationFailureLocation(
                contributor.Kind,
                contributor.JsonPath,
                contributor.ReadableName,
                subject.Table,
                subject.Column
            ),
            "Selected root-table authorization subject does not have a matching root write binding."
        ));

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
