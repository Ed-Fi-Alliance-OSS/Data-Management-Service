// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationAuth1FailurePayloadCodec
{
    [Test]
    public void It_should_encode_and_parse_the_versioned_failure_set_payload()
    {
        var payload = new RelationshipAuthorizationAuth1FailurePayload(
            12,
            [
                new RelationshipAuthorizationAuth1SubjectFailure(
                    1,
                    0,
                    RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull
                ),
                new RelationshipAuthorizationAuth1SubjectFailure(
                    1,
                    1,
                    RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                ),
                new RelationshipAuthorizationAuth1SubjectFailure(
                    0,
                    0,
                    RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
                ),
            ]
        );

        var encoded = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(payload);
        var parsed = RelationshipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            encoded,
            out var parsedPayload
        );

        encoded.Should().Be("1|12|3|1:0:s,1:1:n,0:0:p");
        parsed.Should().BeTrue();
        parsedPayload.Should().BeEquivalentTo(payload);
    }

    [Test]
    public void It_should_extract_postgresql_and_sql_server_wrappers_then_use_the_same_payload_parser()
    {
        var payloadText = "1|7|2|0:0:s,1:0:n";
        var sqlServerMessage =
            $"Conversion failed when converting the varchar value 'AUTH1 - {payloadText}' to data type int.";

        var postgresqlParsed = RelationshipAuthorizationAuth1FailurePayloadCodec.TryParseProviderFailure(
            SqlDialect.Pgsql,
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            payloadText,
            out var postgresqlPayload
        );
        var sqlServerParsed = RelationshipAuthorizationAuth1FailurePayloadCodec.TryParseProviderFailure(
            SqlDialect.Mssql,
            null,
            sqlServerMessage,
            out var sqlServerPayload
        );

        postgresqlParsed.Should().BeTrue();
        sqlServerParsed.Should().BeTrue();
        sqlServerPayload.Should().BeEquivalentTo(postgresqlPayload);
        sqlServerPayload!.EmittedAuth1Index.Should().Be(7);
        sqlServerPayload.SubjectFailures.Should().HaveCount(2);
    }

    [TestCase("2|7|1|0:0:n")]
    [TestCase("1|7|2|0:0:n")]
    [TestCase("1|7|2|0:0:n,")]
    [TestCase("1|7|1|0:0:x")]
    [TestCase("1|7|1|0:0:")]
    [TestCase("1|-7|1|0:0:n")]
    [TestCase("1|7|0|")]
    [TestCase("1|7|2|0:0:n,0:0:s")]
    public void It_should_fail_closed_for_malformed_unknown_version_truncated_or_ambiguous_payloads(
        string payloadText
    )
    {
        var parsed = RelationshipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            payloadText,
            out var payload
        );

        parsed.Should().BeFalse();
        payload.Should().BeNull();
    }
}

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationFailureMapper
{
    [Test]
    public void It_should_map_payload_ordinals_to_external_failure_metadata_in_configured_order()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId"),
                CreateSubject(
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyReference.localEducationAgencyId"
                )
            ),
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Inverted,
                20,
                1,
                CreateSubject(
                    "EducationServiceCenterId",
                    "$.educationServiceCenterReference.educationServiceCenterId"
                )
            ),
        };
        var payload = new RelationshipAuthorizationAuth1FailurePayload(
            42,
            [
                new RelationshipAuthorizationAuth1SubjectFailure(
                    1,
                    0,
                    RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                ),
                new RelationshipAuthorizationAuth1SubjectFailure(
                    0,
                    1,
                    RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull
                ),
                new RelationshipAuthorizationAuth1SubjectFailure(
                    0,
                    0,
                    RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                ),
            ]
        );

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            checkSpecs,
            [300L, 100L, 300L],
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Stored);
        relationshipFailure.EmittedAuth1Index.Should().Be(42);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(100L, 300L);
        relationshipFailure.FailedStrategies.Should().HaveCount(2);

        var firstStrategy = relationshipFailure.FailedStrategies[0];
        firstStrategy.ConfiguredStrategyIndex.Should().Be(10);
        firstStrategy.RelationshipLocalOrder.Should().Be(0);
        firstStrategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        firstStrategy
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        firstStrategy.AuthObject.Should().NotBeNull();
        firstStrategy.AuthObject!.Name.Should().Be("auth.EducationOrganizationIdToEducationOrganizationId");
        firstStrategy.AuthObject.SubjectValueColumn.Should().Be("TargetEducationOrganizationId");
        firstStrategy
            .AuthObject.ClaimEducationOrganizationIdColumn.Should()
            .Be("SourceEducationOrganizationId");
        firstStrategy.FailedSubjects.Select(static subject => subject.SubjectIndex).Should().Equal(0, 1);
        firstStrategy
            .FailedSubjects.Select(static subject => subject.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                RelationshipAuthorizationSubjectFailureKind.StoredValueNull
            );
        firstStrategy.FailedSubjects[0].RootBinding.ResourceName.Should().Be("Ed-Fi.School");
        firstStrategy.FailedSubjects[0].RootBinding.TableName.Should().Be("edfi.School");
        firstStrategy.FailedSubjects[0].RootBinding.ColumnName.Should().Be("SchoolId");
        firstStrategy.FailedSubjects[0].SecurableElements.Should().ContainSingle();
        firstStrategy.FailedSubjects[0].SecurableElements[0].Kind.Should().Be("EducationOrganization");
        firstStrategy
            .FailedSubjects[0]
            .SecurableElements[0]
            .JsonPath.Should()
            .Be("$.schoolReference.schoolId");
        firstStrategy.FailedSubjects[0].SecurableElements[0].ReadableName.Should().Be("SchoolId");

        var secondStrategy = relationshipFailure.FailedStrategies[1];
        secondStrategy.ConfiguredStrategyIndex.Should().Be(20);
        secondStrategy.RelationshipLocalOrder.Should().Be(1);
        secondStrategy
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
        secondStrategy.AuthObject!.SubjectValueColumn.Should().Be("SourceEducationOrganizationId");
        secondStrategy
            .AuthObject.ClaimEducationOrganizationIdColumn.Should()
            .Be("TargetEducationOrganizationId");
        secondStrategy.FailedSubjects.Should().ContainSingle();
    }

    [Test]
    public void It_should_fail_closed_when_payload_ordinals_do_not_match_the_check_specs()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        1,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
    }

    [Test]
    public void It_should_fail_closed_when_the_runtime_failure_kind_does_not_match_the_value_source()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
    }

    private static RelationshipAuthorizationCheckSpec CreateStoredCheckSpec(
        RelationshipAuthorizationHierarchyDirection direction,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(
                direction is RelationshipAuthorizationHierarchyDirection.Normal
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                configuredStrategyIndex
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Stored,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
            subjects,
            new RelationshipAuthorizationCheckTarget.Stored(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId")
            )
        );

    private static RelationshipAuthorizationSubject CreateSubject(string columnName, string jsonPath) =>
        new(
            new QualifiedResourceName("Ed-Fi", "School"),
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new DbColumnName(columnName),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    jsonPath,
                    columnName
                ),
            ]
        );
}

