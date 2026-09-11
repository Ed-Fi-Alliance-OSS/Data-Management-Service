// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

[TestFixture]
[Parallelizable]
public class TrackedChangeAuthorizationSqlEmitterTests
{
    [Test]
    public void It_emits_an_edorg_subject_predicate_for_pgsql()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1L, 2L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate.Should().Contain("c.\"OldSchoolId_Unified\" IN (SELECT");
        predicate.Should().Contain("\"TargetEducationOrganizationId\"");
        predicate.Should().Contain("\"auth\".\"EducationOrganizationIdToEducationOrganizationId\"");
        predicate.Should().Contain("\"SourceEducationOrganizationId\"");
        result.Parameters.Should().NotBeEmpty();
    }

    [Test]
    public void It_filters_on_the_document_id_system_column_for_a_self_person_subject()
    {
        // A person resource's own tombstone authorizes through its DocumentId system column
        // (no Old* prefix), so the predicate reads c.DocumentId rather than an Old*_DocumentId value column.
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithStudentsOnlyIncludingDeletes", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("DocumentId"),
                            new DbTableName(
                                new DbSchemaName("auth"),
                                "EducationOrganizationIdToStudentDocumentIdIncludingDeletes"
                            ),
                            new DbColumnName("Student_DocumentId"),
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate.Should().Contain("c.\"DocumentId\" IN (SELECT");
        predicate.Should().Contain("\"Student_DocumentId\"");
        predicate.Should().Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentIdIncludingDeletes\"");
        predicate.Should().NotContain("OldStudent_DocumentId");
    }

    [Test]
    public void It_emits_a_direct_edorg_claim_match_for_pgsql()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1L, 2L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate
            .Should()
            .Contain(
                $"c.\"OldSchoolId_Unified\" = ANY(@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds})"
            );
        predicate.Should().Contain("OR c.\"OldSchoolId_Unified\" IN (SELECT");
    }

    [Test]
    public void It_emits_a_direct_edorg_claim_match_for_inverted_pgsql()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnlyInverted", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.SourceEdOrgId,
                            AuthNames.TargetEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1L, 2L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate
            .Should()
            .Contain(
                $"c.\"OldSchoolId_Unified\" = ANY(@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds})"
            );
        predicate.Should().Contain("SELECT \"SourceEducationOrganizationId\"");
        predicate
            .Should()
            .Contain(
                $"\"TargetEducationOrganizationId\" = ANY(@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds})"
            );
    }

    [Test]
    public void It_emits_a_direct_edorg_claim_match_for_mssql()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Mssql,
                [1L, 2L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Mssql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate
            .Should()
            .Contain(
                "c.[OldSchoolId_Unified] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1)"
            );
        predicate.Should().Contain("OR c.[OldSchoolId_Unified] IN (SELECT");
    }

    [Test]
    public void It_ANDs_namespace_with_the_relationship_or_group()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: new ReadChangesNamespaceCheckSpec(new DbColumnName("OldNamespace")),
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "ReadChangesNamespacePrefix"
            ),
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        result.Predicates.Should().HaveCount(2); // one namespace predicate AND one relationship-group predicate
        string joined = string.Join(" ", result.Predicates);
        joined.Should().Contain("c.\"OldNamespace\" IS NOT NULL");
        joined.Should().Contain("LIKE");
    }

    [Test]
    public void It_emits_nothing_for_an_empty_plan()
    {
        var plan = new ReadChangesAuthorizationPlan([], null, null, null, []);
        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );
        result.Predicates.Should().BeEmpty();
        result.Parameters.Should().BeEmpty();
    }

    // DMS-1188 fail-closed: a relationship strategy whose claim parameterization represents ZERO claim
    // EdOrg ids must still emit the relationship predicate (proving it is NOT omitted = NOT fail-open),
    // and that predicate must match nothing. On PostgreSQL the match-nothing shape is `= ANY(@...)` over
    // an empty array.
    [Test]
    public void It_emits_a_match_nothing_relationship_predicate_for_empty_claims_on_pgsql()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            // The shape the planner produces for zero claims on PostgreSQL: PgsqlArray, base parameter
            // name, no claim ids. Constructed directly because the shared factory rejects empty lists.
            ClaimParameterization: new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds,
                [],
                [RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Pgsql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        // Fail-closed: the relationship predicate IS present (not omitted) ...
        result.Predicates.Should().ContainSingle();
        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate.Should().Contain("c.\"OldSchoolId_Unified\" IN (SELECT");
        // ... and matches nothing: `= ANY(@ClaimEducationOrganizationIds)` bound to an empty long[].
        predicate
            .Should()
            .Contain(
                $"= ANY(@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds})"
            );

        var claimParameter = result.Parameters.Should().ContainSingle().Subject;
        claimParameter
            .Name.Should()
            .Be($"@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds}");
        claimParameter.Value.Should().BeOfType<long[]>().Which.Should().BeEmpty();
    }

    // DMS-1188 fail-closed (SQL Server): zero claim EdOrg ids → MssqlScalar with no scalar parameter
    // names, which the emitter renders as `IN (SELECT 1 WHERE 1 = 0)` — a predicate that is present
    // (not omitted) and matches nothing. No claim parameters are bound.
    [Test]
    public void It_emits_a_match_nothing_relationship_predicate_for_empty_claims_on_mssql()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            // The shape the planner produces for zero claims on SQL Server: MssqlScalar, no parameter
            // names, no claim ids.
            ClaimParameterization: new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds,
                [],
                []
            ),
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Mssql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        // Fail-closed: the relationship predicate IS present (not omitted) ...
        result.Predicates.Should().ContainSingle();
        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate.Should().Contain("c.[OldSchoolId_Unified] IN (SELECT");
        // ... and matches nothing.
        predicate.Should().Contain("IN (SELECT 1 WHERE 1 = 0)");
        // Zero claim ids → zero bound claim parameters.
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void It_emits_a_table_valued_parameter_claim_filter_for_mssql_when_claims_exceed_threshold()
    {
        // 2000+ distinct claim ids forces the SQL Server table-valued-parameter (MssqlStructured) shape.
        var claimEducationOrganizationIds = Enumerable.Range(0, 2500).Select(value => (long)value).ToArray();

        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            claimEducationOrganizationIds,
            RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
        );

        // Guard: confirm the factory actually selected the structured (TVP) kind at this count.
        claimParameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured);

        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            new DbColumnName("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: null,
            ClaimParameterization: claimParameterization,
            NamespaceParameterization: null,
            CustomViewChecks: []
        );

        var result = TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            SqlDialect.Mssql,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

        string predicate = string.Join(" ", result.Predicates).Replace("\n", " ");
        predicate
            .Should()
            .Contain(
                $"IN (SELECT [Id] FROM @{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds})"
            );

        var claimParameter = result.Parameters.Should().ContainSingle().Subject;
        claimParameter
            .Name.Should()
            .Be($"@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds}");
        claimParameter.Value.Should().BeOfType<DataTable>();
        claimParameter.ConfigureParameter.Should().NotBeNull();

        var structuredTable = (DataTable)claimParameter.Value!;
        structuredTable.Columns.Should().ContainSingle().Which.ColumnName.Should().Be("Id");
        structuredTable.Rows.Should().HaveCount(claimEducationOrganizationIds.Length);
    }
}

