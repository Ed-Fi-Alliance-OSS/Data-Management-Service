// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;

namespace EdFi.DataManagementService.Backend.Plans;

public enum RelationalPeopleAuthorizationSubjectSelectionOutcome
{
    Success,
    SecurityConfigurationError,
}

public sealed record RelationalPeopleAuthorizationStrategySubjectSelection(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects
);

public sealed record RelationalPeopleAuthorizationSubjectSelection(
    RelationalPeopleAuthorizationSubjectSelectionOutcome Outcome,
    IReadOnlyList<RelationalPeopleAuthorizationStrategySubjectSelection> StrategySubjectSelections,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> SecurityConfigurationFailures
);

public static class RelationalPeopleAuthorizationSubjectSelector
{
    internal static RelationalPeopleAuthorizationSubjectSelection Select(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(supportedStrategies);

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var personCandidates = ResolvePersonCandidates(
            concreteResourceModel,
            mappingSet.Model.ConcreteResourcesInNameOrder
        );
        var securityConfigurationFailures = CreateUnresolvedPersonFailures(
            resource,
            supportedStrategies,
            personCandidates.UnresolvedPaths
        );

        if (securityConfigurationFailures.Count > 0)
        {
            return new RelationalPeopleAuthorizationSubjectSelection(
                RelationalPeopleAuthorizationSubjectSelectionOutcome.SecurityConfigurationError,
                [],
                [.. OrderFailures(securityConfigurationFailures)]
            );
        }

        var subjectsByStrategy = new List<RelationalPeopleAuthorizationStrategySubjectSelection>(
            supportedStrategies.Count
        );

        foreach (var supportedStrategy in supportedStrategies)
        {
            var subjects = SelectStrategySubjects(supportedStrategy, resource, personCandidates.Candidates);

            if (subjects.Count == 0)
            {
                continue;
            }

            subjectsByStrategy.Add(
                new RelationalPeopleAuthorizationStrategySubjectSelection(
                    supportedStrategy.ConfiguredStrategy,
                    supportedStrategy.RelationshipLocalOrder,
                    subjects
                )
            );
        }

        return new RelationalPeopleAuthorizationSubjectSelection(
            RelationalPeopleAuthorizationSubjectSelectionOutcome.Success,
            subjectsByStrategy,
            []
        );
    }

    private static ResolvedPeopleCandidates ResolvePersonCandidates(
        ConcreteResourceModel concreteResourceModel,
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        var rootTableModel = concreteResourceModel.RelationalModel.Root;
        var rootTable = rootTableModel.Table;
        var rootDocumentIdColumn = GetRootDocumentIdColumn(rootTableModel);
        var resourceLookup = PersonJoinPathResolver.BuildResourceLookup(allResources);

        List<ResolvedPeopleCandidate> candidates = [];
        List<UnresolvedPersonPath> unresolvedPaths = [];

        ResolvePersonCandidates(
            concreteResourceModel,
            concreteResourceModel.SecurableElements.Student,
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            "Student",
            rootTable,
            rootDocumentIdColumn,
            resourceLookup,
            candidates,
            unresolvedPaths
        );
        ResolvePersonCandidates(
            concreteResourceModel,
            concreteResourceModel.SecurableElements.Contact,
            SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Contact,
            "Contact",
            rootTable,
            rootDocumentIdColumn,
            resourceLookup,
            candidates,
            unresolvedPaths
        );
        ResolvePersonCandidates(
            concreteResourceModel,
            concreteResourceModel.SecurableElements.Staff,
            SecurableElementKind.Staff,
            RelationshipAuthorizationPersonKind.Staff,
            "Staff",
            rootTable,
            rootDocumentIdColumn,
            resourceLookup,
            candidates,
            unresolvedPaths
        );

        return new ResolvedPeopleCandidates(candidates, unresolvedPaths);
    }

