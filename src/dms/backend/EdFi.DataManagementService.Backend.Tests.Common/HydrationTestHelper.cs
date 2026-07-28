// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Shared test model builder for hydration executor integration tests.
/// </summary>
public static class HydrationTestHelper
{
    /// <summary>
    /// Builds a <see cref="ResourceReadPlan"/> for a School resource with an Address child table
    /// and an AddressPeriod nested child table in the given schema.
    /// </summary>
    public static ResourceReadPlan BuildSchoolReadPlan(string schemaName, SqlDialect dialect)
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName(schemaName), "School"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_School",
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
                    ColumnName: new DbColumnName("SchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolId",
                        [new JsonPathSegment.Property("schoolId")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName(schemaName), "SchoolAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
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
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].city",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("city"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var nestedChildTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName(schemaName), "SchoolAddressPeriod"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*].periods[*]",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement(),
                ]
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddressPeriod",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("ParentCollectionItemId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ParentCollectionItemId"),
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
                    ColumnName: new DbColumnName("BeginDate"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 10),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].periods[*].beginDate",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("periods"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("beginDate"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName(schemaName),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable, nestedChildTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ReadPlanCompiler(dialect).Compile(model);
    }

    /// <summary>
    /// Builds a <see cref="ResourceReadPlan"/> for a StudentSchoolAssociation resource with a
    /// nullable School reference (identity-component, FK + propagated identity column) and a
    /// nullable Calendar reference (non-identity, FK + propagated identity column) in the given schema.
    /// </summary>
    public static ResourceReadPlan BuildStudentSchoolAssociationReadPlan(
        string schemaName,
        SqlDialect dialect
    )
    {
        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );

        var schoolIdJsonPath = new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );

        var calendarReferencePath = new JsonPathExpression(
            "$.calendarReference",
            [new JsonPathSegment.Property("calendarReference")]
        );

        var calendarCodeJsonPath = new JsonPathExpression(
            "$.calendarReference.calendarCode",
            [new JsonPathSegment.Property("calendarReference"), new JsonPathSegment.Property("calendarCode")]
        );

        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName(schemaName), "StudentSchoolAssociation"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_StudentSchoolAssociation",
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
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_SchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolIdJsonPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: calendarReferencePath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_CalendarCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    IsNullable: true,
                    SourceJsonPath: calendarCodeJsonPath,
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
            PhysicalSchema: new DbSchemaName(schemaName),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: schoolIdJsonPath,
                            ReferenceJsonPath: schoolIdJsonPath,
                            Column: new DbColumnName("School_SchoolId")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: calendarReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("Calendar_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: calendarCodeJsonPath,
                            ReferenceJsonPath: calendarCodeJsonPath,
                            Column: new DbColumnName("Calendar_CalendarCode")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );

        return new ReadPlanCompiler(dialect).Compile(model);
    }

    /// <summary>
    /// Builds a <see cref="ResourceReadPlan"/> shaped like the Ed-Fi StudentSectionAssociation
    /// resource: a root table carrying three document references (one of them nullable) plus a
    /// descriptor edge, and a child collection table carrying a fourth document reference.
    /// </summary>
    /// <remarks>
    /// This is the shape that exercises both document-reference lookup branch forms at once. The
    /// root contributes several reference columns, so it is scanned once and expanded inline; the
    /// child contributes one, so it keeps the plain projection. The child also verifies that the
    /// lookup scopes through the child's root-scope locator rather than its own key, so references
    /// held only by child rows of off-page documents are excluded.
    /// </remarks>
    public static ResourceReadPlan BuildStudentSectionAssociationReadPlan(
        string schemaName,
        SqlDialect dialect
    )
    {
        var schema = new DbSchemaName(schemaName);

        var studentReferencePath = new JsonPathExpression(
            "$.studentReference",
            [new JsonPathSegment.Property("studentReference")]
        );
        var studentUniqueIdPath = new JsonPathExpression(
            "$.studentReference.studentUniqueId",
            [
                new JsonPathSegment.Property("studentReference"),
                new JsonPathSegment.Property("studentUniqueId"),
            ]
        );
        var sectionReferencePath = new JsonPathExpression(
            "$.sectionReference",
            [new JsonPathSegment.Property("sectionReference")]
        );
        var sectionIdentifierPath = new JsonPathExpression(
            "$.sectionReference.sectionIdentifier",
            [
                new JsonPathSegment.Property("sectionReference"),
                new JsonPathSegment.Property("sectionIdentifier"),
            ]
        );
        var dualCreditReferencePath = new JsonPathExpression(
            "$.dualCreditEducationOrganizationReference",
            [new JsonPathSegment.Property("dualCreditEducationOrganizationReference")]
        );
        var dualCreditEducationOrganizationIdPath = new JsonPathExpression(
            "$.dualCreditEducationOrganizationReference.educationOrganizationId",
            [
                new JsonPathSegment.Property("dualCreditEducationOrganizationReference"),
                new JsonPathSegment.Property("educationOrganizationId"),
            ]
        );
        var attemptStatusDescriptorPath = new JsonPathExpression(
            "$.attemptStatusDescriptor",
            [new JsonPathSegment.Property("attemptStatusDescriptor")]
        );
        var programReferencePath = new JsonPathExpression(
            "$.programs[*].programReference",
            [
                new JsonPathSegment.Property("programs"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("programReference"),
            ]
        );
        var programNamePath = new JsonPathExpression(
            "$.programs[*].programReference.programName",
            [
                new JsonPathSegment.Property("programs"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("programReference"),
                new JsonPathSegment.Property("programName"),
            ]
        );

        var rootTable = new DbTableModel(
            Table: new DbTableName(schema, "StudentSectionAssociation"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_StudentSectionAssociation",
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
                    ColumnName: new DbColumnName("DualCreditEducationOrganization_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: dualCreditReferencePath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "EducationOrganization")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("DualCreditEducationOrganization_EducationOrganizationId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: dualCreditEducationOrganizationIdPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Section_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: sectionReferencePath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Section")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Section_SectionIdentifier"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 255),
                    IsNullable: true,
                    SourceJsonPath: sectionIdentifierPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: studentReferencePath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_StudentUniqueId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: true,
                    SourceJsonPath: studentUniqueIdPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AttemptStatusDescriptor_DescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: attemptStatusDescriptorPath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "AttemptStatusDescriptor")
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var programTable = new DbTableModel(
            Table: new DbTableName(schema, "StudentSectionAssociationProgram"),
            JsonScope: new JsonPathExpression(
                "$.programs[*]",
                [new JsonPathSegment.Property("programs"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentSectionAssociationProgram",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("StudentSectionAssociation_DocumentId"),
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
                    ColumnName: new DbColumnName("Program_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: programReferencePath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Program")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Program_ProgramName"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    IsNullable: true,
                    SourceJsonPath: programNamePath,
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("StudentSectionAssociation_DocumentId")],
                ImmediateParentScopeLocatorColumns:
                [
                    new DbColumnName("StudentSectionAssociation_DocumentId"),
                ],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentSectionAssociation"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, programTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: dualCreditReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("DualCreditEducationOrganization_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: dualCreditEducationOrganizationIdPath,
                            ReferenceJsonPath: dualCreditEducationOrganizationIdPath,
                            Column: new DbColumnName(
                                "DualCreditEducationOrganization_EducationOrganizationId"
                            )
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: sectionReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("Section_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Section"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: sectionIdentifierPath,
                            ReferenceJsonPath: sectionIdentifierPath,
                            Column: new DbColumnName("Section_SectionIdentifier")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: studentReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("Student_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Student"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: studentUniqueIdPath,
                            ReferenceJsonPath: studentUniqueIdPath,
                            Column: new DbColumnName("Student_StudentUniqueId")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: programReferencePath,
                    Table: programTable.Table,
                    FkColumn: new DbColumnName("Program_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Program"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: programNamePath,
                            ReferenceJsonPath: programNamePath,
                            Column: new DbColumnName("Program_ProgramName")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: attemptStatusDescriptorPath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("AttemptStatusDescriptor_DescriptorId"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "AttemptStatusDescriptor")
                ),
            ]
        );

        return new ReadPlanCompiler(dialect).Compile(model);
    }

    /// <summary>
    /// Builds a <see cref="ResourceReadPlan"/> for a resource that carries a descriptor edge but no
    /// document references, so the compiled plan has no document-reference lookup at all.
    /// </summary>
    public static ResourceReadPlan BuildDescriptorOnlyReadPlan(string schemaName, SqlDialect dialect)
    {
        var schema = new DbSchemaName(schemaName);
        var gradeLevelDescriptorPath = new JsonPathExpression(
            "$.gradeLevelDescriptor",
            [new JsonPathSegment.Property("gradeLevelDescriptor")]
        );

        var rootTable = new DbTableModel(
            Table: new DbTableName(schema, "DescriptorOnly"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_DescriptorOnly",
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
                    ColumnName: new DbColumnName("GradeLevelDescriptor_DescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: gradeLevelDescriptorPath,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "DescriptorOnly"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: gradeLevelDescriptorPath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("GradeLevelDescriptor_DescriptorId"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
                ),
            ]
        );

        return new ReadPlanCompiler(dialect).Compile(model);
    }
}
