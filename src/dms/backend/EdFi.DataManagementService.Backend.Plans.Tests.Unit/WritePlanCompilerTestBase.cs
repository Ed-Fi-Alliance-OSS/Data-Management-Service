// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

public abstract class WritePlanCompilerTestBase
{
    protected RelationalResourceModel _supportedRootOnlyModel = null!;

    [SetUp]
    protected void Setup()
    {
        _supportedRootOnlyModel = CreateSupportedRootOnlyModel();
    }

    protected static RelationalResourceModel CreateSupportedRootOnlyModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
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
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("LocalEducationAgencyId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.localEducationAgencyId",
                        [new JsonPathSegment.Property("localEducationAgencyId")]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYear"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    protected static RelationalResourceModel CreateSupportedRootOnlyModelWithNonWritableSchoolYear()
    {
        var model = CreateSupportedRootOnlyModel();
        var schoolYearColumnName = new DbColumnName("SchoolYear");
        var rootTable = model.Root with
        {
            Columns = model
                .Root.Columns.Select(column =>
                    column.ColumnName.Equals(schoolYearColumnName)
                        ? column with
                        {
                            IsWritable = false,
                        }
                        : column
                )
                .ToArray(),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyKeyOnlyModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
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
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithUpdateKeyParameterNameCollision()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentCollision"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_StudentCollision",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("documentId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: CreatePath(
                        "$.documentIdShadow",
                        new JsonPathSegment.Property("documentIdShadow")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear")),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("GradeLevel"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath("$.gradeLevel", new JsonPathSegment.Property("gradeLevel")),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentCollision"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithKeyUnificationClass()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYear"),
                    MemberPathColumns: [new DbColumnName("SchoolYear"), new DbColumnName("SchoolYearAlias")]
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithCompiledKeyUnificationInventory()
    {
        return CreateRootOnlyModelWithCompiledKeyUnificationInventoryCore(
            useSyntheticPresenceColumn: true,
            includeNullOrTrueConstraintForSyntheticPresence: true
        );
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithNonWritableStoredKeyAndPrecomputedTargets()
    {
        var model = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var rootTable = model.Root with
        {
            Columns = model
                .Root.Columns.Select(column =>
                    column.Storage is ColumnStorage.Stored ? column with { IsWritable = false } : column
                )
                .ToArray(),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithMissingSyntheticPresenceConstraint()
    {
        return CreateRootOnlyModelWithCompiledKeyUnificationInventoryCore(
            useSyntheticPresenceColumn: true,
            includeNullOrTrueConstraintForSyntheticPresence: false
        );
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithReferenceSitePresence()
    {
        return CreateRootOnlyModelWithCompiledKeyUnificationInventoryCore(
            useSyntheticPresenceColumn: false,
            includeNullOrTrueConstraintForSyntheticPresence: false
        );
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithReferenceGroupDocumentFkPresence(
        bool useNullPresenceSourceJsonPath
    )
    {
        var model = CreateRootOnlyModelWithReferenceSitePresence();
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var referencePresenceColumn = new DbColumnName("School_DocumentId");
        var previousPresenceColumn = new DbColumnName("SchoolYearTypeDescriptorSecondary_Present");
        var secondaryDescriptorColumn = new DbColumnName("SchoolYearTypeDescriptorSecondary");
        JsonPathExpression? referenceSourcePath = useNullPresenceSourceJsonPath
            ? null
            : CreatePath("$.schoolReference", new JsonPathSegment.Property("schoolReference"));

        DbColumnModel MapColumn(DbColumnModel column)
        {
            if (column.ColumnName.Equals(previousPresenceColumn))
            {
                return new DbColumnModel(
                    ColumnName: referencePresenceColumn,
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: referenceSourcePath,
                    TargetResource: schoolResource
                );
            }

            if (column.ColumnName.Equals(secondaryDescriptorColumn))
            {
                return column with
                {
                    Storage = new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYearTypeDescriptorIdCanonical"),
                        PresenceColumn: referencePresenceColumn
                    ),
                };
            }

            return column;
        }

        var rootTable = model.Root with { Columns = [.. model.Root.Columns.Select(MapColumn)] };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
            DocumentReferenceBindings =
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: CreatePath(
                        "$.schoolReference",
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: rootTable.Table,
                    FkColumn: referencePresenceColumn,
                    TargetResource: schoolResource,
                    IdentityBindings: []
                ),
            ],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithCompiledKeyUnificationInventoryCore(
        bool useSyntheticPresenceColumn,
        bool includeNullOrTrueConstraintForSyntheticPresence
    )
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor");
        var syntheticPresenceColumn = new DbColumnName("SchoolYearTypeDescriptorSecondary_Present");
        JsonPathExpression? syntheticPresencePath = useSyntheticPresenceColumn
            ? null
            : CreatePath(
                "$.localSchoolYearTypeDescriptorPresent",
                new JsonPathSegment.Property("localSchoolYearTypeDescriptorPresent")
            );
        var constraints =
            useSyntheticPresenceColumn && includeNullOrTrueConstraintForSyntheticPresence
                ? new TableConstraint[]
                {
                    new TableConstraint.NullOrTrue(
                        Name: "CK_Student_SchoolYearTypeDescriptorSecondary_Present_NullOrTrue",
                        Column: syntheticPresenceColumn
                    ),
                }
                : [];

        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_Student",
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
                    ColumnName: new DbColumnName("SchoolYearCanonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearTypeDescriptorIdCanonical"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: descriptorResource
                ),
                new DbColumnModel(
                    ColumnName: syntheticPresenceColumn,
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: true,
                    SourceJsonPath: syntheticPresencePath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearPrimary"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear")),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYearCanonical"),
                        PresenceColumn: null
                    )
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearSecondary"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.localSchoolYear",
                        new JsonPathSegment.Property("localSchoolYear")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYearCanonical"),
                        PresenceColumn: null
                    )
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearTypeDescriptorPrimary"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.schoolYearTypeDescriptor",
                        new JsonPathSegment.Property("schoolYearTypeDescriptor")
                    ),
                    TargetResource: descriptorResource,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYearTypeDescriptorIdCanonical"),
                        PresenceColumn: null
                    )
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearTypeDescriptorSecondary"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.localSchoolYearTypeDescriptor",
                        new JsonPathSegment.Property("localSchoolYearTypeDescriptor")
                    ),
                    TargetResource: descriptorResource,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYearTypeDescriptorIdCanonical"),
                        PresenceColumn: syntheticPresenceColumn
                    )
                ),
            ],
            Constraints: constraints
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYearCanonical"),
                    MemberPathColumns:
                    [
                        new DbColumnName("SchoolYearSecondary"),
                        new DbColumnName("SchoolYearPrimary"),
                    ]
                ),
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYearTypeDescriptorIdCanonical"),
                    MemberPathColumns:
                    [
                        new DbColumnName("SchoolYearTypeDescriptorSecondary"),
                        new DbColumnName("SchoolYearTypeDescriptorPrimary"),
                    ]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithStoredPrecomputedNonKeyColumn()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("CanonicalSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithOrphanPrecomputedBinding()
    {
        var model = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearCanonicalOrphan"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithDuplicatePrecomputedProducer()
    {
        var model = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var rootTable = model.Root with
        {
            KeyUnificationClasses =
            [
                .. model.Root.KeyUnificationClasses,
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYearCanonical"),
                    MemberPathColumns: [new DbColumnName("SchoolYearPrimary")]
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithStoredKeyUnificationMemberPathColumn()
    {
        var model = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var storedMemberPathColumn = new DbColumnName("SchoolYearSecondary");
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns.Select(column =>
                    column.ColumnName.Equals(storedMemberPathColumn)
                        ? column with
                        {
                            Storage = new ColumnStorage.Stored(),
                        }
                        : column
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithUnsupportedKeyUnificationMemberPathColumnKind()
    {
        var model = CreateRootOnlyModelWithCompiledKeyUnificationInventory();
        var unsupportedMemberPathColumn = new DbColumnName("SchoolYearSecondary");
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns.Select(column =>
                    column.ColumnName.Equals(unsupportedMemberPathColumn)
                        ? column with
                        {
                            Kind = ColumnKind.ParentKeyPart,
                        }
                        : column
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithUnifiedAliasKeyColumn()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("SchoolYearAlias"), ColumnKind.ParentKeyPart),
                ]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithMissingKeyColumn()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("MissingSchoolYear"), ColumnKind.ParentKeyPart),
                ]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithMissingDocumentIdKeyColumn()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.ParentKeyPart)]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithMissingDocumentIdParentKeyPart()
    {
        return CreateRootOnlyModelWithMissingDocumentIdKeyColumn();
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithDocumentSuffixedParentKeyPart()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns =
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                .. model.Root.Columns.Where(column =>
                    !column.ColumnName.Equals(new DbColumnName("DocumentId"))
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithUnsupportedKeyColumnKind()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar),
                ]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithDuplicateColumnNames()
    {
        var model = CreateSupportedRootOnlyModel();
        var duplicateColumnName = new DbColumnName("SchoolYear");
        var duplicateColumn = model.Root.Columns.Single(column =>
            column.ColumnName.Equals(duplicateColumnName)
        );
        var rootTable = model.Root with { Columns = [.. model.Root.Columns, duplicateColumn] };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithDuplicateUnusedDocumentReferenceBindingKeys()
    {
        var model = CreateSupportedRootOnlyModel();
        var unusedBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: CreatePath(
                "$.unusedReference",
                new JsonPathSegment.Property("unusedReference")
            ),
            Table: model.Root.Table,
            FkColumn: new DbColumnName("Unused_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings: []
        );

        return model with
        {
            DocumentReferenceBindings = [unusedBinding, unusedBinding],
        };
    }

    protected static RelationalResourceModel CreateRootOnlyModelWithDuplicateUnusedDescriptorEdgeSourceKeys()
    {
        var model = CreateSupportedRootOnlyModel();
        var unusedEdgeSource = new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: CreatePath(
                "$.unusedDescriptor",
                new JsonPathSegment.Property("unusedDescriptor")
            ),
            Table: model.Root.Table,
            FkColumn: new DbColumnName("UnusedDescriptorId"),
            DescriptorResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
        );

        return model with
        {
            DescriptorEdgeSources = [unusedEdgeSource, unusedEdgeSource],
        };
    }

    protected static RelationalResourceModel CreateSupportedRootOnlyModelWithUnifiedAliasColumnFirst()
    {
        var model = CreateSupportedRootOnlyModel();

        var unifiedAliasColumns = model
            .Root.Columns.Where(static column => column.Storage is ColumnStorage.UnifiedAlias)
            .ToArray();

        var storedColumns = model
            .Root.Columns.Where(static column => column.Storage is ColumnStorage.Stored)
            .ToArray();

        var rootTable = model.Root with { Columns = [.. unifiedAliasColumns, .. storedColumns] };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    protected static RelationalResourceModel CreateSupportedMultiTableModel()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root;
        var rootScopeExtensionTable = CreateRootScopeExtensionTable();
        var childCollectionTable = CreateChildCollectionTable();
        var nestedChildCollectionTable = CreateNestedChildCollectionTable();

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder =
            [
                rootTable,
                rootScopeExtensionTable,
                childCollectionTable,
                nestedChildCollectionTable,
            ],
        };
    }

    protected static RelationalResourceModel CreateSupportedMultiTableModelWithClonedRootScopeTableInDependencyOrder()
    {
        var model = CreateSupportedMultiTableModel();
        var clonedRootScopeTable = model.Root with { Columns = [.. model.Root.Columns] };

        return model with
        {
            TablesInDependencyOrder = [clonedRootScopeTable, .. model.TablesInDependencyOrder.Skip(1)],
        };
    }

    protected static RelationalResourceModel CreateSupportedMultiTableModelWithoutRootScopeTable()
    {
        var model = CreateSupportedMultiTableModel();

        return model with
        {
            TablesInDependencyOrder = [.. model.TablesInDependencyOrder.Skip(1)],
        };
    }

    protected static RelationalResourceModel CreateSupportedMultiTableModelWithMultipleRootScopeTables()
    {
        var model = CreateSupportedMultiTableModel();
        var duplicateRootScopeTable = model.Root with
        {
            Table = new DbTableName(new DbSchemaName("edfi"), "StudentShadow"),
            Columns = [.. model.Root.Columns],
        };

        return model with
        {
            TablesInDependencyOrder = [.. model.TablesInDependencyOrder, duplicateRootScopeTable],
        };
    }

    protected static RelationalResourceModel CreateSupportedMultiTableModelWithMismatchedRootScopeTable()
    {
        var model = CreateSupportedMultiTableModel();
        var shadowRootScopeTable = model.Root with
        {
            Table = new DbTableName(new DbSchemaName("edfi"), "StudentShadow"),
            Columns = [.. model.Root.Columns],
        };

        return model with
        {
            TablesInDependencyOrder = [shadowRootScopeTable, .. model.TablesInDependencyOrder.Skip(1)],
        };
    }

    protected static RelationalResourceModel CreateSupportedMultiTableModelWithUnifiedAliasColumnsFirst()
    {
        var model = CreateSupportedMultiTableModel();
        var permutedTables = model.TablesInDependencyOrder.Select(ReorderUnifiedAliasColumnsFirst).ToArray();

        return model with
        {
            Root = permutedTables[0],
            TablesInDependencyOrder = permutedTables,
        };
    }

    protected static DbTableModel ReorderUnifiedAliasColumnsFirst(DbTableModel tableModel)
    {
        var unifiedAliasColumns = tableModel
            .Columns.Where(static column => column.Storage is ColumnStorage.UnifiedAlias)
            .ToArray();

        if (unifiedAliasColumns.Length == 0)
        {
            return tableModel;
        }

        var storedColumns = tableModel
            .Columns.Where(static column => column.Storage is ColumnStorage.Stored)
            .ToArray();

        return tableModel with
        {
            Columns = [.. unifiedAliasColumns, .. storedColumns],
        };
    }

    protected static DbTableModel CreateRootScopeExtensionTable()
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
            JsonScope: CreatePath(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtension",
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
                    ColumnName: new DbColumnName("FavoriteColor"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$._ext.sample.favoriteColor",
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("favoriteColor")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("FavoriteColorAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$._ext.sample.favoriteColor",
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("favoriteColor")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("FavoriteColor"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );
    }

    protected static DbTableModel CreateChildCollectionTable()
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
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
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("City"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("CityAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("City"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );
    }

    protected static DbTableModel CreateNestedChildCollectionTable()
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressPeriod"),
            JsonScope: CreatePath(
                "$.addresses[*].periods[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("periods"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressPeriod",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
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
                    ColumnName: new DbColumnName("ParentAddressOrdinal"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("PeriodName"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].periods[*].periodName",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periods"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periodName")
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );
    }

    protected static RelationalResourceModel CreateSingleTableModelCoveringWriteValueSourceKinds(
        bool useNullDocumentFkSourcePath = false
    )
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressRoot"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressRoot",
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
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
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
                    ColumnName: new DbColumnName("ParentAddressOrdinal"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AddressScopeValue"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*]",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement()
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("StreetNumber"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].streetNumber",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("streetNumber")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: useNullDocumentFkSourcePath
                        ? null
                        : CreatePath(
                            "$.addresses[*].schoolReference",
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolReference")
                        ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ProgramTypeDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].programTypeDescriptor",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeDescriptor")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("CanonicalProgramTypeCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ProgramTypeCodeAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].programTypeCode",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeCode")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("CanonicalProgramTypeCode"),
                        PresenceColumn: null
                    )
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ProgramTypeDescriptorIdAlias"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].programTypeDescriptor",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeDescriptor")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("ProgramTypeDescriptorId"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("CanonicalProgramTypeCode"),
                    MemberPathColumns: [new DbColumnName("ProgramTypeCodeAlias")]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentAddress"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: CreatePath(
                        "$.addresses[*].schoolReference",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: childTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                    IdentityBindings: []
                ),
            ],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: CreatePath(
                        "$.addresses[*].programTypeDescriptor",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeDescriptor")
                    ),
                    Table: childTable.Table,
                    FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
            ]
        );
    }

    protected static RelationalResourceModel CreateSingleTableModelWithMissingDocumentReferenceBinding(
        bool useNullDocumentFkSourcePath = false
    )
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds(useNullDocumentFkSourcePath);

        return model with
        {
            DocumentReferenceBindings = [],
        };
    }

    protected static RelationalResourceModel CreateSingleTableModelWithDuplicateDocumentReferenceBinding(
        bool useNullDocumentFkSourcePath = false
    )
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds(useNullDocumentFkSourcePath);
        var binding = model.DocumentReferenceBindings.Single();

        return model with
        {
            DocumentReferenceBindings = [.. model.DocumentReferenceBindings, binding],
        };
    }

    protected static RelationalResourceModel CreateSingleTableModelWithMissingDescriptorEdgeSource()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();

        return model with
        {
            DescriptorEdgeSources = [],
        };
    }

    protected static RelationalResourceModel CreateSingleTableModelWithMismatchedDescriptorSourcePath()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTableName = new DbTableName(new DbSchemaName("edfi"), "StudentAddress");
        var descriptorColumnName = new DbColumnName("ProgramTypeDescriptorId");

        var updatedTablesInDependencyOrder = model
            .TablesInDependencyOrder.Select(table =>
                table.Table.Equals(childTableName)
                    ? table with
                    {
                        Columns =
                        [
                            .. table.Columns.Select(column =>
                                column.ColumnName.Equals(descriptorColumnName)
                                    ? column with
                                    {
                                        SourceJsonPath = CreatePath(
                                            "$.addresses[*].programTypeCode",
                                            new JsonPathSegment.Property("addresses"),
                                            new JsonPathSegment.AnyArrayElement(),
                                            new JsonPathSegment.Property("programTypeCode")
                                        ),
                                    }
                                    : column
                            ),
                        ],
                    }
                    : table
            )
            .ToArray();

        return model with
        {
            TablesInDependencyOrder = updatedTablesInDependencyOrder,
        };
    }

    protected static RelationalResourceModel CreateSingleTableModelWithDuplicateDescriptorEdgeSource()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var edgeSource = model.DescriptorEdgeSources.Single();

        return model with
        {
            DescriptorEdgeSources = [.. model.DescriptorEdgeSources, edgeSource],
        };
    }

    protected static RelationalResourceModel CreateSingleTableModelWithDocumentIdNotFirstInKeyOrder()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTable = GetStudentAddressTable(model);

        var updatedChildTable = childTable with
        {
            Key = new TableKey(
                ConstraintName: childTable.Key.ConstraintName,
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
        };

        return ReplaceStudentAddressTable(model, updatedChildTable);
    }

    protected static RelationalResourceModel CreateSingleTableModelWithOrdinalNotLastInKeyOrder()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTable = GetStudentAddressTable(model);

        var updatedChildTable = childTable with
        {
            Key = new TableKey(
                ConstraintName: childTable.Key.ConstraintName,
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                ]
            ),
        };

        return ReplaceStudentAddressTable(model, updatedChildTable);
    }

    protected static RelationalResourceModel CreateSingleTableModelWithMultipleOrdinalKeyColumns()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTable = GetStudentAddressTable(model);

        var updatedChildTable = childTable with
        {
            Key = new TableKey(
                ConstraintName: childTable.Key.ConstraintName,
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
        };

        return ReplaceStudentAddressTable(model, updatedChildTable);
    }

    private static DbTableModel GetStudentAddressTable(RelationalResourceModel model)
    {
        return model.TablesInDependencyOrder.Single(table =>
            table.Table.Equals(new DbTableName(new DbSchemaName("edfi"), "StudentAddress"))
        );
    }

    private static RelationalResourceModel ReplaceStudentAddressTable(
        RelationalResourceModel model,
        DbTableModel updatedChildTable
    )
    {
        return model with { TablesInDependencyOrder = [model.Root, updatedChildTable] };
    }

    protected static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments)
    {
        return new JsonPathExpression(canonical, segments);
    }

    protected static string CreateWritePlanFingerprint(ResourceWritePlan plan)
    {
        return string.Join(
            "\n--TABLE--\n",
            plan.TablePlansInDependencyOrder.Select(static tablePlan =>
                string.Join(
                    "\n",
                    tablePlan.TableModel.Table.ToString(),
                    tablePlan.InsertSql,
                    tablePlan.UpdateSql ?? "<null>",
                    tablePlan.DeleteByParentSql ?? "<null>",
                    string.Join(
                        "|",
                        tablePlan.ColumnBindings.Select(binding =>
                            $"{binding.Column.ColumnName.Value}:{binding.ParameterName}:{binding.Source.GetType().Name}"
                        )
                    )
                )
            )
        );
    }
}
