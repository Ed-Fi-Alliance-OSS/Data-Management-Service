// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class DocumentCacheMaterializerLinkBearingMappingSet
{
    private const short StudentSchoolAssociationResourceKeyId = 11;
    private const short SchoolResourceKeyId = 30;

    private static readonly QualifiedResourceName StudentSchoolAssociationResource = new(
        "Ed-Fi",
        "StudentSchoolAssociation"
    );
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static MappingSet Create(SqlDialect dialect)
    {
        var readPlan = CreateReadPlan(dialect);
        var studentSchoolAssociationKey = new ResourceKeyEntry(
            StudentSchoolAssociationResourceKeyId,
            StudentSchoolAssociationResource,
            "1.0",
            false
        );
        var schoolKey = new ResourceKeyEntry(SchoolResourceKeyId, SchoolResource, "1.0", false);
        var studentSchoolAssociationModel = new ConcreteResourceModel(
            studentSchoolAssociationKey,
            ResourceStorageKind.RelationalTables,
            readPlan.Model
        );
        var schoolModel = new ConcreteResourceModel(
            schoolKey,
            ResourceStorageKind.RelationalTables,
            CreateSchoolRelationalModel()
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 2,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [studentSchoolAssociationKey, schoolKey]
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [schoolModel, studentSchoolAssociationModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [StudentSchoolAssociationResource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [StudentSchoolAssociationResource] = StudentSchoolAssociationResourceKeyId,
                [SchoolResource] = SchoolResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [StudentSchoolAssociationResourceKeyId] = studentSchoolAssociationKey,
                [SchoolResourceKeyId] = schoolKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(SqlDialect dialect)
    {
        var rootTable = CreateStudentSchoolAssociationRootTable();
        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );
        var schoolReferenceSchoolIdPath = new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );
        var model = new RelationalResourceModel(
            StudentSchoolAssociationResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: SchoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: schoolReferenceSchoolIdPath,
                            ReferenceJsonPath: schoolReferenceSchoolIdPath,
                            Column: new DbColumnName("SchoolReference_SchoolId")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            model,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [
                new TableReadPlan(
                    rootTable,
                    SelectStudentSchoolAssociationByKeysetSql(dialect),
                    SelectStudentSchoolAssociationBySingleDocumentSql(dialect)
                ),
            ],
            [
                new ReferenceIdentityProjectionTablePlan(
                    rootTable.Table,
                    [
                        new ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: true,
                            ReferenceObjectPath: schoolReferencePath,
                            TargetResource: SchoolResource,
                            FkColumnOrdinal: 1,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ReferenceIdentityProjectionFieldOrdinal(
                                    ReferenceJsonPath: schoolReferenceSchoolIdPath,
                                    ColumnOrdinal: 2,
                                    ScalarType: new RelationalScalarType(ScalarKind.Int32)
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            [],
            new DocumentReferenceLookupPlan(
                SelectByKeysetSql: SelectSchoolReferenceLookupByKeysetSql(dialect),
                ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
                SourcesInOrder:
                [
                    new DocumentReferenceLookupSource(rootTable.Table, new DbColumnName("School_DocumentId")),
                ],
                SelectBySingleDocumentSql: SelectSchoolReferenceLookupBySingleDocumentSql(dialect)
            )
        );
    }

    private static RelationalResourceModel CreateSchoolRelationalModel()
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new RelationalResourceModel(
            SchoolResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );
    }

    private static DbTableModel CreateStudentSchoolAssociationRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    SchoolResource
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

    private static string SelectStudentSchoolAssociationByKeysetSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    source."DocumentId",
                    source."School_DocumentId",
                    source."SchoolReference_SchoolId"
                FROM edfi."StudentSchoolAssociation" source
                INNER JOIN "page" keyset
                    ON source."DocumentId" = keyset."DocumentId"
                ORDER BY source."DocumentId";
                """,
            SqlDialect.Mssql => """
                SELECT
                    source.[DocumentId],
                    source.[School_DocumentId],
                    source.[SchoolReference_SchoolId]
                FROM [edfi].[StudentSchoolAssociation] source
                INNER JOIN [#page] keyset
                    ON source.[DocumentId] = keyset.[DocumentId]
                ORDER BY source.[DocumentId];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static string? SelectStudentSchoolAssociationBySingleDocumentSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    source."DocumentId",
                    source."School_DocumentId",
                    source."SchoolReference_SchoolId"
                FROM edfi."StudentSchoolAssociation" source
                WHERE source."DocumentId" = @DocumentId
                ORDER BY source."DocumentId";
                """,
            SqlDialect.Mssql => null,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static string SelectSchoolReferenceLookupByKeysetSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT DISTINCT
                    referenced."DocumentId",
                    referenced."DocumentUuid",
                    referenced."ResourceKeyId"
                FROM edfi."StudentSchoolAssociation" source
                INNER JOIN "page" keyset
                    ON source."DocumentId" = keyset."DocumentId"
                INNER JOIN dms."Document" referenced
                    ON referenced."DocumentId" = source."School_DocumentId"
                ORDER BY referenced."DocumentId";
                """,
            SqlDialect.Mssql => """
                SELECT DISTINCT
                    referenced.[DocumentId],
                    referenced.[DocumentUuid],
                    referenced.[ResourceKeyId]
                FROM [edfi].[StudentSchoolAssociation] source
                INNER JOIN [#page] keyset
                    ON source.[DocumentId] = keyset.[DocumentId]
                INNER JOIN [dms].[Document] referenced
                    ON referenced.[DocumentId] = source.[School_DocumentId]
                ORDER BY referenced.[DocumentId];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static string? SelectSchoolReferenceLookupBySingleDocumentSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT DISTINCT
                    referenced."DocumentId",
                    referenced."DocumentUuid",
                    referenced."ResourceKeyId"
                FROM edfi."StudentSchoolAssociation" source
                INNER JOIN dms."Document" referenced
                    ON referenced."DocumentId" = source."School_DocumentId"
                WHERE source."DocumentId" = @DocumentId
                ORDER BY referenced."DocumentId";
                """,
            SqlDialect.Mssql => null,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };
}