/// <summary>
/// DMS-1193 Task 47: custom view-based checks render as AND predicates correlated on the tracked-change
/// alias, reading only <c>Old*</c> columns and the <c>DocumentId</c> system column, and bind no claim
/// parameters — only descriptor discriminator values where a descriptor identity part is involved.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_TrackedChangeAuthorizationSqlEmitter_CustomViews
{
    private static readonly DbSchemaName _auth = new("auth");
    private static readonly DbSchemaName _edfi = new("edfi");
    private static readonly DbSchemaName _tracked = new("tracked_changes_edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");
    private static readonly QualifiedResourceName _school = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _student = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _gradingPeriod = new("Ed-Fi", "GradingPeriod");
    private static readonly QualifiedResourceName _gradingPeriodDescriptor = new(
        "Ed-Fi",
        "GradingPeriodDescriptor"
    );
    private static readonly QualifiedResourceName _gradeTypeDescriptor = new("Ed-Fi", "GradeTypeDescriptor");
    private static readonly QualifiedResourceName _educationOrganization = new(
        "Ed-Fi",
        "EducationOrganization"
    );

    [TestCase(
        SqlDialect.Pgsql,
        "c.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"SchoolWithAlternativeType\")"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "c.[DocumentId] IN (SELECT [DocumentId] FROM [auth].[SchoolWithAlternativeType])"
    )]
    public void It_renders_a_self_basis_as_a_document_id_view_membership_predicate(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "SchoolWithAlternativeType",
                0,
                _school,
                new ReadChangesCustomViewBasis.StoredDocumentId(_documentId)
            )
        );

        var result = Emit(plan, dialect);

        result.Predicates.Should().Equal(expectedPredicate);
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void It_renders_a_person_basis_from_the_stored_old_person_document_id_column()
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "StudentWithCTECourseEnrollments",
                0,
                _student,
                new ReadChangesCustomViewBasis.StoredDocumentId(Col("OldStudent_DocumentId"))
            )
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        result
            .Predicates.Should()
            .Equal(
                "c.\"OldStudent_DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\")"
            );
        result.Parameters.Should().BeEmpty();
    }

    [TestCase(
        SqlDialect.Pgsql,
        "EXISTS (SELECT 1 FROM \"edfi\".\"School\" b WHERE b.\"SchoolId\" = c.\"OldSchoolId_Unified\" "
            + "AND b.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"SchoolWithAlternativeType\"))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "EXISTS (SELECT 1 FROM [edfi].[School] b WHERE b.[SchoolId] = c.[OldSchoolId_Unified] "
            + "AND b.[DocumentId] IN (SELECT [DocumentId] FROM [auth].[SchoolWithAlternativeType]))"
    )]
    public void It_renders_a_single_live_seek_as_a_direct_exists_on_the_basis_table(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        // StudentSchoolAssociation -> School: one key pair, no descriptor parts, no probe arms.
        var plan = CustomViewOnlyPlan(
            CustomView(
                "SchoolWithAlternativeType",
                0,
                _school,
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfi, "School"),
                    _documentId,
                    [KeyPair("SchoolId", "OldSchoolId_Unified")],
                    [],
                    []
                )
            )
        );

        var result = Emit(plan, dialect);

        result.Predicates.Should().Equal(expectedPredicate);
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void It_unions_the_live_seek_with_one_tombstone_probe_arm_for_a_suffixed_view()
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "SchoolWithAlternativeTypeIncludingDeletes",
                0,
                _school,
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfi, "School"),
                    _documentId,
                    [KeyPair("SchoolId", "OldSchoolId_Unified")],
                    [],
                    [
                        new ReadChangesCustomViewProbeArm(
                            new DbTableName(_tracked, "School"),
                            _documentId,
                            [KeyPair("OldSchoolId", "OldSchoolId_Unified")],
                            []
                        ),
                    ]
                )
            )
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        result
            .Predicates.Should()
            .Equal(
                "EXISTS (SELECT 1 FROM ("
                    + "SELECT b.\"DocumentId\" AS \"DocumentId\" FROM \"edfi\".\"School\" b WHERE b.\"SchoolId\" = c.\"OldSchoolId_Unified\""
                    + " UNION SELECT t.\"DocumentId\" FROM \"tracked_changes_edfi\".\"School\" t WHERE t.\"OldSchoolId\" = c.\"OldSchoolId_Unified\""
                    + ") basis WHERE basis.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"SchoolWithAlternativeTypeIncludingDeletes\"))"
            );
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void It_joins_a_descriptor_key_pair_through_dms_descriptor_and_binds_its_discriminator_parameters()
    {
        // Grade -> GradingPeriod: the basis stores GradingPeriodDescriptor as a *_DescriptorId FK while the
        // Grade tombstone stores its old Namespace/CodeValue, so the seek resolves the descriptor row under
        // the same two-value discriminator shape TrackedChangeQueryPlanner.BuildDescriptorIdentityJoin binds.
        var plan = CustomViewOnlyPlan(
            CustomView(
                "GradingPeriodWithX",
                0,
                _gradingPeriod,
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfi, "GradingPeriod"),
                    _documentId,
                    [
                        KeyPair("GradingPeriodName", "OldGradingPeriodGradingPeriod_GradingPeriodName"),
                        KeyPair("SchoolId", "OldSchoolId_Unified"),
                        KeyPair("SchoolYear", "OldSchoolYear_Unified"),
                    ],
                    [
                        new ReadChangesCustomViewDescriptorKeyPair(
                            Col("GradingPeriodDescriptor_DescriptorId"),
                            Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"),
                            Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"),
                            _gradingPeriodDescriptor
                        ),
                    ],
                    []
                )
            )
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        result
            .Predicates.Should()
            .Equal(
                "EXISTS (SELECT 1 FROM \"edfi\".\"GradingPeriod\" b"
                    + " LEFT JOIN \"dms\".\"Descriptor\" d0 ON d0.\"Discriminator\" IN (@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0)"
                    + " AND d0.\"Namespace\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace\""
                    + " AND d0.\"CodeValue\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue\""
                    + " WHERE b.\"GradingPeriodName\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodName\""
                    + " AND b.\"SchoolId\" = c.\"OldSchoolId_Unified\""
                    + " AND b.\"SchoolYear\" = c.\"OldSchoolYear_Unified\""
                    + " AND b.\"GradingPeriodDescriptor_DescriptorId\" = d0.\"DocumentId\""
                    + " AND b.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"GradingPeriodWithX\"))"
            );
        result
            .Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(
                ("@CustomViewDescriptorDiscriminator0", "GradingPeriodDescriptor"),
                ("@CustomViewDescriptorDiscriminatorQualified0", "Ed-Fi:GradingPeriodDescriptor")
            );
    }

    [Test]
    public void It_compares_old_namespace_and_code_value_directly_on_a_descriptor_probe_arm()
    {
        // Both tombstones store the descriptor's old Namespace/CodeValue, so the probe arm needs no
        // dms.Descriptor join and binds no parameters of its own.
        var plan = CustomViewOnlyPlan(
            CustomView(
                "GradingPeriodWithXIncludingDeletes",
                0,
                _gradingPeriod,
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfi, "GradingPeriod"),
                    _documentId,
                    [KeyPair("GradingPeriodName", "OldGradingPeriodGradingPeriod_GradingPeriodName")],
                    [
                        new ReadChangesCustomViewDescriptorKeyPair(
                            Col("GradingPeriodDescriptor_DescriptorId"),
                            Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"),
                            Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"),
                            _gradingPeriodDescriptor
                        ),
                    ],
                    [
                        new ReadChangesCustomViewProbeArm(
                            new DbTableName(_tracked, "GradingPeriod"),
                            _documentId,
                            [
                                KeyPair(
                                    "OldGradingPeriodName",
                                    "OldGradingPeriodGradingPeriod_GradingPeriodName"
                                ),
                            ],
                            [
                                new ReadChangesCustomViewProbeDescriptorKeyPair(
                                    Col("OldGradingPeriodDescriptor_Namespace"),
                                    Col("OldGradingPeriodDescriptor_CodeValue"),
                                    Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"),
                                    Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue")
                                ),
                            ]
                        ),
                    ]
                )
            )
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        result
            .Predicates.Should()
            .Equal(
                "EXISTS (SELECT 1 FROM ("
                    + "SELECT b.\"DocumentId\" AS \"DocumentId\" FROM \"edfi\".\"GradingPeriod\" b"
                    + " LEFT JOIN \"dms\".\"Descriptor\" d0 ON d0.\"Discriminator\" IN (@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0)"
                    + " AND d0.\"Namespace\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace\""
                    + " AND d0.\"CodeValue\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue\""
                    + " WHERE b.\"GradingPeriodName\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodName\""
                    + " AND b.\"GradingPeriodDescriptor_DescriptorId\" = d0.\"DocumentId\""
                    + " UNION SELECT t.\"DocumentId\" FROM \"tracked_changes_edfi\".\"GradingPeriod\" t"
                    + " WHERE t.\"OldGradingPeriodName\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodName\""
                    + " AND t.\"OldGradingPeriodDescriptor_Namespace\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace\""
                    + " AND t.\"OldGradingPeriodDescriptor_CodeValue\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue\""
                    + ") basis WHERE basis.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"GradingPeriodWithXIncludingDeletes\"))"
            );
        result.Parameters.Should().HaveCount(2);
    }

    [Test]
    public void It_seeks_an_abstract_basis_through_its_union_view_and_probes_each_member_tombstone()
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "EducationOrganizationWithXIncludingDeletes",
                0,
                _educationOrganization,
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfi, "EducationOrganization_View"),
                    _documentId,
                    [KeyPair("EducationOrganizationId", "OldEducationOrganization_EducationOrganizationId")],
                    [],
                    [
                        ProbeArm("LocalEducationAgency", "OldLocalEducationAgencyId"),
                        ProbeArm("School", "OldSchoolId"),
                        ProbeArm("StateEducationAgency", "OldStateEducationAgencyId"),
                    ]
                )
            )
        );

        var result = Emit(plan, SqlDialect.Mssql);

        result
            .Predicates.Should()
            .Equal(
                "EXISTS (SELECT 1 FROM ("
                    + "SELECT b.[DocumentId] AS [DocumentId] FROM [edfi].[EducationOrganization_View] b WHERE b.[EducationOrganizationId] = c.[OldEducationOrganization_EducationOrganizationId]"
                    + " UNION SELECT t.[DocumentId] FROM [tracked_changes_edfi].[LocalEducationAgency] t WHERE t.[OldLocalEducationAgencyId] = c.[OldEducationOrganization_EducationOrganizationId]"
                    + " UNION SELECT t.[DocumentId] FROM [tracked_changes_edfi].[School] t WHERE t.[OldSchoolId] = c.[OldEducationOrganization_EducationOrganizationId]"
                    + " UNION SELECT t.[DocumentId] FROM [tracked_changes_edfi].[StateEducationAgency] t WHERE t.[OldStateEducationAgencyId] = c.[OldEducationOrganization_EducationOrganizationId]"
                    + ") basis WHERE basis.[DocumentId] IN (SELECT [DocumentId] FROM [auth].[EducationOrganizationWithXIncludingDeletes]))"
            );
        result.Parameters.Should().BeEmpty();

        static ReadChangesCustomViewProbeArm ProbeArm(string member, string oldIdentityColumn) =>
            new(
                new DbTableName(_tracked, member),
                _documentId,
                [KeyPair(oldIdentityColumn, "OldEducationOrganization_EducationOrganizationId")],
                []
            );
    }

    [TestCase(
        SqlDialect.Pgsql,
        "EXISTS (SELECT 1 FROM \"dms\".\"Descriptor\" d"
            + " WHERE d.\"Discriminator\" IN (@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0)"
            + " AND d.\"Namespace\" = c.\"OldGradeTypeDescriptor_Namespace\""
            + " AND d.\"CodeValue\" = c.\"OldGradeTypeDescriptor_CodeValue\""
            + " AND d.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"GradeTypeDescriptorWithX\"))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "EXISTS (SELECT 1 FROM [dms].[Descriptor] d"
            + " WHERE d.[Discriminator] IN (@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0)"
            + " AND d.[Namespace] = c.[OldGradeTypeDescriptor_Namespace]"
            + " AND d.[CodeValue] = c.[OldGradeTypeDescriptor_CodeValue]"
            + " AND d.[DocumentId] IN (SELECT [DocumentId] FROM [auth].[GradeTypeDescriptorWithX]))"
    )]
    public void It_seeks_a_descriptor_basis_in_dms_descriptor_under_its_discriminator(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "GradeTypeDescriptorWithX",
                0,
                _gradeTypeDescriptor,
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    _gradeTypeDescriptor,
                    Col("OldGradeTypeDescriptor_Namespace"),
                    Col("OldGradeTypeDescriptor_CodeValue"),
                    ProbeArm: null
                )
            )
        );

        var result = Emit(plan, dialect);

        result.Predicates.Should().Equal(expectedPredicate);
        result
            .Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(
                ("@CustomViewDescriptorDiscriminator0", "GradeTypeDescriptor"),
                ("@CustomViewDescriptorDiscriminatorQualified0", "Ed-Fi:GradeTypeDescriptor")
            );
    }

    [Test]
    public void It_unions_the_descriptor_seek_with_the_shared_descriptor_tombstone_arm_for_a_suffixed_view()
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "GradingPeriodDescriptorWithXIncludingDeletes",
                0,
                _gradingPeriodDescriptor,
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    _gradingPeriodDescriptor,
                    Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"),
                    Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"),
                    new ReadChangesCustomViewDescriptorProbeArm(
                        new DbTableName(_tracked, "Descriptor"),
                        _documentId,
                        Col("Discriminator"),
                        Col("OldNamespace"),
                        Col("OldCodeValue")
                    )
                )
            )
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        result
            .Predicates.Should()
            .Equal(
                "EXISTS (SELECT 1 FROM ("
                    + "SELECT d.\"DocumentId\" AS \"DocumentId\" FROM \"dms\".\"Descriptor\" d"
                    + " WHERE d.\"Discriminator\" IN (@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0)"
                    + " AND d.\"Namespace\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace\""
                    + " AND d.\"CodeValue\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue\""
                    + " UNION SELECT t.\"DocumentId\" FROM \"tracked_changes_edfi\".\"Descriptor\" t"
                    + " WHERE t.\"Discriminator\" IN (@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0)"
                    + " AND t.\"OldNamespace\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace\""
                    + " AND t.\"OldCodeValue\" = c.\"OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue\""
                    + ") basis WHERE basis.\"DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"GradingPeriodDescriptorWithXIncludingDeletes\"))"
            );
        result
            .Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(
                ("@CustomViewDescriptorDiscriminator0", "GradingPeriodDescriptor"),
                ("@CustomViewDescriptorDiscriminatorQualified0", "Ed-Fi:GradingPeriodDescriptor")
            );
    }

    [Test]
    public void It_numbers_descriptor_discriminator_parameters_across_the_custom_views_of_one_plan()
    {
        var plan = CustomViewOnlyPlan(
            CustomView(
                "GradeTypeDescriptorWithX",
                0,
                _gradeTypeDescriptor,
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    _gradeTypeDescriptor,
                    Col("OldGradeTypeDescriptor_Namespace"),
                    Col("OldGradeTypeDescriptor_CodeValue"),
                    ProbeArm: null
                )
            ),
            CustomView(
                "GradingPeriodWithX",
                1,
                _gradingPeriod,
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfi, "GradingPeriod"),
                    _documentId,
                    [KeyPair("GradingPeriodName", "OldGradingPeriodGradingPeriod_GradingPeriodName")],
                    [
                        new ReadChangesCustomViewDescriptorKeyPair(
                            Col("GradingPeriodDescriptor_DescriptorId"),
                            Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"),
                            Col("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"),
                            _gradingPeriodDescriptor
                        ),
                    ],
                    []
                )
            )
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        result.Predicates.Should().HaveCount(2);
        result
            .Predicates[0]
            .Should()
            .Contain("@CustomViewDescriptorDiscriminator0, @CustomViewDescriptorDiscriminatorQualified0");
        result
            .Predicates[1]
            .Should()
            .Contain("@CustomViewDescriptorDiscriminator1, @CustomViewDescriptorDiscriminatorQualified1");
        result
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(
                "@CustomViewDescriptorDiscriminator0",
                "@CustomViewDescriptorDiscriminatorQualified0",
                "@CustomViewDescriptorDiscriminator1",
                "@CustomViewDescriptorDiscriminatorQualified1"
            );
    }

    [Test]
    public void It_appends_custom_views_as_separate_and_terms_after_the_namespace_and_relationship_predicates()
    {
        var plan = new ReadChangesAuthorizationPlan(
            RelationshipChecks:
            [
                new ReadChangesRelationshipCheckSpec(
                    new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                    [
                        new ReadChangesAuthorizationSubject(
                            Col("OldSchoolId_Unified"),
                            AuthNames.EdOrgIdToEdOrgId,
                            AuthNames.TargetEdOrgId,
                            AuthNames.SourceEdOrgId
                        ),
                    ]
                ),
            ],
            NamespaceCheck: new ReadChangesNamespaceCheckSpec(Col("OldNamespace")),
            ClaimParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            ),
            NamespaceParameterization: NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "ReadChangesNamespacePrefix"
            ),
            CustomViewChecks:
            [
                CustomView(
                    "SchoolWithAlternativeType",
                    2,
                    _school,
                    new ReadChangesCustomViewBasis.LiveSeek(
                        new DbTableName(_edfi, "School"),
                        _documentId,
                        [KeyPair("SchoolId", "OldSchoolId_Unified")],
                        [],
                        []
                    )
                ),
                CustomView(
                    "StudentWithCTECourseEnrollments",
                    3,
                    _student,
                    new ReadChangesCustomViewBasis.StoredDocumentId(Col("OldStudent_DocumentId"))
                ),
            ]
        );

        var result = Emit(plan, SqlDialect.Pgsql);

        // Namespace and the relationship OR-group keep their existing positions; each custom view is its own
        // AND term after them, in CMS local order.
        result.Predicates.Should().HaveCount(4);
        result.Predicates[0].Should().StartWith("(c.\"OldNamespace\" IS NOT NULL");
        result.Predicates[1].Should().StartWith("((");
        result
            .Predicates[1]
            .Should()
            .Contain("\"auth\".\"EducationOrganizationIdToEducationOrganizationId\"");
        result.Predicates[2].Should().StartWith("EXISTS (SELECT 1 FROM \"edfi\".\"School\" b");
        result
            .Predicates[3]
            .Should()
            .Be(
                "c.\"OldStudent_DocumentId\" IN (SELECT \"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\")"
            );
        // Custom views bind no claim parameters: only the namespace and claim lists are bound.
        result
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(
                "@ReadChangesNamespacePrefix",
                $"@{RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds}"
            );
    }

    private static TrackedChangeAuthorizationSql Emit(
        ReadChangesAuthorizationPlan plan,
        SqlDialect dialect
    ) =>
        TrackedChangeAuthorizationSqlEmitter.Emit(
            plan,
            dialect,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );

    private static ReadChangesAuthorizationPlan CustomViewOnlyPlan(
        params ReadChangesCustomViewCheckSpec[] customViewChecks
    ) => new([], null, null, null, customViewChecks);

    private static ReadChangesCustomViewCheckSpec CustomView(
        string strategyName,
        int localOrder,
        QualifiedResourceName basisResource,
        ReadChangesCustomViewBasis basis
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, localOrder),
            localOrder,
            basisResource,
            new DbTableName(_auth, strategyName),
            ProbeBasisTombstones: strategyName.EndsWith("IncludingDeletes", StringComparison.Ordinal),
            basis
        );

    private static ReadChangesCustomViewKeyPair KeyPair(string basisColumn, string trackedOldColumn) =>
        new(Col(basisColumn), Col(trackedOldColumn));

    private static DbColumnName Col(string name) => new(name);
}
