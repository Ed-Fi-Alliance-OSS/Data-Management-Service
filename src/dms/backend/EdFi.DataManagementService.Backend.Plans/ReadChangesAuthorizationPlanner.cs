// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Isolated ReadChanges authorization planner for the /deletes and /keyChanges endpoints. Reuses the
/// shared parameterization and view inventory but owns a ReadChanges-specific strategy table:
/// only the strategies in <see cref="_supportedSubjectsByStrategyName"/> (plus NoFurtherAuthorizationRequired,
/// a no-op, and NamespaceBased, split out) are valid; any other configured strategy fails with a 500
/// security configuration outcome.
/// </summary>
public static class ReadChangesAuthorizationPlanner
{
    private sealed record ReadChangesStrategyDefinition(
        RelationshipAuthorizationHierarchyDirection Direction,
        IReadOnlyList<ReadChangesSubjectKind> Subjects
    );

    private enum ReadChangesSubjectKind
    {
        EdOrg,
        Student,
        Contact,
        Staff,
        StudentThroughResponsibility,
    }

    private static readonly IReadOnlyDictionary<
        string,
        ReadChangesStrategyDefinition
    > _supportedSubjectsByStrategyName = new Dictionary<string, ReadChangesStrategyDefinition>(
        StringComparer.Ordinal
    )
    {
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly] = new(
            RelationshipAuthorizationHierarchyDirection.Normal,
            [ReadChangesSubjectKind.EdOrg]
        ),
        [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted] = new(
            RelationshipAuthorizationHierarchyDirection.Inverted,
            [ReadChangesSubjectKind.EdOrg]
        ),
        ["RelationshipsWithEdOrgsAndPeopleIncludingDeletes"] = new(
            RelationshipAuthorizationHierarchyDirection.Normal,
            [
                ReadChangesSubjectKind.EdOrg,
                ReadChangesSubjectKind.Student,
                ReadChangesSubjectKind.Contact,
                ReadChangesSubjectKind.Staff,
            ]
        ),
        ["RelationshipsWithStudentsOnlyIncludingDeletes"] = new(
            RelationshipAuthorizationHierarchyDirection.Normal,
            [ReadChangesSubjectKind.Student]
        ),
        ["RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"] = new(
            RelationshipAuthorizationHierarchyDirection.Normal,
            [ReadChangesSubjectKind.StudentThroughResponsibility]
        ),
    };

    public static ReadChangesAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(trackedChangeTable);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);
        ArgumentNullException.ThrowIfNull(context);

        bool hasNamespace = configuredAuthorizationStrategies.Any(s =>
            string.Equals(
                s.StrategyName,
                AuthorizationStrategyNameConstants.NamespaceBased,
                StringComparison.Ordinal
            )
        );

        var relationshipStrategies = configuredAuthorizationStrategies
            .Where(s =>
                !string.Equals(
                    s.StrategyName,
                    AuthorizationStrategyNameConstants.NamespaceBased,
                    StringComparison.Ordinal
                )
                && !string.Equals(
                    s.StrategyName,
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        // Classify: any relationship strategy not in the ReadChanges table is a 500.
        string[] unavailable =
        [
            .. relationshipStrategies
                .Where(s => !_supportedSubjectsByStrategyName.ContainsKey(s.StrategyName))
                .Select(s => s.StrategyName),
        ];
        if (unavailable.Length > 0)
        {
            return new ReadChangesAuthorizationPlanOutcome.SecurityConfiguration(unavailable);
        }

        // Relationship subject resolution (Task 5). A null result means a usable-column resolution failure → 500.
        List<ReadChangesRelationshipCheckSpec> relationshipChecks = [];
        List<string> resolutionFailures = [];
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup =
            PersonJoinPathResolver.BuildResourceLookup(mappingSet.Model.ConcreteResourcesInNameOrder);
        foreach (var strategy in relationshipStrategies)
        {
            var definition = _supportedSubjectsByStrategyName[strategy.StrategyName];
            var subjects = ResolveSubjects(resource, trackedChangeTable, definition, resourceLookup);
            if (subjects is null)
            {
                resolutionFailures.Add(strategy.StrategyName);
                continue;
            }
            relationshipChecks.Add(new ReadChangesRelationshipCheckSpec(strategy, subjects));
        }
        if (resolutionFailures.Count > 0)
        {
            return new ReadChangesAuthorizationPlanOutcome.SecurityConfiguration(resolutionFailures);
        }

        // Namespace planning (Task 6).
        ReadChangesNamespaceCheckSpec? namespaceCheck = null;
        if (hasNamespace)
        {
            DbColumnName? namespaceColumn = ResolveTrackedNamespaceColumn(resource, trackedChangeTable);
            if (namespaceColumn is null)
            {
                return new ReadChangesAuthorizationPlanOutcome.SecurityConfiguration([
                    AuthorizationStrategyNameConstants.NamespaceBased,
                ]);
            }
            if (context.NamespacePrefixes.Count == 0)
            {
                return new ReadChangesAuthorizationPlanOutcome.NamespaceNoPrefixesConfigured(
                    AuthorizationStrategyNameConstants.NamespaceBased
                );
            }
            namespaceCheck = new ReadChangesNamespaceCheckSpec(namespaceColumn.Value);
        }

        var claimParameterization =
            relationshipChecks.Count == 0
                ? null
                : CreateClaimParameterization(mappingSet.Key.Dialect, context.ClaimEducationOrganizationIds);

        NamespacePrefixParameterization? namespaceParameterization = null;
        if (
            namespaceCheck is not null
            && !NamespacePrefixParameterizationFactory.TryCreate(
                mappingSet.Key.Dialect,
                context.NamespacePrefixes,
                "ReadChangesNamespacePrefix",
                out namespaceParameterization,
                out string securityConfigurationMessage,
                out _
            )
        )
        {
            return new ReadChangesAuthorizationPlanOutcome.SecurityConfiguration(
                [],
                [securityConfigurationMessage]
            );
        }

        return new ReadChangesAuthorizationPlanOutcome.Plan(
            new ReadChangesAuthorizationPlan(
                relationshipChecks,
                namespaceCheck,
                claimParameterization,
                namespaceParameterization
            )
        );
    }

    /// <summary>
    /// Builds the claim EdOrg parameterization for the relationship OR-group. With at least one claim
    /// EdOrg id this defers to the shared <see cref="AuthorizationClaimEducationOrganizationIdParameterizationFactory"/>
    /// (identical to the live single-record/page paths).
    /// <para>
    /// DMS-1188 — FAIL CLOSED for the zero-claim case. The shared factory rejects an empty claim list
    /// (it throws), but the design requires a relationship strategy with zero claim EdOrg ids to yield
    /// NO ROWS (empty result), mirroring the live path's <c>NoClaims</c> behavior — not a 500. We must
    /// also NOT return <c>null</c> here: <see cref="ReadChangesAuthorizationPlan.ClaimParameterization"/>
    /// being null while <see cref="ReadChangesAuthorizationPlan.RelationshipChecks"/> is non-empty would
    /// cause the emitter to OMIT the relationship predicate entirely (returning ALL rows = fail-open).
    /// Instead we construct a parameterization that the emitter renders as match-nothing:
    /// PostgreSQL <c>= ANY(@p)</c> against an empty array matches nothing, and SQL Server with zero
    /// scalar parameter names emits <c>IN (SELECT 1 WHERE 1 = 0)</c>. The downstream emitter,
    /// <c>AuthorizationClaimEducationOrganizationIdSqlHelper.BuildFilterParametersInOrder</c>, and the
    /// parameter binder all handle these empty shapes without throwing. The shared factory is left
    /// untouched so the live authorization paths keep their reject-empty contract.
    /// </para>
    /// </summary>
    private static AuthorizationClaimEducationOrganizationIdParameterization CreateClaimParameterization(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds
    )
    {
        if (claimEducationOrganizationIds.Count > 0)
        {
            return AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                dialect,
                claimEducationOrganizationIds,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            );
        }

        // Zero claims → dialect-specific match-nothing shape (never MssqlStructured, which an empty TVP
        // cannot represent). Mirrors the kinds the shared factory would select below its TVP threshold.
        return dialect switch
        {
            SqlDialect.Pgsql => new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds,
                [],
                [RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            ),
            SqlDialect.Mssql => new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds,
                [],
                []
            ),
            _ => throw new NotSupportedException(
                $"ReadChanges authorization does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private static IReadOnlyList<ReadChangesAuthorizationSubject>? ResolveSubjects(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        ReadChangesStrategyDefinition definition,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        List<ReadChangesAuthorizationSubject> subjects = [];

        foreach (var subjectKind in definition.Subjects)
        {
            if (subjectKind is ReadChangesSubjectKind.EdOrg)
            {
                // EdOrg subjects: every EdOrg securable element resolves to a tracked OldX column,
                // probed against the EdOrg hierarchy view (direction selects which column is matched).
                foreach (var edOrgSecurable in resource.SecurableElements.EducationOrganization)
                {
                    DbColumnName? trackedColumn = ResolveTrackedColumnForSecurable(
                        resource,
                        trackedChangeTable,
                        edOrgSecurable.JsonPath
                    );
                    if (trackedColumn is null)
                    {
                        return null;
                    }
                    bool inverted =
                        definition.Direction is RelationshipAuthorizationHierarchyDirection.Inverted;
                    subjects.Add(
                        new ReadChangesAuthorizationSubject(
                            trackedColumn.Value,
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthViewSubjectColumn: inverted
                                ? AuthNames.SourceEdOrgId
                                : AuthNames.TargetEdOrgId,
                            AuthViewClaimColumn: inverted ? AuthNames.TargetEdOrgId : AuthNames.SourceEdOrgId
                        )
                    );
                }
            }
            else
            {
                // Person subjects: use the denormalized OldX_DocumentId column + the IncludingDeletes view.
                ReadChangesAuthViewKind viewKind = subjectKind switch
                {
                    ReadChangesSubjectKind.Student => ReadChangesAuthViewKind.Student,
                    ReadChangesSubjectKind.Contact => ReadChangesAuthViewKind.Contact,
                    ReadChangesSubjectKind.Staff => ReadChangesAuthViewKind.Staff,
                    ReadChangesSubjectKind.StudentThroughResponsibility =>
                        ReadChangesAuthViewKind.StudentDeletedResponsibility,
                    _ => throw new InvalidOperationException($"Unsupported subject kind '{subjectKind}'."),
                };
                SecurableElementKind personKind = subjectKind switch
                {
                    ReadChangesSubjectKind.Student or ReadChangesSubjectKind.StudentThroughResponsibility =>
                        SecurableElementKind.Student,
                    ReadChangesSubjectKind.Contact => SecurableElementKind.Contact,
                    ReadChangesSubjectKind.Staff => SecurableElementKind.Staff,
                    _ => throw new InvalidOperationException(
                        $"Unsupported person subject kind '{subjectKind}'."
                    ),
                };

                ReadChangesAuthorizationViewInfo view =
                    AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions.Single(v =>
                        v.Kind == viewKind
                    );

                foreach (var personPath in PersonSecurablePaths(resource, personKind))
                {
                    TrackedChangeColumnInfo? personColumn = ResolveTrackedPersonColumnForSecurable(
                        resource,
                        trackedChangeTable,
                        resourceLookup,
                        personKind,
                        personPath
                    );
                    if (personColumn is null)
                    {
                        return null;
                    }

                    subjects.Add(
                        new ReadChangesAuthorizationSubject(
                            personColumn.OldColumnName,
                            view.View,
                            view.PersonDocumentIdOutputColumn,
                            view.ClaimEducationOrganizationIdColumn
                        )
                    );
                }
            }
        }

        // A supported strategy that selected no applicable subjects on this resource fails closed.
        return subjects.Count == 0 ? null : subjects;
    }

    private static IReadOnlyList<string> PersonSecurablePaths(
        ConcreteResourceModel resource,
        SecurableElementKind personKind
    ) =>
        personKind switch
        {
            SecurableElementKind.Student => resource.SecurableElements.Student,
            SecurableElementKind.Contact => resource.SecurableElements.Contact,
            SecurableElementKind.Staff => resource.SecurableElements.Staff,
            _ => [],
        };

    private static TrackedChangeColumnInfo? ResolveTrackedPersonColumnForSecurable(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        SecurableElementKind personKind,
        string personPath
    )
    {
        TrackedChangeColumnInfo[] exactPathMatches =
        [
            .. trackedChangeTable.ValueColumnsInTableOrder.Where(c =>
                IsPersonColumnForKindOrSelfPath(resource, trackedChangeTable, c, personKind)
                && string.Equals(c.SourceJsonPath, personPath, StringComparison.Ordinal)
            ),
        ];
        if (exactPathMatches.Length > 0)
        {
            return exactPathMatches.Length == 1 ? exactPathMatches[0] : null;
        }

        List<string> skippedArrayNestedPaths = [];
        var chain = PersonJoinPathResolver.ResolveShortestPersonChain(
            resource,
            [personPath],
            PersonResourceName(personKind),
            resourceLookup,
            skippedArrayNestedPaths,
            out _
        );
        if (chain is null || chain.Count == 0)
        {
            return null;
        }

        TrackedChangeColumnInfo[] joinPathMatches =
        [
            .. trackedChangeTable.ValueColumnsInTableOrder.Where(c =>
                c.PersonJoinName is not null
                && IsPersonColumnForKind(trackedChangeTable, c, personKind)
                && PersonJoinPath(trackedChangeTable, c.PersonJoinName) is { } joinPath
                && joinPath.SequenceEqual(chain)
            ),
        ];

        return joinPathMatches.Length == 1 ? joinPathMatches[0] : null;
    }

    private static string PersonResourceName(SecurableElementKind personKind) =>
        personKind switch
        {
            SecurableElementKind.Student => "Student",
            SecurableElementKind.Contact => "Contact",
            SecurableElementKind.Staff => "Staff",
            _ => throw new InvalidOperationException($"Unsupported person securable kind '{personKind}'."),
        };

    private static bool IsPersonColumnForKind(
        TrackedChangeTableInfo table,
        TrackedChangeColumnInfo column,
        SecurableElementKind personKind
    ) =>
        column.Role is TrackedChangeColumnRole.PersonDocumentId
        && column.PersonJoinName is not null
        && PersonJoinKind(table, column.PersonJoinName) == personKind;

    private static bool IsPersonColumnForKindOrSelfPath(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo table,
        TrackedChangeColumnInfo column,
        SecurableElementKind personKind
    )
    {
        if (column.Role != TrackedChangeColumnRole.PersonDocumentId)
        {
            return false;
        }

        if (column.PersonJoinName is not null)
        {
            return PersonJoinKind(table, column.PersonJoinName) == personKind;
        }

        if (
            column.CanonicalStorageColumn is not { } canonicalStorageColumn
            || !canonicalStorageColumn.Equals(new DbColumnName("DocumentId"))
        )
        {
            return false;
        }

        return PersonJoinPathResolver.IsSelfPersonIdentityPath(
            resource.RelationalModel.Resource,
            personKind,
            column.SourceJsonPath
        );
    }

    private static SecurableElementKind? PersonJoinKind(
        TrackedChangeTableInfo table,
        string personJoinName
    ) =>
        table
            .PersonJoins.Where(j => string.Equals(j.PersonJoinName, personJoinName, StringComparison.Ordinal))
            .Select(j => (SecurableElementKind?)j.PersonKind)
            .SingleOrDefault();

    private static IReadOnlyList<ColumnPathStep>? PersonJoinPath(
        TrackedChangeTableInfo table,
        string personJoinName
    ) =>
        table
            .PersonJoins.Where(j => string.Equals(j.PersonJoinName, personJoinName, StringComparison.Ordinal))
            .Select(j => j.JoinPath)
            .SingleOrDefault();

    /// <summary>
    /// Maps a securable-element JSON path to its tracked-change OldX column by matching the live root
    /// column the securable resolves to against each tracked column's canonical/source identity.
    /// </summary>
    private static DbColumnName? ResolveTrackedColumnForSecurable(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        string securableJsonPath
    )
    {
        ColumnPathStep? step = SecurableElementLocationResolver.ResolvePreferred(resource, securableJsonPath);
        DbColumnName? liveColumn =
            step is not null && step.SourceTable == resource.RelationalModel.Root.Table
                ? step.SourceColumnName
                : null;

        foreach (
            var column in trackedChangeTable.ValueColumnsInTableOrder.Where(c =>
                c.Origin.HasFlag(TrackedChangeColumnOrigin.SecurableElement)
                && c.Role is TrackedChangeColumnRole.Scalar
            )
        )
        {
            if (string.Equals(column.SourceJsonPath, securableJsonPath, StringComparison.Ordinal))
            {
                return column.OldColumnName;
            }
            if (liveColumn is { } resolvedLive && column.CanonicalStorageColumn == resolvedLive)
            {
                return column.OldColumnName;
            }
        }

        return null;
    }

    private static DbColumnName? ResolveTrackedNamespaceColumn(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable
    )
    {
        // Shared-descriptor resources persist Namespace on the shared descriptor tracked-change table.
        if (
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            && resource.DescriptorMetadata is { } descriptorMetadata
        )
        {
            DbColumnName liveNamespace = descriptorMetadata.ColumnContract.Namespace;
            var descriptorMatch = trackedChangeTable.ValueColumnsInTableOrder.FirstOrDefault(c =>
                c.CanonicalStorageColumn == liveNamespace
                || string.Equals(c.SourceJsonPath, "$.namespace", StringComparison.Ordinal)
            );
            return descriptorMatch?.OldColumnName;
        }

        foreach (var namespacePath in resource.SecurableElements.Namespace)
        {
            DbColumnName? trackedColumn = ResolveTrackedColumnForSecurable(
                resource,
                trackedChangeTable,
                namespacePath
            );
            if (trackedColumn is not null)
            {
                return trackedColumn;
            }
        }

        return null;
    }
}
