// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;
using static EdFi.DataManagementService.Backend.Plans.RelationshipAuthorizationPlanningHelpers;

namespace EdFi.DataManagementService.Backend.Plans;

public sealed record RelationalPeopleAuthorizationStrategySubjectSelection(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors
);

public sealed record RelationalPeopleAuthorizationStrategySkippedContributors(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors
);

public sealed record RelationalPeopleAuthorizationSubjectSelection(
    IReadOnlyList<RelationalPeopleAuthorizationStrategySubjectSelection> StrategySubjectSelections,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> SecurityConfigurationFailures,
    IReadOnlyList<RelationalPeopleAuthorizationStrategySkippedContributors> StrategySkippedContributors
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
            mappingSet.Model.GetConcreteResourceModelsByResource()
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
        List<RelationalPeopleAuthorizationStrategySkippedContributors> skippedContributorsByStrategy = [];

        foreach (var supportedStrategy in supportedStrategies)
        {
            var selectedSubjects = SelectStrategySubjects(
                supportedStrategy,
                resource,
                personCandidates.Candidates,
                personCandidates.SkippedPaths
            );

            if (selectedSubjects.SkippedContributors.Count > 0)
            {
                skippedContributorsByStrategy.Add(
                    new RelationalPeopleAuthorizationStrategySkippedContributors(
                        supportedStrategy.ConfiguredStrategy,
                        supportedStrategy.RelationshipLocalOrder,
                        selectedSubjects.SkippedContributors
                    )
                );
            }

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
                else
                {
                    if (!HasUnresolvedPersonPath(supportedStrategy, personCandidates.UnresolvedPaths))
                    {
                        securityConfigurationFailures.AddRange(
                            CreateNoApplicablePersonSubjectFailures(resource, supportedStrategy)
                        );
                    }
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
                subjectsByStrategy,
                [.. OrderFailures(securityConfigurationFailures)],
                skippedContributorsByStrategy
            );
        }

        return new RelationalPeopleAuthorizationSubjectSelection(
            subjectsByStrategy,
            [],
            skippedContributorsByStrategy
        );
    }

    private static bool HasUnresolvedPersonPath(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        IReadOnlyList<UnresolvedPersonPath> unresolvedPaths
    ) =>
        supportedStrategy.EligibleSubjects.Any(eligibleSubject =>
            eligibleSubject.PersonAuthViewKind is not null
            && unresolvedPaths.Any(unresolvedPath => unresolvedPath.Kind == eligibleSubject.Kind)
        );

    private static ResolvedPeopleCandidates ResolvePersonCandidates(
        ConcreteResourceModel concreteResourceModel,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        var rootTableModel = concreteResourceModel.RelationalModel.Root;
        var rootTable = rootTableModel.Table;
        var rootDocumentIdColumn = GetRootDocumentIdColumn(
            rootTableModel,
            "people relationship authorization planning"
        );
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
                PersonJoinPathResolver.IsSelfPersonIdentityPath(
                    concreteResourceModel.RelationalModel.Resource,
                    securableElementKind,
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
                        ToReadableName(unresolvedRootLevelPath),
                        contributionOrder
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
                    .Values.OrderBy(subject => GetSubjectEligibilityOrder(supportedStrategy, subject))
                    .ThenBy(static subject =>
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

    private static int GetSubjectEligibilityOrder(
        SupportedRelationshipAuthorizationStrategy supportedStrategy,
        RelationshipAuthorizationSubject subject
    )
    {
        for (
            var eligibilityOrder = 0;
            eligibilityOrder < supportedStrategy.EligibleSubjects.Count;
            eligibilityOrder++
        )
        {
            var eligibleSubject = supportedStrategy.EligibleSubjects[eligibilityOrder];

            if (eligibleSubject.PersonAuthViewKind is not { } authViewKind)
            {
                continue;
            }

            if (!subject.Contributors.Any(contributor => contributor.Kind == eligibleSubject.Kind))
            {
                continue;
            }

            var authObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);

            if (subject.AuthObject == authObject)
            {
                return eligibilityOrder;
            }
        }

        return int.MaxValue;
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
                                    unresolvedPath.ReadableName,
                                    unresolvedPath.ContributionOrder
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

    private static IEnumerable<RelationshipAuthorizationFailureMetadata> CreateNoApplicablePersonSubjectFailures(
        QualifiedResourceName resource,
        SupportedRelationshipAuthorizationStrategy supportedStrategy
    ) =>
        supportedStrategy
            .EligibleSubjects.Where(static eligibleSubject => eligibleSubject.PersonAuthViewKind is not null)
            .Select(eligibleSubject =>
            {
                var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
                    eligibleSubject.PersonAuthViewKind!.Value
                );

                return new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                    resource,
                    supportedStrategy.ConfiguredStrategy,
                    supportedStrategy.RelationshipLocalOrder,
                    AuthObject: authObject,
                    Location: new RelationshipAuthorizationFailureLocation(
                        Kind: eligibleSubject.Kind,
                        AuthorizationObjectName: authObject.Name.ToString()
                    ),
                    Hint: $"No applicable {eligibleSubject.Kind} relationship authorization subject was selected for strategy '{supportedStrategy.ConfiguredStrategy.StrategyName}'."
                )
                {
                    PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                        MapPersonKind(eligibleSubject.Kind),
                        authObject
                    ),
                };
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
        string ReadableName,
        int ContributionOrder
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
