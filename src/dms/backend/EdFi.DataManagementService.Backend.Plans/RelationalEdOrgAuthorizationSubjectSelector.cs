// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

public enum RelationalEdOrgAuthorizationSubjectSelectionOutcome
{
    Success,
    SecurityConfigurationError,
}

public sealed record RelationalEdOrgAuthorizationSubjectSelection(
    RelationalEdOrgAuthorizationSubjectSelectionOutcome Outcome,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> SecurityConfigurationFailures
);

public sealed class RelationalEdOrgAuthorizationSubjectSelector
{
    private readonly RelationalEdOrgAuthorizationElementResolutionCache _elementResolutionCache;

    public RelationalEdOrgAuthorizationSubjectSelector(
        RelationalEdOrgAuthorizationElementResolutionCache elementResolutionCache
    )
    {
        _elementResolutionCache =
            elementResolutionCache ?? throw new ArgumentNullException(nameof(elementResolutionCache));
    }

    internal RelationalEdOrgAuthorizationSubjectSelection Select(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    )
    {
        ArgumentNullException.ThrowIfNull(supportedStrategy);

        return Select(
            mappingSet,
            resource,
            [
                new SupportedRelationshipAuthorizationStrategySelection(
                    supportedStrategy.ConfiguredStrategy,
                    supportedStrategy.RelationshipLocalOrder,
                    RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(supportedStrategy.Direction)
                ),
            ]
        );
    }

    private RelationalEdOrgAuthorizationSubjectSelection Select(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySelection> supportedStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(supportedStrategies);

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var elementResolutions = _elementResolutionCache.GetOrResolveAll(mappingSet, resource);
        var tableModelsByName = concreteResourceModel.RelationalModel.TablesInDependencyOrder.ToDictionary(
            static table => table.Table
        );

        List<SelectedRootSubjectCandidate> selectedRootSubjectCandidates = [];
        List<RelationshipAuthorizationFailureMetadata> securityConfigurationFailures = [];
        List<NonRootResolvedCandidate> nonRootResolvedCandidates = [];

        for (var elementIndex = 0; elementIndex < elementResolutions.Count; elementIndex++)
        {
            var elementResolution = elementResolutions[elementIndex];
            var rootCandidate = SelectPreferredConcreteRootCandidate(
                tableModelsByName,
                elementResolution.ResolvedCandidates
            );

            if (rootCandidate is not null)
            {
                selectedRootSubjectCandidates.Add(
                    new SelectedRootSubjectCandidate(elementIndex, rootCandidate)
                );

                continue;
            }

            if (elementResolution.ResolvedCandidates.Count == 0)
            {
                securityConfigurationFailures.AddRange(
                    CreateUnresolvedFailures(resource, supportedStrategies, elementResolution.Element)
                );

                continue;
            }

            nonRootResolvedCandidates.AddRange(
                elementResolution.ResolvedCandidates.Select(candidate => new NonRootResolvedCandidate(
                    elementIndex,
                    candidate,
                    GetCandidateTableKind(tableModelsByName, candidate)
                ))
            );
        }

        if (selectedRootSubjectCandidates.Count == 0)
        {
            securityConfigurationFailures.AddRange(
                CreateNoApplicableRootSubjectFailures(
                    resource,
                    supportedStrategies,
                    nonRootResolvedCandidates
                )
            );
        }

        if (securityConfigurationFailures.Count > 0)
        {
            return new RelationalEdOrgAuthorizationSubjectSelection(
                RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError,
                [],
                [.. OrderFailures(securityConfigurationFailures)]
            );
        }

        return new RelationalEdOrgAuthorizationSubjectSelection(
            RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success,
            [.. GroupRootSubjects(resource, supportedStrategies, selectedRootSubjectCandidates)],
            []
        );
    }

    private static ResolvedEdOrgSecurableElementCandidate? SelectPreferredConcreteRootCandidate(
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByName,
        IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> candidates
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => GetCandidateTableKind(tableModelsByName, candidate) is DbTableKind.Root)
            .OrderBy(static candidate => candidate.JsonPath.Length)
            .ThenBy(static candidate => candidate.JsonPath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Step.SourceColumnName.Value, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateUnresolvedFailures(
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySelection> supportedStrategies,
        EdOrgSecurableElement unresolvedElement
    )
    {
        return supportedStrategies.Select(supportedStrategy => new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
            resource,
            supportedStrategy.ConfiguredStrategy,
            supportedStrategy.RelationshipLocalOrder,
            Location: new RelationshipAuthorizationFailureLocation(
                Kind: SecurableElementKind.EducationOrganization,
                JsonPath: unresolvedElement.JsonPath,
                ReadableName: unresolvedElement.MetaEdName
            ),
            Hint: "EducationOrganization securable element did not resolve to a relational column."
        ));
    }

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateNoApplicableRootSubjectFailures(
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySelection> supportedStrategies,
        IReadOnlyList<NonRootResolvedCandidate> nonRootResolvedCandidates
    )
    {
        if (nonRootResolvedCandidates.Count == 0)
        {
            return supportedStrategies.Select(
                supportedStrategy => new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                    resource,
                    supportedStrategy.ConfiguredStrategy,
                    supportedStrategy.RelationshipLocalOrder,
                    Location: new RelationshipAuthorizationFailureLocation(
                        Kind: SecurableElementKind.EducationOrganization
                    ),
                    Hint: "No EducationOrganization securable elements are configured for this resource."
                )
            );
        }

