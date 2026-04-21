// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ReferenceIdentityQueryTargetResolverTests
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly QualifiedResourceName _courseTranscriptResource = new(
        "Ed-Fi",
        "CourseTranscript"
    );
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _studentAcademicRecordResource = new(
        "Ed-Fi",
        "StudentAcademicRecord"
    );
    private static readonly QualifiedResourceName _academicSubjectDescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );
    private static readonly QualifiedResourceName _gradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );

    [Test]
    public void It_should_inventory_only_root_table_reference_identity_candidates_in_model_order()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "StudentAcademicRecord_DocumentId",
                    "$.studentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumn(
                    "StudentAcademicRecord_StudentUniqueId",
                    "$.studentAcademicRecordReference.studentUniqueId"
                ),
            ]
        );
        var childTable = CreateChildTable("CourseTranscript_Credits");
        var rootBinding = CreateBinding(
            referenceObjectPath: "$.studentAcademicRecordReference",
            table: rootTable.Table,
            fkColumn: "StudentAcademicRecord_DocumentId",
            targetResource: _studentAcademicRecordResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.studentAcademicRecordReference.studentUniqueId",
            column: "StudentAcademicRecord_StudentUniqueId"
        );
        var ignoredChildBinding = CreateBinding(
            referenceObjectPath: "$.ignoredReference",
            table: childTable.Table,
            fkColumn: "Ignored_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.ignoredReference.studentUniqueId",
            column: "MissingChildColumn"
        );
        var model = CreateModel(rootTable, [rootTable, childTable], [rootBinding, ignoredChildBinding]);

        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);

        var group = resolver.CandidateGroupsInOrder.Should().ContainSingle().Subject;
        group.CandidatesInOrder.Should().ContainSingle();

        var candidate = group.CandidatesInOrder.Single();
        candidate.IdentityJsonPath.Canonical.Should().Be("$.studentReference.studentUniqueId");
        candidate.ReferenceJsonPath.Canonical.Should().Be("$.studentAcademicRecordReference.studentUniqueId");
        candidate.Column.Should().Be(new DbColumnName("StudentAcademicRecord_StudentUniqueId"));
        candidate.TargetResource.Should().Be(_studentAcademicRecordResource);
        candidate.ReferenceObjectPath.Canonical.Should().Be("$.studentAcademicRecordReference");
        candidate.FkColumn.Should().Be(new DbColumnName("StudentAcademicRecord_DocumentId"));
        candidate
            .RepresentativeBindingColumn.Should()
            .Be(new DbColumnName("StudentAcademicRecord_StudentUniqueId"));
    }

    [Test]
    public void It_should_group_same_site_duplicate_reference_paths_under_the_representative_binding_column()
    {
        var rootTable = CreateRootTable(
            "StudentCTEProgramAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                new DbColumnModel(
                    ColumnName: new DbColumnName("StudentUniqueIdCanonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                ScalarColumn(
                    "Student_StudentUniqueIdAlias",
                    "$.studentReference.studentUniqueId",
                    new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("StudentUniqueIdCanonical"),
                        PresenceColumn: new DbColumnName("Student_DocumentId")
                    )
                ),
                ScalarColumn(
                    "Student_StudentUniqueIdDuplicateAlias",
                    "$.studentReference.studentUniqueId",
                    new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("StudentUniqueIdCanonical"),
                        PresenceColumn: new DbColumnName("Student_DocumentId")
                    )
                ),
            ]
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: Path("$.studentReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("Student_DocumentId"),
            TargetResource: _studentResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    IdentityJsonPath: Path("$.studentReference.studentUniqueId"),
                    ReferenceJsonPath: Path("$.studentReference.studentUniqueId"),
                    Column: new DbColumnName("Student_StudentUniqueIdAlias")
                ),
                new ReferenceIdentityBinding(
                    IdentityJsonPath: Path("$.studentReference.studentUniqueId"),
                    ReferenceJsonPath: Path("$.studentReference.studentUniqueId"),
                    Column: new DbColumnName("Student_StudentUniqueIdDuplicateAlias")
                ),
            ]
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);

        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var resolution = resolver.ResolveExactPath(Path("$.studentReference.studentUniqueId"));

        var group = resolver.CandidateGroupsInOrder.Should().ContainSingle().Subject;
        group.RepresentativeBindingColumn.Should().Be(new DbColumnName("Student_StudentUniqueIdAlias"));
        group
            .CandidatesInOrder.Select(static candidate => candidate.Column)
            .Should()
            .Equal(
                new DbColumnName("Student_StudentUniqueIdAlias"),
                new DbColumnName("Student_StudentUniqueIdDuplicateAlias")
            );
        group
            .CandidatesInOrder.Should()
            .OnlyContain(candidate =>
                candidate.RepresentativeBindingColumn == new DbColumnName("Student_StudentUniqueIdAlias")
            );

        var match = resolution.Should().BeOfType<ReferenceIdentityQueryCandidateResolution.Match>().Subject;
        match.CandidateGroup.Should().Be(group);
        match.MatchedCandidatesInOrder.Should().HaveCount(2);
        resolver
            .ResolveTargetOrThrow(match)
            .Should()
            .Be(new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Student_StudentUniqueIdAlias")));
    }

    [Test]
    public void It_should_resolve_scalar_candidates_to_root_column_targets()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "StudentAcademicRecord_DocumentId",
                    "$.studentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumn(
                    "StudentAcademicRecord_StudentUniqueId",
                    "$.studentAcademicRecordReference.studentUniqueId"
                ),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentAcademicRecordReference",
            table: rootTable.Table,
            fkColumn: "StudentAcademicRecord_DocumentId",
            targetResource: _studentAcademicRecordResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.studentAcademicRecordReference.studentUniqueId",
            column: "StudentAcademicRecord_StudentUniqueId"
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var match = ResolveExactMatch(resolver, "$.studentAcademicRecordReference.studentUniqueId");

        var target = resolver.ResolveTargetOrThrow(match);

        target
            .Should()
            .Be(
                new RelationalQueryFieldTarget.RootColumn(
                    new DbColumnName("StudentAcademicRecord_StudentUniqueId")
                )
            );
    }

    [Test]
    public void It_should_resolve_descriptor_candidates_to_descriptor_id_column_targets()
    {
        var rootTable = CreateRootTable(
            "StudentProgramAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentReference",
            table: rootTable.Table,
            fkColumn: "Student_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.academicSubjectDescriptor",
            referencePath: "$.studentReference.academicSubjectDescriptor",
            column: "Student_AcademicSubjectDescriptorId"
        );
        var model = CreateModel(
            rootTable,
            [rootTable],
            [binding],
            [
                DescriptorEdge(
                    "$.studentReference.academicSubjectDescriptor",
                    rootTable.Table,
                    "Student_AcademicSubjectDescriptorId",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var match = ResolveExactMatch(resolver, "$.studentReference.academicSubjectDescriptor");

        var target = resolver.ResolveTargetOrThrow(match);

        target
            .Should()
            .Be(
                new RelationalQueryFieldTarget.DescriptorIdColumn(
                    new DbColumnName("Student_AcademicSubjectDescriptorId"),
                    _academicSubjectDescriptorResource
                )
            );
    }

    [Test]
    public void It_should_fail_fast_when_a_scalar_target_column_is_missing_scalar_type_metadata()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "StudentAcademicRecord_DocumentId",
                    "$.studentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumnWithoutType(
                    "StudentAcademicRecord_StudentUniqueId",
                    "$.studentAcademicRecordReference.studentUniqueId"
                ),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentAcademicRecordReference",
            table: rootTable.Table,
            fkColumn: "StudentAcademicRecord_DocumentId",
            targetResource: _studentAcademicRecordResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.studentAcademicRecordReference.studentUniqueId",
            column: "StudentAcademicRecord_StudentUniqueId"
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var match = ResolveExactMatch(resolver, "$.studentAcademicRecordReference.studentUniqueId");

        var act = () => resolver.ResolveTargetOrThrow(match);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*scalar target column 'StudentAcademicRecord_StudentUniqueId' is missing scalar type metadata*"
            );
    }

    [Test]
    public void It_should_fail_fast_when_the_representative_target_column_has_an_invalid_kind()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "Student_StudentUniqueId",
                    "$.studentReference.studentUniqueId",
                    _studentResource
                ),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentReference",
            table: rootTable.Table,
            fkColumn: "Student_StudentUniqueId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.studentReference.studentUniqueId",
            column: "Student_StudentUniqueId"
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var match = ResolveExactMatch(resolver, "$.studentReference.studentUniqueId");

        var act = () => resolver.ResolveTargetOrThrow(match);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*target column 'Student_StudentUniqueId' has unsupported kind 'DocumentFk'*");
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_target_metadata_is_missing()
    {
        var rootTable = CreateRootTable(
            "StudentProgramAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentReference",
            table: rootTable.Table,
            fkColumn: "Student_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.academicSubjectDescriptor",
            referencePath: "$.studentReference.academicSubjectDescriptor",
            column: "Student_AcademicSubjectDescriptorId"
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var match = ResolveExactMatch(resolver, "$.studentReference.academicSubjectDescriptor");

        var act = () => resolver.ResolveTargetOrThrow(match);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*descriptor target column 'Student_AcademicSubjectDescriptorId' has no matching DescriptorEdgeSource metadata*"
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_duplicate_candidates_resolve_to_different_descriptor_resources()
    {
        var rootTable = CreateRootTable(
            "StudentProgramAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                DescriptorCanonicalColumn("StudentDescriptorUnifiedId", _academicSubjectDescriptorResource),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentDescriptorUnifiedId"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
                DescriptorColumn(
                    "Student_GradeLevelDescriptorId",
                    "$.studentReference.academicSubjectDescriptor",
                    _gradeLevelDescriptorResource,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentDescriptorUnifiedId"),
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
                new ReferenceIdentityBinding(
                    IdentityJsonPath: Path("$.studentReference.academicSubjectDescriptor"),
                    ReferenceJsonPath: Path("$.studentReference.academicSubjectDescriptor"),
                    Column: new DbColumnName("Student_AcademicSubjectDescriptorId")
                ),
                new ReferenceIdentityBinding(
                    IdentityJsonPath: Path("$.studentReference.academicSubjectDescriptor"),
                    ReferenceJsonPath: Path("$.studentReference.academicSubjectDescriptor"),
                    Column: new DbColumnName("Student_GradeLevelDescriptorId")
                ),
            ]
        );
        var model = CreateModel(
            rootTable,
            [rootTable],
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
                    "Student_GradeLevelDescriptorId",
                    _gradeLevelDescriptorResource
                ),
            ]
        );
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);
        var match = ResolveExactMatch(resolver, "$.studentReference.academicSubjectDescriptor");

        var act = () => resolver.ResolveTargetOrThrow(match);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*resolves descriptor candidates to multiple descriptor resources*");
    }

    [Test]
    public void It_should_return_no_match_when_no_candidate_identity_or_reference_path_matches()
    {
        var (model, rootTable) = CreateAmbiguousIdentityPathModel();
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);

        var resolution = resolver.ResolveExactPath(Path("$.unmatchedReference.studentUniqueId"));

        resolution.Should().BeOfType<ReferenceIdentityQueryCandidateResolution.NoMatch>();
    }

    [Test]
    public void It_should_return_ambiguous_when_an_exact_identity_path_matches_multiple_reference_sites()
    {
        var (model, rootTable) = CreateAmbiguousIdentityPathModel();
        var resolver = new ReferenceIdentityQueryTargetResolver(model, rootTable);

        var resolution = resolver.ResolveExactPath(Path("$.studentReference.studentUniqueId"));

        var ambiguous = resolution
            .Should()
            .BeOfType<ReferenceIdentityQueryCandidateResolution.Ambiguous>()
            .Subject;
        ambiguous
            .CandidateGroupsInOrder.Select(static group => group.ReferenceObjectPath.Canonical)
            .Should()
            .Equal("$.primaryStudentReference", "$.secondaryStudentReference");
    }

    [Test]
    public void It_should_fail_fast_when_a_root_reference_identity_candidate_column_is_missing()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource)]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentReference",
            table: rootTable.Table,
            fkColumn: "Student_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.studentReference.studentUniqueId",
            column: "MissingStudentUniqueId"
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);

        var act = () => new ReferenceIdentityQueryTargetResolver(model, rootTable);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*missing member column 'MissingStudentUniqueId'*");
    }

    [Test]
    public void It_should_fail_fast_when_a_candidate_column_source_path_does_not_match_the_reference_path()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                ScalarColumn("Student_StudentUniqueId", "$.studentReference.differentUniqueId"),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath: "$.studentReference",
            table: rootTable.Table,
            fkColumn: "Student_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.studentReference.studentUniqueId",
            column: "Student_StudentUniqueId"
        );
        var model = CreateModel(rootTable, [rootTable], [binding]);

        var act = () => new ReferenceIdentityQueryTargetResolver(model, rootTable);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*does not match grouped ReferenceJsonPath '$.studentReference.studentUniqueId'*");
    }

    private static (RelationalResourceModel Model, DbTableModel RootTable) CreateAmbiguousIdentityPathModel()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn("PrimaryStudent_DocumentId", "$.primaryStudentReference", _studentResource),
                ScalarColumn("PrimaryStudent_StudentUniqueId", "$.primaryStudentReference.studentUniqueId"),
                DocumentFkColumn(
                    "SecondaryStudent_DocumentId",
                    "$.secondaryStudentReference",
                    _studentResource
                ),
                ScalarColumn(
                    "SecondaryStudent_StudentUniqueId",
                    "$.secondaryStudentReference.studentUniqueId"
                ),
            ]
        );
        var primaryBinding = CreateBinding(
            referenceObjectPath: "$.primaryStudentReference",
            table: rootTable.Table,
            fkColumn: "PrimaryStudent_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.primaryStudentReference.studentUniqueId",
            column: "PrimaryStudent_StudentUniqueId"
        );
        var secondaryBinding = CreateBinding(
            referenceObjectPath: "$.secondaryStudentReference",
            table: rootTable.Table,
            fkColumn: "SecondaryStudent_DocumentId",
            targetResource: _studentResource,
            identityPath: "$.studentReference.studentUniqueId",
            referencePath: "$.secondaryStudentReference.studentUniqueId",
            column: "SecondaryStudent_StudentUniqueId"
        );

        return (CreateModel(rootTable, [rootTable], [primaryBinding, secondaryBinding]), rootTable);
    }

    private static RelationalResourceModel CreateModel(
        DbTableModel rootTable,
        IReadOnlyList<DbTableModel> tables,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    )
    {
        return CreateModel(rootTable, tables, documentReferenceBindings, []);
    }

    private static RelationalResourceModel CreateModel(
        DbTableModel rootTable,
        IReadOnlyList<DbTableModel> tables,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        return new RelationalResourceModel(
            Resource: _courseTranscriptResource,
            PhysicalSchema: _edfiSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: tables,
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

    private static DbTableModel CreateChildTable(string tableName)
    {
        return new DbTableModel(
            Table: new DbTableName(_edfiSchema, tableName),
            JsonScope: Path("$.credits[*]"),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey),
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
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
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

    private static DbColumnModel ScalarColumn(string columnName, string sourcePath)
    {
        return ScalarColumn(columnName, sourcePath, new ColumnStorage.Stored());
    }

    private static DbColumnModel ScalarColumn(string columnName, string sourcePath, ColumnStorage storage)
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: null,
            Storage: storage
        );
    }

    private static DbColumnModel ScalarColumnWithoutType(string columnName, string sourcePath)
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.Scalar,
            ScalarType: null,
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: null,
            Storage: new ColumnStorage.Stored()
        );
    }

    private static DbColumnModel DescriptorColumn(
        string columnName,
        string sourcePath,
        QualifiedResourceName descriptorResource
    )
    {
        return DescriptorColumn(columnName, sourcePath, descriptorResource, new ColumnStorage.Stored());
    }

    private static DbColumnModel DescriptorColumn(
        string columnName,
        string sourcePath,
        QualifiedResourceName descriptorResource,
        ColumnStorage storage
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: descriptorResource,
            Storage: storage
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
            TargetResource: descriptorResource,
            Storage: new ColumnStorage.Stored()
        );
    }

    private static DocumentReferenceBinding CreateBinding(
        string referenceObjectPath,
        DbTableName table,
        string fkColumn,
        QualifiedResourceName targetResource,
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
            TargetResource: targetResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    IdentityJsonPath: Path(identityPath),
                    ReferenceJsonPath: Path(referencePath),
                    Column: new DbColumnName(column)
                ),
            ]
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

    private static ReferenceIdentityQueryCandidateResolution.Match ResolveExactMatch(
        ReferenceIdentityQueryTargetResolver resolver,
        string queryPath
    )
    {
        return resolver
            .ResolveExactPath(Path(queryPath))
            .Should()
            .BeOfType<ReferenceIdentityQueryCandidateResolution.Match>()
            .Subject;
    }

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}