    private static void ResolvePersonCandidates(
        ConcreteResourceModel concreteResourceModel,
        IReadOnlyList<string> personPaths,
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind,
        string personResourceName,
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<ResolvedPeopleCandidate> candidates,
        List<UnresolvedPersonPath> unresolvedPaths
    )
    {
        for (var contributionOrder = 0; contributionOrder < personPaths.Count; contributionOrder++)
        {
            var personPath = personPaths[contributionOrder];

            if (
                PersonJoinPathResolver.IsPersonResource(
                    concreteResourceModel.RelationalModel.Resource,
                    personResourceName
                )
            )
            {
                candidates.Add(
                    CreateSelfPersonCandidate(
                        securableElementKind,
                        personKind,
                        personPath,
                        contributionOrder,
                        rootTable,
                        rootDocumentIdColumn
                    )
                );

                continue;
            }

            List<string> skippedArrayNestedPaths = [];
            var chain = PersonJoinPathResolver.ResolveShortestPersonChain(
                concreteResourceModel,
                [personPath],
                personResourceName,
                resourceLookup,
                skippedArrayNestedPaths,
                out var unresolvedRootLevelPaths
            );

            if (chain is not null)
            {
                candidates.Add(
                    CreateResolvedPersonCandidate(
                        securableElementKind,
                        personKind,
                        personPath,
                        contributionOrder,
                        rootTable,
                        rootDocumentIdColumn,
                        chain
                    )
                );

                continue;
            }

            if (unresolvedRootLevelPaths.Count > 0)
            {
                unresolvedPaths.AddRange(
                    unresolvedRootLevelPaths.Select(unresolvedRootLevelPath => new UnresolvedPersonPath(
                        securableElementKind,
                        unresolvedRootLevelPath,
                        ToReadableName(unresolvedRootLevelPath)
                    ))
                );
            }
        }
    }

    private static ResolvedPeopleCandidate CreateSelfPersonCandidate(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind,
        string personPath,
        int contributionOrder,
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn
    ) =>
        new(
            securableElementKind,
            personKind,
            personPath,
            ToReadableName(personPath),
            contributionOrder,
            new RelationshipAuthorizationPersonSubjectPath(
                RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                []
            ),
            rootTable,
            rootDocumentIdColumn,
            new RelationshipAuthorizationPersonStoredAnchor(rootTable, rootDocumentIdColumn)
        );

    private static ResolvedPeopleCandidate CreateResolvedPersonCandidate(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind,
        string personPath,
        int contributionOrder,
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        IReadOnlyList<ColumnPathStep> chain
    )
    {
        var terminalStep = chain[^1];
        var pathKind =
            chain.Count is 1 && terminalStep.SourceTable.Equals(rootTable)
                ? RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                : RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath;

        return new ResolvedPeopleCandidate(
            securableElementKind,
            personKind,
            personPath,
            ToReadableName(personPath),
            contributionOrder,
            new RelationshipAuthorizationPersonSubjectPath(pathKind, chain),
            terminalStep.SourceTable,
            terminalStep.SourceColumnName,
            new RelationshipAuthorizationPersonStoredAnchor(rootTable, rootDocumentIdColumn)
        );
    }

    private static IReadOnlyList<RelationshipAuthorizationSubject> SelectStrategySubjects(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        QualifiedResourceName resource,
        IReadOnlyList<ResolvedPeopleCandidate> candidates
    )
    {
        var subjectsByPredicateKey = new Dictionary<PersonPredicateKey, RelationshipAuthorizationSubject>();

        foreach (var eligibleSubject in supportedStrategy.EligibleSubjects)
        {
            if (eligibleSubject.PersonAuthViewKind is not { } authViewKind)
            {
                continue;
            }

            var authObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);

            foreach (var candidate in candidates.Where(candidate => candidate.Kind == eligibleSubject.Kind))
            {
                var predicateKey = CreatePredicateKey(candidate, authObject);
                var contributor = new RelationshipAuthorizationSubjectContributor(
                    candidate.Kind,
                    candidate.JsonPath,
                    candidate.ReadableName,
                    candidate.ContributionOrder
                );

                if (subjectsByPredicateKey.TryGetValue(predicateKey, out var existingSubject))
                {
                    subjectsByPredicateKey[predicateKey] = existingSubject with
                    {
                        Contributors = [.. existingSubject.Contributors, contributor],
                    };

                    continue;
                }

                subjectsByPredicateKey.Add(
                    predicateKey,
                    new RelationshipAuthorizationSubject(
                        resource,
                        candidate.TerminalTable,
                        candidate.TerminalColumn,
                        [contributor],
                        new RelationshipAuthorizationPersonSubjectMetadata(
                            candidate.PersonKind,
                            candidate.Path,
                            authObject,
                            candidate.StoredAnchor,
                            ProposedAnchor: null
                        )
                    )
                );
            }
        }

