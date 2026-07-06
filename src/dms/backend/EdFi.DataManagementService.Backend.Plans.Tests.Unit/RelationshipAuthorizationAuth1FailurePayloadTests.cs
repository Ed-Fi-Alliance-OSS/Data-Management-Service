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
    public void It_should_reject_negative_strategy_ordinals()
    {
        var act = () =>
            new RelationshipAuthorizationAuth1SubjectFailure(
                -1,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("strategyOrdinal")
            .WithMessage("AUTH1 strategy ordinal cannot be negative.*");
    }

    [Test]
    public void It_should_reject_negative_subject_ordinals()
    {
        var act = () =>
            new RelationshipAuthorizationAuth1SubjectFailure(
                0,
                -1,
                RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("subjectOrdinal")
            .WithMessage("AUTH1 subject ordinal cannot be negative.*");
    }

    [Test]
    public void It_should_allow_zero_ordinals_and_zero_emitted_auth1_index()
    {
        var subjectFailure = new RelationshipAuthorizationAuth1SubjectFailure(
            0,
            0,
            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
        );
        var payload = new RelationshipAuthorizationAuth1FailurePayload(0, [subjectFailure]);

        payload.EmittedAuth1Index.Should().Be(0);
        payload.SubjectFailures.Should().ContainSingle().Which.Should().Be(subjectFailure);
    }

    [Test]
    public void It_should_reject_negative_emitted_auth1_indexes()
    {
        var act = () =>
            new RelationshipAuthorizationAuth1FailurePayload(
                -1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("emittedAuth1Index")
            .WithMessage("Emitted AUTH1 index cannot be negative.*");
    }

    [Test]
    public void It_should_reject_empty_subject_failure_lists()
    {
        var act = () => new RelationshipAuthorizationAuth1FailurePayload(0, []);

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("subjectFailures")
            .WithMessage("AUTH1 relationship authorization payload requires at least one subject failure.*");
    }

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

    [Test]
    public void It_should_extract_sql_server_payloads_when_the_marker_starts_the_message_and_the_payload_ends_the_message()
    {
        const string payloadText = "1|9|1|0:0:n";
        var parsed = RelationshipAuthorizationAuth1FailurePayloadCodec.TryParseProviderFailure(
            SqlDialect.Mssql,
            null,
            $"AUTH1 - {payloadText}",
            out var payload
        );

        parsed.Should().BeTrue();
        payload!.EmittedAuth1Index.Should().Be(9);
        payload.SubjectFailures.Should().ContainSingle();
    }

    [Test]
    public void It_should_throw_for_unknown_failure_kinds_when_encoding()
    {
        var payload = new RelationshipAuthorizationAuth1FailurePayload(
            0,
            [
                new RelationshipAuthorizationAuth1SubjectFailure(
                    0,
                    0,
                    (RelationshipAuthorizationAuth1SubjectFailureKind)999
                ),
            ]
        );

        var act = () => RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(payload);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("failureKind")
            .WithMessage("Unsupported AUTH1 relationship failure kind.*");
    }

    [TestCase(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode, "1|7|1|0:0:n", true)]
    [TestCase(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode, null, true)]
    [TestCase(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode, "", true)]
    [TestCase(null, "1|7|1|0:0:n", false)]
    [TestCase("", "1|7|1|0:0:n", false)]
    [TestCase("23505", "AUTH1 - 1|7|1|0:0:n", false)]
    public void It_should_identify_postgresql_provider_failures_by_error_code(
        string? providerErrorCode,
        string? providerMessage,
        bool expected
    )
    {
        var result = RelationshipAuthorizationAuth1FailurePayloadCodec.IsProviderFailure(
            SqlDialect.Pgsql,
            providerErrorCode,
            providerMessage
        );

        result.Should().Be(expected);
    }

    [TestCase(null, "Conversion failed when converting the varchar value 'AUTH1 - 1|7|1|0:0:n'.", true)]
    [TestCase("", "Conversion failed when converting the varchar value 'AUTH1 - 1|7|1|0:0:n'.", true)]
    [TestCase(
        RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
        "Conversion failed when converting the varchar value 'AUTH1 - 1|7|1|0:0:n'.",
        true
    )]
    [TestCase(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode, null, false)]
    [TestCase(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode, "", false)]
    [TestCase(null, "Conversion failed without a relationship authorization marker.", false)]
    public void It_should_identify_sql_server_provider_failures_by_message_marker(
        string? providerErrorCode,
        string? providerMessage,
        bool expected
    )
    {
        var result = RelationshipAuthorizationAuth1FailurePayloadCodec.IsProviderFailure(
            SqlDialect.Mssql,
            providerErrorCode,
            providerMessage
        );

        result.Should().Be(expected);
    }

    [Test]
    public void It_should_not_identify_provider_failures_for_unsupported_sql_dialects()
    {
        var result = RelationshipAuthorizationAuth1FailurePayloadCodec.IsProviderFailure(
            (SqlDialect)999,
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            "AUTH1 - 1|7|1|0:0:n"
        );

        result.Should().BeFalse();
    }

    [Test]
    public void It_should_leave_extracted_payload_empty_when_the_provider_message_is_blank()
    {
        var extracted = RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            SqlDialect.Mssql,
            null,
            " ",
            out var payloadText
        );

        extracted.Should().BeFalse();
        payloadText.Should().BeEmpty();
    }

    [Test]
    public void It_should_leave_extracted_payload_empty_when_postgresql_error_code_does_not_match()
    {
        var extracted = RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            SqlDialect.Pgsql,
            "P0001",
            "1|7|1|0:0:n",
            out var payloadText
        );

        extracted.Should().BeFalse();
        payloadText.Should().BeEmpty();
    }

    [Test]
    public void It_should_leave_extracted_payload_empty_when_sql_server_message_lacks_the_marker()
    {
        var extracted = RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            SqlDialect.Mssql,
            null,
            "Conversion failed without an AUTH1 payload.",
            out var payloadText
        );

        extracted.Should().BeFalse();
        payloadText.Should().BeEmpty();
    }

    [Test]
    public void It_should_leave_extracted_payload_empty_for_unsupported_sql_dialects()
    {
        var extracted = RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            (SqlDialect)999,
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            "AUTH1 - 1|7|1|0:0:n",
            out var payloadText
        );

        extracted.Should().BeFalse();
        payloadText.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_no_payload_when_provider_payload_extraction_fails()
    {
        var parsed = RelationshipAuthorizationAuth1FailurePayloadCodec.TryParseProviderFailure(
            (SqlDialect)999,
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            "AUTH1 - 1|7|1|0:0:n",
            out var payload
        );

        parsed.Should().BeFalse();
        payload.Should().BeNull();
    }

    [Test]
    public void It_should_fail_sql_server_payload_extraction_when_the_marker_has_no_payload()
    {
        var extracted = RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            SqlDialect.Mssql,
            null,
            "AUTH1 - ",
            out var payloadText
        );

        extracted.Should().BeFalse();
        payloadText.Should().BeEmpty();
    }

    [Test]
    public void It_should_extract_sql_server_payloads_with_lowercase_boundary_characters()
    {
        var extracted = RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            SqlDialect.Mssql,
            null,
            "AUTH1 - az.",
            out var payloadText
        );

        extracted.Should().BeTrue();
        payloadText.Should().Be("az");
    }

    [TestCase("")]
    [TestCase("   ")]
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

        var mapped = TryMapAuth1Failure(payload, checkSpecs, [300L, 100L, 300L], out var relationshipFailure);

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
        firstStrategy
            .FailedSubjects.Select(static subject => subject.Hint)
            .Should()
            .Equal(
                "No matching relationship authorization row was found for the subject value and claim EducationOrganizationIds.",
                "Stored relationship authorization subject value is null."
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
    public void It_should_preserve_unknown_strategy_name_as_strategy_kind()
    {
        const string strategyName = "CustomRelationshipAuthorizationStrategy";
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                strategyName,
                RelationshipAuthorizationHierarchyDirection.Inverted,
                10,
                0,
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Inverted
                ),
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                42,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedStrategy = relationshipFailure!.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy.StrategyName.Should().Be(strategyName);
        failedStrategy.StrategyKind.Should().Be(strategyName);
    }

    [Test]
    public void It_should_fail_closed_when_the_emitted_check_index_does_not_match_the_expected_check()
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
        var payload = new RelationshipAuthorizationAuth1FailurePayload(
            42,
            [
                new RelationshipAuthorizationAuth1SubjectFailure(
                    0,
                    0,
                    RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                ),
            ]
        );

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            expectedEmittedAuth1Index: 41,
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
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

        var mapped = TryMapAuth1Failure(
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
    public void It_should_fail_closed_when_payload_contains_a_failure_for_an_unknown_strategy_ordinal()
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

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        1,
                        0,
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
    public void It_should_fail_closed_when_payload_repeats_a_strategy_subject_ordinal()
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

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull
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
    public void It_should_fail_closed_when_payload_omits_a_failed_or_strategy()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Inverted,
                11,
                1,
                CreateSubject(
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyReference.localEducationAgencyId"
                )
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
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

        var mapped = TryMapAuth1Failure(
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

    [Test]
    public void It_should_fail_closed_when_auth1_check_specs_mix_value_sources()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Inverted,
                11,
                1,
                CreateSubject(
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyReference.localEducationAgencyId"
                )
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        1,
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

    [Test]
    public void It_should_throw_when_auth1_person_subject_uses_an_unsupported_value_source()
    {
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                46,
                0,
                authObject,
                CreatePersonSubject(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.Student,
                    AuthNames.StudentDocumentId,
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                )
            ) with
            {
                ValueSource = (RelationshipAuthorizationValueSource)999,
            },
        };
        var act = () =>
            TryMapAuth1Failure(
                new RelationshipAuthorizationAuth1FailurePayload(
                    1,
                    [
                        new RelationshipAuthorizationAuth1SubjectFailure(
                            0,
                            0,
                            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                        ),
                    ]
                ),
                checkSpecs,
                [100L],
                out _
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("valueSource")
            .WithMessage("Unsupported relationship authorization value source.*");
    }

    [Test]
    public void It_should_throw_when_auth1_non_person_subject_uses_an_unsupported_value_source()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                47,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ) with
            {
                ValueSource = (RelationshipAuthorizationValueSource)999,
            },
        };
        var act = () =>
            TryMapAuth1Failure(
                new RelationshipAuthorizationAuth1FailurePayload(
                    1,
                    [
                        new RelationshipAuthorizationAuth1SubjectFailure(
                            0,
                            0,
                            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                        ),
                    ]
                ),
                checkSpecs,
                [100L],
                out _
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("valueSource")
            .WithMessage("Unsupported relationship authorization value source.*");
    }

    [Test]
    public void It_should_throw_when_auth1_edorg_strategy_uses_an_unsupported_direction()
    {
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                48,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ) with
            {
                Direction = (RelationshipAuthorizationHierarchyDirection)999,
            },
        };
        var act = () =>
            TryMapAuth1Failure(
                new RelationshipAuthorizationAuth1FailurePayload(
                    1,
                    [
                        new RelationshipAuthorizationAuth1SubjectFailure(
                            0,
                            0,
                            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                        ),
                    ]
                ),
                checkSpecs,
                [100L],
                out _
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("direction")
            .WithMessage("Unsupported relationship authorization direction.*");
    }

    [Test]
    public void It_should_map_mixed_proposed_missing_and_no_relationship_failures_for_one_strategy()
    {
        var checkSpecs = new[]
        {
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId"),
                CreateSubject(
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyReference.localEducationAgencyId"
                )
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
                    ),
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

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Proposed);
        relationshipFailure.FailedStrategies.Should().ContainSingle();
        relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects.Select(static subject => subject.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                RelationshipAuthorizationSubjectFailureKind.NoRelationship
            );
        relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects.Select(static subject => subject.RootBinding.ColumnName)
            .Should()
            .Equal("SchoolId", "LocalEducationAgencyId");
        relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects.Select(static subject => subject.Hint)
            .Should()
            .Equal(
                "Proposed relationship authorization subject value is missing.",
                "No matching relationship authorization row was found for the subject value and claim EducationOrganizationIds."
            );
    }

    [Test]
    public void It_should_map_mixed_proposed_missing_and_no_relationship_failures_across_or_strategies()
    {
        var checkSpecs = new[]
        {
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                10,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Inverted,
                11,
                1,
                CreateSubject(
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyReference.localEducationAgencyId"
                )
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                1,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        1,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Proposed);
        relationshipFailure.FailedStrategies.Should().HaveCount(2);
        relationshipFailure
            .FailedStrategies.Select(static strategy => strategy.ConfiguredStrategyIndex)
            .Should()
            .Equal(10, 11);
        relationshipFailure
            .FailedStrategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        relationshipFailure
            .FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                RelationshipAuthorizationSubjectFailureKind.NoRelationship
            );
        relationshipFailure
            .FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.RootBinding.ColumnName)
            .Should()
            .Equal("SchoolId", "LocalEducationAgencyId");
        relationshipFailure.FailedStrategies[0].AuthObject.Should().NotBeNull();
        relationshipFailure
            .FailedStrategies[0]
            .AuthObject!.SubjectValueColumn.Should()
            .Be("TargetEducationOrganizationId");
        relationshipFailure
            .FailedStrategies[0]
            .AuthObject!.ClaimEducationOrganizationIdColumn.Should()
            .Be("SourceEducationOrganizationId");
        relationshipFailure.FailedStrategies[1].AuthObject.Should().NotBeNull();
        relationshipFailure
            .FailedStrategies[1]
            .AuthObject!.SubjectValueColumn.Should()
            .Be("SourceEducationOrganizationId");
        relationshipFailure
            .FailedStrategies[1]
            .AuthObject!.ClaimEducationOrganizationIdColumn.Should()
            .Be("TargetEducationOrganizationId");
    }

    [Test]
    public void It_should_map_people_auth1_ordinals_to_person_failure_metadata()
    {
        var subject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                30,
                0,
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                subject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                3,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.FailedStrategies.Should().ContainSingle();

        var failedStrategy = relationshipFailure.FailedStrategies[0];
        failedStrategy
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        failedStrategy.AuthObject!.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedStrategy
            .AuthObject.FailureHint.Should()
            .Be(
                RelationshipAuthorizationAuthObject
                    .CreatePerson(RelationshipAuthorizationPersonAuthViewKind.Student)
                    .FailureHint
            );
        failedStrategy.FailedSubjects.Should().ContainSingle();

        var failedSubject = failedStrategy.FailedSubjects[0];
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.StoredValueNull);
        failedSubject.RootBinding.TableName.Should().Be("edfi.School");
        failedSubject.RootBinding.ColumnName.Should().Be("DocumentId");
        failedSubject.SecurableElements.Should().ContainSingle();
        failedSubject.SecurableElements[0].Kind.Should().Be("Student");
        failedSubject.SecurableElements[0].JsonPath.Should().Be("$.studentReference.studentUniqueId");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        failedSubject
            .AuthObject.FailureHint.Should()
            .Be(
                RelationshipAuthorizationAuthObject
                    .CreatePerson(RelationshipAuthorizationPersonAuthViewKind.Student)
                    .FailureHint
            );
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.PathKind.Should().Be("DirectRootColumn");
        failedSubject.PersonSubject.DocumentIdPath.Should().ContainSingle();
        failedSubject.PersonSubject.DocumentIdPath[0].SourceTableName.Should().Be("edfi.School");
        failedSubject.PersonSubject.DocumentIdPath[0].SourceColumnName.Should().Be("Student_DocumentId");
        failedSubject.PersonSubject.DocumentIdPath[0].TargetTableName.Should().Be("edfi.Student");
        failedSubject.PersonSubject.DocumentIdPath[0].TargetColumnName.Should().Be("DocumentId");
        failedSubject.PersonSubject.StoredAnchor.RootTableName.Should().Be("edfi.School");
        failedSubject.PersonSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
        failedSubject.PersonSubject.ProposedAnchor.Should().BeNull();
        failedSubject
            .PersonSubject.Hint.Should()
            .Be(
                RelationshipAuthorizationAuthObject
                    .CreatePerson(RelationshipAuthorizationPersonAuthViewKind.Student)
                    .FailureHint
            );
    }

    [Test]
    public void It_should_map_stored_transitive_people_root_binding_from_stored_anchor()
    {
        var courseTranscriptTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var rootDocumentIdColumn = new DbColumnName("DocumentId");
        var firstHopColumn = new DbColumnName("StudentAcademicRecord_DocumentId");
        var subject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentAcademicRecordReference.studentUniqueId",
            "StudentUniqueId",
            pathKind: RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
            pathSteps:
            [
                new ColumnPathStep(
                    courseTranscriptTable,
                    firstHopColumn,
                    studentAcademicRecordTable,
                    rootDocumentIdColumn
                ),
                new ColumnPathStep(
                    studentAcademicRecordTable,
                    AuthNames.StudentDocumentId,
                    studentTable,
                    rootDocumentIdColumn
                ),
            ],
            rootTable: courseTranscriptTable,
            resourceName: "CourseTranscript"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                31,
                0,
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                subject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                4,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.RootBinding.TableName.Should().Be("edfi.CourseTranscript");
        failedSubject.RootBinding.ColumnName.Should().Be("DocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PathKind.Should().Be("TransitiveJoinPath");
        failedSubject
            .PersonSubject.DocumentIdPath.Select(static step => step.SourceTableName)
            .Should()
            .Equal("edfi.CourseTranscript", "edfi.StudentAcademicRecord");
        failedSubject
            .PersonSubject.DocumentIdPath.Select(static step => step.SourceColumnName)
            .Should()
            .Equal("StudentAcademicRecord_DocumentId", "Student_DocumentId");
        failedSubject.PersonSubject.StoredAnchor.RootTableName.Should().Be("edfi.CourseTranscript");
        failedSubject.PersonSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
    }

    [Test]
    public void It_should_keep_people_auth_view_metadata_on_mixed_strategy_failed_subjects()
    {
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                RelationshipAuthorizationHierarchyDirection.Normal,
                31,
                0,
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
                CreateSubject("SchoolId", "$.schoolReference.schoolId"),
                studentSubject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                4,
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

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.FailedStrategies.Should().ContainSingle();
        relationshipFailure
            .FailedStrategies[0]
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople);
        relationshipFailure
            .FailedStrategies[0]
            .AuthObject!.Name.Should()
            .Be("auth.EducationOrganizationIdToStudentDocumentId");

        var failedSubject = relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.SubjectIndex.Should().Be(1);
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
    }

    [Test]
    public void It_should_use_null_strategy_auth_object_when_mixed_auth_object_runtime_subjects_fail()
    {
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                RelationshipAuthorizationHierarchyDirection.Normal,
                33,
                0,
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
                CreateSubject("SchoolId", "$.schoolReference.schoolId"),
                studentSubject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                5,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
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

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedStrategy = relationshipFailure!.FailedStrategies.Should().ContainSingle().Subject;

        failedStrategy.AuthObject.Should().BeNull();
        failedStrategy.FailedSubjects.Select(static subject => subject.SubjectIndex).Should().Equal(0, 1);
        failedStrategy
            .FailedSubjects.Select(static subject => subject.AuthObject.Name.ToString())
            .Should()
            .Equal(
                "auth.EducationOrganizationIdToEducationOrganizationId",
                "auth.EducationOrganizationIdToStudentDocumentId"
            );
    }

    [Test]
    public void It_should_use_null_strategy_auth_object_for_people_only_runtime_failures_with_distinct_auth_objects()
    {
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var contactSubject = CreatePersonSubject(
            SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Contact,
            RelationshipAuthorizationPersonAuthViewKind.Contact,
            AuthNames.ContactDocumentId,
            "$.contactReference.contactUniqueId",
            "ContactUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                34,
                0,
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                studentSubject,
                contactSubject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                6,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
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

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedStrategy = relationshipFailure!.FailedStrategies.Should().ContainSingle().Subject;

        failedStrategy.AuthObject.Should().BeNull();
        failedStrategy
            .FailedSubjects.Select(static subject => subject.AuthObject.Name.ToString())
            .Should()
            .Equal(
                "auth.EducationOrganizationIdToStudentDocumentId",
                "auth.EducationOrganizationIdToContactDocumentId"
            );
        failedStrategy
            .FailedSubjects.Select(static subject => subject.PersonSubject?.PersonKind)
            .Should()
            .Equal("Student", "Contact");
    }

    [Test]
    public void It_should_keep_homogeneous_people_runtime_strategy_auth_object_when_multiple_subjects_fail()
    {
        var primaryStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var alternateStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            new DbColumnName("AlternateStudent_DocumentId"),
            "$.alternateStudentReference.studentUniqueId",
            "AlternateStudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                35,
                0,
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                primaryStudentSubject,
                alternateStudentSubject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                7,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
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

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedStrategy = relationshipFailure!.FailedStrategies.Should().ContainSingle().Subject;

        failedStrategy.AuthObject.Should().NotBeNull();
        failedStrategy.AuthObject!.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedStrategy
            .FailedSubjects.Select(static subject => subject.AuthObject.Name.ToString())
            .Should()
            .OnlyContain(static name => name == "auth.EducationOrganizationIdToStudentDocumentId");
    }

    [Test]
    public void It_should_map_people_no_claims_failures_with_auth_view_hints()
    {
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
        );
        var subject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
                RelationshipAuthorizationHierarchyDirection.Normal,
                32,
                0,
                authObject,
                subject
            ),
        };
        var noClaimsFailures = new[]
        {
            new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                new QualifiedResourceName("Ed-Fi", "School"),
                checkSpecs[0].ConfiguredStrategy,
                checkSpecs[0].RelationshipLocalOrder,
                ValueSource: RelationshipAuthorizationValueSource.Stored,
                AuthObject: authObject,
                Hint: authObject.FailureHint
            )
            {
                PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                    RelationshipAuthorizationPersonKind.Student,
                    authObject
                ),
                Contributors = subject.Contributors,
            },
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            noClaimsFailures,
            [],
            5,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.ClaimEducationOrganizationIds.Should().BeEmpty();
        relationshipFailure.FailedStrategies.Should().ContainSingle();
        relationshipFailure
            .FailedStrategies[0]
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility);

        var failedSubject = relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.Hint.Should().Be(authObject.FailureHint);
        failedSubject
            .AuthObject.Name.Should()
            .Be("auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility");
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.Hint.Should().Be(authObject.FailureHint);
    }

    [Test]
    public void It_should_fail_closed_when_no_claims_metadata_is_empty_or_incomplete()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var checkSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            32,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );
        var validFailure = CreateNoClaimsFailure(
            resource,
            checkSpec,
            checkSpec.Subjects[0].AuthObject,
            "valid no claims"
        );

        AssertNoClaimsFailureDoesNotMap([], [validFailure]);

        RelationshipAuthorizationFailureMetadata[][] incompleteFailureCases =
        [
            [],
            [
                validFailure,
                validFailure with
                {
                    FailureKind = RelationshipAuthorizationFailureKind.NoExecutableSubjects,
                },
            ],
            [validFailure, validFailure with { ConfiguredStrategy = null }],
            [validFailure, validFailure with { RelationshipLocalOrder = null }],
            [validFailure, validFailure with { ValueSource = null }],
        ];

        foreach (var failures in incompleteFailureCases)
        {
            AssertNoClaimsFailureDoesNotMap([checkSpec], failures);
        }
    }

    [Test]
    public void It_should_fail_closed_when_no_claims_metadata_references_an_unknown_strategy_identity()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var checkSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            34,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );
        var unmatchedCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            35,
            0,
            CreateSubject("LocalEducationAgencyId", "$.localEducationAgencyReference.localEducationAgencyId")
        );

        AssertNoClaimsFailureDoesNotMap(
            [checkSpec],
            [
                CreateNoClaimsFailure(
                    resource,
                    unmatchedCheckSpec,
                    unmatchedCheckSpec.Subjects[0].AuthObject,
                    "unknown strategy hint"
                ),
            ]
        );
    }

    [Test]
    public void It_should_fail_closed_when_no_claims_value_sources_are_not_homogeneous()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var storedCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            40,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );
        var proposedCheckSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            41,
            1,
            CreateSubject("LocalEducationAgencyId", "$.localEducationAgencyReference.localEducationAgencyId")
        );
        var storedFailure = CreateNoClaimsFailure(
            resource,
            storedCheckSpec,
            storedCheckSpec.Subjects[0].AuthObject,
            "stored no claims"
        );

        AssertNoClaimsFailureDoesNotMap([storedCheckSpec, proposedCheckSpec], [storedFailure]);
        AssertNoClaimsFailureDoesNotMap(
            [storedCheckSpec],
            [
                storedFailure,
                storedFailure with
                {
                    ValueSource = RelationshipAuthorizationValueSource.Proposed,
                },
            ]
        );
    }

    [Test]
    public void It_should_order_no_claims_failures_by_strategy_identity_and_sort_claim_ids()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var finalCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            20,
            0,
            CreateSubject(
                "EducationServiceCenterId",
                "$.educationServiceCenterReference.educationServiceCenterId"
            )
        );
        var laterLocalOrderCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            10,
            1,
            CreateSubject("LocalEducationAgencyId", "$.localEducationAgencyReference.localEducationAgencyId")
        );
        var earlierCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            10,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            [finalCheckSpec, laterLocalOrderCheckSpec, earlierCheckSpec],
            [
                CreateNoClaimsFailure(
                    resource,
                    finalCheckSpec,
                    finalCheckSpec.Subjects[0].AuthObject,
                    "final strategy hint"
                ),
                CreateNoClaimsFailure(
                    resource,
                    laterLocalOrderCheckSpec,
                    laterLocalOrderCheckSpec.Subjects[0].AuthObject,
                    "later local order hint"
                ),
                CreateNoClaimsFailure(
                    resource,
                    earlierCheckSpec,
                    earlierCheckSpec.Subjects[0].AuthObject,
                    null
                ),
                CreateNoClaimsFailure(
                    resource,
                    earlierCheckSpec,
                    earlierCheckSpec.Subjects[0].AuthObject,
                    "earlier strategy hint"
                ),
            ],
            [300L, 100L, 300L],
            12,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(100L, 300L);
        relationshipFailure
            .FailedStrategies.Select(static strategy => strategy.ConfiguredStrategyIndex)
            .Should()
            .Equal(10, 10, 20);
        relationshipFailure
            .FailedStrategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1, 0);
        relationshipFailure.FailedStrategies[0].Hint.Should().Be("earlier strategy hint");
    }

    [Test]
    public void It_should_allow_no_claims_strategy_hint_to_be_null_when_no_failure_hint_exists()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var checkSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            33,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            [checkSpec],
            [CreateNoClaimsFailure(resource, checkSpec, checkSpec.Subjects[0].AuthObject, null)],
            [],
            13,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.FailedStrategies.Should().ContainSingle();
        relationshipFailure.FailedStrategies[0].Hint.Should().BeNull();
        relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects.Should()
            .ContainSingle()
            .Subject.Hint.Should()
            .Be("Relationship authorization requires at least one claim EducationOrganizationId.");
    }

    [Test]
    public void It_should_map_mixed_auth_object_no_claims_failures_with_per_subject_auth_objects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var edOrgAuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );
        var studentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var edOrgSubject = CreateSubject("SchoolId", "$.schoolReference.schoolId");
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                RelationshipAuthorizationHierarchyDirection.Normal,
                36,
                0,
                edOrgAuthObject,
                edOrgSubject,
                studentSubject
            ),
        };
        var noClaimsFailures = new[]
        {
            new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                resource,
                checkSpecs[0].ConfiguredStrategy,
                checkSpecs[0].RelationshipLocalOrder,
                ValueSource: RelationshipAuthorizationValueSource.Stored,
                AuthObject: edOrgAuthObject,
                Hint: "edorg no claims"
            )
            {
                Contributors = edOrgSubject.Contributors,
            },
            new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                resource,
                checkSpecs[0].ConfiguredStrategy,
                checkSpecs[0].RelationshipLocalOrder,
                ValueSource: RelationshipAuthorizationValueSource.Stored,
                AuthObject: studentAuthObject,
                Hint: studentAuthObject.FailureHint
            )
            {
                PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                    RelationshipAuthorizationPersonKind.Student,
                    studentAuthObject,
                    studentSubject.PersonMetadata!.Path
                ),
                Contributors = studentSubject.Contributors,
            },
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            noClaimsFailures,
            [],
            8,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedStrategy = relationshipFailure!.FailedStrategies.Should().ContainSingle().Subject;

        failedStrategy.AuthObject.Should().BeNull();
        failedStrategy.FailedSubjects.Select(static subject => subject.SubjectIndex).Should().Equal(0, 1);
        failedStrategy
            .FailedSubjects.Select(static subject => subject.FailureKind)
            .Should()
            .OnlyContain(static failureKind =>
                failureKind == RelationshipAuthorizationSubjectFailureKind.NoRelationship
            );
        failedStrategy
            .FailedSubjects.Select(static subject => subject.AuthObject.Name.ToString())
            .Should()
            .Equal(
                "auth.EducationOrganizationIdToEducationOrganizationId",
                "auth.EducationOrganizationIdToStudentDocumentId"
            );
        failedStrategy.FailedSubjects[0].Hint.Should().Be("edorg no claims");
        failedStrategy.FailedSubjects[1].Hint.Should().Be(studentAuthObject.FailureHint);
        failedStrategy.FailedSubjects[1].PersonSubject.Should().NotBeNull();
        failedStrategy.FailedSubjects[1].PersonSubject!.Hint.Should().Be(studentAuthObject.FailureHint);
    }

    [Test]
    public void It_should_match_people_no_claims_metadata_by_subject_path_when_available()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var primaryStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var alternateStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            new DbColumnName("AlternateStudent_DocumentId"),
            "$.alternateStudentReference.studentUniqueId",
            "AlternateStudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                37,
                0,
                authObject,
                primaryStudentSubject,
                alternateStudentSubject
            ),
        };
        var noClaimsFailures = new[]
        {
            CreatePeopleNoClaimsFailure(
                resource,
                checkSpecs[0],
                authObject,
                primaryStudentSubject,
                "primary path hint"
            ),
            CreatePeopleNoClaimsFailure(
                resource,
                checkSpecs[0],
                authObject,
                alternateStudentSubject,
                "alternate path hint"
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            noClaimsFailures,
            [],
            9,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedStrategy = relationshipFailure!.FailedStrategies.Should().ContainSingle().Subject;

        failedStrategy.AuthObject.Should().NotBeNull();
        failedStrategy.AuthObject!.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedStrategy
            .FailedSubjects.Select(static subject => subject.Hint)
            .Should()
            .Equal("primary path hint", "alternate path hint");
        failedStrategy
            .FailedSubjects.Select(static subject => subject.AuthObject.Name.ToString())
            .Should()
            .OnlyContain(static name => name == "auth.EducationOrganizationIdToStudentDocumentId");
    }

    [Test]
    public void It_should_prefer_generic_people_no_claims_metadata_before_path_mismatch_or_auth_object_fallback()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var primaryStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var alternateStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            new DbColumnName("AlternateStudent_DocumentId"),
            "$.alternateStudentReference.studentUniqueId",
            "AlternateStudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                38,
                0,
                authObject,
                primaryStudentSubject
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [
                CreateNoClaimsFailure(resource, checkSpecs[0], authObject, "auth object fallback hint"),
                CreatePeopleNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    authObject,
                    alternateStudentSubject,
                    "path mismatch hint"
                ),
                CreatePeopleNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    authObject,
                    primaryStudentSubject,
                    "generic person hint"
                ) with
                {
                    PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                        RelationshipAuthorizationPersonKind.Student,
                        authObject
                    ),
                },
            ],
            [],
            10,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.Hint.Should().Be("generic person hint");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
    }

    [Test]
    public void It_should_use_people_path_mismatch_no_claims_metadata_when_no_exact_or_generic_metadata_exists()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var primaryStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var alternateStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            new DbColumnName("AlternateStudent_DocumentId"),
            "$.alternateStudentReference.studentUniqueId",
            "AlternateStudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                41,
                0,
                authObject,
                primaryStudentSubject
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [
                CreateNoClaimsFailure(resource, checkSpecs[0], authObject, "auth object fallback hint"),
                CreatePeopleNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    authObject,
                    alternateStudentSubject,
                    "path mismatch hint"
                ),
            ],
            [],
            14,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.Hint.Should().Be("path mismatch hint");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
    }

    [Test]
    public void It_should_use_default_no_claims_subject_hint_when_people_metadata_has_no_matching_auth_object()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var studentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var edOrgAuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                43,
                0,
                studentAuthObject,
                studentSubject
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [CreateNoClaimsFailure(resource, checkSpecs[0], edOrgAuthObject, "wrong auth object hint")],
            [],
            16,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject
            .Hint.Should()
            .Be("Relationship authorization requires at least one claim EducationOrganizationId.");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
    }

    [Test]
    public void It_should_skip_mismatched_people_no_claims_metadata_before_generic_people_metadata()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var studentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var contactAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Contact
        );
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var contactSubject = CreatePersonSubject(
            SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Contact,
            RelationshipAuthorizationPersonAuthViewKind.Contact,
            AuthNames.ContactDocumentId,
            "$.contactReference.contactUniqueId",
            "ContactUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RelationshipAuthorizationHierarchyDirection.Normal,
                44,
                0,
                studentAuthObject,
                studentSubject
            ),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [
                CreatePeopleNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    contactAuthObject,
                    contactSubject,
                    "wrong contact hint"
                ),
                CreatePeopleNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    studentAuthObject,
                    studentSubject,
                    "generic student hint"
                ) with
                {
                    PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                        RelationshipAuthorizationPersonKind.Student,
                        studentAuthObject
                    ),
                },
            ],
            [],
            17,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.Hint.Should().Be("generic student hint");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
    }

    [Test]
    public void It_should_use_default_no_claims_subject_hint_when_non_person_metadata_is_only_people_metadata()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var studentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var subject = CreateSubject("SchoolId", "$.schoolReference.schoolId");
        var studentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            AuthNames.StudentDocumentId,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId"
        );
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal, 45, 0, subject),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [
                CreatePeopleNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    studentAuthObject,
                    studentSubject,
                    "person-only metadata hint"
                ),
            ],
            [],
            18,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject
            .Hint.Should()
            .Be("Relationship authorization requires at least one claim EducationOrganizationId.");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToEducationOrganizationId");
        failedSubject.PersonSubject.Should().BeNull();
    }

    [Test]
    public void It_should_prefer_auth_object_specific_non_person_no_claims_metadata_before_generic_metadata()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var subject = CreateSubject("SchoolId", "$.schoolReference.schoolId");
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal, 39, 0, subject),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [
                CreateNoClaimsFailure(resource, checkSpecs[0], authObject: null, "generic hint"),
                CreateNoClaimsFailure(
                    resource,
                    checkSpecs[0],
                    subject.AuthObject,
                    "auth object specific hint"
                ),
            ],
            [],
            11,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.Hint.Should().Be("auth object specific hint");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToEducationOrganizationId");
        failedSubject.PersonSubject.Should().BeNull();
    }

    [Test]
    public void It_should_use_generic_non_person_no_claims_metadata_when_specific_auth_object_metadata_is_absent()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var subject = CreateSubject("SchoolId", "$.schoolReference.schoolId");
        var checkSpecs = new[]
        {
            CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal, 42, 0, subject),
        };

        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            [CreateNoClaimsFailure(resource, checkSpecs[0], authObject: null, "generic hint")],
            [],
            15,
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();

        var failedSubject = relationshipFailure!
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;

        failedSubject.Hint.Should().Be("generic hint");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToEducationOrganizationId");
        failedSubject.PersonSubject.Should().BeNull();
    }

    [Test]
    public void It_should_preserve_people_document_id_paths_and_proposed_anchor_metadata()
    {
        var schoolTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var courseTranscriptTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentSchoolAssociationTable = new DbTableName(
            new DbSchemaName("edfi"),
            "StudentSchoolAssociation"
        );
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var staffTable = new DbTableName(new DbSchemaName("edfi"), "Staff");
        var rootDocumentIdColumn = new DbColumnName("DocumentId");
        var directStudentColumn = AuthNames.StudentDocumentId;
        var firstHopColumn = new DbColumnName("StudentSchoolAssociation_DocumentId");
        var transitiveStudentColumn = AuthNames.StudentDocumentId;
        var directStudentProposedAnchor = new RelationshipAuthorizationPersonProposedAnchor(
            RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
            CreateProposedBinding(schoolTable, directStudentColumn)
        );
        var transitiveStudentProposedAnchor = new RelationshipAuthorizationPersonProposedAnchor(
            RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
            CreateProposedBinding(courseTranscriptTable, firstHopColumn)
        );
        var selfStaffProposedAnchor = new RelationshipAuthorizationPersonProposedAnchor(
            RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
            CreateProposedBinding(staffTable, rootDocumentIdColumn)
        );

        var directStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            directStudentColumn,
            "$.studentReference.studentUniqueId",
            "StudentUniqueId",
            rootTable: schoolTable,
            proposedAnchor: directStudentProposedAnchor
        );
        var transitiveStudentSubject = CreatePersonSubject(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            transitiveStudentColumn,
            "$.courseTranscriptReference.studentUniqueId",
            "StudentUniqueId",
            pathKind: RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
            pathSteps:
            [
                new ColumnPathStep(
                    courseTranscriptTable,
                    firstHopColumn,
                    studentSchoolAssociationTable,
                    rootDocumentIdColumn
                ),
                new ColumnPathStep(
                    studentSchoolAssociationTable,
                    transitiveStudentColumn,
                    studentTable,
                    rootDocumentIdColumn
                ),
            ],
            rootTable: courseTranscriptTable,
            proposedAnchor: transitiveStudentProposedAnchor,
            resourceName: "CourseTranscript"
        );
        var selfStaffSubject = CreatePersonSubject(
            SecurableElementKind.Staff,
            RelationshipAuthorizationPersonKind.Staff,
            RelationshipAuthorizationPersonAuthViewKind.Staff,
            rootDocumentIdColumn,
            "$.staffUniqueId",
            "StaffUniqueId",
            pathKind: RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
            pathSteps: [],
            rootTable: staffTable,
            proposedAnchor: selfStaffProposedAnchor,
            resourceName: "Staff"
        );
        var checkSpecs = new[]
        {
            CreatePeopleProposedCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                40,
                0,
                directStudentSubject
            ),
            CreatePeopleProposedCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                41,
                1,
                transitiveStudentSubject
            ),
            CreatePeopleProposedCheckSpec(
                AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                42,
                2,
                selfStaffSubject
            ),
        };

        var mapped = TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(
                6,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        1,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        2,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            ),
            checkSpecs,
            [100L],
            out var relationshipFailure
        );

        mapped.Should().BeTrue();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Proposed);

        var directFailedSubject = relationshipFailure.FailedStrategies[0].FailedSubjects[0];
        directFailedSubject.RootBinding.TableName.Should().Be("edfi.School");
        directFailedSubject.RootBinding.ColumnName.Should().Be("Student_DocumentId");
        directFailedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");

        var directPersonSubject = directFailedSubject.PersonSubject;
        directPersonSubject.Should().NotBeNull();
        directPersonSubject!.PersonKind.Should().Be("Student");
        directPersonSubject.Hint.Should().NotBeNull();
        directPersonSubject.PathKind.Should().Be("DirectRootColumn");
        directPersonSubject.DocumentIdPath.Should().ContainSingle();
        directPersonSubject.DocumentIdPath[0].SourceTableName.Should().Be("edfi.School");
        directPersonSubject.DocumentIdPath[0].SourceColumnName.Should().Be("Student_DocumentId");
        directPersonSubject.DocumentIdPath[0].TargetTableName.Should().Be("edfi.Student");
        directPersonSubject.DocumentIdPath[0].TargetColumnName.Should().Be("DocumentId");
        directPersonSubject.StoredAnchor.RootTableName.Should().Be("edfi.School");
        directPersonSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
        directPersonSubject.ProposedAnchor.Should().NotBeNull();
        directPersonSubject.ProposedAnchor!.Kind.Should().Be("RootRow");
        directPersonSubject.ProposedAnchor.Binding.TableName.Should().Be("edfi.School");
        directPersonSubject.ProposedAnchor.Binding.ColumnName.Should().Be("Student_DocumentId");
        directPersonSubject.ProposedAnchor.Binding.BindingIndex.Should().Be(0);
        directPersonSubject.ProposedAnchor.Binding.LogicalKey.Should().Be("Student_DocumentId");
        directPersonSubject.ProposedAnchor.Binding.ParameterSeed.Should().Be("student_DocumentId");

        var transitiveFailedSubject = relationshipFailure.FailedStrategies[1].FailedSubjects[0];
        transitiveFailedSubject.RootBinding.TableName.Should().Be("edfi.CourseTranscript");
        transitiveFailedSubject.RootBinding.ColumnName.Should().Be("StudentSchoolAssociation_DocumentId");

        var transitivePersonSubject = transitiveFailedSubject.PersonSubject;
        transitivePersonSubject.Should().NotBeNull();
        transitivePersonSubject!.PersonKind.Should().Be("Student");
        transitiveFailedSubject
            .AuthObject.Name.Should()
            .Be("auth.EducationOrganizationIdToStudentDocumentId");
        transitiveFailedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        transitivePersonSubject.Hint.Should().NotBeNull();
        transitivePersonSubject.PathKind.Should().Be("TransitiveJoinPath");
        transitivePersonSubject.DocumentIdPath.Should().HaveCount(2);
        transitivePersonSubject
            .DocumentIdPath.Select(static step => step.SourceTableName)
            .Should()
            .Equal("edfi.CourseTranscript", "edfi.StudentSchoolAssociation");
        transitivePersonSubject
            .DocumentIdPath.Select(static step => step.SourceColumnName)
            .Should()
            .Equal("StudentSchoolAssociation_DocumentId", "Student_DocumentId");
        transitivePersonSubject
            .DocumentIdPath.Select(static step => step.TargetTableName)
            .Should()
            .Equal("edfi.StudentSchoolAssociation", "edfi.Student");
        transitivePersonSubject.StoredAnchor.RootTableName.Should().Be("edfi.CourseTranscript");
        transitivePersonSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
        transitivePersonSubject.ProposedAnchor.Should().NotBeNull();
        transitivePersonSubject.ProposedAnchor!.Kind.Should().Be("FirstHop");
        transitivePersonSubject.ProposedAnchor.Binding.TableName.Should().Be("edfi.CourseTranscript");
        transitivePersonSubject
            .ProposedAnchor.Binding.ColumnName.Should()
            .Be("StudentSchoolAssociation_DocumentId");
        transitivePersonSubject
            .ProposedAnchor.Binding.ParameterSeed.Should()
            .Be("studentSchoolAssociation_DocumentId");

        var selfFailedSubject = relationshipFailure.FailedStrategies[2].FailedSubjects[0];
        selfFailedSubject.RootBinding.TableName.Should().Be("edfi.Staff");
        selfFailedSubject.RootBinding.ColumnName.Should().Be("DocumentId");
        selfFailedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStaffDocumentId");
        selfFailedSubject.AuthObject.SubjectValueColumn.Should().Be("Staff_DocumentId");

        var selfPersonSubject = selfFailedSubject.PersonSubject;
        selfPersonSubject.Should().NotBeNull();
        selfPersonSubject!.PersonKind.Should().Be("Staff");
        selfPersonSubject.Hint.Should().NotBeNull();
        selfPersonSubject.PathKind.Should().Be("SelfRootDocumentId");
        selfPersonSubject.DocumentIdPath.Should().BeEmpty();
        selfPersonSubject.StoredAnchor.RootTableName.Should().Be("edfi.Staff");
        selfPersonSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
        selfPersonSubject.ProposedAnchor.Should().NotBeNull();
        selfPersonSubject.ProposedAnchor!.Kind.Should().Be("RootRow");
        selfPersonSubject.ProposedAnchor.Binding.TableName.Should().Be("edfi.Staff");
        selfPersonSubject.ProposedAnchor.Binding.ColumnName.Should().Be("DocumentId");
    }

    private static RelationshipAuthorizationCheckSpec CreateStoredCheckSpec(
        RelationshipAuthorizationHierarchyDirection direction,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        CreateStoredCheckSpec(
            direction is RelationshipAuthorizationHierarchyDirection.Normal
                ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            direction,
            configuredStrategyIndex,
            relationshipLocalOrder,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
            subjects
        );

    private static RelationshipAuthorizationCheckSpec CreateStoredCheckSpec(
        string strategyName,
        RelationshipAuthorizationHierarchyDirection direction,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        RelationshipAuthorizationAuthObject authObject,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, configuredStrategyIndex),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Stored,
            StampNonPersonSubjects(authObject, subjects),
            new RelationshipAuthorizationCheckTarget.Stored(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId")
            )
        );

    private static RelationshipAuthorizationCheckSpec CreateProposedCheckSpec(
        RelationshipAuthorizationHierarchyDirection direction,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        params RelationshipAuthorizationSubject[] subjects
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                direction is RelationshipAuthorizationHierarchyDirection.Normal
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                configuredStrategyIndex
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Proposed,
            StampNonPersonSubjects(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
                subjects
            ),
            new RelationshipAuthorizationCheckTarget.Proposed(
                rootTable,
                [
                    .. subjects.Select(
                        static (subject, bindingIndex) =>
                            new RelationshipAuthorizationProposedValueBinding(
                                subject.Table,
                                subject.Column,
                                bindingIndex,
                                subject.Column.Value,
                                PlanNamingConventions.CamelCaseFirstCharacter(subject.Column.Value)
                            )
                    ),
                ]
            )
        );
    }

    private static RelationshipAuthorizationCheckSpec CreatePeopleProposedCheckSpec(
        string strategyName,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        RelationshipAuthorizationSubject subject
    )
    {
        var proposedAnchor =
            subject.PersonMetadata?.ProposedAnchor
            ?? throw new ArgumentException(
                "People proposed check specs require a person subject with proposed anchor metadata.",
                nameof(subject)
            );

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(strategyName, configuredStrategyIndex),
            relationshipLocalOrder,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [subject],
            new RelationshipAuthorizationCheckTarget.Proposed(
                proposedAnchor.Binding.Table,
                [proposedAnchor.Binding]
            )
        );
    }

    private static RelationshipAuthorizationSubject CreateSubject(string columnName, string jsonPath) =>
        new(
            new QualifiedResourceName("Ed-Fi", "School"),
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new DbColumnName(columnName),
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                RelationshipAuthorizationHierarchyDirection.Normal
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    jsonPath,
                    columnName
                ),
            ]
        );

    private static RelationshipAuthorizationSubject CreatePersonSubject(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind,
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        DbColumnName personDocumentIdColumn,
        string jsonPath,
        string readableName,
        RelationshipAuthorizationPersonSubjectPathKind pathKind =
            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
        IReadOnlyList<ColumnPathStep>? pathSteps = null,
        DbTableName? rootTable = null,
        RelationshipAuthorizationPersonProposedAnchor? proposedAnchor = null,
        string resourceName = "School"
    )
    {
        var subjectRootTable = rootTable ?? new DbTableName(new DbSchemaName("edfi"), "School");
        var personTable = new DbTableName(new DbSchemaName("edfi"), personKind.ToString());
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);
        var documentIdColumn = new DbColumnName("DocumentId");
        var subjectPathSteps =
            pathSteps
            ?? [new ColumnPathStep(subjectRootTable, personDocumentIdColumn, personTable, documentIdColumn)];
        var subjectTable =
            pathKind is RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath
                ? subjectPathSteps[^1].SourceTable
                : subjectRootTable;

        return new RelationshipAuthorizationSubject(
            new QualifiedResourceName("Ed-Fi", resourceName),
            subjectTable,
            personDocumentIdColumn,
            authObject,
            [new RelationshipAuthorizationSubjectContributor(securableElementKind, jsonPath, readableName)],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(pathKind, subjectPathSteps),
                new RelationshipAuthorizationPersonStoredAnchor(subjectRootTable, documentIdColumn),
                proposedAnchor
            )
        );
    }

    private static bool TryMapAuth1Failure(
        RelationshipAuthorizationAuth1FailurePayload payload,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<long> claimEducationOrganizationIds,
        out RelationshipAuthorizationFailure? relationshipFailure
    ) =>
        RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            payload.EmittedAuth1Index,
            checkSpecs,
            claimEducationOrganizationIds,
            out relationshipFailure
        );

    private static RelationshipAuthorizationSubject[] StampNonPersonSubjects(
        RelationshipAuthorizationAuthObject authObject,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects
    ) =>
        [
            .. subjects.Select(subject =>
                subject.IsPersonSubject ? subject : subject with { AuthObject = authObject }
            ),
        ];

    private static void AssertNoClaimsFailureDoesNotMap(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> noClaimsFailures
    )
    {
        var mapped = RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            checkSpecs,
            noClaimsFailures,
            [],
            1,
            out var relationshipFailure
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
    }

    private static RelationshipAuthorizationFailureMetadata CreateNoClaimsFailure(
        QualifiedResourceName resource,
        RelationshipAuthorizationCheckSpec checkSpec,
        RelationshipAuthorizationAuthObject? authObject,
        string? hint
    ) =>
        new(
            RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
            resource,
            checkSpec.ConfiguredStrategy,
            checkSpec.RelationshipLocalOrder,
            ValueSource: checkSpec.ValueSource,
            AuthObject: authObject,
            Hint: hint
        );

    private static RelationshipAuthorizationFailureMetadata CreatePeopleNoClaimsFailure(
        QualifiedResourceName resource,
        RelationshipAuthorizationCheckSpec checkSpec,
        RelationshipAuthorizationAuthObject authObject,
        RelationshipAuthorizationSubject subject,
        string hint
    ) =>
        new(
            RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
            resource,
            checkSpec.ConfiguredStrategy,
            checkSpec.RelationshipLocalOrder,
            ValueSource: checkSpec.ValueSource,
            AuthObject: authObject,
            Hint: hint
        )
        {
            PersonMetadata = new RelationshipAuthorizationPersonFailureMetadata(
                subject.PersonMetadata!.PersonKind,
                authObject,
                subject.PersonMetadata.Path
            ),
            Contributors = subject.Contributors,
        };

    private static RelationshipAuthorizationProposedValueBinding CreateProposedBinding(
        DbTableName table,
        DbColumnName column
    ) =>
        new(
            table,
            column,
            BindingIndex: 0,
            LogicalKey: column.Value,
            ParameterSeed: PlanNamingConventions.CamelCaseFirstCharacter(column.Value)
        );
}

