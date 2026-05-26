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
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors
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
        List<RelationshipAuthorizationFailureMetadata> securityConfigurationFailures =
        [
            .. CreateUnresolvedPersonFailures(
                resource,
                supportedStrategies,
                personCandidates.UnresolvedPaths
            ),
        ];

        var subjectsByStrategy = new List<RelationalPeopleAuthorizationStrategySubjectSelection>(
            supportedStrategies.Count
        );

        foreach (var supportedStrategy in supportedStrategies)
        {
            var selectedSubjects = SelectStrategySubjects(
                supportedStrategy,
                resource,
                personCandidates.Candidates,
                personCandidates.SkippedPaths
            );

            if (selectedSubjects.Subjects.Count == 0)
            {
                if (selectedSubjects.SkippedContributors.Count > 0)
                {
                    securityConfigurationFailures.AddRange(
                        CreateNoApplicablePersonSubjectFailures(
                            resource,
                            supportedStrategy,
                            selectedSubjects.SkippedContributors
                        )
                    );
                }

                continue;
            }

            subjectsByStrategy.Add(
                new RelationalPeopleAuthorizationStrategySubjectSelection(
                    supportedStrategy.ConfiguredStrategy,
                    supportedStrategy.RelationshipLocalOrder,
                    selectedSubjects.Subjects,
                    selectedSubjects.SkippedContributors
                )
            );
        }

        if (securityConfigurationFailures.Count > 0)
        {
            return new RelationalPeopleAuthorizationSubjectSelection(
                RelationalPeopleAuthorizationSubjectSelectionOutcome.SecurityConfigurationError,
                subjectsByStrategy,
                [.. OrderFailures(securityConfigurationFailures)]
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
        List<SkippedPersonPath> skippedPaths = [];

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
            unresolvedPaths,
            skippedPaths
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
            unresolvedPaths,
            skippedPaths
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
            unresolvedPaths,
            skippedPaths
        );

        return new ResolvedPeopleCandidates(candidates, unresolvedPaths, skippedPaths);
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
        List<UnresolvedPersonPath> unresolvedPaths,
        List<SkippedPersonPath> skippedPaths
    )
    {
        for (var contributionOrder = 0; contributionOrder < personPaths.Count; contributionOrder++)
        {
            var personPath = personPaths[contributionOrder];

            if (
                IsSelfPersonIdentityPath(
                    concreteResourceModel.RelationalModel.Resource,
                    securableElementKind,
                    personResourceName,
                    personPath
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

            if (skippedArrayNestedPaths.Count > 0)
            {
                skippedPaths.AddRange(
                    skippedArrayNestedPaths.Select(skippedArrayNestedPath =>
                        CreateSkippedPersonPath(
                            concreteResourceModel,
                            securableElementKind,
                            personKind,
                            skippedArrayNestedPath,
                            contributionOrder
                        )
                    )
                );

                continue;
            }

            if (chain is not null)
            {
                if (!chain[0].SourceTable.Equals(rootTable))
                {
                    skippedPaths.Add(
                        CreateSkippedPersonPath(
                            securableElementKind,
                            personKind,
                            personPath,
                            contributionOrder,
                            chain[0].SourceTable,
                            chain[0].SourceColumnName
                        )
                    );

                    continue;
                }

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

    private static SelectedPeopleStrategySubjects SelectStrategySubjects(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        QualifiedResourceName resource,
        IReadOnlyList<ResolvedPeopleCandidate> candidates,
        IReadOnlyList<SkippedPersonPath> skippedPaths
    )
    {
        var subjectsByPredicateKey = new Dictionary<PersonPredicateKey, RelationshipAuthorizationSubject>();
        List<RelationshipAuthorizationSkippedSubjectContributor> skippedContributors = [];

        foreach (var eligibleSubject in supportedStrategy.EligibleSubjects)
        {
            if (eligibleSubject.PersonAuthViewKind is not { } authViewKind)
            {
                continue;
            }

            var authObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);

            skippedContributors.AddRange(
                skippedPaths
                    .Where(skippedPath => skippedPath.Kind == eligibleSubject.Kind)
                    .Select(skippedPath => new RelationshipAuthorizationSkippedSubjectContributor(
                        skippedPath.Kind,
                        skippedPath.JsonPath,
                        skippedPath.ReadableName,
                        skippedPath.ContributionOrder,
                        skippedPath.Reason,
                        skippedPath.PersonKind,
                        authObject,
                        skippedPath.Table,
                        skippedPath.Column
                    ))
            );

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
                        authObject,
                        [contributor],
                        new RelationshipAuthorizationPersonSubjectMetadata(
                            candidate.PersonKind,
                            candidate.Path,
                            candidate.StoredAnchor,
                            ProposedAnchor: null
                        )
                    )
                );
            }
        }

        return new SelectedPeopleStrategySubjects(
            [
                .. subjectsByPredicateKey
                    .Values.OrderBy(static subject =>
                        subject.Contributors.Min(static contributor => contributor.ContributionOrder)
                    )
                    .ThenBy(static subject => subject.Table.ToString(), StringComparer.Ordinal)
                    .ThenBy(static subject => subject.Column.Value, StringComparer.Ordinal),
            ],
            [
                .. skippedContributors
                    .OrderBy(static contributor => contributor.ContributionOrder)
                    .ThenBy(static contributor => contributor.JsonPath, StringComparer.Ordinal)
                    .ThenBy(static contributor => contributor.ReadableName, StringComparer.Ordinal),
            ]
        );
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
                            .Select(unresolvedPath =>
                            {
                                var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
                                    eligibleSubject.PersonAuthViewKind!.Value
                                );
                                var personKind = MapPersonKind(unresolvedPath.Kind);
                                var contributor = new RelationshipAuthorizationSubjectContributor(
                                    unresolvedPath.Kind,
                                    unresolvedPath.JsonPath,
                                    unresolvedPath.ReadableName
                                );

                                return new RelationshipAuthorizationFailureMetadata(
                                    RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                                    resource,
                                    supportedStrategy.ConfiguredStrategy,
                                    supportedStrategy.RelationshipLocalOrder,
                                    AuthObject: authObject,
                                    Location: new RelationshipAuthorizationFailureLocation(
                                        unresolvedPath.Kind,
                                        unresolvedPath.JsonPath,
                                        unresolvedPath.ReadableName,
                                        AuthorizationObjectName: authObject.Name.ToString()
                                    ),
                                    Hint: "Person securable element did not resolve to a DocumentId-based relational path."
                                )
                                {
                                    PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                                        personKind,
                                        authObject
                                    ),
                                    Contributors = [contributor],
                                };
                            })
                    )
            ),
        ];
    }

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateNoApplicablePersonSubjectFailures(
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> skippedContributors
    ) =>
        skippedContributors.Select(skippedContributor => new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
            resource,
            supportedStrategy.ConfiguredStrategy,
            supportedStrategy.RelationshipLocalOrder,
            AuthObject: skippedContributor.AuthObject,
            Location: new RelationshipAuthorizationFailureLocation(
                Kind: skippedContributor.Kind,
                JsonPath: skippedContributor.JsonPath,
                ReadableName: skippedContributor.ReadableName,
                Table: skippedContributor.Table,
                Column: skippedContributor.Column,
                AuthorizationObjectName: skippedContributor.AuthObject?.Name.ToString()
            ),
            Hint: $"Person securable element skipped by subject-scope filtering: {skippedContributor.Reason}."
        )
        {
            PersonMetadata =
                skippedContributor.PersonKind is null || skippedContributor.AuthObject is null
                    ? null
                    : new RelationshipAuthorizationPersonFailureMetadata(
                        skippedContributor.PersonKind.Value,
                        skippedContributor.AuthObject
                    ),
            SkippedContributors = [skippedContributor],
        });

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
            candidate.StoredAnchor.RootTable,
            candidate.StoredAnchor.RootDocumentIdColumn,
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

    private static SkippedPersonPath CreateSkippedPersonPath(
        ConcreteResourceModel concreteResourceModel,
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind,
        string personPath,
        int contributionOrder
    )
    {
        var location = ResolveSkippedPersonLocation(concreteResourceModel, personPath);

        return CreateSkippedPersonPath(
            securableElementKind,
            personKind,
            personPath,
            contributionOrder,
            location?.Table,
            location?.Column
        );
    }

    private static SkippedPersonPath CreateSkippedPersonPath(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind,
        string personPath,
        int contributionOrder,
        DbTableName? table,
        DbColumnName? column
    ) =>
        new(
            securableElementKind,
            personKind,
            personPath,
            ToReadableName(personPath),
            contributionOrder,
            RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope,
            table,
            column
        );

    private static SkippedPersonLocation? ResolveSkippedPersonLocation(
        ConcreteResourceModel concreteResourceModel,
        string personPath
    )
    {
        foreach (var binding in concreteResourceModel.RelationalModel.DocumentReferenceBindings)
        {
            if (
                !binding.IdentityBindings.Any(identityBinding =>
                    string.Equals(
                        identityBinding.ReferenceJsonPath.Canonical,
                        personPath,
                        StringComparison.Ordinal
                    )
                )
            )
            {
                continue;
            }

            var tableModel = FindTable(concreteResourceModel, binding.Table);
            var column = tableModel is null
                ? binding.FkColumn
                : PersonJoinPathResolver.ResolveToCanonicalColumn(tableModel, binding.FkColumn);

            return new SkippedPersonLocation(binding.Table, column);
        }

        return null;
    }

    private static bool IsSelfPersonIdentityPath(
        QualifiedResourceName resource,
        SecurableElementKind securableElementKind,
        string personResourceName,
        string personPath
    ) =>
        PersonJoinPathResolver.IsPersonResource(resource, personResourceName)
        && string.Equals(
            GetPersonResourceName(securableElementKind),
            personResourceName,
            StringComparison.Ordinal
        )
        && string.Equals(
            personPath,
            GetSelfPersonIdentityJsonPath(securableElementKind),
            StringComparison.Ordinal
        );

    private static string GetSelfPersonIdentityJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentUniqueId",
            SecurableElementKind.Contact => "$.contactUniqueId",
            SecurableElementKind.Staff => "$.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported relationship authorization person securable element kind."
            ),
        };

    private static string GetPersonResourceName(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "Student",
            SecurableElementKind.Contact => "Contact",
            SecurableElementKind.Staff => "Staff",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported relationship authorization person securable element kind."
            ),
        };

    private static DbTableModel? FindTable(ConcreteResourceModel concreteResourceModel, DbTableName table)
    {
        foreach (var tableModel in concreteResourceModel.RelationalModel.TablesInDependencyOrder)
        {
            if (tableModel.Table == table)
            {
                return tableModel;
            }
        }

        return null;
    }

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
        IReadOnlyList<UnresolvedPersonPath> UnresolvedPaths,
        IReadOnlyList<SkippedPersonPath> SkippedPaths
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

    private sealed record SkippedPersonPath(
        SecurableElementKind Kind,
        RelationshipAuthorizationPersonKind PersonKind,
        string JsonPath,
        string ReadableName,
        int ContributionOrder,
        RelationshipAuthorizationSkippedSubjectReason Reason,
        DbTableName? Table,
        DbColumnName? Column
    );

    private sealed record SkippedPersonLocation(DbTableName Table, DbColumnName Column);

    private sealed record SelectedPeopleStrategySubjects(
        IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
        IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors
    );

    private sealed record PersonPredicateKey(
        RelationshipAuthorizationPersonKind PersonKind,
        DbTableName AuthObjectName,
        DbColumnName SubjectValueColumn,
        DbTableName TerminalTable,
        DbColumnName TerminalColumn,
        RelationshipAuthorizationPersonSubjectPathKind PathKind,
        DbTableName StoredAnchorTable,
        DbColumnName StoredAnchorColumn,
        string StepKey
    );
}
