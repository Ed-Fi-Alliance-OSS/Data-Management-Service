// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class ReferenceDerivedWritePlanFixture
{
    private static readonly QualifiedResourceName _defaultResource = new("Ed-Fi", "Program");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _schoolCategoryDescriptorResource = new(
        "Ed-Fi",
        "SchoolCategoryDescriptor"
    );

    public static RelationalResourceModel CreateModel(QualifiedResourceName? resource = null)
    {
        var resolvedResource = resource ?? _defaultResource;
        var resourceNameToken = resolvedResource.ResourceName.Replace(
            "-",
            string.Empty,
            StringComparison.Ordinal
        );
        var schoolReferencePath = Path("$.schoolReference");
        var schoolIdPath = Path("$.schoolReference.schoolId");
        var schoolYearPath = Path("$.schoolReference.schoolYear");
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), $"{resourceNameToken}ReferenceDerived"),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceNameToken}ReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolYearPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolId_Canonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolIdAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolIdPath,
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                        PresenceColumn: new DbColumnName("School_DocumentId")
                    )
                ),
            ],
            Constraints: []
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                    MemberPathColumns: [new DbColumnName("School_RefSchoolIdAlias")]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: resolvedResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: table.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolIdPath,
                            Column: new DbColumnName("School_RefSchoolIdAlias")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolYearPath,
                            Column: new DbColumnName("School_RefSchoolYear")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    public static ResourceWritePlan CreateWritePlan(QualifiedResourceName? resource = null)
    {
        return CreateWritePlan(CreateModel(resource));
    }

    public static RelationalResourceModel CreateMixedSourceModel(QualifiedResourceName? resource = null)
    {
        var model = CreateModel(resource);
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolId_LocalAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: Path("$.localSchoolId"),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                        PresenceColumn: null
                    )
                ),
            ],
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                    MemberPathColumns:
                    [
                        new DbColumnName("School_RefSchoolIdAlias"),
                        new DbColumnName("SchoolId_LocalAlias"),
                    ]
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    public static ResourceWritePlan CreateWritePlan(RelationalResourceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var table = model.Root;

        DbColumnModel Column(string name)
        {
            return table.Columns.Single(column =>
                string.Equals(column.ColumnName.Value, name, StringComparison.Ordinal)
            );
        }

        return new ResourceWritePlan(
            Model: model,
            TablePlansInDependencyOrder:
            [
                new TableWritePlan(
                    TableModel: table,
                    InsertSql: $"INSERT INTO [edfi].[{table.Table.Name}] ([DocumentId], [School_DocumentId], [School_RefSchoolYear], [SchoolId_Canonical])\nVALUES (@documentId, @schoolDocumentId, @schoolRefSchoolYear, @schoolIdCanonical);",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(
                        MaxRowsPerBatch: 525,
                        ParametersPerRow: 4,
                        MaxParametersPerCommand: 2100
                    ),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            Column: Column("DocumentId"),
                            Source: new WriteValueSource.DocumentId(),
                            ParameterName: "documentId"
                        ),
                        new WriteColumnBinding(
                            Column: Column("School_DocumentId"),
                            Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                            ParameterName: "schoolDocumentId"
                        ),
                        new WriteColumnBinding(
                            Column: Column("School_RefSchoolYear"),
                            Source: new WriteValueSource.ReferenceDerived(
                                ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                                    BindingIndex: 0,
                                    ReferenceObjectPath: Path("$.schoolReference"),
                                    ReferenceJsonPath: Path("$.schoolReference.schoolYear")
                                )
                            ),
                            ParameterName: "schoolRefSchoolYear"
                        ),
                        new WriteColumnBinding(
                            Column: Column("SchoolId_Canonical"),
                            Source: new WriteValueSource.Precomputed(),
                            ParameterName: "schoolIdCanonical"
                        ),
                    ],
                    KeyUnificationPlans:
                    [
                        new KeyUnificationWritePlan(
                            CanonicalColumn: new DbColumnName("SchoolId_Canonical"),
                            CanonicalBindingIndex: 3,
                            MembersInOrder:
                            [
                                new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
                                    MemberPathColumn: new DbColumnName("School_RefSchoolIdAlias"),
                                    RelativePath: Path("$.schoolReference.schoolId"),
                                    ReferenceSource: new ReferenceDerivedValueSourceMetadata(
                                        BindingIndex: 0,
                                        ReferenceObjectPath: Path("$.schoolReference"),
                                        ReferenceJsonPath: Path("$.schoolReference.schoolId")
                                    ),
                                    PresenceColumn: new DbColumnName("School_DocumentId"),
                                    PresenceBindingIndex: 1,
                                    PresenceIsSynthetic: false
                                ),
                            ]
                        ),
                    ]
                ),
            ]
        );
    }

    public static RelationalResourceModel CreateDescriptorBackedModel(QualifiedResourceName? resource = null)
    {
        var resolvedResource = resource ?? _defaultResource;
        var resourceNameToken = resolvedResource.ResourceName.Replace(
            "-",
            string.Empty,
            StringComparison.Ordinal
        );
        var schoolReferencePath = Path("$.schoolReference");
        var schoolCategoryDescriptorPath = Path("$.schoolReference.schoolCategoryDescriptor");
        var table = new DbTableModel(
            Table: new DbTableName(
                new DbSchemaName("edfi"),
                $"{resourceNameToken}DescriptorReferenceDerived"
            ),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceNameToken}DescriptorReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolCategoryDescriptorPath,
                    TargetResource: _schoolCategoryDescriptorResource
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: resolvedResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: table.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolCategoryDescriptorPath,
                            Column: new DbColumnName("SchoolCategoryDescriptorId")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    public static RelationalResourceModel CreateDescriptorBackedKeyUnificationModel(
        QualifiedResourceName? resource = null
    )
    {
        var resolvedResource = resource ?? _defaultResource;
        var resourceNameToken = resolvedResource.ResourceName.Replace(
            "-",
            string.Empty,
            StringComparison.Ordinal
        );
        var schoolReferencePath = Path("$.schoolReference");
        var schoolCategoryDescriptorPath = Path("$.schoolReference.schoolCategoryDescriptor");
        var table = new DbTableModel(
            Table: new DbTableName(
                new DbSchemaName("edfi"),
                $"{resourceNameToken}DescriptorKeyUnificationReferenceDerived"
            ),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{resourceNameToken}DescriptorKeyUnificationReferenceDerived",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: _schoolCategoryDescriptorResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolCategoryDescriptorId_Alias"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolCategoryDescriptorPath,
                    TargetResource: _schoolCategoryDescriptorResource,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                        PresenceColumn: new DbColumnName("School_DocumentId")
                    )
                ),
            ],
            Constraints: []
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolCategoryDescriptorId_Canonical"),
                    MemberPathColumns: [new DbColumnName("SchoolCategoryDescriptorId_Alias")]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: resolvedResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: table.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolCategoryDescriptorPath,
                            Column: new DbColumnName("SchoolCategoryDescriptorId_Alias")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    private static JsonPathExpression Path(string value)
    {
        return JsonPathExpressionCompiler.Compile(value);
    }
}
