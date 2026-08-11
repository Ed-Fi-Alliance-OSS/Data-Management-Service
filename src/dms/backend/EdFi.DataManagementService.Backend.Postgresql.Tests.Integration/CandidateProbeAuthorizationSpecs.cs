// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Builds one authorization specification per shape the page candidate compiler emits, for the
/// candidate uniqueness probes.
/// </summary>
/// <remarks>
/// The auth objects come from the production contracts rather than from hardcoded names, so the probe
/// seeds and queries exactly the tables and columns the compiled SQL references.
/// </remarks>
internal static class CandidateProbeAuthorizationSpecs
{
    public static RelationshipAuthorizationAuthObject EdOrgAuthObject { get; } =
        RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );

    public static RelationshipAuthorizationAuthObject InvertedEdOrgAuthObject { get; } =
        RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Inverted
        );

    public static RelationshipAuthorizationAuthObject SelfPersonAuthObject { get; } =
        RelationshipAuthorizationAuthObject.CreatePerson(RelationshipAuthorizationPersonAuthViewKind.Student);

    public static PageDocumentIdAuthorizationSpec RelationshipEdOrg(
        SqlDialect dialect,
        DbTableName rootTable,
        DbColumnName schoolIdColumn,
        IReadOnlyList<long> claimEducationOrganizationIds
    ) =>
        new(
            Strategies:
            [
                new PageDocumentIdAuthorizationStrategy(
                    "RelationshipsWithEdOrgsOnly",
                    [CreateEdOrgSubject(rootTable, schoolIdColumn, EdOrgAuthObject)]
                ),
            ],
            ClaimEducationOrganizationIdParameterization: CreateClaimParameterization(
                dialect,
                claimEducationOrganizationIds
            )
        );

    public static PageDocumentIdAuthorizationSpec TwoRelationshipStrategies(
        SqlDialect dialect,
        DbTableName rootTable,
        DbColumnName schoolIdColumn,
        IReadOnlyList<long> claimEducationOrganizationIds
    ) =>
        new(
            Strategies:
            [
                new PageDocumentIdAuthorizationStrategy(
                    "RelationshipsWithEdOrgsOnly",
                    [CreateEdOrgSubject(rootTable, schoolIdColumn, EdOrgAuthObject)]
                ),
                new PageDocumentIdAuthorizationStrategy(
                    "RelationshipsWithEdOrgsOnlyInverted",
                    [CreateEdOrgSubject(rootTable, schoolIdColumn, InvertedEdOrgAuthObject)]
                ),
            ],
            ClaimEducationOrganizationIdParameterization: CreateClaimParameterization(
                dialect,
                claimEducationOrganizationIds
            )
        );

    public static PageDocumentIdAuthorizationSpec RelationshipSelfPerson(
        SqlDialect dialect,
        DbTableName rootTable,
        DbColumnName documentIdColumn,
        IReadOnlyList<long> claimEducationOrganizationIds
    ) =>
        new(
            Strategies:
            [
                new PageDocumentIdAuthorizationStrategy(
                    "RelationshipsWithStudentsOnly",
                    [
                        new PageDocumentIdAuthorizationPersonSubject(
                            rootTable,
                            documentIdColumn,
                            SelfPersonAuthObject,
                            [],
                            new RelationshipAuthorizationPersonSubjectMetadata(
                                RelationshipAuthorizationPersonKind.Student,
                                new RelationshipAuthorizationPersonSubjectPath(
                                    RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                                    []
                                ),
                                new RelationshipAuthorizationPersonStoredAnchor(rootTable, documentIdColumn),
                                ProposedAnchor: null
                            )
                        ),
                    ]
                ),
            ],
            ClaimEducationOrganizationIdParameterization: CreateClaimParameterization(
                dialect,
                claimEducationOrganizationIds
            )
        );

    public static PageDocumentIdAuthorizationSpec Namespace(
        SqlDialect dialect,
        DbTableName rootTable,
        DbColumnName namespaceColumn,
        string authorizedPrefix
    ) =>
        new(
            Strategies: [],
            NamespaceChecks:
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    rootTable,
                    namespaceColumn
                ),
            ],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                dialect,
                [authorizedPrefix],
                "namespacePrefixes"
            )
        );

    public static PageDocumentIdAuthorizationSpec SingleStepCustomView(
        DbTableName rootTable,
        DbColumnName documentIdColumn,
        DbTableName customViewTable
    ) =>
        new(
            Strategies: [],
            CustomViewChecks:
            [
                new PageDocumentIdAuthorizationCustomViewCheck(
                    "CandidateProbeView",
                    0,
                    customViewTable,
                    documentIdColumn,
                    [new ColumnPathStep(rootTable, documentIdColumn, null, null)],
                    rootTable,
                    documentIdColumn
                ),
            ]
        );

    public static PageDocumentIdAuthorizationSpec MultiStepCustomView(
        DbTableName rootTable,
        DbColumnName documentIdColumn,
        DbTableName childTable,
        DbColumnName childPersonColumn,
        DbTableName customViewTable
    ) =>
        new(
            Strategies: [],
            CustomViewChecks:
            [
                new PageDocumentIdAuthorizationCustomViewCheck(
                    "CandidateProbeView",
                    0,
                    customViewTable,
                    documentIdColumn,
                    [
                        new ColumnPathStep(rootTable, documentIdColumn, childTable, documentIdColumn),
                        new ColumnPathStep(childTable, childPersonColumn, null, null),
                    ],
                    rootTable,
                    documentIdColumn
                ),
            ]
        );

    public static PageDocumentIdAuthorizationSpec NamespaceAndCustomView(
        SqlDialect dialect,
        DbTableName rootTable,
        DbColumnName namespaceColumn,
        DbColumnName documentIdColumn,
        DbTableName customViewTable,
        string authorizedPrefix
    ) =>
        new(
            Strategies: [],
            NamespaceChecks:
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    rootTable,
                    namespaceColumn
                ),
            ],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                dialect,
                [authorizedPrefix],
                "namespacePrefixes"
            ),
            CustomViewChecks:
            [
                new PageDocumentIdAuthorizationCustomViewCheck(
                    "CandidateProbeView",
                    1,
                    customViewTable,
                    documentIdColumn,
                    [new ColumnPathStep(rootTable, documentIdColumn, null, null)],
                    rootTable,
                    documentIdColumn
                ),
            ]
        );

    private static PageDocumentIdAuthorizationEdOrgSubject CreateEdOrgSubject(
        DbTableName rootTable,
        DbColumnName schoolIdColumn,
        RelationshipAuthorizationAuthObject authObject
    ) => new(rootTable, schoolIdColumn, authObject, []);

    private static AuthorizationClaimEducationOrganizationIdParameterization CreateClaimParameterization(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds
    ) =>
        AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            claimEducationOrganizationIds,
            "ClaimEducationOrganizationIds"
        );
}