[TestFixture]
[Parallelizable]
public class Given_SingleRecordRelationshipAuthorizationSqlCompiler
{
    [Test]
    public void It_should_compile_postgresql_auth1_sql_with_one_claim_edorg_array_parameter()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [200L, 100L, 100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId"),
                        CreateSubject(
                            "LocalEducationAgencyId",
                            "$.localEducationAgencyReference.localEducationAgencyId"
                        )
                    ),
                ],
                parameterization,
                5
            )
        );

        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("DocumentId", "ClaimEducationOrganizationIds");
        plan.ParametersInOrder[1].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.PgsqlArray);
        plan.AuthorizationSql.Should().Contain("\"dms\".\"throw_error\"('AUTH1'");
        plan.AuthorizationSql.Should().Contain("CONCAT('1|', '5|', COUNT(1)::text, '|'");
        plan.AuthorizationSql.Should().Contain("STRING_AGG");
        plan.AuthorizationSql.Should().Contain("= ANY(@ClaimEducationOrganizationIds)");
        plan.AuthorizationSql.Should().Contain("CASE WHEN target.\"SchoolId\" IS NULL THEN 's' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain("NOT EXISTS (SELECT 1 FROM failed_subjects WHERE \"StrategyOrdinal\" = 0)");
    }

    [Test]
    public void It_should_compile_sql_server_auth1_sql_with_scalar_claim_edorg_parameters_below_threshold()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            [200L, 100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Inverted,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId")
                    ),
                ],
                parameterization,
                6
            )
        );

        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("DocumentId", "ClaimEducationOrganizationIds_0", "ClaimEducationOrganizationIds_1");
        plan.ParametersInOrder.Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);
        plan.AuthorizationSql.Should()
            .Contain("CAST(CONCAT('AUTH1 - ', (SELECT [Payload] FROM failure_payload)) AS INT)");
        plan.AuthorizationSql.Should().Contain("CONCAT('1|', '6|', COUNT(1), '|'");
        plan.AuthorizationSql.Should().Contain("STRING_AGG");
        plan.AuthorizationSql.Should()
            .Contain("IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1)");
        plan.AuthorizationSql.Should().Contain("[SourceEducationOrganizationId] = target.[SchoolId]");
        plan.AuthorizationSql.Should().Contain("[TargetEducationOrganizationId] IN");
    }

    [Test]
    public void It_should_compile_sql_server_auth1_sql_with_structured_claim_edorg_parameter_at_threshold()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(2000),
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId")
                    ),
                ],
                parameterization,
                7
            )
        );

        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("DocumentId", "ClaimEducationOrganizationIds");
        plan.ParametersInOrder[1]
            .Binding.Should()
            .BeEquivalentTo(QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id"));
        plan.AuthorizationSql.Should().Contain("IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)");
    }

    private static IReadOnlyList<long> CreateClaimEducationOrganizationIds(int count)
    {
        long[] claimEducationOrganizationIds = new long[count];

        for (var index = 0; index < count; index++)
        {
            claimEducationOrganizationIds[index] = index + 1L;
        }

        return claimEducationOrganizationIds;
    }

    private static RelationshipAuthorizationCheckSpec CreateStoredCheckSpec(
        RelationshipAuthorizationHierarchyDirection direction,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(
                direction is RelationshipAuthorizationHierarchyDirection.Normal
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                configuredStrategyIndex
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Stored,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
            subjects,
            new RelationshipAuthorizationCheckTarget.Stored(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId")
            )
        );

    private static RelationshipAuthorizationSubject CreateSubject(string columnName, string jsonPath) =>
        new(
            new QualifiedResourceName("Ed-Fi", "School"),
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new DbColumnName(columnName),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    jsonPath,
                    columnName
                ),
            ]
        );
}
