// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class CompactDescriptorReferentialIdentityTestModel
{
    public static DocumentIdentity CreateDescriptorIdentity(string descriptorUri) =>
        new([
            IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                descriptorUri
            ),
        ]);

    public static DocumentIdentity CreateIdentity(string descriptorUri) =>
        new([
            IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new("$.programTypeDescriptor"),
                descriptorUri
            ),
            IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new("$.programReference.programTypeDescriptor"),
                descriptorUri
            ),
        ]);

    public static DerivedRelationalModelSet Build(SqlDialect dialect, bool unifiedReferenceIdentity = false)
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "ProgramOffering");
        var documentIdColumn = new DbColumnName("DocumentId");
        var programTypeDescriptorColumn = new DbColumnName("ProgramTypeDescriptor_DescriptorId");
        var graduationPlanTypeDescriptorColumn = new DbColumnName(
            "GraduationPlanTypeDescriptor_DescriptorId"
        );
        var resource = new QualifiedResourceName("Ed-Fi", "ProgramOffering");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var rootTable = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_ProgramOffering", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    programTypeDescriptorColumn,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.programTypeDescriptor", []),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
                new DbColumnModel(
                    graduationPlanTypeDescriptorColumn,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.graduationPlanTypeDescriptor", []),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "GraduationPlanTypeDescriptor")
                ),
            ],
            []
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_ProgramOffering_ReferentialIdentity"),
                tableName,
                [documentIdColumn],
                [programTypeDescriptorColumn, graduationPlanTypeDescriptorColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "ProgramOffering",
                    [
                        new IdentityElementMapping(
                            programTypeDescriptorColumn,
                            "$.programTypeDescriptor",
                            new RelationalScalarType(ScalarKind.Int32),
                            IsDescriptorReference: true
                        ),
                        new IdentityElementMapping(
                            graduationPlanTypeDescriptorColumn,
                            "$.graduationPlanTypeDescriptor",
                            new RelationalScalarType(ScalarKind.Int32),
                            IsDescriptorReference: true
                        ),
                    ]
                )
            ),
        ];

        var modelSet = new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            triggers
        );

        if (!unifiedReferenceIdentity)
        {
            return modelSet;
        }

        var copiedDescriptorColumn = new DbColumnName("Program_ProgramTypeDescriptor_DescriptorId");
        var programDocumentIdColumn = new DbColumnName("Program_DocumentId");
        var copiedDescriptorPath = new JsonPathExpression("$.programReference.programTypeDescriptor", []);
        var unifiedRoot = rootTable with
        {
            Columns =
            [
                rootTable.Columns[0],
                rootTable.Columns[1],
                rootTable.Columns[1] with
                {
                    ColumnName = copiedDescriptorColumn,
                    SourceJsonPath = copiedDescriptorPath,
                    Storage = new ColumnStorage.UnifiedAlias(programTypeDescriptorColumn, null),
                    IsWritable = false,
                },
                new DbColumnModel(
                    programDocumentIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Program")
                ),
            ],
        };
        var unifiedResource = relationalModel with
        {
            Root = unifiedRoot,
            TablesInDependencyOrder = [unifiedRoot],
            DocumentReferenceBindings =
            [
                new DocumentReferenceBinding(
                    true,
                    new JsonPathExpression("$.programReference", []),
                    tableName,
                    programDocumentIdColumn,
                    new QualifiedResourceName("Ed-Fi", "Program"),
                    [
                        new(
                            new JsonPathExpression("$.programTypeDescriptor", []),
                            copiedDescriptorPath,
                            copiedDescriptorColumn
                        ),
                    ]
                ),
            ],
        };
        var primaryIdentity = (TriggerKindParameters.ReferentialIdentityMaintenance)triggers[0].Parameters;
        IReadOnlyList<IdentityElementMapping> unifiedElements =
        [
            primaryIdentity.IdentityElements[0],
            primaryIdentity.IdentityElements[0] with
            {
                Column = copiedDescriptorColumn,
                IdentityJsonPath = copiedDescriptorPath.Canonical,
            },
        ];
        var superclassKey = new ResourceKeyEntry(2, new("Ed-Fi", "GeneralProgramOffering"), "1.0.0", true);

        return modelSet with
        {
            EffectiveSchema = modelSet.EffectiveSchema with
            {
                ResourceKeyCount = 2,
                ResourceKeysInIdOrder = [resourceKey, superclassKey],
            },
            ConcreteResourcesInNameOrder =
            [
                new(resourceKey, ResourceStorageKind.RelationalTables, unifiedResource),
            ],
            AbstractUnionViewsInNameOrder =
            [
                new(
                    superclassKey,
                    new(schema, "GeneralProgramOffering_View"),
                    [
                        new(documentIdColumn, new(ScalarKind.Int64), null, null),
                        new(new("Discriminator"), new(ScalarKind.String), null, null),
                        new(
                            programTypeDescriptorColumn,
                            new(ScalarKind.Int32),
                            rootTable.Columns[1].SourceJsonPath,
                            rootTable.Columns[1].TargetResource,
                            true
                        ),
                        new(
                            copiedDescriptorColumn,
                            new(ScalarKind.Int32),
                            copiedDescriptorPath,
                            rootTable.Columns[1].TargetResource,
                            true
                        ),
                    ],
                    [
                        new(
                            resourceKey,
                            tableName,
                            [
                                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                                new AbstractUnionViewProjectionExpression.StringLiteral(
                                    "Ed-Fi.ProgramOffering"
                                ),
                                new AbstractUnionViewProjectionExpression.SourceColumn(
                                    programTypeDescriptorColumn
                                ),
                                new AbstractUnionViewProjectionExpression.SourceColumn(
                                    copiedDescriptorColumn
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            TriggersInCreateOrder =
            [
                triggers[0] with
                {
                    IdentityProjectionColumns = [programTypeDescriptorColumn],
                    Parameters = primaryIdentity with
                    {
                        IdentityElements = unifiedElements,
                        SuperclassAlias = new(2, "Ed-Fi", "GeneralProgramOffering", unifiedElements),
                    },
                },
            ],
        };
    }
}
