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
            NamespaceParameterization: null
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
            NamespaceParameterization: null
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
            NamespaceParameterization: null
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
            NamespaceParameterization: null
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
            )
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
        var plan = new ReadChangesAuthorizationPlan([], null, null, null);
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
            NamespaceParameterization: null
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
            NamespaceParameterization: null
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
            NamespaceParameterization: null
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