        return supportedStrategies.SelectMany(supportedStrategy =>
            nonRootResolvedCandidates.Select(nonRootCandidate => new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                resource,
                supportedStrategy.ConfiguredStrategy,
                supportedStrategy.RelationshipLocalOrder,
                Location: new RelationshipAuthorizationFailureLocation(
                    Kind: SecurableElementKind.EducationOrganization,
                    JsonPath: nonRootCandidate.Candidate.JsonPath,
                    ReadableName: nonRootCandidate.Candidate.ReadableName,
                    Table: nonRootCandidate.Candidate.Step.SourceTable,
                    Column: nonRootCandidate.Candidate.Step.SourceColumnName
                ),
                Hint: $"Resolved to non-root table kind '{nonRootCandidate.TableKind}' instead of '{DbTableKind.Root}'."
            ))
        );
    }

    private static IEnumerable<RelationshipAuthorizationSubject> GroupRootSubjects(
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategySelection> supportedStrategies,
        IReadOnlyList<SelectedRootSubjectCandidate> selectedRootSubjectCandidates
    )
    {
        var groupedCandidates = selectedRootSubjectCandidates
            .GroupBy(static candidate =>
                (candidate.Candidate.Step.SourceTable, candidate.Candidate.Step.SourceColumnName)
            )
            .OrderBy(static group => group.Min(candidate => candidate.ConfiguredElementIndex))
            .ThenBy(static group => group.Key.SourceTable.ToString(), StringComparer.Ordinal)
            .ThenBy(static group => group.Key.SourceColumnName.Value, StringComparer.Ordinal)
            .ToArray();

        return supportedStrategies.SelectMany(supportedStrategy =>
            groupedCandidates.Select(group => new RelationshipAuthorizationSubject(
                resource,
                group.Key.SourceTable,
                group.Key.SourceColumnName,
                supportedStrategy.AuthObject,
                [
                    .. group
                        .OrderBy(static candidate => candidate.ConfiguredElementIndex)
                        .ThenBy(static candidate => candidate.Candidate.JsonPath, StringComparer.Ordinal)
                        .ThenBy(static candidate => candidate.Candidate.ReadableName, StringComparer.Ordinal)
                        .Select(static candidate => new RelationshipAuthorizationSubjectContributor(
                            SecurableElementKind.EducationOrganization,
                            candidate.Candidate.JsonPath,
                            candidate.Candidate.ReadableName
                        )),
                ]
            ))
        );
    }

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> OrderFailures(
        IEnumerable<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        failures
            .OrderBy(static failure => failure.ConfiguredStrategy?.RawConfiguredIndex ?? int.MaxValue)
            .ThenBy(static failure => failure.Location?.JsonPath, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.ReadableName, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.Table?.ToString(), StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.Column?.Value, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Hint, StringComparer.Ordinal);

    private static DbTableKind GetCandidateTableKind(
        IReadOnlyDictionary<DbTableName, DbTableModel> tableModelsByName,
        ResolvedEdOrgSecurableElementCandidate candidate
    )
    {
        if (!tableModelsByName.TryGetValue(candidate.Step.SourceTable, out var tableModel))
        {
            throw new InvalidOperationException(
                $"Resolved EducationOrganization candidate table '{candidate.Step.SourceTable}' was not found in the resource model."
            );
        }

        return tableModel.IdentityMetadata.TableKind;
    }

    private sealed record SelectedRootSubjectCandidate(
        int ConfiguredElementIndex,
        ResolvedEdOrgSecurableElementCandidate Candidate
    );

    private sealed record SupportedRelationshipAuthorizationStrategySelection(
        ConfiguredAuthorizationStrategy ConfiguredStrategy,
        int RelationshipLocalOrder,
        RelationshipAuthorizationAuthObject AuthObject
    );

    private sealed record NonRootResolvedCandidate(
        int ConfiguredElementIndex,
        ResolvedEdOrgSecurableElementCandidate Candidate,
        DbTableKind TableKind
    );
}
