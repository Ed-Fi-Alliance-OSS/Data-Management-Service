// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_SingleRecordCustomViewAuthorizationPlanner
{
    private static readonly DbSchemaName EdFiSchema = new("edfi");
    private static readonly DbSchemaName DmsSchema = new("dms");
    private static readonly DbSchemaName AuthSchema = new("auth");

    private static DbTableName Table(string name) => new(EdFiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string resource, bool isAbstract = false) =>
        new(id, new QualifiedResourceName("Ed-Fi", resource), "1.0", isAbstract);

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

    private static DbTableModel CreateChildTable(DbTableName table, IReadOnlyList<DbColumnModel> columns) =>
        new(
            table,
            Path("$.items[*]"),
            new TableKey("PK_Child", [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.Scalar)]),
            columns,
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Unspecified,
                [Col("CollectionItemId")],
                [Col("DocumentId")],
                [],
                []
            ),
        };

    private static DbColumnModel DocumentFkColumn(string name, string targetResource) =>
        new(
            Col(name),
            ColumnKind.DocumentFk,
            null,
            false,
            null,
            new QualifiedResourceName("Ed-Fi", targetResource)
        );

    private static RelationalResourceModel CreateModel(
        string resource,
        DbTableModel root,
        IReadOnlyList<DocumentReferenceBinding>? bindings = null,
        IReadOnlyList<DescriptorEdgeSource>? descriptorEdges = null,
        IReadOnlyList<DbTableModel>? tables = null
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resource),
            EdFiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            tables ?? [root],
            bindings ?? [],
            descriptorEdges ?? []
        );

    private static ConcreteResourceModel CreateConcrete(
        short keyId,
        string resource,
        RelationalResourceModel model
    ) => new(ResourceKey(keyId, resource), ResourceStorageKind.RelationalTables, model);

    /// <summary>
    /// A reference identity binding. <c>IdentityJsonPath</c> is the identity path on the <em>target</em>
    /// resource, so by default it is the reference path's leaf rehung at the root — the shape MetaEd emits.
    /// </summary>
    private static ReferenceIdentityBinding IdentityBinding(
        string referenceJsonPath,
        string column,
        string? identityJsonPath = null
    ) =>
        new(
            Path(identityJsonPath ?? DeriveIdentityJsonPath(referenceJsonPath)),
            Path(referenceJsonPath),
            Col(column)
        );

    private static string DeriveIdentityJsonPath(string referenceJsonPath)
    {
        var segments = referenceJsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries);

        return $"$.{segments[^1]}";
    }

    private static MappingSet CreateMappingSet(params ConcreteResourceModel[] resources) =>
        new(
            new MappingSetKey("hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo("1.0", "1.0", "test", 0, [], [], []),
                SqlDialect.Pgsql,
                [],
                resources,
                [],
                [],
                [],
                []
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>(),
            new Dictionary<short, ResourceKeyEntry>(),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

    private static SupportedCustomViewAuthorizationStrategy Strategy(
        string strategyName,
        string basisResource,
        int rawConfiguredIndex = 0,
        int authorizationLocalOrder = 0
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, rawConfiguredIndex),
            authorizationLocalOrder,
            new QualifiedResourceName("Ed-Fi", basisResource)
        );

    /// <summary>CourseTranscript -> Student via a root-owned reference.</summary>
    private static (MappingSet MappingSet, ConcreteResourceModel Subject) DirectStudentBasisFixture()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [DocumentFkColumn("Student_DocumentId", "Student")]
        );
        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [IdentityBinding("$.studentReference.studentUniqueId", "Student_StudentUniqueId")],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "CourseTranscript",
            CreateModel("CourseTranscript", subjectRoot, [subjectBinding])
        );
        var student = CreateConcrete(2, "Student", CreateModel("Student", studentRoot));

        return (CreateMappingSet(subject, student), subject);
    }

    [Test]
    public void It_should_reject_ReadMany_because_GET_many_has_its_own_planner()
    {
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var act = () =>
            SingleRecordCustomViewAuthorizationPlanner.Plan(
                mappingSet,
                subject,
                [Strategy("StudentWithCTECourseEnrollments", "Student")],
                NamespaceAuthorizationOperation.ReadMany
            );

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("operation");
    }

    [Test]
    public void It_should_plan_no_checks_when_no_custom_views_are_configured()
    {
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            subject,
            [],
            NamespaceAuthorizationOperation.Update
        );

        outcome
            .Should()
            .BeOfType<SingleRecordCustomViewAuthorizationPlanOutcome.Plan>()
            .Subject.Checks.Should()
            .BeEmpty();
    }

    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    public void It_should_plan_only_a_stored_check_for_read_single_and_delete(
        NamespaceAuthorizationOperation operation
    )
    {
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            operation
        );

        checks.Should().ContainSingle();
        checks[0].Index.Should().Be(0);
        checks[0].ValueSource.Should().Be(CustomViewAuthorizationCheckValueSource.Stored);
        checks[0].AuthView.Should().Be(new DbTableName(AuthSchema, "StudentWithCTECourseEnrollments"));
        checks[0].AuthViewDocumentIdColumn.Should().Be(Col("DocumentId"));
        checks[0].BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "Student"));
        checks[0].ReadableSecurableElements.Should().Equal("StudentUniqueId");
        checks[0].FailureHint.Should().Be("You may need a Student with CTE Course Enrollments.");
        checks[0]
            .CheckTarget.Should()
            .BeOfType<CustomViewAuthorizationCheckTarget.Stored>()
            .Which.RootTable.Should()
            .Be(Table("CourseTranscript"));
    }

    [Test]
    public void It_should_plan_a_stored_then_proposed_pair_for_update()
    {
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.Update
        );

        checks.Should().HaveCount(2);
        checks[0].Index.Should().Be(0);
        checks[0].ValueSource.Should().Be(CustomViewAuthorizationCheckValueSource.Stored);
        checks[1].Index.Should().Be(1);
        checks[1].ValueSource.Should().Be(CustomViewAuthorizationCheckValueSource.Proposed);
        var binding = checks[1]
            .CheckTarget.Should()
            .BeOfType<CustomViewAuthorizationCheckTarget.Proposed>()
            .Subject.Binding;
        binding.Table.Should().Be(Table("CourseTranscript"));
        binding.Column.Should().Be(Col("Student_DocumentId"));
        binding.ParameterSeed.Should().Be("customViewAuthorization1");
    }

    [Test]
    public void It_should_emit_every_stored_check_before_any_proposed_check()
    {
        // Stored values are authorized before proposed values, so with two strategies the indexes must read
        // stored, stored, proposed, proposed rather than interleaving per strategy.
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [
                Strategy("StudentWithCTECourseEnrollments", "Student", rawConfiguredIndex: 0),
                Strategy("StudentWithAnIep", "Student", rawConfiguredIndex: 1, authorizationLocalOrder: 1),
            ],
            NamespaceAuthorizationOperation.Update
        );

        checks
            .Select(check => check.ValueSource)
            .Should()
            .Equal(
                CustomViewAuthorizationCheckValueSource.Stored,
                CustomViewAuthorizationCheckValueSource.Stored,
                CustomViewAuthorizationCheckValueSource.Proposed,
                CustomViewAuthorizationCheckValueSource.Proposed
            );
        checks.Select(check => check.Index).Should().Equal(0, 1, 2, 3);
        checks
            .Select(check => check.ConfiguredStrategy.StrategyName)
            .Should()
            .Equal(
                "StudentWithCTECourseEnrollments",
                "StudentWithAnIep",
                "StudentWithCTECourseEnrollments",
                "StudentWithAnIep"
            );
    }

    [Test]
    public void It_should_preserve_the_configured_index_each_check_is_ordered_by()
    {
        // RawConfiguredIndex is the only input that orders custom-view checks against NamespaceBased, so it
        // must survive planning unchanged on both the stored and proposed check of a strategy.
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student", rawConfiguredIndex: 7)],
            NamespaceAuthorizationOperation.Update
        );

        checks.Select(check => check.ConfiguredStrategy.RawConfiguredIndex).Should().AllBeEquivalentTo(7);
    }

    [Test]
    public void It_should_plan_a_self_basis_stored_check_against_the_root_document_id()
    {
        var (mappingSet, subject) = SelfBasisStudentFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        checks.Should().ContainSingle();
        checks[0].PathToBasisResource.Should().ContainSingle();
        checks[0].PathToBasisResource[0].SourceColumnName.Should().Be(Col("DocumentId"));
        // A self-basis path terminates on the subject's own DocumentId, so no reference names the element.
        // The basis resource's own authoritative identity is used instead; the cross-boundary failure carries
        // only this list, so the planner is the last layer that can supply it.
        checks[0].ReadableSecurableElements.Should().Equal("StudentUniqueId");
    }

    [Test]
    public void It_should_prefer_a_non_role_named_reference_when_resolving_a_self_basis_identity()
    {
        // A role-named reference to the basis must not win the identity lookup, so the readable name does not
        // depend on which resource happens to reference the basis first.
        var studentRoot = CreateRootTable(Table("Student"));
        var subject = CreateConcrete(1, "Student", CreateModel("Student", studentRoot));
        var roleNamedReferrer = CreateConcrete(
            2,
            "AlphaAssociation",
            CreateModel(
                "AlphaAssociation",
                CreateRootTable(
                    Table("AlphaAssociation"),
                    [DocumentFkColumn("ReferencedStudent_DocumentId", "Student")]
                ),
                [
                    new DocumentReferenceBinding(
                        true,
                        Path("$.referencedStudentReference"),
                        Table("AlphaAssociation"),
                        Col("ReferencedStudent_DocumentId"),
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [
                            IdentityBinding(
                                "$.referencedStudentReference.roleNamedUniqueId",
                                "ReferencedStudent_StudentUniqueId"
                            ),
                        ],
                        IsRequired: true,
                        IsRoleNamed: true
                    ),
                ]
            )
        );
        var plainReferrer = CreateConcrete(
            3,
            "ZuluAssociation",
            CreateModel(
                "ZuluAssociation",
                CreateRootTable(
                    Table("ZuluAssociation"),
                    [DocumentFkColumn("Student_DocumentId", "Student")]
                ),
                [
                    new DocumentReferenceBinding(
                        true,
                        Path("$.studentReference"),
                        Table("ZuluAssociation"),
                        Col("Student_DocumentId"),
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [IdentityBinding("$.studentReference.studentUniqueId", "Student_StudentUniqueId")],
                        IsRequired: true
                    ),
                ]
            )
        );
        var mappingSet = CreateMappingSet(subject, roleNamedReferrer, plainReferrer);

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        checks[0].ReadableSecurableElements.Should().Equal("StudentUniqueId");
    }

    [Test]
    public void It_should_preserve_every_part_of_a_composite_self_basis_identity()
    {
        var courseRoot = CreateRootTable(Table("Course"));
        var subject = CreateConcrete(1, "Course", CreateModel("Course", courseRoot));
        var referrer = CreateConcrete(
            2,
            "CourseOffering",
            CreateModel(
                "CourseOffering",
                CreateRootTable(Table("CourseOffering"), [DocumentFkColumn("Course_DocumentId", "Course")]),
                [
                    new DocumentReferenceBinding(
                        true,
                        Path("$.courseReference"),
                        Table("CourseOffering"),
                        Col("Course_DocumentId"),
                        new QualifiedResourceName("Ed-Fi", "Course"),
                        [
                            IdentityBinding("$.courseReference.courseCode", "Course_CourseCode"),
                            IdentityBinding(
                                "$.courseReference.educationOrganizationId",
                                "Course_EducationOrganizationId"
                            ),
                        ],
                        IsRequired: true
                    ),
                ]
            )
        );
        var mappingSet = CreateMappingSet(subject, referrer);

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("CourseWithAnHonorsFlag", "Course")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        checks[0].ReadableSecurableElements.Should().Equal("CourseCode", "EducationOrganizationId");
    }

    [Test]
    public void It_should_fall_back_to_the_basis_resource_name_when_nothing_references_it()
    {
        // A descriptor reached by its own view is the realistic case: descriptor edges carry only the
        // referencing value path, so no authoritative identity path exists to name.
        var descriptorRoot = CreateRootTable(Table("TransportationTypeDescriptor"));
        var subject = CreateConcrete(
            1,
            "TransportationTypeDescriptor",
            CreateModel("TransportationTypeDescriptor", descriptorRoot)
        );
        var mappingSet = CreateMappingSet(subject);

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("TransportationTypeDescriptorWithABus", "TransportationTypeDescriptor")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        checks[0].ReadableSecurableElements.Should().Equal("TransportationTypeDescriptor");
    }

    [Test]
    public void It_should_plan_a_self_basis_proposed_check_as_unprovable_rather_than_binding_a_value()
    {
        var (mappingSet, subject) = SelfBasisStudentFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.Update
        );

        checks.Should().HaveCount(2);
        checks[1].ValueSource.Should().Be(CustomViewAuthorizationCheckValueSource.Proposed);
        checks[1]
            .CheckTarget.Should()
            .BeOfType<CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable>()
            .Which.RootTable.Should()
            .Be(Table("Student"));
    }

    [Test]
    public void It_should_plan_a_directly_referenced_descriptor_basis()
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
                    null
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

        var subject = CreateConcrete(
            1,
            "StudentTransportation",
            CreateModel("StudentTransportation", subjectRoot, [], [descriptorEdge])
        );
        var descriptor = CreateConcrete(
            2,
            "TransportationTypeDescriptor",
            CreateModel(
                "TransportationTypeDescriptor",
                CreateRootTable(Table("TransportationTypeDescriptor"))
            )
        );
        var mappingSet = CreateMappingSet(subject, descriptor);

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("TransportationTypeDescriptorWithABus", "TransportationTypeDescriptor")],
            NamespaceAuthorizationOperation.Update
        );

        checks.Should().HaveCount(2);
        checks[0].PathToBasisResource[^1].TargetTable.Should().Be(new DbTableName(DmsSchema, "Descriptor"));
        checks[0].ReadableSecurableElements.Should().Equal("TransportationTypeDescriptor");
        checks[0].FailureHint.Should().Be("You may need a Transportation Type Descriptor with A Bus.");
        checks[1]
            .CheckTarget.Should()
            .BeOfType<CustomViewAuthorizationCheckTarget.Proposed>()
            .Subject.Binding.Column.Should()
            .Be(Col("TransportationTypeDescriptor_DescriptorId"));
    }

    [Test]
    public void It_should_report_every_readable_element_of_a_composite_identity_basis()
    {
        var courseRoot = CreateRootTable(Table("Course"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [DocumentFkColumn("Course_DocumentId", "Course")]
        );
        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.courseReference"),
            subjectRoot.Table,
            Col("Course_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Course"),
            [
                IdentityBinding("$.courseReference.courseCode", "Course_CourseCode"),
                IdentityBinding(
                    "$.courseReference.educationOrganizationId",
                    "Course_EducationOrganizationId"
                ),
            ],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "CourseTranscript",
            CreateModel("CourseTranscript", subjectRoot, [subjectBinding])
        );
        var course = CreateConcrete(2, "Course", CreateModel("Course", courseRoot));
        var mappingSet = CreateMappingSet(subject, course);

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("CourseWithAnHonorsFlag", "Course")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        checks[0].ReadableSecurableElements.Should().Equal("CourseCode", "EducationOrganizationId");
    }

    [Test]
    public void It_should_report_a_security_configuration_failure_when_no_join_path_reaches_the_basis()
    {
        var subject = CreateConcrete(
            1,
            "BellSchedule",
            CreateModel("BellSchedule", CreateRootTable(Table("BellSchedule")))
        );
        var student = CreateConcrete(2, "Student", CreateModel("Student", CreateRootTable(Table("Student"))));
        var mappingSet = CreateMappingSet(subject, student);

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        var securityConfiguration = outcome
            .Should()
            .BeOfType<SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        securityConfiguration.PlannedChecks.Should().BeEmpty();
        securityConfiguration.Failures.Should().ContainSingle();
        securityConfiguration
            .Failures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.NoCustomViewJoinPath);
        securityConfiguration
            .Failures[0]
            .Location!.AuthorizationObjectName.Should()
            .Be("auth.StudentWithCTECourseEnrollments");
    }

    [Test]
    public void It_should_carry_checks_planned_ahead_of_a_failure_so_earlier_views_can_still_be_validated()
    {
        var (mappingSet, subject) = DirectStudentBasisFixture();

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            subject,
            [
                Strategy("StudentWithCTECourseEnrollments", "Student", rawConfiguredIndex: 0),
                Strategy("BellScheduleWithAPeriod", "BellSchedule", rawConfiguredIndex: 1),
            ],
            NamespaceAuthorizationOperation.ReadSingle
        );

        var securityConfiguration = outcome
            .Should()
            .BeOfType<SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        securityConfiguration.Failures.Should().ContainSingle();
        securityConfiguration.PlannedChecks.Should().ContainSingle();
        securityConfiguration
            .PlannedChecks[0]
            .ConfiguredStrategy.StrategyName.Should()
            .Be("StudentWithCTECourseEnrollments");
    }

    [Test]
    public void It_should_fail_closed_for_a_write_whose_basis_is_reached_only_through_a_child_collection()
    {
        var (mappingSet, subject) = ChildCollectionBasisFixture();

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.Update
        );

        var securityConfiguration = outcome
            .Should()
            .BeOfType<SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        securityConfiguration.PlannedChecks.Should().BeEmpty();
        securityConfiguration
            .Failures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.MissingProposedCustomViewRootBinding);
    }

    [Test]
    public void It_should_still_plan_a_stored_only_check_for_a_child_collection_basis_path()
    {
        // A stored check can walk the child hop: the root DocumentId is known. Only a proposed check cannot,
        // so GET-by-id and DELETE keep working where a write fails closed.
        var (mappingSet, subject) = ChildCollectionBasisFixture();

        var checks = PlannedChecks(
            mappingSet,
            subject,
            [Strategy("StudentWithCTECourseEnrollments", "Student")],
            NamespaceAuthorizationOperation.ReadSingle
        );

        checks.Should().ContainSingle();
        checks[0].ValueSource.Should().Be(CustomViewAuthorizationCheckValueSource.Stored);
        checks[0].PathToBasisResource.Should().HaveCount(2);
    }

    /// <summary>
    /// Student as its own basis, with another resource referencing Student so the basis's authoritative
    /// identity path is discoverable — the shape every real Data Standard model has.
    /// </summary>
    private static (MappingSet MappingSet, ConcreteResourceModel Subject) SelfBasisStudentFixture()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subject = CreateConcrete(1, "Student", CreateModel("Student", studentRoot));
        var referrer = CreateConcrete(
            2,
            "StudentSchoolAssociation",
            CreateModel(
                "StudentSchoolAssociation",
                CreateRootTable(
                    Table("StudentSchoolAssociation"),
                    [DocumentFkColumn("Student_DocumentId", "Student")]
                ),
                [
                    new DocumentReferenceBinding(
                        true,
                        Path("$.studentReference"),
                        Table("StudentSchoolAssociation"),
                        Col("Student_DocumentId"),
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [IdentityBinding("$.studentReference.studentUniqueId", "Student_StudentUniqueId")],
                        IsRequired: true
                    ),
                ]
            )
        );

        return (CreateMappingSet(subject, referrer), subject);
    }

    /// <summary>
    /// A subject whose only route to Student is an identity reference living on a child collection table.
    /// </summary>
    private static (MappingSet MappingSet, ConcreteResourceModel Subject) ChildCollectionBasisFixture()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(Table("Section"));
        var childTable = CreateChildTable(
            Table("SectionStudent"),
            [
                new DbColumnModel(Col("DocumentId"), ColumnKind.Scalar, null, false, null, null),
                DocumentFkColumn("Student_DocumentId", "Student"),
            ]
        );
        var childBinding = new DocumentReferenceBinding(
            true,
            Path("$.students[*].studentReference"),
            childTable.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [IdentityBinding("$.students[*].studentReference.studentUniqueId", "Student_StudentUniqueId")],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "Section",
            CreateModel("Section", subjectRoot, [childBinding], tables: [subjectRoot, childTable])
        );
        var student = CreateConcrete(2, "Student", CreateModel("Student", studentRoot));

        return (CreateMappingSet(subject, student), subject);
    }

    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> PlannedChecks(
        MappingSet mappingSet,
        ConcreteResourceModel subject,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> strategies,
        NamespaceAuthorizationOperation operation
    ) =>
        SingleRecordCustomViewAuthorizationPlanner
            .Plan(mappingSet, subject, strategies, operation)
            .Should()
            .BeOfType<SingleRecordCustomViewAuthorizationPlanOutcome.Plan>()
            .Subject.Checks;
}