        return
        [
            .. subjectsByPredicateKey
                .Values.OrderBy(static subject =>
                    subject.Contributors.Min(static contributor => contributor.ContributionOrder)
                )
                .ThenBy(static subject => subject.Table.ToString(), StringComparer.Ordinal)
                .ThenBy(static subject => subject.Column.Value, StringComparer.Ordinal),
        ];
    }

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CreateUnresolvedPersonFailures(
        QualifiedResourceName resource,
        IReadOnlyList<SupportedRelationshipAuthorizationStrategy> supportedStrategies,
        IReadOnlyList<UnresolvedPersonPath> unresolvedPaths
    )
    {
        if (unresolvedPaths.Count == 0)
        {
            return [];
        }

        return
        [
            .. supportedStrategies.SelectMany(supportedStrategy =>
                supportedStrategy
                    .EligibleSubjects.Where(static eligibleSubject =>
                        eligibleSubject.PersonAuthViewKind is not null
                    )
                    .SelectMany(eligibleSubject =>
                        unresolvedPaths
                            .Where(unresolvedPath => unresolvedPath.Kind == eligibleSubject.Kind)
                            .Select(unresolvedPath => new RelationshipAuthorizationFailureMetadata(
                                RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                                resource,
                                supportedStrategy.ConfiguredStrategy,
                                supportedStrategy.RelationshipLocalOrder,
                                Location: new RelationshipAuthorizationFailureLocation(
                                    unresolvedPath.Kind,
                                    unresolvedPath.JsonPath,
                                    unresolvedPath.ReadableName
                                ),
                                Hint: "Person securable element did not resolve to a DocumentId-based relational path."
                            ))
                    )
            ),
        ];
    }

    private static PersonPredicateKey CreatePredicateKey(
        ResolvedPeopleCandidate candidate,
        RelationshipAuthorizationAuthObject authObject
    ) =>
        new(
            candidate.PersonKind,
            authObject.Name,
            authObject.SubjectValueColumn,
            candidate.TerminalTable,
            candidate.TerminalColumn,
            candidate.Path.Kind,
            CreateStepKey(candidate.Path.Steps)
        );

    private static string CreateStepKey(IReadOnlyList<ColumnPathStep> steps) =>
        string.Join(
            "|",
            steps.Select(static step =>
                $"{step.SourceTable}.{step.SourceColumnName.Value}>{step.TargetTable?.ToString() ?? ""}.{step.TargetColumnName?.Value ?? ""}"
            )
        );

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> OrderFailures(
        IEnumerable<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        failures
            .OrderBy(static failure => failure.ConfiguredStrategy?.RawConfiguredIndex ?? int.MaxValue)
            .ThenBy(static failure => failure.RelationshipLocalOrder ?? int.MaxValue)
            .ThenBy(static failure => failure.Location?.JsonPath, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Location?.ReadableName, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Hint, StringComparer.Ordinal);

    private static string ToReadableName(string jsonPath)
    {
        var leaf = jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? jsonPath;
        leaf = leaf.Replace("[*]", string.Empty, StringComparison.Ordinal);

        return string.IsNullOrEmpty(leaf) ? jsonPath : leaf[..1].ToUpperInvariant() + leaf[1..];
    }

    private static DbColumnName GetRootDocumentIdColumn(DbTableModel rootTableModel)
    {
        var rootScopeLocatorColumns = rootTableModel.IdentityMetadata.RootScopeLocatorColumns;

        return rootScopeLocatorColumns.Count switch
        {
            1 => rootScopeLocatorColumns[0],
            0 => throw new InvalidOperationException(
                $"Root table '{rootTableModel.Table}' does not expose a root-scope locator column for people relationship authorization planning."
            ),
            _ => throw new InvalidOperationException(
                $"Root table '{rootTableModel.Table}' exposes multiple root-scope locator columns, which is not supported for people relationship authorization planning."
            ),
        };
    }

    private sealed record ResolvedPeopleCandidates(
        IReadOnlyList<ResolvedPeopleCandidate> Candidates,
        IReadOnlyList<UnresolvedPersonPath> UnresolvedPaths
    );

    private sealed record ResolvedPeopleCandidate(
        SecurableElementKind Kind,
        RelationshipAuthorizationPersonKind PersonKind,
        string JsonPath,
        string ReadableName,
        int ContributionOrder,
        RelationshipAuthorizationPersonSubjectPath Path,
        DbTableName TerminalTable,
        DbColumnName TerminalColumn,
        RelationshipAuthorizationPersonStoredAnchor StoredAnchor
    );

    private sealed record UnresolvedPersonPath(
        SecurableElementKind Kind,
        string JsonPath,
        string ReadableName
    );

    private sealed record PersonPredicateKey(
        RelationshipAuthorizationPersonKind PersonKind,
        DbTableName AuthObjectName,
        DbColumnName SubjectValueColumn,
        DbTableName TerminalTable,
        DbColumnName TerminalColumn,
        RelationshipAuthorizationPersonSubjectPathKind PathKind,
        string StepKey
    );
}
