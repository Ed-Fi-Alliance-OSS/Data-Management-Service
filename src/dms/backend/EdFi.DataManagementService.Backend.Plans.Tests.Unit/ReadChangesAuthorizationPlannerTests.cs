// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class ReadChangesAuthorizationPlannerTests
{
    // Covers the unsupported-strategy AC: OwnershipBased, RelationshipsWithPeopleOnly,
    // RelationshipsWithEdOrgsAndPeopleInverted, the live-only RelationshipsWithEdOrgsAndPeople, and a
    // custom view-based name (deferred from v1) — all map to a 500 security-configuration outcome.
    [TestCase("OwnershipBased")]
    [TestCase("RelationshipsWithPeopleOnly")]
    [TestCase("RelationshipsWithEdOrgsAndPeopleInverted")]
    [TestCase("RelationshipsWithEdOrgsAndPeople")]
    [TestCase("SchoolWithStudents")]
    public void It_returns_security_configuration_for_unsupported_strategy(string strategyName)
    {
        var outcome = Plan(
            EdOrgResource(), // resource with one EdOrg securable (helper below)
            EdOrgTrackedTable(), // tracked table with the OldX EdOrg column (helper below)
            new RelationalAuthorizationContext([1L], []),
            strategyName
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Contain(strategyName);
    }

    [Test]
    public void It_returns_empty_plan_for_NoFurtherAuthorizationRequired()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.RelationshipChecks.Should().BeEmpty();
        plan.NamespaceCheck.Should().BeNull();
    }

    [Test]
    public void It_resolves_edorg_only_to_the_hierarchy_view_normal_direction()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        var subject = plan.RelationshipChecks.Single().Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("OldSchoolId_Unified");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToEducationOrganizationId");
        subject.AuthViewSubjectColumn.Value.Should().Be("TargetEducationOrganizationId"); // normal direction
        subject.AuthViewClaimColumn.Value.Should().Be("SourceEducationOrganizationId");
    }

    [Test]
    public void It_resolves_edorg_only_inverted_to_swapped_columns()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.AuthViewSubjectColumn.Value.Should().Be("SourceEducationOrganizationId"); // inverted
        subject.AuthViewClaimColumn.Value.Should().Be("TargetEducationOrganizationId");
    }

    [Test]
    public void It_returns_security_configuration_when_any_declared_edorg_securable_has_no_tracked_old_column()
    {
        var outcome = Plan(
            MultiEdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure
            .UnavailableStrategyNames.Should()
            .Contain(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
    }

    [Test]
    public void It_resolves_students_only_including_deletes_to_the_including_deletes_view()
    {
        // StudentTrackedTable() has a PersonDocumentId column (PersonKind=Student, OldStudent_DocumentId).
        var outcome = Plan(
            StudentResource(),
            StudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithStudentsOnlyIncludingDeletes"
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("OldStudent_DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
        subject.AuthViewSubjectColumn.Value.Should().Be("Student_DocumentId");
    }

    [Test]
    public void It_resolves_top_level_student_self_document_id_for_including_deletes_relationships()
    {
        var outcome = Plan(
            TopLevelStudentResource(),
            TopLevelStudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("OldStudent_DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
        subject.AuthViewSubjectColumn.Value.Should().Be("Student_DocumentId");
    }

    [Test]
    public void It_resolves_ds52_top_level_student_self_document_id_for_including_deletes_relationships()
    {
        (DerivedRelationalModelSet modelSet, MappingSet mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        ConcreteResourceModel resource = modelSet.ConcreteResourcesInNameOrder.Single(r =>
            r.ResourceKey.Resource.ResourceName == "Student" && r.ResourceKey.Resource.ProjectName == "Ed-Fi"
        );
        TrackedChangeTableInfo trackedChangeTable = modelSet.TrackedChangeTablesInNameOrder.Single(t =>
            t.SourceTable == resource.RelationalModel.Root.Table
        );

        var outcome = ReadChangesAuthorizationPlanner.Plan(
            mappingSet,
            resource,
            trackedChangeTable,
            [new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsAndPeopleIncludingDeletes", 0)],
            new RelationalAuthorizationContext([1L], [])
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("OldStudent_DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
        subject.AuthViewSubjectColumn.Value.Should().Be("Student_DocumentId");
    }

    [Test]
    public void It_returns_security_configuration_when_any_declared_person_securable_has_no_tracked_old_document_id()
    {
        var outcome = Plan(
            MultiStudentResource(),
            PartialMultiStudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithStudentsOnlyIncludingDeletes"
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Contain("RelationshipsWithStudentsOnlyIncludingDeletes");
    }

    [Test]
    public void It_returns_security_configuration_when_declared_person_securable_has_no_tracked_old_document_id()
    {
        var outcome = Plan(
            EdOrgAndStudentResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Contain("RelationshipsWithEdOrgsAndPeopleIncludingDeletes");
    }

    // DMS-1188: an authenticated client with ZERO EducationOrganization claim ids that hits a
    // relationship-based ReadChanges strategy must FAIL CLOSED (empty result), mirroring the live
    // path's NoClaims behavior — not throw (which previously surfaced as a generic 500). The planner
    // must still produce a non-null claim parameterization (so the emitter renders the relationship
    // predicate as match-nothing) rather than null (which would omit the predicate = fail-open).
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_returns_a_match_nothing_plan_for_a_relationship_strategy_with_no_claim_edorg_ids(
        SqlDialect dialect
    )
    {
        var outcome = Plan(
            CreateMappingSet(dialect),
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;

        // The relationship subject is still planned (fail-closed: the predicate is emitted, not omitted).
        plan.RelationshipChecks.Should().ContainSingle();

        // The claim parameterization is present but represents zero claims in the match-nothing shape:
        //   PG  → PgsqlArray with the base parameter name and no claim ids,
        //   MSSQL → MssqlScalar with no scalar parameter names and no claim ids.
        plan.ClaimParameterization.Should().NotBeNull();
        plan.ClaimParameterization!.ClaimEducationOrganizationIds.Should().BeEmpty();

        AuthorizationClaimEducationOrganizationIdParameterizationKind expectedKind = dialect switch
        {
            SqlDialect.Pgsql => AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
            SqlDialect.Mssql => AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
        plan.ClaimParameterization.Kind.Should().Be(expectedKind);

        if (dialect is SqlDialect.Pgsql)
        {
            plan.ClaimParameterization.ParameterNamesInOrder.Should()
                .Equal(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds);
        }
        else
        {
            // MssqlScalar with zero ids → zero scalar parameter names (emitter renders 1 = 0).
            plan.ClaimParameterization.ParameterNamesInOrder.Should().BeEmpty();
        }
    }

    [Test]
    public void It_resolves_namespace_to_the_tracked_old_namespace_column()
    {
        var outcome = Plan(
            NamespaceResource(),
            NamespaceTrackedTable(),
            new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]),
            AuthorizationStrategyNameConstants.NamespaceBased
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.NamespaceCheck!.TrackedOldNamespaceColumn.Value.Should().Be("OldNamespace");
        plan.NamespaceParameterization.Should().NotBeNull();
    }

    [Test]
    public void It_returns_403_no_prefixes_when_namespace_configured_without_prefixes()
    {
        var outcome = Plan(
            NamespaceResource(),
            NamespaceTrackedTable(),
            new RelationalAuthorizationContext([], []),
            AuthorizationStrategyNameConstants.NamespaceBased
        );

        outcome.Should().BeOfType<ReadChangesAuthorizationPlanOutcome.NamespaceNoPrefixesConfigured>();
    }

    [Test]
    public void It_resolves_namespace_for_shared_descriptor_resources_via_descriptor_contract()
    {
        var outcome = Plan(
            DescriptorNamespaceResource(),
            DescriptorNamespaceTrackedTable(),
            new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]),
            AuthorizationStrategyNameConstants.NamespaceBased
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.NamespaceCheck!.TrackedOldNamespaceColumn.Value.Should().Be("OldNamespace");
        plan.NamespaceParameterization.Should().NotBeNull();
    }

    // ---- Test wrapper -------------------------------------------------------

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _trackedSchema = new("tracked_changes_edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    private static ReadChangesAuthorizationPlanOutcome Plan(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        RelationalAuthorizationContext context,
        params string[] strategyNames
    ) => Plan(CreateMappingSet(), resource, trackedChangeTable, context, strategyNames);

    private static ReadChangesAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        RelationalAuthorizationContext context,
        params string[] strategyNames
    )
    {
        ConfiguredAuthorizationStrategy[] configuredAuthorizationStrategies =
        [
            .. strategyNames.Select(
                static (strategyName, index) => new ConfiguredAuthorizationStrategy(strategyName, index)
            ),
        ];

        return ReadChangesAuthorizationPlanner.Plan(
            mappingSet,
            resource,
            trackedChangeTable,
            configuredAuthorizationStrategies,
            context
        );
    }

    // ---- Resource-model builder (adapted from RelationalAuthorizationPlannerTests) ------------------

    /// <summary>
    /// A concrete resource whose single EdOrg securable element resolves to a root-table
    /// <c>SchoolId_Unified</c> column at <c>$.schoolReference.schoolId</c>.
    /// </summary>
    private static ConcreteResourceModel EdOrgResource()
    {
        var root = RootTable(
            "School",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
            ]
        );
        var model = new RelationalResourceModel(
            _schoolResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, _schoolResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource that declares two EdOrg securable paths. The paired partial tracked table
    /// intentionally carries only the school path, proving that one successful subject cannot mask
    /// another missing tracked old column.
    /// </summary>
    private static ConcreteResourceModel MultiEdOrgResource()
    {
        var root = RootTable(
            "School",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("DistrictId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.districtReference.districtId"),
                    null
                ),
            ]
        );
        var model = new RelationalResourceModel(
            _schoolResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(6, _schoolResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.districtReference.districtId", "DistrictId"),
                ],
                [],
                [],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource that declares both EdOrg and Student securable elements. Used to prove a
    /// missing tracked-change Student DocumentId column cannot be masked by the resolvable EdOrg subject.
    /// </summary>
    private static ConcreteResourceModel EdOrgAndStudentResource()
    {
        var root = RootTable(
            "StudentSchoolAssociation",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("StudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.studentReference.studentUniqueId"),
                    null
                ),
            ]
        );
        var resource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var model = new RelationalResourceModel(
            resource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(5, resource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource with two Student person securable paths. The paired partial tracked table
    /// intentionally carries only the primary Student path.
    /// </summary>
    private static ConcreteResourceModel MultiStudentResource()
    {
        var studentResource = new QualifiedResourceName("Ed-Fi", "StudentPairAssociation");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "StudentPairAssociation"),
            Path("$"),
            new TableKey("PK_StudentPairAssociation", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("PrimaryStudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.primaryStudentReference.studentUniqueId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("SecondaryStudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.secondaryStudentReference.studentUniqueId"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            studentResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(7, studentResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.primaryStudentReference.studentUniqueId", "$.secondaryStudentReference.studentUniqueId"],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource with a single Student person securable element at
    /// <c>$.studentReference.studentUniqueId</c>. Person resolution keys off the tracked table's
    /// <see cref="TrackedChangePersonJoinInfo"/>, so the resource model only needs to be coherent.
    /// </summary>
    private static ConcreteResourceModel StudentResource()
    {
        var studentResource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "StudentSchoolAssociation"),
            Path("$"),
            new TableKey("PK_StudentSchoolAssociation", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("StudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.studentReference.studentUniqueId"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            studentResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(2, studentResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A top-level Student resource whose Student securable path is the resource's own identity path.
    /// </summary>
    private static ConcreteResourceModel TopLevelStudentResource()
    {
        var studentResource = new QualifiedResourceName("Ed-Fi", "Student");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "Student"),
            Path("$"),
            new TableKey("PK_Student", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("StudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.studentUniqueId"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            studentResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(8, studentResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], [], ["$.studentUniqueId"], [], []),
        };
    }

    /// <summary>
    /// A concrete resource whose single Namespace securable element resolves to a root-table
    /// <c>Namespace</c> column at <c>$.namespace</c>.
    /// </summary>
    private static ConcreteResourceModel NamespaceResource()
    {
        var namespaceResource = new QualifiedResourceName("Ed-Fi", "Survey");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "Survey"),
            Path("$"),
            new TableKey("PK_Survey", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 255),
                    false,
                    Path("$.namespace"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            namespaceResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(3, namespaceResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], ["$.namespace"], [], [], []),
        };
    }

    /// <summary>
    /// A shared-descriptor concrete resource (<see cref="ResourceStorageKind.SharedDescriptorTable"/>)
    /// whose <see cref="DescriptorMetadata"/> column contract maps Namespace to the shared
    /// <c>dms.Descriptor</c> <c>Namespace</c> column. Such resources carry no Namespace securable
    /// element path; resolution flows through the descriptor contract instead.
    /// </summary>
    private static ConcreteResourceModel DescriptorNamespaceResource()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "Descriptor"),
            Path("$"),
            new TableKey("PK_Descriptor", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 255),
                    false,
                    Path("$.namespace"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            descriptorResource,
            _edfiSchema,
            ResourceStorageKind.SharedDescriptorTable,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(4, descriptorResource, "1.0", false),
            ResourceStorageKind.SharedDescriptorTable,
            model,
            new DescriptorMetadata(
                new DescriptorColumnContract(
                    new DbColumnName("Namespace"),
                    new DbColumnName("CodeValue"),
                    null,
                    null,
                    null,
                    null,
                    null
                ),
                DiscriminatorStrategy.ResourceKeyId
            )
        );
    }

    private static DbTableModel RootTable(string name, IReadOnlyList<DbColumnModel> columns) =>
        new(
            new DbTableName(_edfiSchema, name),
            Path("$"),
            new TableKey("PK_" + name, [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            columns,
            []
        );

    // ---- Tracked-change builder (adapted from TrackedChangeQueryPlannerTests) -----------------------

    /// <summary>
    /// A tracked-change table carrying the <c>OldSchoolId_Unified</c> identity/securable column that
    /// mirrors the EdOrg securable element on <see cref="EdOrgResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo EdOrgTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "School"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "School"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldSchoolId_Unified"),
                    NewColumnName: new DbColumnName("NewSchoolId_Unified"),
                    SourceJsonPath: "$.schoolReference.schoolId",
                    CanonicalStorageColumn: new DbColumnName("SchoolId_Unified"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns:
            [
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Id,
                    new DbColumnName("Id"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.ChangeVersion,
                    new DbColumnName("ChangeVersion"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.CreatedAt,
                    new DbColumnName("CreatedAt"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
            ],
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    /// <summary>
    /// A tracked-change table carrying a denormalized <c>OldStudent_DocumentId</c> person column
    /// (<see cref="TrackedChangeColumnRole.PersonDocumentId"/>) reached through a
    /// <c>Student</c> person join, mirroring the Student securable element on <see cref="StudentResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo StudentTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "StudentSchoolAssociation"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "StudentSchoolAssociation"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldStudent_DocumentId"),
                    NewColumnName: new DbColumnName("NewStudent_DocumentId"),
                    SourceJsonPath: "$.studentReference.studentUniqueId",
                    CanonicalStorageColumn: null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.PersonDocumentId,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement,
                    PersonJoinName: "Student"
                ),
            ],
            SystemColumns:
            [
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Id,
                    new DbColumnName("Id"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.ChangeVersion,
                    new DbColumnName("ChangeVersion"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.CreatedAt,
                    new DbColumnName("CreatedAt"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
            ],
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: [new TrackedChangePersonJoinInfo("Student", SecurableElementKind.Student, [])]
        );

    /// <summary>
    /// A tracked-change table carrying a direct self <c>OldStudent_DocumentId</c> person column for a
    /// top-level Student resource.
    /// </summary>
    private static TrackedChangeTableInfo TopLevelStudentTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "Student"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "Student"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldStudent_DocumentId"),
                    NewColumnName: new DbColumnName("NewStudent_DocumentId"),
                    SourceJsonPath: "$.studentUniqueId",
                    CanonicalStorageColumn: new DbColumnName("DocumentId"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.PersonDocumentId,
                    Origin: TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    /// <summary>
    /// A tracked-change table carrying only one of <see cref="MultiStudentResource"/>'s declared
    /// Student person DocumentId columns.
    /// </summary>
    private static TrackedChangeTableInfo PartialMultiStudentTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "StudentPairAssociation"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "StudentPairAssociation"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldPrimaryStudent_DocumentId"),
                    NewColumnName: new DbColumnName("NewPrimaryStudent_DocumentId"),
                    SourceJsonPath: "$.primaryStudentReference.studentUniqueId",
                    CanonicalStorageColumn: null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.PersonDocumentId,
                    Origin: TrackedChangeColumnOrigin.SecurableElement,
                    PersonJoinName: "PrimaryStudent"
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: [new TrackedChangePersonJoinInfo("PrimaryStudent", SecurableElementKind.Student, [])]
        );

    /// <summary>
    /// A tracked-change table carrying the <c>OldNamespace</c> securable column that mirrors the
    /// Namespace securable element on <see cref="NamespaceResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo NamespaceTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "Survey"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "Survey"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldNamespace"),
                    NewColumnName: new DbColumnName("NewNamespace"),
                    SourceJsonPath: "$.namespace",
                    CanonicalStorageColumn: new DbColumnName("Namespace"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.String, 255),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    /// <summary>
    /// A shared-descriptor tracked-change table (<see cref="TrackedChangeTableKind.SharedDescriptor"/>)
    /// carrying the <c>OldNamespace</c> column for the shared <c>dms.Descriptor</c> table, mirroring
    /// the descriptor column contract on <see cref="DescriptorNamespaceResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo DescriptorNamespaceTrackedTable() =>
        new(
            Table: new DbTableName(new DbSchemaName("tracked_changes_dms"), "Descriptor"),
            Kind: TrackedChangeTableKind.SharedDescriptor,
            SourceTable: new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldNamespace"),
                    NewColumnName: new DbColumnName("NewNamespace"),
                    SourceJsonPath: "$.namespace",
                    CanonicalStorageColumn: new DbColumnName("Namespace"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.String, 255),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    private static IReadOnlyList<TrackedChangeSystemColumnInfo> NamespaceSystemColumns() =>
        [
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.Id,
                new DbColumnName("Id"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.ChangeVersion,
                new DbColumnName("ChangeVersion"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.CreatedAt,
                new DbColumnName("CreatedAt"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
        ];

    // ---- MappingSet builder (adapted from RelationalAuthorizationPlannerTests.EmptyMappingSet) ------

    private static MappingSet CreateMappingSet(SqlDialect dialect = SqlDialect.Pgsql) =>
        new(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 0,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder: []
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}
