// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_SecurableElementColumnPathResolver_BasisPath
{
    private static readonly DbSchemaName EdFiSchema = new("edfi");
    private static readonly DbSchemaName DmsSchema = new("dms");

    private static DbTableName Table(string name) => new(EdFiSchema, name);

    private static DbTableName DescriptorTable => new(DmsSchema, "Descriptor");

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(
        short id,
        string project,
        string resource,
        bool isAbstract = false
    ) => new(id, new QualifiedResourceName(project, resource), "1.0", isAbstract);

    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path("$"),
            new TableKey("PK_Test", [new DbKeyColumn(Col("DocumentId"), ColumnKind.Scalar)]),
            columns ?? [],
            []
        );

    private static RelationalResourceModel CreateModel(
        string project,
        string resource,
        DbTableModel root,
        IReadOnlyList<DocumentReferenceBinding>? bindings = null,
        IReadOnlyList<DescriptorEdgeSource>? descriptorEdges = null,
        DbSchemaName? schema = null,
        ResourceStorageKind storageKind = ResourceStorageKind.RelationalTables
    ) =>
        new(
            new QualifiedResourceName(project, resource),
            schema ?? EdFiSchema,
            storageKind,
            root,
            [root],
            bindings ?? [],
            descriptorEdges ?? []
        );

    private static ConcreteResourceModel CreateConcrete(
        short keyId,
        string project,
        string resource,
        RelationalResourceModel model
    ) => new(ResourceKey(keyId, project, resource), ResourceStorageKind.RelationalTables, model);

    private static IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> CreateLookup(
        params ConcreteResourceModel[] resources
    ) => resources.ToDictionary(resource => resource.ResourceKey.Resource);

    private static DerivedRelationalModelSet CreateModelSet(
        IReadOnlyList<ConcreteResourceModel> resources,
        IReadOnlyList<AbstractUnionViewInfo>? abstractUnionViews = null
    ) =>
        new(
            new EffectiveSchemaInfo("1.0", "1.0", "test", 0, [], [], []),
            SqlDialect.Pgsql,
            [],
            resources,
            [],
            abstractUnionViews ?? [],
            [],
            []
        );

    private static AbstractUnionViewInfo CreateAbstractView(
        string abstractResourceName,
        params string[] memberResourceNames
    ) =>
        new(
            ResourceKey(100, "Ed-Fi", abstractResourceName, true),
            Table(abstractResourceName),
            [],
            memberResourceNames
                .Select(
                    (memberResourceName, index) =>
                        new AbstractUnionViewArm(
                            ResourceKey((short)(index + 1), "Ed-Fi", memberResourceName),
                            Table(memberResourceName),
                            []
                        )
                )
                .ToArray()
        );

    [Test]
    public void It_should_return_a_single_terminal_step_when_the_basis_is_the_subject()
    {
        var subjectRoot = CreateRootTable(Table("Student"));
        var subjectModel = CreateModel("Ed-Fi", "Student", subjectRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "Student", subjectModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("Student"));
        result[0].SourceColumnName.Should().Be(Col("DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_a_direct_basis_reference_with_a_single_terminal_step()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_a_basis_path_from_subject_and_basis_resource_names()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);
        var modelSet = CreateModelSet([subject, student]);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            modelSet
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_expose_the_documented_view_basis_column_path_overload()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);
        var modelSet = CreateModelSet([subject, student]);

        var result = SecurableElementColumnPathResolver.ResolveSecurableElementColumnPath(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            modelSet
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
    }

    [Test]
    public void It_should_return_empty_when_the_subject_resource_name_is_not_in_the_model_set()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);
        var modelSet = CreateModelSet([student]);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            modelSet
        );

        result.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_empty_when_the_basis_resource_name_is_not_in_the_model_set()
    {
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var modelSet = CreateModelSet([subject]);

        var result = SecurableElementColumnPathResolver.ResolveSecurableElementColumnPath(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            modelSet
        );

        result.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_empty_when_the_lower_level_helper_receives_an_unknown_concrete_basis_resource()
    {
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject),
            []
        );

        result.Should().BeEmpty();
    }

    [Test]
    public void It_should_not_match_reference_bindings_by_unqualified_table_name()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var bindingWithWrongSchema = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            new DbTableName(new DbSchemaName("sample"), "CourseTranscript"),
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [bindingWithWrongSchema]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Should().BeEmpty();
    }

    [Test]
    public void It_should_resolve_transitive_basis_paths_in_join_order()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var sarRoot = CreateRootTable(
            Table("StudentAcademicRecord"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("StudentAcademicRecord_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentAcademicRecordReference"),
            subjectRoot.Table,
            Col("StudentAcademicRecord_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord"),
            [],
            IsRequired: true
        );

        var sarBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            sarRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var sarModel = CreateModel("Ed-Fi", "StudentAcademicRecord", sarRoot, [sarBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var sar = CreateConcrete(2, "Ed-Fi", "StudentAcademicRecord", sarModel);
        var student = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, sar, student),
            []
        );

        result.Should().HaveCount(2);
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("StudentAcademicRecord_DocumentId"));
        result[0].TargetTable.Should().Be(Table("StudentAcademicRecord"));
        result[0].TargetColumnName.Should().Be(Col("DocumentId"));
        result[1].SourceTable.Should().Be(Table("StudentAcademicRecord"));
        result[1].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result[1].TargetTable.Should().BeNull();
        result[1].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_prefer_required_non_role_named_identity_paths_over_optional_or_role_named_alternatives()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("PreferredStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("OptionalStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("RoleNamedStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        var preferredBinding = new DocumentReferenceBinding(
            true,
            Path("$.preferredStudentReference"),
            subjectRoot.Table,
            Col("PreferredStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true,
            IsRoleNamed: false
        );
        var optionalBinding = new DocumentReferenceBinding(
            true,
            Path("$.optionalStudentReference"),
            subjectRoot.Table,
            Col("OptionalStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: false,
            IsRoleNamed: false
        );
        var roleNamedBinding = new DocumentReferenceBinding(
            true,
            Path("$.roleNamedStudentReference"),
            subjectRoot.Table,
            Col("RoleNamedStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true,
            IsRoleNamed: true
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "CourseTranscript",
            subjectRoot,
            [preferredBinding, optionalBinding, roleNamedBinding]
        );
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("PreferredStudent_DocumentId"));
    }

    [Test]
    public void It_should_not_treat_nested_non_role_named_references_as_role_named()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var detailTable = new DbTableName(EdFiSchema, "CourseTranscriptDetail");
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("RoleNamedStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectDetail = new DbTableModel(
            detailTable,
            Path("$.details[*]"),
            new TableKey(
                "PK_CourseTranscriptDetail",
                [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.ParentKeyPart, null, false, null, null),
                new DbColumnModel(
                    Col("CourseTranscript_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    Col("NestedStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [Col("CollectionItemId")],
                [Col("CourseTranscript_DocumentId")],
                [Col("CourseTranscript_DocumentId")],
                []
            ),
        };

        var roleNamedBinding = new DocumentReferenceBinding(
            true,
            Path("$.roleNamedStudentReference"),
            subjectRoot.Table,
            Col("RoleNamedStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true,
            IsRoleNamed: true
        );
        var nestedNonRoleNamedBinding = new DocumentReferenceBinding(
            true,
            Path("$.details[*].studentReference"),
            detailTable,
            Col("NestedStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true,
            IsRoleNamed: false
        );
        var subjectModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            EdFiSchema,
            ResourceStorageKind.RelationalTables,
            subjectRoot,
            [subjectRoot, subjectDetail],
            [roleNamedBinding, nestedNonRoleNamedBinding],
            []
        );
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", CreateModel("Ed-Fi", "Student", studentRoot));

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Should().HaveCount(2);
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("DocumentId"));
        result[0].TargetTable.Should().Be(detailTable);
        result[0].TargetColumnName.Should().Be(Col("CourseTranscript_DocumentId"));
        result[1].SourceTable.Should().Be(detailTable);
        result[1].SourceColumnName.Should().Be(Col("NestedStudent_DocumentId"));
        result[1].TargetTable.Should().BeNull();
        result[1].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_choose_the_shortest_path_when_all_hop_priorities_tie()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var intermediateRoot = CreateRootTable(
            Table("Intermediate"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Intermediate_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Intermediate")
                ),
            ]
        );

        var directBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );
        var intermediateBinding = new DocumentReferenceBinding(
            true,
            Path("$.intermediateReference"),
            subjectRoot.Table,
            Col("Intermediate_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Intermediate"),
            [],
            IsRequired: true
        );
        var intermediateToStudentBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            intermediateRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "CourseTranscript",
            subjectRoot,
            [directBinding, intermediateBinding]
        );
        var intermediateModel = CreateModel(
            "Ed-Fi",
            "Intermediate",
            intermediateRoot,
            [intermediateToStudentBinding]
        );
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var intermediate = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);
        var student = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, intermediate, student),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result[0].TargetTable.Should().BeNull();
    }

    [Test]
    public void It_should_choose_the_fewest_join_steps_when_equal_priority_paths_have_different_owning_tables()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectDetailTable = new DbTableName(EdFiSchema, "CourseTranscriptDetail");
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("RootStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectDetail = new DbTableModel(
            subjectDetailTable,
            Path("$.details[*]"),
            new TableKey(
                "PK_CourseTranscriptDetail",
                [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.ParentKeyPart, null, false, null, null),
                new DbColumnModel(
                    Col("CourseTranscript_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    Col("NestedStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [Col("CollectionItemId")],
                [Col("CourseTranscript_DocumentId")],
                [Col("CourseTranscript_DocumentId")],
                []
            ),
        };

        var nestedBinding = new DocumentReferenceBinding(
            true,
            Path("$.details[*].studentReference"),
            subjectDetailTable,
            Col("NestedStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );
        var rootBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("RootStudent_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );
        var subjectModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            EdFiSchema,
            ResourceStorageKind.RelationalTables,
            subjectRoot,
            [subjectRoot, subjectDetail],
            [nestedBinding, rootBinding],
            []
        );
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", CreateModel("Ed-Fi", "Student", studentRoot));

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("RootStudent_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_allow_a_non_identity_first_hop()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var intermediateRoot = CreateRootTable(
            Table("Intermediate"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Intermediate_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Intermediate")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            false,
            Path("$.intermediateReference"),
            subjectRoot.Table,
            Col("Intermediate_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Intermediate"),
            [],
            IsRequired: true
        );
        var intermediateBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            intermediateRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var intermediateModel = CreateModel("Ed-Fi", "Intermediate", intermediateRoot, [intermediateBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var intermediate = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);
        var student = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, intermediate, student),
            []
        );

        result.Should().HaveCount(2);
        result[0].SourceColumnName.Should().Be(Col("Intermediate_DocumentId"));
        result[1].SourceColumnName.Should().Be(Col("Student_DocumentId"));
    }

    [Test]
    public void It_should_reject_a_non_identity_intermediate_hop()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var intermediateRoot = CreateRootTable(
            Table("Intermediate"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Intermediate_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Intermediate")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.intermediateReference"),
            subjectRoot.Table,
            Col("Intermediate_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Intermediate"),
            [],
            IsRequired: true
        );
        var intermediateBinding = new DocumentReferenceBinding(
            false,
            Path("$.studentReference"),
            intermediateRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var intermediateModel = CreateModel("Ed-Fi", "Intermediate", intermediateRoot, [intermediateBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var intermediate = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);
        var student = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, intermediate, student),
            []
        );

        result.Should().BeEmpty();
    }

    [Test]
    public void It_should_not_lose_a_valid_path_to_a_cycle_branch()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var cycleRoot = CreateRootTable(
            Table("Cycle"),
            [
                new DbColumnModel(
                    Col("CourseTranscript_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "CourseTranscript")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Cycle_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Cycle")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );
        var cycleBinding = new DocumentReferenceBinding(
            true,
            Path("$.cycleReference"),
            subjectRoot.Table,
            Col("Cycle_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Cycle"),
            [],
            IsRequired: true
        );
        var cycleBackBinding = new DocumentReferenceBinding(
            true,
            Path("$.courseTranscriptReference"),
            cycleRoot.Table,
            Col("CourseTranscript_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "CourseTranscript",
            subjectRoot,
            [subjectBinding, cycleBinding]
        );
        var cycleModel = CreateModel("Ed-Fi", "Cycle", cycleRoot, [cycleBackBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var cycle = CreateConcrete(2, "Ed-Fi", "Cycle", cycleModel);
        var student = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, cycle, student),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
    }

    [Test]
    public void It_should_match_abstract_basis_members()
    {
        var schoolRoot = CreateRootTable(Table("School"));
        var subjectRoot = CreateRootTable(
            Table("StudentSchoolAssociation"),
            [
                new DbColumnModel(
                    Col("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "School")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.schoolReference"),
            subjectRoot.Table,
            Col("School_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "School"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "StudentSchoolAssociation", subjectRoot, [subjectBinding]);
        var schoolModel = CreateModel("Ed-Fi", "School", schoolRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "StudentSchoolAssociation", subjectModel);
        var school = CreateConcrete(2, "Ed-Fi", "School", schoolModel);
        var abstractView = CreateAbstractView("EducationOrganization", "School");

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            CreateLookup(subject, school),
            [abstractView]
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("School_DocumentId"));
        result[0].TargetTable.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_a_direct_abstract_basis_reference_without_a_concrete_lookup()
    {
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("EducationOrganization_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "EducationOrganization")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.educationOrganizationReference"),
            subjectRoot.Table,
            Col("EducationOrganization_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var abstractView = CreateAbstractView("EducationOrganization");

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            CreateLookup(subject),
            [abstractView]
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("EducationOrganization_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_a_concrete_subject_as_self_reference_for_an_abstract_basis()
    {
        var schoolRoot = CreateRootTable(Table("School"));
        var schoolModel = CreateModel("Ed-Fi", "School", schoolRoot);
        var school = CreateConcrete(1, "Ed-Fi", "School", schoolModel);
        var abstractView = CreateAbstractView("EducationOrganization", "School");

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            school,
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            CreateLookup(school),
            [abstractView]
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("School"));
        result[0].SourceColumnName.Should().Be(Col("DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_a_concrete_basis_through_an_abstract_reference()
    {
        var subjectRoot = CreateRootTable(
            Table("Intervention"),
            [
                new DbColumnModel(
                    Col("EducationOrganization_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "EducationOrganization")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.educationOrganizationReference"),
            subjectRoot.Table,
            Col("EducationOrganization_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "Intervention", subjectRoot, [subjectBinding]);
        var subject = CreateConcrete(1, "Ed-Fi", "Intervention", subjectModel);
        var abstractView = CreateAbstractView("EducationOrganization", "School");

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "School"),
            CreateLookup(subject),
            [abstractView]
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("Intervention"));
        result[0].SourceColumnName.Should().Be(Col("EducationOrganization_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_apply_documented_priorities_before_exact_concrete_basis_matching()
    {
        var schoolRoot = CreateRootTable(Table("School"));
        var subjectRoot = CreateRootTable(
            Table("Intervention"),
            [
                new DbColumnModel(
                    Col("RoleNamedSchool_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    Col("EducationOrganization_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "EducationOrganization")
                ),
            ]
        );

        var exactRoleNamedBinding = new DocumentReferenceBinding(
            true,
            Path("$.roleNamedSchoolReference"),
            subjectRoot.Table,
            Col("RoleNamedSchool_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "School"),
            [],
            IsRequired: true,
            IsRoleNamed: true
        );
        var abstractNonRoleNamedBinding = new DocumentReferenceBinding(
            true,
            Path("$.educationOrganizationReference"),
            subjectRoot.Table,
            Col("EducationOrganization_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            [],
            IsRequired: true,
            IsRoleNamed: false
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "Intervention",
            subjectRoot,
            [exactRoleNamedBinding, abstractNonRoleNamedBinding]
        );
        var schoolModel = CreateModel("Ed-Fi", "School", schoolRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "Intervention", subjectModel);
        var school = CreateConcrete(2, "Ed-Fi", "School", schoolModel);
        var abstractView = CreateAbstractView("EducationOrganization", "School");

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "School"),
            CreateLookup(subject, school),
            [abstractView]
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("EducationOrganization_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_not_prefer_a_direct_exact_abstract_basis_over_higher_priority_concrete_member()
    {
        var schoolRoot = CreateRootTable(Table("School"));
        var subjectRoot = CreateRootTable(
            Table("Intervention"),
            [
                new DbColumnModel(
                    Col("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    Col("EducationOrganization_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "EducationOrganization")
                ),
            ]
        );

        var concreteMemberIdentityBinding = new DocumentReferenceBinding(
            true,
            Path("$.schoolReference"),
            subjectRoot.Table,
            Col("School_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "School"),
            [],
            IsRequired: true,
            IsRoleNamed: false
        );
        var exactAbstractNonIdentityBinding = new DocumentReferenceBinding(
            false,
            Path("$.roleNamedEducationOrganizationReference"),
            subjectRoot.Table,
            Col("EducationOrganization_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            [],
            IsRequired: false,
            IsRoleNamed: true
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "Intervention",
            subjectRoot,
            [exactAbstractNonIdentityBinding, concreteMemberIdentityBinding]
        );
        var schoolModel = CreateModel("Ed-Fi", "School", schoolRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "Intervention", subjectModel);
        var school = CreateConcrete(2, "Ed-Fi", "School", schoolModel);
        var abstractView = CreateAbstractView("EducationOrganization", "School");

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            CreateLookup(subject, school),
            [abstractView]
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("School_DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_a_direct_descriptor_basis_reference()
    {
        var subjectRoot = CreateRootTable(
            Table("StudentTransportation"),
            [
                new DbColumnModel(
                    Col("TransportationTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
                ),
            ]
        );

        var descriptorEdge = new DescriptorEdgeSource(
            true,
            Path("$.transportationTypeDescriptor"),
            subjectRoot.Table,
            Col("TransportationTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "StudentTransportation",
            subjectRoot,
            descriptorEdges: [descriptorEdge]
        );
        var subject = CreateConcrete(1, "Ed-Fi", "StudentTransportation", subjectModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            CreateLookup(subject),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(Table("StudentTransportation"));
        result[0].SourceColumnName.Should().Be(Col("TransportationTypeDescriptor_DescriptorId"));
        result[0].TargetTable.Should().Be(DescriptorTable);
        result[0].TargetColumnName.Should().Be(Col("DocumentId"));
    }

    [Test]
    public void It_should_prefer_identity_descriptor_basis_edges_over_non_identity_edges()
    {
        var subjectRoot = CreateRootTable(
            Table("StudentTransportation"),
            [
                new DbColumnModel(
                    Col("OptionalTransportationTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
                ),
                new DbColumnModel(
                    Col("IdentityTransportationTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
                ),
            ]
        );

        var optionalDescriptorEdge = new DescriptorEdgeSource(
            false,
            Path("$.optionalTransportationTypeDescriptor"),
            subjectRoot.Table,
            Col("OptionalTransportationTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
        );
        var identityDescriptorEdge = new DescriptorEdgeSource(
            true,
            Path("$.identityTransportationTypeDescriptor"),
            subjectRoot.Table,
            Col("IdentityTransportationTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "StudentTransportation",
            subjectRoot,
            descriptorEdges: [optionalDescriptorEdge, identityDescriptorEdge]
        );
        var subject = CreateConcrete(1, "Ed-Fi", "StudentTransportation", subjectModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            CreateLookup(subject),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("IdentityTransportationTypeDescriptor_DescriptorId"));
    }

    [Test]
    public void It_should_prefer_non_role_named_descriptor_basis_edges_over_role_named_edges()
    {
        var subjectRoot = CreateRootTable(
            Table("StudentTransportation"),
            [
                new DbColumnModel(
                    Col("RoleNamedTransportationTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
                ),
                new DbColumnModel(
                    Col("TransportationTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
                ),
            ]
        );

        var roleNamedDescriptorEdge = new DescriptorEdgeSource(
            false,
            Path("$.roleNamedTransportationTypeDescriptor"),
            subjectRoot.Table,
            Col("RoleNamedTransportationTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            IsRequired: true,
            IsRoleNamed: true
        );
        var nonRoleNamedDescriptorEdge = new DescriptorEdgeSource(
            false,
            Path("$.transportationTypeDescriptor"),
            subjectRoot.Table,
            Col("TransportationTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            IsRequired: true,
            IsRoleNamed: false
        );

        var subjectModel = CreateModel(
            "Ed-Fi",
            "StudentTransportation",
            subjectRoot,
            descriptorEdges: [roleNamedDescriptorEdge, nonRoleNamedDescriptorEdge]
        );
        var subject = CreateConcrete(1, "Ed-Fi", "StudentTransportation", subjectModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            CreateLookup(subject),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("TransportationTypeDescriptor_DescriptorId"));
    }

    [Test]
    public void It_should_resolve_an_indirect_descriptor_basis_reference()
    {
        var sectionRoot = CreateRootTable(
            Table("Section"),
            [
                new DbColumnModel(
                    Col("ProgramTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("StudentSectionAssociation"),
            [
                new DbColumnModel(
                    Col("Section_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Section")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.sectionReference"),
            subjectRoot.Table,
            Col("Section_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Section"),
            [],
            IsRequired: true
        );
        var sectionDescriptorEdge = new DescriptorEdgeSource(
            true,
            Path("$.programTypeDescriptor"),
            sectionRoot.Table,
            Col("ProgramTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
        );

        var subjectModel = CreateModel("Ed-Fi", "StudentSectionAssociation", subjectRoot, [subjectBinding]);
        var sectionModel = CreateModel(
            "Ed-Fi",
            "Section",
            sectionRoot,
            descriptorEdges: [sectionDescriptorEdge]
        );
        var subject = CreateConcrete(1, "Ed-Fi", "StudentSectionAssociation", subjectModel);
        var section = CreateConcrete(2, "Ed-Fi", "Section", sectionModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
            CreateLookup(subject, section),
            []
        );

        result.Should().HaveCount(2);
        result[0].SourceTable.Should().Be(Table("StudentSectionAssociation"));
        result[0].SourceColumnName.Should().Be(Col("Section_DocumentId"));
        result[0].TargetTable.Should().Be(Table("Section"));
        result[0].TargetColumnName.Should().Be(Col("DocumentId"));
        result[1].SourceTable.Should().Be(Table("Section"));
        result[1].SourceColumnName.Should().Be(Col("ProgramTypeDescriptor_DescriptorId"));
        result[1].TargetTable.Should().Be(DescriptorTable);
        result[1].TargetColumnName.Should().Be(Col("DocumentId"));
    }

    [Test]
    public void It_should_allow_a_non_identity_terminal_descriptor_basis_edge_after_identity_intermediate_hop()
    {
        var intermediateRoot = CreateRootTable(
            Table("Intermediate"),
            [
                new DbColumnModel(
                    Col("ProgramTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    true,
                    null,
                    new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("StudentSectionAssociation"),
            [
                new DbColumnModel(
                    Col("Intermediate_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Intermediate")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.intermediateReference"),
            subjectRoot.Table,
            Col("Intermediate_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Intermediate"),
            [],
            IsRequired: true
        );
        var intermediateDescriptorEdge = new DescriptorEdgeSource(
            false,
            Path("$.programTypeDescriptor"),
            intermediateRoot.Table,
            Col("ProgramTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
            IsRequired: false
        );

        var subjectModel = CreateModel("Ed-Fi", "StudentSectionAssociation", subjectRoot, [subjectBinding]);
        var intermediateModel = CreateModel(
            "Ed-Fi",
            "Intermediate",
            intermediateRoot,
            descriptorEdges: [intermediateDescriptorEdge]
        );
        var subject = CreateConcrete(1, "Ed-Fi", "StudentSectionAssociation", subjectModel);
        var intermediate = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
            CreateLookup(subject, intermediate),
            []
        );

        result.Should().HaveCount(2);
        result[0].SourceTable.Should().Be(Table("StudentSectionAssociation"));
        result[0].SourceColumnName.Should().Be(Col("Intermediate_DocumentId"));
        result[0].TargetTable.Should().Be(Table("Intermediate"));
        result[0].TargetColumnName.Should().Be(Col("DocumentId"));
        result[1].SourceTable.Should().Be(Table("Intermediate"));
        result[1].SourceColumnName.Should().Be(Col("ProgramTypeDescriptor_DescriptorId"));
        result[1].TargetTable.Should().Be(DescriptorTable);
        result[1].TargetColumnName.Should().Be(Col("DocumentId"));
    }

    [Test]
    public void It_should_reject_a_non_identity_intermediate_resource_hop_when_the_basis_is_a_descriptor()
    {
        var descriptorOwnerRoot = CreateRootTable(
            Table("DescriptorOwner"),
            [
                new DbColumnModel(
                    Col("ProgramTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
            ]
        );
        var intermediateRoot = CreateRootTable(
            Table("Intermediate"),
            [
                new DbColumnModel(
                    Col("DescriptorOwner_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "DescriptorOwner")
                ),
            ]
        );
        var subjectRoot = CreateRootTable(
            Table("StudentSectionAssociation"),
            [
                new DbColumnModel(
                    Col("Intermediate_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Intermediate")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.intermediateReference"),
            subjectRoot.Table,
            Col("Intermediate_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Intermediate"),
            [],
            IsRequired: true
        );
        var nonIdentityIntermediateBinding = new DocumentReferenceBinding(
            false,
            Path("$.descriptorOwnerReference"),
            intermediateRoot.Table,
            Col("DescriptorOwner_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "DescriptorOwner"),
            [],
            IsRequired: true
        );
        var descriptorEdge = new DescriptorEdgeSource(
            true,
            Path("$.programTypeDescriptor"),
            descriptorOwnerRoot.Table,
            Col("ProgramTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "StudentSectionAssociation", subjectRoot, [subjectBinding]);
        var intermediateModel = CreateModel(
            "Ed-Fi",
            "Intermediate",
            intermediateRoot,
            [nonIdentityIntermediateBinding]
        );
        var descriptorOwnerModel = CreateModel(
            "Ed-Fi",
            "DescriptorOwner",
            descriptorOwnerRoot,
            descriptorEdges: [descriptorEdge]
        );
        var subject = CreateConcrete(1, "Ed-Fi", "StudentSectionAssociation", subjectModel);
        var intermediate = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);
        var descriptorOwner = CreateConcrete(3, "Ed-Fi", "DescriptorOwner", descriptorOwnerModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
            CreateLookup(subject, intermediate, descriptorOwner),
            []
        );

        result.Should().BeEmpty();
    }

    [Test]
    public void It_should_resolve_a_descriptor_self_reference()
    {
        var descriptorRoot = new DbTableModel(
            DescriptorTable,
            Path("$"),
            new TableKey("PK_Descriptor", [new DbKeyColumn(Col("DocumentId"), ColumnKind.Scalar)]),
            [new DbColumnModel(Col("DocumentId"), ColumnKind.Scalar, null, false, null, null)],
            []
        );

        var descriptorModel = CreateModel(
            "Ed-Fi",
            "TransportationTypeDescriptor",
            descriptorRoot,
            schema: DmsSchema,
            storageKind: ResourceStorageKind.SharedDescriptorTable
        );
        var descriptor = CreateConcrete(1, "Ed-Fi", "TransportationTypeDescriptor", descriptorModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            descriptor,
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            CreateLookup(descriptor),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceTable.Should().Be(DescriptorTable);
        result[0].SourceColumnName.Should().Be(Col("DocumentId"));
        result[0].TargetTable.Should().BeNull();
        result[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_include_the_root_locator_step_for_non_root_reference_bindings()
    {
        var intermediateRoot = CreateRootTable(
            Table("Intermediate"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var subjectDetailTable = new DbTableName(EdFiSchema, "CourseTranscriptDetail");
        var subjectDetailBinding = new DocumentReferenceBinding(
            true,
            Path("$.details[*].intermediateReference"),
            subjectDetailTable,
            Col("Intermediate_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Intermediate"),
            [],
            IsRequired: true
        );
        var subjectRoot = CreateRootTable(Table("CourseTranscript"));
        var subjectDetail = new DbTableModel(
            subjectDetailTable,
            Path("$.details[*]"),
            new TableKey(
                "PK_CourseTranscriptDetail",
                [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.ParentKeyPart, null, false, null, null),
                new DbColumnModel(
                    Col("CourseTranscript_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    Col("Intermediate_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Intermediate")
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [Col("CollectionItemId")],
                [Col("CourseTranscript_DocumentId")],
                [Col("CourseTranscript_DocumentId")],
                []
            ),
        };
        var subjectModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            EdFiSchema,
            ResourceStorageKind.RelationalTables,
            subjectRoot,
            [subjectRoot, subjectDetail],
            [subjectDetailBinding],
            []
        );
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var intermediateModel = CreateModel(
            "Ed-Fi",
            "Intermediate",
            intermediateRoot,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReference"),
                    intermediateRoot.Table,
                    Col("Student_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [],
                    IsRequired: true
                ),
            ]
        );
        var intermediate = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);
        var studentModel = CreateModel("Ed-Fi", "Student", CreateRootTable(Table("Student")));
        var student = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, intermediate, student),
            []
        );

        result.Should().HaveCount(3);
        result[0].SourceTable.Should().Be(Table("CourseTranscript"));
        result[0].SourceColumnName.Should().Be(Col("DocumentId"));
        result[0].TargetTable.Should().Be(subjectDetailTable);
        result[0].TargetColumnName.Should().Be(Col("CourseTranscript_DocumentId"));
        result[1].SourceTable.Should().Be(subjectDetailTable);
        result[1].SourceColumnName.Should().Be(Col("Intermediate_DocumentId"));
        result[1].TargetTable.Should().Be(Table("Intermediate"));
        result[1].TargetColumnName.Should().Be(Col("DocumentId"));
        result[2].SourceTable.Should().Be(Table("Intermediate"));
        result[2].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result[2].TargetTable.Should().BeNull();
        result[2].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_use_canonical_source_columns_for_unified_alias_bindings()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Student_DocumentId_Canonical"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    Col("Student_DocumentId_Alias"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    new ColumnStorage.UnifiedAlias(Col("Student_DocumentId_Canonical"), null)
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId_Alias"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);
        var student = CreateConcrete(2, "Ed-Fi", "Student", studentModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Should().ContainSingle();
        result[0].SourceColumnName.Should().Be(Col("Student_DocumentId_Canonical"));
    }

    [Test]
    public void It_should_return_empty_when_no_path_exists()
    {
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [
                new DbColumnModel(
                    Col("Missing_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Missing")
                ),
            ]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.missingReference"),
            subjectRoot.Table,
            Col("Missing_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Missing"),
            [],
            IsRequired: true
        );

        var subjectModel = CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding]);
        var subject = CreateConcrete(1, "Ed-Fi", "CourseTranscript", subjectModel);

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePath(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject),
            []
        );

        result.Should().BeEmpty();
    }
}