[TestFixture]
[Parallelizable]
public class Given_SingleRecordRelationshipAuthorizationSqlCompiler
{
    private static MappingSet CreateCacheMappingSet(SqlDialect dialect)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKeyEntry = new ResourceKeyEntry(1, resource, "5.2.0", IsAbstractResource: false);
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "5.2",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: new string('f', 64),
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.2.0", false, new string('e', 64)),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );
        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: dialect,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.2.0", false, new DbSchemaName("edfi")),
            ],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKeyEntry.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKeyEntry.ResourceKeyId] = resourceKeyEntry,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

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
        plan.AuthorizationSql.Should()
            .Contain(
                "CASE WHEN target.\"SchoolId\" IS NULL OR NOT (target.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds) OR EXISTS (SELECT 1 FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" a0_0 WHERE a0_0.\"TargetEducationOrganizationId\" = target.\"SchoolId\" AND a0_0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))) THEN 1 ELSE 0 END"
            );
        plan.AuthorizationSql.Should().Contain("CASE WHEN target.\"SchoolId\" IS NULL THEN 's' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain("NOT EXISTS (SELECT 1 FROM failed_subjects WHERE \"StrategyOrdinal\" = 0)");
    }

    [Test]
    public void It_should_reuse_cached_postgresql_plans_when_only_claim_edorg_values_change()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs =
        [
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        ];
        var firstParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var secondParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [200L, 300L],
            "ClaimEducationOrganizationIds"
        );

        var firstPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, firstParameterization, 5)
        );
        var secondPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, secondParameterization, 5)
        );

        secondPlan.Should().BeSameAs(firstPlan);
    }

    [Test]
    public void It_should_reuse_cached_postgresql_plans_for_structurally_equivalent_stored_check_specs()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        var firstCheckSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };
        var secondCheckSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };

        firstCheckSpecs.Should().NotBeSameAs(secondCheckSpecs);

        var firstPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                firstCheckSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );
        var secondPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                secondCheckSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );

        secondPlan.Should().BeSameAs(firstPlan);
    }

    [Test]
    public void It_should_scope_cached_plans_to_the_mapping_set_instance()
    {
        var firstMappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        var secondMappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs =
        [
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        ];

        firstMappingSet.Should().NotBeSameAs(secondMappingSet);

        var firstPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            firstMappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                checkSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );
        var secondPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            secondMappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                checkSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );

        secondPlan.Should().NotBeSameAs(firstPlan);
        secondPlan.AuthorizationSql.Should().Be(firstPlan.AuthorizationSql);
    }

    [Test]
    public void It_should_reuse_cached_postgresql_plans_for_structurally_equivalent_proposed_check_specs()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        var firstCheckSpecs = new[]
        {
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };
        var secondCheckSpecs = new[]
        {
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };

        firstCheckSpecs.Should().NotBeSameAs(secondCheckSpecs);

        var firstPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                firstCheckSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );
        var secondPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                secondCheckSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );

        secondPlan.Should().BeSameAs(firstPlan);
        AssertSingleProposedValueParameterName(secondPlan, "relationshipAuthorization_0_0_schoolId");
    }

    [Test]
    public void It_should_not_reuse_cached_postgresql_plans_when_check_spec_sql_shape_changes()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        var schoolCheckSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        };
        var localEducationAgencyCheckSpecs = new[]
        {
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject(
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyReference.localEducationAgencyId"
                )
            ),
        };

        var schoolPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                schoolCheckSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );
        var localEducationAgencyPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                localEducationAgencyCheckSpecs,
                CreateSingleClaimParameterization(),
                5
            )
        );

        localEducationAgencyPlan.Should().NotBeSameAs(schoolPlan);
        schoolPlan.AuthorizationSql.Should().Contain("\"SchoolId\"");
        localEducationAgencyPlan.AuthorizationSql.Should().Contain("\"LocalEducationAgencyId\"");
    }

    [Test]
    public void It_should_validate_uncached_claim_edorg_values_before_reusing_cached_plan()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs =
        [
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        ];
        var validParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var invalidParameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
            "ClaimEducationOrganizationIds",
            [],
            ["ClaimEducationOrganizationIds"]
        );

        SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, validParameterization, 5)
        );

        var compile = () =>
            SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, invalidParameterization, 5)
            );

        compile.Should().Throw<ArgumentException>().WithMessage("*at least one claim EdOrg id*");
    }

    [Test]
    public void It_should_not_reuse_cached_proposed_plans_when_reserved_parameter_names_change()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Pgsql);
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs =
        [
            CreateProposedCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        ];
        var parameterization = CreateSingleClaimParameterization();

        var unreservedPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, parameterization, 5)
        );
        var reservedPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                checkSpecs,
                parameterization,
                5,
                ReservedParameterNames: ["relationshipAuthorization_0_0_schoolId"]
            )
        );

        reservedPlan.Should().NotBeSameAs(unreservedPlan);
        AssertSingleProposedValueParameterName(unreservedPlan, "relationshipAuthorization_0_0_schoolId");
        AssertSingleProposedValueParameterName(reservedPlan, "relationshipAuthorization_0_0_schoolId_2");
    }

    [Test]
    public void It_should_not_reuse_cached_sql_server_scalar_plans_when_parameter_shape_changes()
    {
        var mappingSet = CreateCacheMappingSet(SqlDialect.Mssql);
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs =
        [
            CreateStoredCheckSpec(
                RelationshipAuthorizationHierarchyDirection.Normal,
                0,
                0,
                CreateSubject("SchoolId", "$.schoolReference.schoolId")
            ),
        ];
        var oneClaimParameterization =
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Mssql,
                [100L],
                "ClaimEducationOrganizationIds"
            );
        var twoClaimParameterization =
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Mssql,
                [100L, 200L],
                "ClaimEducationOrganizationIds"
            );

        var oneClaimPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, oneClaimParameterization, 5)
        );
        var twoClaimPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            mappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(checkSpecs, twoClaimParameterization, 5)
        );

        twoClaimPlan.Should().NotBeSameAs(oneClaimPlan);
        oneClaimPlan
            .ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("DocumentId", "ClaimEducationOrganizationIds_0");
        twoClaimPlan
            .ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("DocumentId", "ClaimEducationOrganizationIds_0", "ClaimEducationOrganizationIds_1");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stored_target_cte_with_ordered_root_columns(SqlDialect dialect)
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);

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

        var documentTable = new DbTableName(new DbSchemaName("dms"), "Document");
        var schoolTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var documentId = QuoteIdentifier(dialect, "DocumentId");
        var strategyOrdinal = QuoteIdentifier(dialect, "StrategyOrdinal");
        var subjectOrdinal = QuoteIdentifier(dialect, "SubjectOrdinal");
        var failureKind = QuoteIdentifier(dialect, "FailureKind");
        var failed = QuoteIdentifier(dialect, "Failed");
        var payload = QuoteIdentifier(dialect, "Payload");
        var authorizationResult = QuoteIdentifier(dialect, "AuthorizationResult");
        var contentVersion = QuoteIdentifier(dialect, "ContentVersion");
        var schoolId = QuoteIdentifier(dialect, "SchoolId");
        var localEducationAgencyId = QuoteIdentifier(dialect, "LocalEducationAgencyId");
        var targetEdOrgId = QuoteIdentifier(dialect, AuthNames.TargetEdOrgId.Value);
        var sourceEdOrgId = QuoteIdentifier(dialect, AuthNames.SourceEdOrgId.Value);
        var edOrgAuthRelation = QuoteRelation(dialect, AuthNames.EdOrgIdToEdOrgId);
        var claimEdOrgFilter = ClaimEducationOrganizationIdFilterFragment(dialect);
        var authorizationSuccessSql =
            $"NOT EXISTS (SELECT 1 FROM failed_subjects WHERE {strategyOrdinal} = 0)";
        var auth1AbortSql =
            dialect is SqlDialect.Pgsql
                ? $"{QuoteIdentifier(dialect, "dms")}.{QuoteIdentifier(dialect, "throw_error")}('{RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode}', (SELECT {payload} FROM failure_payload))"
                : $"CAST(CONCAT('{RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode} - ', (SELECT {payload} FROM failure_payload)) AS INT)";
        var expectedTargetCtePrefix = string.Join(
            '\n',
            "WITH target AS (",
            "    SELECT",
            $"        d.{contentVersion},",
            $"        r.{QuoteIdentifier(dialect, "LocalEducationAgencyId")},",
            $"        r.{QuoteIdentifier(dialect, "SchoolId")}",
            $"    FROM {QuoteRelation(dialect, schoolTable)} r",
            $"    INNER JOIN {QuoteRelation(dialect, documentTable)} d",
            $"        ON d.{documentId} = r.{documentId}",
            $"    WHERE r.{documentId} = @DocumentId",
            "),",
            $"subject_failures ({strategyOrdinal}, {subjectOrdinal}, {failureKind}, {failed}) AS ("
        );
        var expectedSubjectFailureSelects = string.Join(
            '\n',
            $"subject_failures ({strategyOrdinal}, {subjectOrdinal}, {failureKind}, {failed}) AS (",
            "    SELECT",
            "        0,",
            "        0,",
            $"        CASE WHEN target.{schoolId} IS NULL THEN 's' ELSE 'n' END,",
            $"        CASE WHEN target.{schoolId} IS NULL OR NOT (target.{schoolId}{claimEdOrgFilter} OR EXISTS (SELECT 1 FROM {edOrgAuthRelation} a0_0 WHERE a0_0.{targetEdOrgId} = target.{schoolId} AND a0_0.{sourceEdOrgId}{claimEdOrgFilter})) THEN 1 ELSE 0 END",
            "",
            "    FROM target",
            "    UNION ALL",
            "    SELECT",
            "        0,",
            "        1,",
            $"        CASE WHEN target.{localEducationAgencyId} IS NULL THEN 's' ELSE 'n' END,",
            $"        CASE WHEN target.{localEducationAgencyId} IS NULL OR NOT (target.{localEducationAgencyId}{claimEdOrgFilter} OR EXISTS (SELECT 1 FROM {edOrgAuthRelation} a0_1 WHERE a0_1.{targetEdOrgId} = target.{localEducationAgencyId} AND a0_1.{sourceEdOrgId}{claimEdOrgFilter})) THEN 1 ELSE 0 END",
            "",
            "    FROM target",
            "),"
        );

        plan.AuthorizationSql.Should().StartWith(expectedTargetCtePrefix);
        plan.AuthorizationSql.Should().Contain(expectedSubjectFailureSelects);
        plan.AuthorizationSql.Should()
            .Contain(
                string.Join(
                    '\n',
                    "    FROM target",
                    "),",
                    "failed_subjects AS (",
                    "    SELECT * FROM subject_failures",
                    $"    WHERE {QuoteIdentifier(dialect, "Failed")} = 1",
                    "),",
                    "failure_payload AS ("
                )
            );
        plan.AuthorizationSql.Should().Contain(")\nSELECT CASE");
        plan.AuthorizationSql.Should()
            .EndWith(
                string.Join(
                    '\n',
                    "SELECT CASE",
                    $"    WHEN {authorizationSuccessSql} THEN 1",
                    $"    ELSE {auth1AbortSql}",
                    $"END AS {authorizationResult},",
                    contentVersion,
                    "FROM target;"
                ) + "\n"
            );
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
        plan.AuthorizationSql.Should()
            .Contain(
                "CASE WHEN target.[SchoolId] IS NULL OR NOT (target.[SchoolId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1) OR EXISTS (SELECT 1 FROM [auth].[EducationOrganizationIdToEducationOrganizationId] a0_0 WHERE a0_0.[SourceEducationOrganizationId] = target.[SchoolId] AND a0_0.[TargetEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1))) THEN 1 ELSE 0 END"
            );
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
        plan.AuthorizationSql.Should()
            .Contain("target.[SchoolId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds) OR EXISTS");
    }

    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Student,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        RelationshipAuthorizationPersonKind.Contact,
        "Contact_DocumentId"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        RelationshipAuthorizationPersonKind.Staff,
        "Staff_DocumentId"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Student,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        RelationshipAuthorizationPersonKind.Contact,
        "Contact_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        RelationshipAuthorizationPersonKind.Staff,
        "Staff_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId"
    )]
    public void It_should_compile_stored_direct_people_authorization_sql(
        SqlDialect dialect,
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        string personDocumentIdColumnName
    )
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var subject = CreateDirectPersonSubject(
            authViewKind,
            personKind,
            new DbColumnName(personDocumentIdColumnName)
        );

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        0,
                        0,
                        new DbTableName(new DbSchemaName("edfi"), "School"),
                        subject
                    ),
                ],
                parameterization,
                14
            )
        );

        var subjectColumn = QuoteIdentifier(dialect, personDocumentIdColumnName);
        var authObject = subject.AuthObject;
        var strategyOrdinal = QuoteIdentifier(dialect, "StrategyOrdinal");
        var subjectOrdinal = QuoteIdentifier(dialect, "SubjectOrdinal");
        var failureKind = QuoteIdentifier(dialect, "FailureKind");
        var failed = QuoteIdentifier(dialect, "Failed");
        var subjectValueColumn = QuoteIdentifier(dialect, authObject.SubjectValueColumn.Value);
        var claimEducationOrganizationIdColumn = QuoteIdentifier(
            dialect,
            authObject.ClaimEducationOrganizationIdColumn.Value
        );
        var expectedSubjectFailureSelect = string.Join(
            '\n',
            $"subject_failures ({strategyOrdinal}, {subjectOrdinal}, {failureKind}, {failed}) AS (",
            "    SELECT",
            "        0,",
            "        0,",
            $"        CASE WHEN target.{subjectColumn} IS NULL THEN 's' ELSE 'n' END,",
            $"        CASE WHEN NOT EXISTS (SELECT 1 FROM {QuoteRelation(dialect, authObject.Name)} a0_0 WHERE a0_0.{subjectValueColumn} = target.{subjectColumn} AND a0_0.{claimEducationOrganizationIdColumn}{ClaimEducationOrganizationIdFilterFragment(dialect)}) THEN 1 ELSE 0 END",
            "",
            "    FROM target",
            "),"
        );

        plan.AuthorizationSql.Should()
            .Contain($"CASE WHEN target.{subjectColumn} IS NULL THEN 's' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain(
                $"EXISTS (SELECT 1 FROM {QuoteRelation(dialect, authObject.Name)} a0_0 WHERE a0_0.{QuoteIdentifier(dialect, authObject.SubjectValueColumn.Value)} = target.{subjectColumn} AND a0_0.{QuoteIdentifier(dialect, authObject.ClaimEducationOrganizationIdColumn.Value)}{ClaimEducationOrganizationIdFilterFragment(dialect)})"
            );
        plan.AuthorizationSql.Should().Contain(expectedSubjectFailureSelect);
        plan.AuthorizationSql.Should().NotContain("UniqueId");
        plan.AuthorizationSql.Should().NotContain("USI");
    }

    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Student,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        RelationshipAuthorizationPersonKind.Contact,
        "Contact_DocumentId"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        RelationshipAuthorizationPersonKind.Staff,
        "Staff_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Student,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        RelationshipAuthorizationPersonKind.Contact,
        "Contact_DocumentId"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        RelationshipAuthorizationPersonKind.Staff,
        "Staff_DocumentId"
    )]
    public void It_should_compile_stored_self_people_authorization_sql(
        SqlDialect dialect,
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        string authViewPersonDocumentIdColumnName
    )
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), personKind.ToString());
        var subject = CreateSelfPersonSubject(authViewKind, personKind, rootTable);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        0,
                        0,
                        rootTable,
                        subject
                    ),
                ],
                parameterization,
                15
            )
        );

        var authObject = subject.AuthObject;
        var documentId = QuoteIdentifier(dialect, "DocumentId");
        var subjectValueColumn = QuoteIdentifier(dialect, authObject.SubjectValueColumn.Value);
        var claimEducationOrganizationIdColumn = QuoteIdentifier(
            dialect,
            authObject.ClaimEducationOrganizationIdColumn.Value
        );
        var authorizationSuccessSql =
            $"EXISTS (SELECT 1 FROM {QuoteRelation(dialect, authObject.Name)} a0_0 WHERE a0_0.{subjectValueColumn} = target.{documentId} AND a0_0.{claimEducationOrganizationIdColumn}{ClaimEducationOrganizationIdFilterFragment(dialect)})";

        plan.AuthorizationSql.Should().Contain($"FROM {QuoteRelation(dialect, rootTable)} r");
        plan.AuthorizationSql.Should()
            .Contain(
                $"CASE WHEN target.{QuoteIdentifier(dialect, "DocumentId")} IS NULL THEN 's' ELSE 'n' END"
            );
        plan.AuthorizationSql.Should().Contain($"CASE WHEN NOT {authorizationSuccessSql} THEN 1 ELSE 0 END");
        plan.AuthorizationSql.Should()
            .Contain(
                $"a0_0.{QuoteIdentifier(dialect, authViewPersonDocumentIdColumnName)} = target.{QuoteIdentifier(dialect, "DocumentId")}"
            );
        plan.AuthorizationSql.Should().NotContain("UniqueId");
        plan.AuthorizationSql.Should().NotContain("USI");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stored_transitive_people_authorization_sql_with_invalid_data_failures(
        SqlDialect dialect
    )
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var subject = CreateTransitivePersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            rootTable,
            [
                new ColumnPathStep(
                    rootTable,
                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                    studentAcademicRecordTable,
                    new DbColumnName("DocumentId")
                ),
                new ColumnPathStep(
                    studentAcademicRecordTable,
                    AuthNames.StudentDocumentId,
                    studentTable,
                    new DbColumnName("DocumentId")
                ),
            ]
        );

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        0,
                        0,
                        rootTable,
                        subject
                    ),
                ],
                parameterization,
                16
            )
        );

        var documentId = QuoteIdentifier(dialect, "DocumentId");
        var firstHopColumn = QuoteIdentifier(dialect, "StudentAcademicRecord_DocumentId");
        var studentDocumentId = QuoteIdentifier(dialect, AuthNames.StudentDocumentId.Value);
        var sourceEdOrgId = QuoteIdentifier(dialect, AuthNames.SourceEdOrgId.Value);
        var pathExistsSql =
            $"SELECT 1 FROM {QuoteRelation(dialect, rootTable)} p0_0_0 JOIN {QuoteRelation(dialect, studentAcademicRecordTable)} p0_0_1 ON p0_0_1.{documentId} = p0_0_0.{firstHopColumn} WHERE p0_0_0.{documentId} = target.{documentId} AND p0_0_1.{studentDocumentId} IS NOT NULL";
        var authorizationExistsSql =
            $"EXISTS (SELECT 1 FROM {QuoteRelation(dialect, subject.AuthObject.Name)} a0_0 WHERE a0_0.{studentDocumentId} = p0_0_1.{studentDocumentId} AND a0_0.{sourceEdOrgId}{ClaimEducationOrganizationIdFilterFragment(dialect)})";

        plan.AuthorizationSql.Should()
            .Contain($"CASE WHEN NOT EXISTS ({pathExistsSql}) THEN 's' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain(
                $"CASE WHEN NOT EXISTS ({pathExistsSql} AND {authorizationExistsSql}) THEN 1 ELSE 0 END"
            );
        plan.AuthorizationSql.Should().NotContain("UniqueId");
        plan.AuthorizationSql.Should().NotContain("USI");
    }

    [Test]
    public void It_should_reject_people_subjects_whose_stored_anchor_does_not_match_the_root_table()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var mismatchedRootTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var subject = CreateDirectPersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId,
            rootTable
        );
        var personMetadata =
            subject.PersonMetadata
            ?? throw new InvalidOperationException("Expected a person authorization subject.");
        var mismatchedSubject = subject with
        {
            PersonMetadata = personMetadata with
            {
                StoredAnchor = new RelationshipAuthorizationPersonStoredAnchor(
                    mismatchedRootTable,
                    new DbColumnName("DocumentId")
                ),
            },
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateStoredPeopleCheckSpec(
                            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                            0,
                            0,
                            rootTable,
                            mismatchedSubject
                        ),
                    ],
                    parameterization,
                    16
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("checkSpecs")
            .WithMessage("*does not match root table*");
    }

    [Test]
    public void It_should_reject_stored_self_people_subjects_whose_column_does_not_match_the_root_document_id()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var subject = CreateSelfPersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            rootTable
        ) with
        {
            Column = AuthNames.StudentDocumentId,
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateStoredPeopleCheckSpec(
                            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                            0,
                            0,
                            rootTable,
                            subject
                        ),
                    ],
                    parameterization,
                    16
                )
            );

        compile.Should().Throw<InvalidOperationException>().WithMessage("*root DocumentId column*");
    }

    [Test]
    public void It_should_reject_stored_direct_people_paths_whose_source_table_does_not_match_the_root_table()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var mismatchedRootTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var subject = CreateDirectPersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId,
            rootTable
        );
        var personMetadata =
            subject.PersonMetadata
            ?? throw new InvalidOperationException("Expected a person authorization subject.");
        var mismatchedSubject = subject with
        {
            PersonMetadata = personMetadata with
            {
                Path = new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                    [
                        new ColumnPathStep(
                            mismatchedRootTable,
                            AuthNames.StudentDocumentId,
                            new DbTableName(new DbSchemaName("edfi"), "Student"),
                            new DbColumnName("DocumentId")
                        ),
                    ]
                ),
            },
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateStoredPeopleCheckSpec(
                            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                            0,
                            0,
                            rootTable,
                            mismatchedSubject
                        ),
                    ],
                    parameterization,
                    16
                )
            );

        compile.Should().Throw<InvalidOperationException>().WithMessage("*does not match root table*");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_mixed_edorg_and_people_subjects_in_one_stored_strategy(SqlDialect dialect)
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId", rootTable),
                        CreateDirectPersonSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            AuthNames.StudentDocumentId,
                            rootTable
                        )
                    ),
                    CreateStoredPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        1,
                        1,
                        rootTable,
                        CreateDirectPersonSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Staff,
                            RelationshipAuthorizationPersonKind.Staff,
                            AuthNames.StaffDocumentId,
                            rootTable
                        )
                    ),
                ],
                parameterization,
                17
            )
        );

        plan.AuthorizationSql.Should().Contain("a0_0");
        plan.AuthorizationSql.Should().Contain("a0_1");
        plan.AuthorizationSql.Should().Contain("a1_0");
        plan.AuthorizationSql.Should()
            .Contain(
                $"NOT EXISTS (SELECT 1 FROM failed_subjects WHERE {QuoteIdentifier(dialect, "StrategyOrdinal")} = 0) OR NOT EXISTS (SELECT 1 FROM failed_subjects WHERE {QuoteIdentifier(dialect, "StrategyOrdinal")} = 1)"
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_proposed_direct_people_authorization_sql(SqlDialect dialect)
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var subject = CreateDirectPersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId,
            rootTable
        );
        var proposedBinding = CreateProposedBinding(rootTable, AuthNames.StudentDocumentId);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateProposedPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        0,
                        0,
                        rootTable,
                        subject,
                        RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                        proposedBinding
                    ),
                ],
                parameterization,
                18
            )
        );

        var parameterName = plan.ProposedValueParametersInOrder.Should().ContainSingle().Which.ParameterName;
        var proposedValue = ProposedValueFragment(dialect, parameterName);
        var studentDocumentId = QuoteIdentifier(dialect, AuthNames.StudentDocumentId.Value);
        var sourceEdOrgId = QuoteIdentifier(dialect, AuthNames.SourceEdOrgId.Value);

        plan.AuthorizationSql.Should().Contain($"CASE WHEN {proposedValue} IS NULL THEN 'p' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain(
                $"EXISTS (SELECT 1 FROM {QuoteRelation(dialect, subject.AuthObject.Name)} a0_0 WHERE a0_0.{studentDocumentId} = {proposedValue} AND a0_0.{sourceEdOrgId}{ClaimEducationOrganizationIdFilterFragment(dialect)})"
            );
        plan.AuthorizationSql.Should().NotContain("UniqueId");
        plan.AuthorizationSql.Should().NotContain("USI");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_proposed_existing_target_self_people_authorization_sql(SqlDialect dialect)
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var documentIdColumn = new DbColumnName("DocumentId");
        var subject = CreateSelfPersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            rootTable
        );
        var proposedBinding = CreateProposedBinding(rootTable, documentIdColumn);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateProposedPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        0,
                        0,
                        rootTable,
                        subject,
                        RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId,
                        proposedBinding
                    ),
                ],
                parameterization,
                19
            )
        );

        var parameterName = plan.ProposedValueParametersInOrder.Should().ContainSingle().Which.ParameterName;
        var proposedValue = ProposedValueFragment(dialect, parameterName);
        var studentDocumentId = QuoteIdentifier(dialect, AuthNames.StudentDocumentId.Value);
        var sourceEdOrgId = QuoteIdentifier(dialect, AuthNames.SourceEdOrgId.Value);

        plan.AuthorizationSql.Should().Contain($"CASE WHEN {proposedValue} IS NULL THEN 'p' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain(
                $"EXISTS (SELECT 1 FROM {QuoteRelation(dialect, subject.AuthObject.Name)} a0_0 WHERE a0_0.{studentDocumentId} = {proposedValue} AND a0_0.{sourceEdOrgId}{ClaimEducationOrganizationIdFilterFragment(dialect)})"
            );
        plan.AuthorizationSql.Should().NotContain("UniqueId");
        plan.AuthorizationSql.Should().NotContain("USI");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_proposed_transitive_people_authorization_sql_with_element_required_failures(
        SqlDialect dialect
    )
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var studentSchoolAssociationTable = new DbTableName(
            new DbSchemaName("edfi"),
            "StudentSchoolAssociation"
        );
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var firstHopColumn = new DbColumnName("StudentAcademicRecord_DocumentId");
        var secondHopColumn = new DbColumnName("StudentSchoolAssociation_DocumentId");
        var subject = CreateTransitivePersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            rootTable,
            [
                new ColumnPathStep(
                    rootTable,
                    firstHopColumn,
                    studentAcademicRecordTable,
                    new DbColumnName("DocumentId")
                ),
                new ColumnPathStep(
                    studentAcademicRecordTable,
                    secondHopColumn,
                    studentSchoolAssociationTable,
                    new DbColumnName("DocumentId")
                ),
                new ColumnPathStep(
                    studentSchoolAssociationTable,
                    AuthNames.StudentDocumentId,
                    studentTable,
                    new DbColumnName("DocumentId")
                ),
            ]
        );
        var proposedBinding = CreateProposedBinding(rootTable, firstHopColumn);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateProposedPeopleCheckSpec(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        0,
                        0,
                        rootTable,
                        subject,
                        RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                        proposedBinding
                    ),
                ],
                parameterization,
                20
            )
        );

        var parameterName = plan.ProposedValueParametersInOrder.Should().ContainSingle().Which.ParameterName;
        var proposedValue = ProposedValueFragment(dialect, parameterName);
        var documentId = QuoteIdentifier(dialect, "DocumentId");
        var secondHopDocumentId = QuoteIdentifier(dialect, secondHopColumn.Value);
        var studentDocumentId = QuoteIdentifier(dialect, AuthNames.StudentDocumentId.Value);
        var sourceEdOrgId = QuoteIdentifier(dialect, AuthNames.SourceEdOrgId.Value);
        var invalidDataColumn = QuoteIdentifier(dialect, "InvalidData");
        var invalidDataPathPredicate =
            $"{proposedValue} IS NULL OR NOT EXISTS (SELECT 1 FROM {QuoteRelation(dialect, studentAcademicRecordTable)} p0_0_0 JOIN {QuoteRelation(dialect, studentSchoolAssociationTable)} p0_0_1 ON p0_0_1.{documentId} = p0_0_0.{secondHopDocumentId} WHERE p0_0_0.{documentId} = {proposedValue} AND p0_0_1.{studentDocumentId} IS NOT NULL)";
        var authorizationExistsSql =
            $"EXISTS (SELECT 1 FROM {QuoteRelation(dialect, subject.AuthObject.Name)} a0_0 WHERE a0_0.{studentDocumentId} = p0_0_1.{studentDocumentId} AND a0_0.{sourceEdOrgId}{ClaimEducationOrganizationIdFilterFragment(dialect)})";

        plan.AuthorizationSql.Should()
            .Contain($"SELECT CASE WHEN {invalidDataPathPredicate} THEN 1 ELSE 0 END AS {invalidDataColumn}");
        plan.AuthorizationSql.Should()
            .Contain($"CASE WHEN invalid_data.{invalidDataColumn} = 1 THEN 'p' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain(
                $"CASE WHEN invalid_data.{invalidDataColumn} = 1 OR NOT EXISTS (SELECT 1 FROM {QuoteRelation(dialect, studentAcademicRecordTable)} p0_0_0 JOIN {QuoteRelation(dialect, studentSchoolAssociationTable)} p0_0_1 ON p0_0_1.{documentId} = p0_0_0.{secondHopDocumentId} WHERE p0_0_0.{documentId} = {proposedValue} AND p0_0_1.{studentDocumentId} IS NOT NULL AND {authorizationExistsSql}) THEN 1 ELSE 0 END"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                string.Join(
                    '\n',
                    "    SELECT",
                    "        0,",
                    "        0,",
                    $"        CASE WHEN invalid_data.{invalidDataColumn} = 1 THEN 'p' ELSE 'n' END,",
                    $"        CASE WHEN invalid_data.{invalidDataColumn} = 1 OR NOT EXISTS (SELECT 1 FROM {QuoteRelation(dialect, studentAcademicRecordTable)} p0_0_0 JOIN {QuoteRelation(dialect, studentSchoolAssociationTable)} p0_0_1 ON p0_0_1.{documentId} = p0_0_0.{secondHopDocumentId} WHERE p0_0_0.{documentId} = {proposedValue} AND p0_0_1.{studentDocumentId} IS NOT NULL AND {authorizationExistsSql}) THEN 1 ELSE 0 END",
                    "",
                    "    FROM (",
                    $"        SELECT CASE WHEN {invalidDataPathPredicate} THEN 1 ELSE 0 END AS {invalidDataColumn}",
                    "    ) invalid_data",
                    "),",
                    "failed_subjects AS ("
                )
            );
        CountOccurrences(plan.AuthorizationSql, invalidDataPathPredicate).Should().Be(1);
        plan.AuthorizationSql.Should().NotContain("UniqueId");
        plan.AuthorizationSql.Should().NotContain("USI");
    }

    [Test]
    public void It_should_reject_proposed_transitive_people_paths_with_disconnected_steps()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var studentSchoolAssociationTable = new DbTableName(
            new DbSchemaName("edfi"),
            "StudentSchoolAssociation"
        );
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var firstHopColumn = new DbColumnName("StudentAcademicRecord_DocumentId");
        var secondHopColumn = new DbColumnName("StudentSchoolAssociation_DocumentId");
        var subject = CreateTransitivePersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            rootTable,
            [
                new ColumnPathStep(
                    rootTable,
                    firstHopColumn,
                    studentAcademicRecordTable,
                    new DbColumnName("DocumentId")
                ),
                new ColumnPathStep(
                    studentSchoolAssociationTable,
                    secondHopColumn,
                    studentTable,
                    new DbColumnName("DocumentId")
                ),
                new ColumnPathStep(
                    studentTable,
                    AuthNames.StudentDocumentId,
                    studentTable,
                    new DbColumnName("DocumentId")
                ),
            ]
        );
        var proposedBinding = CreateProposedBinding(rootTable, firstHopColumn);

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateProposedPeopleCheckSpec(
                            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                            0,
                            0,
                            rootTable,
                            subject,
                            RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                            proposedBinding
                        ),
                    ],
                    parameterization,
                    20
                )
            );

        compile
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*path step 1 source table*does not match previous target table*");
    }

    [Test]
    public void It_should_compile_postgresql_proposed_auth1_sql_with_or_strategies_and_and_subjects()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [200L, 100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateProposedCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId"),
                        CreateSubject(
                            "LocalEducationAgencyId",
                            "$.localEducationAgencyReference.localEducationAgencyId"
                        )
                    ),
                    CreateProposedCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        1,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId")
                    ),
                    CreateProposedCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Inverted,
                        1,
                        2,
                        CreateSubject(
                            "EducationServiceCenterId",
                            "$.educationServiceCenterReference.educationServiceCenterId"
                        )
                    ),
                ],
                parameterization,
                8,
                ReservedParameterNames: ["documentUuid", "resourceKeyId", "schoolId"]
            )
        );

        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(
                "relationshipAuthorization_0_0_schoolId",
                "relationshipAuthorization_0_1_localEducationAgencyId",
                "relationshipAuthorization_1_0_schoolId",
                "relationshipAuthorization_2_0_educationServiceCenterId",
                "ClaimEducationOrganizationIds"
            );
        plan.ProposedValueParametersInOrder.Should()
            .BeEquivalentTo(
                new[]
                {
                    new RelationshipAuthorizationProposedValueSqlParameter(
                        0,
                        0,
                        "relationshipAuthorization_0_0_schoolId"
                    ),
                    new RelationshipAuthorizationProposedValueSqlParameter(
                        0,
                        1,
                        "relationshipAuthorization_0_1_localEducationAgencyId"
                    ),
                    new RelationshipAuthorizationProposedValueSqlParameter(
                        1,
                        0,
                        "relationshipAuthorization_1_0_schoolId"
                    ),
                    new RelationshipAuthorizationProposedValueSqlParameter(
                        2,
                        0,
                        "relationshipAuthorization_2_0_educationServiceCenterId"
                    ),
                },
                options => options.WithStrictOrdering()
            );
        plan.AuthorizationSql.Should().NotContain("FROM target");
        plan.AuthorizationSql.Should().Contain("\"dms\".\"throw_error\"('AUTH1'");
        plan.AuthorizationSql.Should().Contain("CONCAT('1|', '8|', COUNT(1)::text, '|'");
        plan.AuthorizationSql.Should()
            .Contain(
                "CASE WHEN CAST(@relationshipAuthorization_0_0_schoolId AS bigint) IS NULL THEN 'p' ELSE 'n' END"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "CASE WHEN CAST(@relationshipAuthorization_0_0_schoolId AS bigint) IS NULL OR NOT (CAST(@relationshipAuthorization_0_0_schoolId AS bigint) = ANY(@ClaimEducationOrganizationIds) OR EXISTS (SELECT 1 FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" a0_0 WHERE a0_0.\"TargetEducationOrganizationId\" = CAST(@relationshipAuthorization_0_0_schoolId AS bigint) AND a0_0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))) THEN 1 ELSE 0 END"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "\"TargetEducationOrganizationId\" = CAST(@relationshipAuthorization_0_0_schoolId AS bigint)"
            );
        plan.AuthorizationSql.Should()
            .Contain("\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)");
        plan.AuthorizationSql.Should()
            .Contain(
                "\"SourceEducationOrganizationId\" = CAST(@relationshipAuthorization_2_0_educationServiceCenterId AS bigint)"
            );
        plan.AuthorizationSql.Should()
            .Contain("\"TargetEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)");
        plan.AuthorizationSql.Should()
            .Contain(
                "CAST(@relationshipAuthorization_2_0_educationServiceCenterId AS bigint) = ANY(@ClaimEducationOrganizationIds)"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "NOT EXISTS (SELECT 1 FROM failed_subjects WHERE \"StrategyOrdinal\" = 0) OR NOT EXISTS (SELECT 1 FROM failed_subjects WHERE \"StrategyOrdinal\" = 1) OR NOT EXISTS (SELECT 1 FROM failed_subjects WHERE \"StrategyOrdinal\" = 2)"
            );
    }

    [Test]
    public void It_should_compile_sql_server_proposed_auth1_sql_with_collision_free_parameters()
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
                    CreateProposedCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId")
                    ),
                ],
                parameterization,
                9,
                ReservedParameterNames:
                [
                    "relationshipAuthorization_0_0_schoolId",
                    "documentUuid",
                    "resourceKeyId",
                ]
            )
        );

        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(
                "relationshipAuthorization_0_0_schoolId_2",
                "ClaimEducationOrganizationIds_0",
                "ClaimEducationOrganizationIds_1"
            );
        plan.ProposedValueParametersInOrder.Should()
            .ContainSingle()
            .Which.ParameterName.Should()
            .Be("relationshipAuthorization_0_0_schoolId_2");
        plan.AuthorizationSql.Should()
            .Contain("CAST(CONCAT('AUTH1 - ', (SELECT [Payload] FROM failure_payload)) AS INT)");
        plan.AuthorizationSql.Should()
            .Contain("CASE WHEN @relationshipAuthorization_0_0_schoolId_2 IS NULL THEN 'p' ELSE 'n' END");
        plan.AuthorizationSql.Should()
            .Contain(
                "CASE WHEN @relationshipAuthorization_0_0_schoolId_2 IS NULL OR NOT (@relationshipAuthorization_0_0_schoolId_2 IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1) OR EXISTS (SELECT 1 FROM [auth].[EducationOrganizationIdToEducationOrganizationId] a0_0 WHERE a0_0.[TargetEducationOrganizationId] = @relationshipAuthorization_0_0_schoolId_2 AND a0_0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1))) THEN 1 ELSE 0 END"
            );
        plan.AuthorizationSql.Should()
            .Contain("[TargetEducationOrganizationId] = @relationshipAuthorization_0_0_schoolId_2");
        plan.AuthorizationSql.Should()
            .Contain("IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1)");
        plan.AuthorizationSql.Should().NotContain("@DocumentId");
    }

    [Test]
    public void It_should_reserve_document_id_parameter_name_when_allocating_proposed_value_parameters()
    {
        const string candidateParameterName = "relationshipAuthorization_0_0_schoolId";
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateSingleSubjectProposedSqlSpec(parameterization) with
            {
                DocumentIdParameterName = candidateParameterName,
            }
        );

        AssertSingleProposedValueParameterName(plan, $"{candidateParameterName}_2");
    }

    [Test]
    public void It_should_reserve_claim_base_parameter_name_when_allocating_proposed_value_parameters()
    {
        const string candidateParameterName = "relationshipAuthorization_0_0_schoolId";
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            [100L, 200L],
            candidateParameterName
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(CreateSingleSubjectProposedSqlSpec(parameterization));

        AssertSingleProposedValueParameterName(plan, $"{candidateParameterName}_2");
    }

    [Test]
    public void It_should_reserve_claim_concrete_parameter_names_when_allocating_proposed_value_parameters()
    {
        const string claimBaseParameterName = "relationshipAuthorization_0_0_schoolId";
        const string candidateParameterName = $"{claimBaseParameterName}_0";
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            [100L],
            claimBaseParameterName
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            CreateSingleSubjectProposedSqlSpec(parameterization, parameterSeed: "schoolId_0")
        );

        AssertSingleProposedValueParameterName(plan, $"{candidateParameterName}_2");
    }

    [Test]
    public void It_should_advance_parameter_suffix_until_an_unused_name_is_found()
    {
        const string candidateParameterName = "relationshipAuthorization_0_0_schoolId";
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateSingleSubjectProposedSqlSpec(
                parameterization,
                reservedParameterNames: [candidateParameterName, $"{candidateParameterName}_2"]
            )
        );

        AssertSingleProposedValueParameterName(plan, $"{candidateParameterName}_3");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_proposed_auth1_sql_with_expected_ctes_payload_and_final_select(
        SqlDialect dialect
    )
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(dialect);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateProposedCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId")
                    ),
                    CreateProposedCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Inverted,
                        1,
                        1,
                        CreateSubject(
                            "LocalEducationAgencyId",
                            "$.localEducationAgencyReference.localEducationAgencyId"
                        )
                    ),
                ],
                parameterization,
                21
            )
        );

        var strategyOrdinal = QuoteIdentifier(dialect, "StrategyOrdinal");
        var subjectOrdinal = QuoteIdentifier(dialect, "SubjectOrdinal");
        var failureKind = QuoteIdentifier(dialect, "FailureKind");
        var failed = QuoteIdentifier(dialect, "Failed");
        var payload = QuoteIdentifier(dialect, "Payload");
        var authorizationResult = QuoteIdentifier(dialect, "AuthorizationResult");
        var targetEdOrgId = QuoteIdentifier(dialect, AuthNames.TargetEdOrgId.Value);
        var sourceEdOrgId = QuoteIdentifier(dialect, AuthNames.SourceEdOrgId.Value);
        var firstProposedValue = ProposedValueFragment(
            dialect,
            plan.ProposedValueParametersInOrder[0].ParameterName
        );
        var secondProposedValue = ProposedValueFragment(
            dialect,
            plan.ProposedValueParametersInOrder[1].ParameterName
        );
        var claimEdOrgFilter = ClaimEducationOrganizationIdFilterFragment(dialect);
        var edOrgAuthRelation = QuoteRelation(dialect, AuthNames.EdOrgIdToEdOrgId);
        var payloadSql =
            dialect is SqlDialect.Pgsql
                ? $"CONCAT('1|', '21|', COUNT(1)::text, '|', STRING_AGG(CONCAT({strategyOrdinal}, ':', {subjectOrdinal}, ':', {failureKind}), ',' ORDER BY {strategyOrdinal}, {subjectOrdinal}))"
                : $"CONCAT('1|', '21|', COUNT(1), '|', STRING_AGG(CONCAT({strategyOrdinal}, ':', {subjectOrdinal}, ':', {failureKind}), ',') WITHIN GROUP (ORDER BY {strategyOrdinal}, {subjectOrdinal}))";
        var authorizationSuccessSql =
            $"NOT EXISTS (SELECT 1 FROM failed_subjects WHERE {strategyOrdinal} = 0) OR NOT EXISTS (SELECT 1 FROM failed_subjects WHERE {strategyOrdinal} = 1)";
        var auth1AbortSql =
            dialect is SqlDialect.Pgsql
                ? $"{QuoteIdentifier(dialect, "dms")}.{QuoteIdentifier(dialect, "throw_error")}('{RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode}', (SELECT {payload} FROM failure_payload))"
                : $"CAST(CONCAT('{RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode} - ', (SELECT {payload} FROM failure_payload)) AS INT)";

        plan.AuthorizationSql.Should()
            .StartWith(
                string.Join(
                    '\n',
                    "WITH",
                    $"subject_failures ({strategyOrdinal}, {subjectOrdinal}, {failureKind}, {failed}) AS ("
                )
            );
        plan.AuthorizationSql.Should()
            .Contain(
                string.Join(
                    '\n',
                    $"subject_failures ({strategyOrdinal}, {subjectOrdinal}, {failureKind}, {failed}) AS (",
                    "    SELECT",
                    "        0,",
                    "        0,",
                    $"        CASE WHEN {firstProposedValue} IS NULL THEN 'p' ELSE 'n' END,",
                    $"        CASE WHEN {firstProposedValue} IS NULL OR NOT ({firstProposedValue}{claimEdOrgFilter} OR EXISTS (SELECT 1 FROM {edOrgAuthRelation} a0_0 WHERE a0_0.{targetEdOrgId} = {firstProposedValue} AND a0_0.{sourceEdOrgId}{claimEdOrgFilter})) THEN 1 ELSE 0 END",
                    "",
                    "    UNION ALL",
                    "    SELECT",
                    "        1,",
                    "        0,",
                    $"        CASE WHEN {secondProposedValue} IS NULL THEN 'p' ELSE 'n' END,",
                    $"        CASE WHEN {secondProposedValue} IS NULL OR NOT ({secondProposedValue}{claimEdOrgFilter} OR EXISTS (SELECT 1 FROM {edOrgAuthRelation} a1_0 WHERE a1_0.{sourceEdOrgId} = {secondProposedValue} AND a1_0.{targetEdOrgId}{claimEdOrgFilter})) THEN 1 ELSE 0 END",
                    "",
                    "),"
                )
            );
        plan.AuthorizationSql.Should()
            .Contain(
                string.Join(
                    '\n',
                    "),",
                    "failed_subjects AS (",
                    "    SELECT * FROM subject_failures",
                    $"    WHERE {failed} = 1",
                    "),",
                    "failure_payload AS (",
                    $"    SELECT {payloadSql} AS {payload}",
                    "    FROM failed_subjects",
                    ")",
                    "SELECT CASE"
                )
            );
        plan.AuthorizationSql.Should()
            .EndWith(
                string.Join(
                    '\n',
                    "SELECT CASE",
                    $"    WHEN {authorizationSuccessSql} THEN 1",
                    $"    ELSE {auth1AbortSql}",
                    $"END AS {authorizationResult}",
                    ";"
                ) + "\n"
            );
        plan.AuthorizationSql.Should().NotContain(QuoteIdentifier(dialect, "ContentVersion"));
    }

    [Test]
    public void It_should_reject_postgresql_proposed_auth1_sql_for_non_edorg_hierarchy_auth_object()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var customAuthObject = new RelationshipAuthorizationAuthObject(
            new DbTableName(AuthNames.AuthSchema, "StudentAuthorization"),
            new DbColumnName("StudentUniqueId"),
            AuthNames.SourceEdOrgId
        );
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("StudentUniqueId", "$.studentReference.studentUniqueId")
        );

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        checkSpec with
                        {
                            Subjects = [checkSpec.Subjects[0] with { AuthObject = customAuthObject }],
                        },
                    ],
                    parameterization,
                    12
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*supports only EdOrg hierarchy or People relationship checks*auth.StudentAuthorization*"
            );
    }

    [Test]
    public void It_should_reject_mixed_subject_auth_objects_at_the_single_record_execution_boundary()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var studentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var checkSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId"),
            CreateSubject("Student_DocumentId", "$.studentReference.studentUniqueId")
        );

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        checkSpec with
                        {
                            Subjects =
                            [
                                checkSpec.Subjects[0],
                                checkSpec.Subjects[1] with
                                {
                                    AuthObject = studentAuthObject,
                                    Contributors =
                                    [
                                        new RelationshipAuthorizationSubjectContributor(
                                            SecurableElementKind.Student,
                                            "$.studentReference.studentUniqueId",
                                            "StudentUniqueId"
                                        ),
                                    ],
                                },
                            ],
                        },
                    ],
                    parameterization,
                    13
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*Single-record relationship authorization SQL supports only EdOrg hierarchy or People relationship checks*auth.EducationOrganizationIdToStudentDocumentId*"
            );
    }

    [Test]
    public void It_should_not_emit_direct_claim_match_when_the_auth_object_disallows_it()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                [
                    CreateStoredHierarchyOnlyCheckSpec(
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        0,
                        0,
                        CreateSubject("SchoolId", "$.schoolReference.schoolId")
                    ),
                ],
                parameterization,
                11
            )
        );

        plan.AuthorizationSql.Should()
            .Contain(
                "CASE WHEN target.\"SchoolId\" IS NULL OR NOT EXISTS (SELECT 1 FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" a0_0 WHERE a0_0.\"TargetEducationOrganizationId\" = target.\"SchoolId\" AND a0_0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)) THEN 1 ELSE 0 END"
            );
        plan.AuthorizationSql.Should()
            .NotContain("target.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds)");
    }

    [Test]
    public void It_should_allow_zero_emitted_auth1_index()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

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
                0
            )
        );

        plan.AuthorizationSql.Should().Contain("CONCAT('1|', '0|', COUNT(1)::text, '|'");
    }

    [Test]
    public void It_should_reject_negative_emitted_auth1_indexes()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(
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
                    -1
                )
            );

        compile
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("spec")
            .WithMessage("Emitted AUTH1 index cannot be negative.*");
    }

    [Test]
    public void It_should_reject_invalid_document_id_parameter_names()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(
                CreateSingleSubjectStoredSqlSpec(parameterization) with
                {
                    DocumentIdParameterName = "Document-Id",
                }
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("DocumentIdParameterName")
            .WithMessage("Parameter name must match pattern*");
    }

    [Test]
    public void It_should_reject_invalid_reserved_parameter_names_for_stored_specs()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(
                CreateSingleSubjectStoredSqlSpec(parameterization) with
                {
                    ReservedParameterNames = ["reserved-name"],
                }
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("ReservedParameterNames")
            .WithMessage("Parameter name must match pattern*");
    }

    [Test]
    public void It_should_reject_empty_check_spec_batches()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(new SingleRecordRelationshipAuthorizationSqlSpec([], parameterization, 1));

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("Single-record relationship authorization requires at least one check spec.*");
    }

    [Test]
    public void It_should_reject_batches_with_any_empty_subject_list()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateStoredCheckSpec(
                            RelationshipAuthorizationHierarchyDirection.Normal,
                            0,
                            0,
                            CreateSubject("SchoolId", "$.schoolReference.schoolId")
                        ),
                        CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal, 1, 1),
                    ],
                    parameterization,
                    1
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage(
                "Single-record relationship authorization check specs require at least one subject.*"
            );
    }

    [Test]
    public void It_should_reject_subjects_outside_the_single_record_root_table()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var localEducationAgencyTable = new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency");

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateStoredCheckSpec(
                            RelationshipAuthorizationHierarchyDirection.Normal,
                            0,
                            0,
                            CreateSubject(
                                "LocalEducationAgencyId",
                                "$.localEducationAgencyReference.localEducationAgencyId",
                                localEducationAgencyTable,
                                "LocalEducationAgency"
                            )
                        ),
                    ],
                    parameterization,
                    1
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("checkSpecs")
            .WithMessage("*does not match root table*");
    }

    [Test]
    public void It_should_reject_claim_parameter_names_that_collide_with_document_id()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "DocumentId"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () => compiler.Compile(CreateSingleSubjectStoredSqlSpec(parameterization));

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage(
                "DocumentId parameter name must not collide with claim EdOrg authorization parameter names.*"
            );
    }

    [Test]
    public void It_should_reject_stored_value_source_mismatches_even_when_targets_are_stored()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var storedCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        storedCheckSpec,
                        storedCheckSpec with
                        {
                            ValueSource = RelationshipAuthorizationValueSource.Proposed,
                        },
                    ],
                    parameterization,
                    1
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("*must be all stored-value or all proposed-value*");
    }

    [Test]
    public void It_should_reject_proposed_value_source_mismatches_even_when_targets_are_proposed()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var proposedCheckSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        proposedCheckSpec,
                        proposedCheckSpec with
                        {
                            ValueSource = RelationshipAuthorizationValueSource.Stored,
                        },
                    ],
                    parameterization,
                    1
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("*must be all stored-value or all proposed-value*");
    }

    [Test]
    public void It_should_reject_stored_check_specs_that_do_not_share_one_root_target()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var storedCheckSpec = CreateStoredCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        storedCheckSpec,
                        storedCheckSpec with
                        {
                            CheckTarget = new RelationshipAuthorizationCheckTarget.Stored(
                                new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency"),
                                new DbColumnName("DocumentId")
                            ),
                        },
                    ],
                    parameterization,
                    1
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Single-record relationship authorization stored check specs must share one root target.*"
            );
    }

    [Test]
    public void It_should_reject_proposed_check_specs_that_do_not_share_one_root_target()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var schoolCheckSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        );
        var localEducationAgencyTable = new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency");
        var localEducationAgencyId = new DbColumnName("LocalEducationAgencyId");
        var localEducationAgencyCheckSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            1,
            1,
            CreateSubject(
                localEducationAgencyId.Value,
                "$.localEducationAgencyReference.localEducationAgencyId",
                localEducationAgencyTable,
                "LocalEducationAgency"
            )
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(
                localEducationAgencyTable,
                [CreateProposedBinding(localEducationAgencyTable, localEducationAgencyId)]
            ),
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [schoolCheckSpec, localEducationAgencyCheckSpec],
                    parameterization,
                    1
                )
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage(
                "Single-record relationship authorization proposed check specs must share one root target.*"
            );
    }

    [Test]
    public void It_should_reject_proposed_check_specs_when_binding_count_does_not_match_subject_count()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(rootTable, []),
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec([checkSpec], parameterization, 1)
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("*has 1 subjects but 0 root bindings*");
    }

    [Test]
    public void It_should_reject_proposed_bindings_that_target_a_different_table_than_the_root()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var localEducationAgencyTable = new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency");
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(
                rootTable,
                [CreateProposedBinding(localEducationAgencyTable, new DbColumnName("SchoolId"))]
            ),
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec([checkSpec], parameterization, 1)
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("*targets table*root target*");
    }

    [Test]
    public void It_should_reject_proposed_bindings_that_do_not_match_the_subject_table()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var localEducationAgencyTable = new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency");
        var localEducationAgencyId = new DbColumnName("LocalEducationAgencyId");
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject(
                localEducationAgencyId.Value,
                "$.localEducationAgencyReference.localEducationAgencyId",
                localEducationAgencyTable,
                "LocalEducationAgency"
            )
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(
                rootTable,
                [CreateProposedBinding(rootTable, localEducationAgencyId)]
            ),
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec([checkSpec], parameterization, 1)
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("*subject proposed anchor targets table*");
    }

    [Test]
    public void It_should_reject_proposed_bindings_that_do_not_match_the_subject_column()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(
                rootTable,
                [CreateProposedBinding(rootTable, new DbColumnName("LocalEducationAgencyId"))]
            ),
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec([checkSpec], parameterization, 1)
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("spec")
            .WithMessage("*subject proposed anchor targets column*");
    }

    [Test]
    public void It_should_reject_blank_proposed_binding_parameter_seeds()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var binding = CreateProposedBinding(rootTable, new DbColumnName("SchoolId")) with
        {
            ParameterSeed = " ",
        };
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(rootTable, [binding]),
        };

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec([checkSpec], parameterization, 1)
            );

        compile.Should().Throw<ArgumentException>().WithParameterName("binding.ParameterSeed");
    }

    [Test]
    public void It_should_reject_invalid_proposed_binding_parameter_seeds()
    {
        var parameterization = CreateSingleClaimParameterization();
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(
                CreateSingleSubjectProposedSqlSpec(parameterization, parameterSeed: "school-id")
            );

        compile
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("parameterName")
            .WithMessage("Parameter name must match pattern*");
    }

    [Test]
    public void It_should_reject_postgresql_array_claim_parameterization_without_exactly_the_base_parameter()
    {
        var parameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
            "ClaimEducationOrganizationIds",
            [100L],
            ["ClaimEducationOrganizationIds", "ClaimEducationOrganizationIds_1"]
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () => compiler.Compile(CreateSingleSubjectStoredSqlSpec(parameterization));

        compile.Should().Throw<ArgumentException>().WithMessage("*exactly the base parameter name*");
    }

    [Test]
    public void It_should_reject_sql_server_structured_claim_parameterization_without_exactly_the_base_parameter()
    {
        var parameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured,
            "ClaimEducationOrganizationIds",
            [100L],
            ["ClaimEducationOrganizationIds_0"]
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var compile = () => compiler.Compile(CreateSingleSubjectStoredSqlSpec(parameterization));

        compile.Should().Throw<ArgumentException>().WithMessage("*exactly the base parameter name*");
    }

    [Test]
    public void It_should_reject_sql_server_scalar_claim_parameterization_without_one_parameter_per_claim()
    {
        var parameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
            "ClaimEducationOrganizationIds",
            [100L, 200L],
            ["ClaimEducationOrganizationIds_0"]
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var compile = () => compiler.Compile(CreateSingleSubjectStoredSqlSpec(parameterization));

        compile.Should().Throw<ArgumentException>().WithMessage("*one parameter name per claim EdOrg id*");
    }

    [Test]
    public void It_should_reject_mixed_stored_and_proposed_check_batches()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [100L],
            "ClaimEducationOrganizationIds"
        );
        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var compile = () =>
            compiler.Compile(
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    [
                        CreateStoredCheckSpec(
                            RelationshipAuthorizationHierarchyDirection.Normal,
                            0,
                            0,
                            CreateSubject("SchoolId", "$.schoolReference.schoolId")
                        ),
                        CreateProposedCheckSpec(
                            RelationshipAuthorizationHierarchyDirection.Normal,
                            1,
                            1,
                            CreateSubject(
                                "LocalEducationAgencyId",
                                "$.localEducationAgencyReference.localEducationAgencyId"
                            )
                        ),
                    ],
                    parameterization,
                    10
                )
            );

        compile.Should().Throw<ArgumentException>().WithMessage("*all stored-value or all proposed-value*");
    }

    private static SingleRecordRelationshipAuthorizationSqlSpec CreateSingleSubjectStoredSqlSpec(
        AuthorizationClaimEducationOrganizationIdParameterization parameterization
    ) =>
        new(
            [
                CreateStoredCheckSpec(
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    0,
                    0,
                    CreateSubject("SchoolId", "$.schoolReference.schoolId")
                ),
            ],
            parameterization,
            10
        );

    private static SingleRecordRelationshipAuthorizationSqlSpec CreateSingleSubjectProposedSqlSpec(
        AuthorizationClaimEducationOrganizationIdParameterization parameterization,
        string parameterSeed = "schoolId",
        IReadOnlyList<string>? reservedParameterNames = null
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var binding = CreateProposedBinding(rootTable, schoolIdColumn) with { ParameterSeed = parameterSeed };
        var checkSpec = CreateProposedCheckSpec(
            RelationshipAuthorizationHierarchyDirection.Normal,
            0,
            0,
            CreateSubject("SchoolId", "$.schoolReference.schoolId")
        ) with
        {
            CheckTarget = new RelationshipAuthorizationCheckTarget.Proposed(rootTable, [binding]),
        };

        return new SingleRecordRelationshipAuthorizationSqlSpec(
            [checkSpec],
            parameterization,
            10,
            ReservedParameterNames: reservedParameterNames
        );
    }

    private static void AssertSingleProposedValueParameterName(
        SingleRecordRelationshipAuthorizationSqlPlan plan,
        string expectedParameterName
    )
    {
        plan.ProposedValueParametersInOrder.Should()
            .ContainSingle()
            .Which.ParameterName.Should()
            .Be(expectedParameterName);
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .StartWith(expectedParameterName);
    }

    private static AuthorizationClaimEducationOrganizationIdParameterization CreateSingleClaimParameterization(
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [100L],
            "ClaimEducationOrganizationIds"
        );

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
            StampNonPersonSubjects(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
                subjects
            ),
            new RelationshipAuthorizationCheckTarget.Stored(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId")
            )
        );

    private static RelationshipAuthorizationCheckSpec CreateStoredHierarchyOnlyCheckSpec(
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
            StampNonPersonSubjects(CreateHierarchyOnlyAuthObject(direction), subjects),
            new RelationshipAuthorizationCheckTarget.Stored(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId")
            )
        );

    private static RelationshipAuthorizationAuthObject CreateHierarchyOnlyAuthObject(
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        direction switch
        {
            RelationshipAuthorizationHierarchyDirection.Normal => new RelationshipAuthorizationAuthObject(
                AuthNames.EdOrgIdToEdOrgId,
                AuthNames.TargetEdOrgId,
                AuthNames.SourceEdOrgId
            ),
            RelationshipAuthorizationHierarchyDirection.Inverted => new RelationshipAuthorizationAuthObject(
                AuthNames.EdOrgIdToEdOrgId,
                AuthNames.SourceEdOrgId,
                AuthNames.TargetEdOrgId
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unsupported relationship authorization hierarchy direction."
            ),
        };

    private static RelationshipAuthorizationCheckSpec CreateProposedCheckSpec(
        RelationshipAuthorizationHierarchyDirection direction,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        params RelationshipAuthorizationSubject[] subjects
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                direction is RelationshipAuthorizationHierarchyDirection.Normal
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                configuredStrategyIndex
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Proposed,
            StampNonPersonSubjects(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
                subjects
            ),
            new RelationshipAuthorizationCheckTarget.Proposed(
                rootTable,
                [
                    .. subjects.Select(
                        static (subject, bindingIndex) =>
                            new RelationshipAuthorizationProposedValueBinding(
                                subject.Table,
                                subject.Column,
                                bindingIndex,
                                subject.Column.Value,
                                PlanNamingConventions.CamelCaseFirstCharacter(subject.Column.Value)
                            )
                    ),
                ]
            )
        );
    }

    private static RelationshipAuthorizationSubject CreateSubject(
        string columnName,
        string jsonPath,
        DbTableName? table = null,
        string resourceName = "School"
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resourceName),
            table ?? new DbTableName(new DbSchemaName("edfi"), "School"),
            new DbColumnName(columnName),
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                RelationshipAuthorizationHierarchyDirection.Normal
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    jsonPath,
                    columnName
                ),
            ]
        );

    private static RelationshipAuthorizationCheckSpec CreateStoredPeopleCheckSpec(
        string strategyName,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        DbTableName rootTable,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, configuredStrategyIndex),
            relationshipLocalOrder,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Stored,
            subjects,
            new RelationshipAuthorizationCheckTarget.Stored(rootTable, new DbColumnName("DocumentId"))
        );

    private static RelationshipAuthorizationCheckSpec CreateProposedPeopleCheckSpec(
        string strategyName,
        int configuredStrategyIndex,
        int relationshipLocalOrder,
        DbTableName rootTable,
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonProposedAnchorKind proposedAnchorKind,
        RelationshipAuthorizationProposedValueBinding proposedBinding
    )
    {
        if (subject.PersonMetadata is not { } personMetadata)
        {
            throw new InvalidOperationException("Proposed People check specs require person metadata.");
        }

        var proposedSubject = subject with
        {
            PersonMetadata = personMetadata with
            {
                ProposedAnchor = new RelationshipAuthorizationPersonProposedAnchor(
                    proposedAnchorKind,
                    proposedBinding
                ),
            },
        };

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(strategyName, configuredStrategyIndex),
            relationshipLocalOrder,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [proposedSubject],
            new RelationshipAuthorizationCheckTarget.Proposed(rootTable, [proposedBinding])
        );
    }

    private static RelationshipAuthorizationProposedValueBinding CreateProposedBinding(
        DbTableName table,
        DbColumnName column
    ) =>
        new(
            table,
            column,
            BindingIndex: 0,
            LogicalKey: column.Value,
            ParameterSeed: PlanNamingConventions.CamelCaseFirstCharacter(column.Value)
        );

    private static RelationshipAuthorizationSubject CreateDirectPersonSubject(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbColumnName rootPersonDocumentIdColumn,
        DbTableName? rootTable = null,
        string resourceName = "School"
    )
    {
        var resolvedRootTable = rootTable ?? new DbTableName(new DbSchemaName("edfi"), "School");
        var documentIdColumn = new DbColumnName("DocumentId");

        return new RelationshipAuthorizationSubject(
            new QualifiedResourceName("Ed-Fi", resourceName),
            resolvedRootTable,
            rootPersonDocumentIdColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(authViewKind),
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapPersonKind(personKind),
                    $"$.{personKind.ToString().ToLowerInvariant()}Reference.{personKind.ToString().ToLowerInvariant()}UniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                    [
                        new ColumnPathStep(
                            resolvedRootTable,
                            rootPersonDocumentIdColumn,
                            new DbTableName(new DbSchemaName("edfi"), personKind.ToString()),
                            documentIdColumn
                        ),
                    ]
                ),
                new RelationshipAuthorizationPersonStoredAnchor(resolvedRootTable, documentIdColumn),
                ProposedAnchor: null
            )
        );
    }

    private static RelationshipAuthorizationSubject CreateSelfPersonSubject(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbTableName rootTable
    )
    {
        var documentIdColumn = new DbColumnName("DocumentId");

        return new RelationshipAuthorizationSubject(
            new QualifiedResourceName("Ed-Fi", personKind.ToString()),
            rootTable,
            documentIdColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(authViewKind),
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapPersonKind(personKind),
                    $"$.{personKind.ToString().ToLowerInvariant()}UniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                    []
                ),
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, documentIdColumn),
                ProposedAnchor: null
            )
        );
    }

    private static RelationshipAuthorizationSubject CreateTransitivePersonSubject(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbTableName rootTable,
        IReadOnlyList<ColumnPathStep> pathSteps
    )
    {
        var documentIdColumn = new DbColumnName("DocumentId");
        var terminalStep = pathSteps[^1];

        return new RelationshipAuthorizationSubject(
            new QualifiedResourceName("Ed-Fi", rootTable.Name),
            terminalStep.SourceTable,
            terminalStep.SourceColumnName,
            RelationshipAuthorizationAuthObject.CreatePerson(authViewKind),
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapPersonKind(personKind),
                    $"$.{personKind.ToString().ToLowerInvariant()}Reference.{personKind.ToString().ToLowerInvariant()}UniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                    pathSteps
                ),
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, documentIdColumn),
                ProposedAnchor: null
            )
        );
    }

    private static SecurableElementKind MapPersonKind(RelationshipAuthorizationPersonKind personKind) =>
        personKind switch
        {
            RelationshipAuthorizationPersonKind.Student => SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Contact => SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Staff => SecurableElementKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(personKind),
                personKind,
                "Unsupported person kind."
            ),
        };

    private static string QuoteIdentifier(SqlDialect dialect, string identifier) =>
        dialect is SqlDialect.Pgsql ? $"\"{identifier}\"" : $"[{identifier}]";

    private static string QuoteRelation(SqlDialect dialect, DbTableName table) =>
        $"{QuoteIdentifier(dialect, table.Schema.Value)}.{QuoteIdentifier(dialect, table.Name)}";

    private static string ClaimEducationOrganizationIdFilterFragment(SqlDialect dialect) =>
        dialect is SqlDialect.Pgsql
            ? " = ANY(@ClaimEducationOrganizationIds)"
            : " IN (@ClaimEducationOrganizationIds_0)";

    private static string ProposedValueFragment(SqlDialect dialect, string parameterName) =>
        dialect is SqlDialect.Pgsql ? $"CAST(@{parameterName} AS bigint)" : $"@{parameterName}";

    private static int CountOccurrences(string value, string searchValue)
    {
        var count = 0;
        var searchIndex = 0;

        while ((searchIndex = value.IndexOf(searchValue, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += searchValue.Length;
        }

        return count;
    }

    private static RelationshipAuthorizationSubject[] StampNonPersonSubjects(
        RelationshipAuthorizationAuthObject authObject,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects
    ) =>
        [
            .. subjects.Select(subject =>
                subject.IsPersonSubject ? subject : subject with { AuthObject = authObject }
            ),
        ];
}
