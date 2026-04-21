// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_RelationalQueryCapabilityCompiler
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _studentAssociationResource = new(
        "Ed-Fi",
        "StudentAssociation"
    );
    private static readonly QualifiedResourceName _academicSubjectDescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

    [Test]
    public void It_should_keep_unique_exact_root_scalar_and_descriptor_matches_supported()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                ScalarColumn("SchoolYear", "$.schoolYear", ScalarKind.Int32),
                DescriptorColumn(
                    "AcademicSubjectDescriptorId",
                    "$.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var model = CreateModel(
            rootTable,
            [],
            [
                DescriptorEdge(
                    "$.academicSubjectDescriptor",
                    rootTable.Table,
                    "AcademicSubjectDescriptorId",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            model,
            ("schoolYear", [("$.schoolYear", "number")]),
            ("academicSubjectDescriptor", [("$.academicSubjectDescriptor", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["schoolYear"]
            .Target.Should()
            .Be(new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolYear")));
        capability
            .SupportedFieldsByQueryField["academicSubjectDescriptor"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.DescriptorIdColumn(
                    new DbColumnName("AcademicSubjectDescriptorId"),
                    _academicSubjectDescriptorResource
                )
            );
    }

    [Test]
    public void It_should_collapse_same_site_exact_root_scalar_duplicates_to_representative_binding_column()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                ScalarCanonicalColumn("StudentUniqueIdCanonical"),
                ScalarColumn(
                    "Student_StudentUniqueIdAlias",
                    "$.studentReference.studentUniqueId",
                    ScalarKind.String,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentUniqueIdCanonical"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
                ScalarColumn(
                    "Student_StudentUniqueIdDuplicateAlias",
                    "$.studentReference.studentUniqueId",
                    ScalarKind.String,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentUniqueIdCanonical"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
            ]
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.studentReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("Student_DocumentId"),
            TargetResource: _studentResource,
            IdentityBindings:
            [
                ReferenceIdentity(
                    "$.studentReference.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueIdAlias"
                ),
                ReferenceIdentity(
                    "$.studentReference.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueIdDuplicateAlias"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(rootTable, [binding], []),
            ("studentUniqueId", [("$.studentReference.studentUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["studentUniqueId"]
            .Target.Should()
            .Be(new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Student_StudentUniqueIdAlias")));
    }

    [Test]
    public void It_should_collapse_same_site_exact_root_descriptor_duplicates_to_representative_binding_column()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                DescriptorCanonicalColumn(
                    "StudentAcademicSubjectDescriptorUnifiedId",
                    _academicSubjectDescriptorResource
                ),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentAcademicSubjectDescriptorUnifiedId"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorDuplicateId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentAcademicSubjectDescriptorUnifiedId"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
            ]
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.studentReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("Student_DocumentId"),
            TargetResource: _studentResource,
            IdentityBindings:
            [
                ReferenceIdentity(
                    "$.studentReference.academicSubjectDescriptor",
                    "$.studentReference.academicSubjectDescriptor",
                    "Student_AcademicSubjectDescriptorId"
                ),
                ReferenceIdentity(
                    "$.studentReference.academicSubjectDescriptor",
                    "$.studentReference.academicSubjectDescriptor",
                    "Student_AcademicSubjectDescriptorDuplicateId"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                rootTable,
                [binding],
                [
                    DescriptorEdge(
                        "$.studentReference.academicSubjectDescriptor",
                        rootTable.Table,
                        "Student_AcademicSubjectDescriptorId",
                        _academicSubjectDescriptorResource
                    ),
                    DescriptorEdge(
                        "$.studentReference.academicSubjectDescriptor",
                        rootTable.Table,
                        "Student_AcademicSubjectDescriptorDuplicateId",
                        _academicSubjectDescriptorResource
                    ),
                ]
            ),
            ("academicSubjectDescriptor", [("$.studentReference.academicSubjectDescriptor", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["academicSubjectDescriptor"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.DescriptorIdColumn(
                    new DbColumnName("Student_AcademicSubjectDescriptorId"),
                    _academicSubjectDescriptorResource
                )
            );
    }

    [Test]
    public void It_should_leave_cross_site_exact_root_scalar_duplicates_ambiguous()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("PrimaryStudent_DocumentId", "$.primaryStudentReference", _studentResource),
                ScalarColumn("PrimaryStudent_StudentUniqueId", "$.studentReference.studentUniqueId"),
                DocumentFkColumn(
                    "SecondaryStudent_DocumentId",
                    "$.secondaryStudentReference",
                    _studentResource
                ),
                ScalarColumn("SecondaryStudent_StudentUniqueId", "$.studentReference.studentUniqueId"),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                rootTable,
                [
                    CreateBinding(
                        "$.primaryStudentReference",
                        rootTable.Table,
                        "PrimaryStudent_DocumentId",
                        "$.studentReference.studentUniqueId",
                        "$.studentReference.studentUniqueId",
                        "PrimaryStudent_StudentUniqueId"
                    ),
                    CreateBinding(
                        "$.secondaryStudentReference",
                        rootTable.Table,
                        "SecondaryStudent_DocumentId",
                        "$.studentReference.studentUniqueId",
                        "$.studentReference.studentUniqueId",
                        "SecondaryStudent_StudentUniqueId"
                    ),
                ],
                []
            ),
            ("studentUniqueId", [("$.studentReference.studentUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.AmbiguousRootTarget);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    private static ConcreteResourceModel CreateConcreteResource(
        RelationalResourceModel model,
        params (string QueryFieldName, (string Path, string Type)[] Paths)[] queryFields
    )
    {
        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, model.Resource, "5.2.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            QueryFieldMappingsByQueryField = CreateQueryFieldMappings(queryFields),
        };
    }

    private static RelationalResourceModel CreateModel(
        DbTableModel rootTable,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        return new RelationalResourceModel(
            Resource: _studentAssociationResource,
            PhysicalSchema: _edfiSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: documentReferenceBindings,
            DescriptorEdgeSources: descriptorEdgeSources
        );
    }

    private static DbTableModel CreateRootTable(string tableName, IReadOnlyList<DbColumnModel> extraColumns)
    {
        return new DbTableModel(
            Table: new DbTableName(_edfiSchema, tableName),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
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
                .. extraColumns,
            ],
            Constraints: []
        );
    }

    private static DbColumnModel DocumentFkColumn(
        string columnName,
        string sourcePath,
        QualifiedResourceName targetResource
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DocumentFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: targetResource
        );
    }

    private static DbColumnModel ScalarCanonicalColumn(string columnName)
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnModel ScalarColumn(string columnName, string sourcePath)
    {
        return ScalarColumn(columnName, sourcePath, ScalarKind.String);
    }

    private static DbColumnModel ScalarColumn(
        string columnName,
        string sourcePath,
        ScalarKind scalarKind,
        ColumnStorage? storage = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.Scalar,
            ScalarType: scalarKind == ScalarKind.String
                ? new RelationalScalarType(scalarKind, MaxLength: 32)
                : new RelationalScalarType(scalarKind),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: null,
            Storage: storage ?? new ColumnStorage.Stored()
        );
    }

    private static DbColumnModel DescriptorCanonicalColumn(
        string columnName,
        QualifiedResourceName descriptorResource
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: descriptorResource
        );
    }

    private static DbColumnModel DescriptorColumn(
        string columnName,
        string sourcePath,
        QualifiedResourceName descriptorResource,
        ColumnStorage? storage = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: descriptorResource,
            Storage: storage ?? new ColumnStorage.Stored()
        );
    }

    private static DocumentReferenceBinding CreateBinding(
        string referenceObjectPath,
        DbTableName table,
        string fkColumn,
        string identityPath,
        string referencePath,
        string column
    )
    {
        return new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path(referenceObjectPath),
            Table: table,
            FkColumn: new DbColumnName(fkColumn),
            TargetResource: _studentResource,
            IdentityBindings: [ReferenceIdentity(identityPath, referencePath, column)]
        );
    }

    private static ReferenceIdentityBinding ReferenceIdentity(
        string identityPath,
        string referencePath,
        string column
    )
    {
        return new ReferenceIdentityBinding(
            IdentityJsonPath: Path(identityPath),
            ReferenceJsonPath: Path(referencePath),
            Column: new DbColumnName(column)
        );
    }

    private static DescriptorEdgeSource DescriptorEdge(
        string descriptorValuePath,
        DbTableName table,
        string fkColumn,
        QualifiedResourceName descriptorResource
    )
    {
        return new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: Path(descriptorValuePath),
            Table: table,
            FkColumn: new DbColumnName(fkColumn),
            DescriptorResource: descriptorResource
        );
    }

    private static IReadOnlyDictionary<string, RelationalQueryFieldMapping> CreateQueryFieldMappings(
        params (string QueryFieldName, (string Path, string Type)[] Paths)[] queryFields
    )
    {
        return queryFields.ToDictionary(
            static queryField => queryField.QueryFieldName,
            static queryField => new RelationalQueryFieldMapping(
                queryField.QueryFieldName,
                queryField
                    .Paths.Select(static path => new RelationalQueryFieldPath(
                        JsonPathExpressionCompiler.Compile(path.Path),
                        path.Type
                    ))
                    .ToArray()
            ),
            StringComparer.Ordinal
        );
    }

    private static JsonPathExpression Path(string canonical) => JsonPathExpressionCompiler.Compile(canonical);
}
