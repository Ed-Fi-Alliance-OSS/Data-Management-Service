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

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
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

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
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

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
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
        failedStrategy.FailedSubjects.Should().ContainSingle();

        var failedSubject = failedStrategy.FailedSubjects[0];
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.StoredValueNull);
        failedSubject.RootBinding.ColumnName.Should().Be("Student_DocumentId");
        failedSubject.SecurableElements.Should().ContainSingle();
        failedSubject.SecurableElements[0].Kind.Should().Be("Student");
        failedSubject.SecurableElements[0].JsonPath.Should().Be("$.studentReference.studentUniqueId");
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
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

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
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

        failedSubject
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoClaimEducationOrganizationIds);
        failedSubject.Hint.Should().Be(authObject.FailureHint);
        failedSubject
            .AuthObject.Name.Should()
            .Be("auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility");
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.Hint.Should().Be(authObject.FailureHint);
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

        var mapped = RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
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
        transitiveFailedSubject.RootBinding.TableName.Should().Be("edfi.StudentSchoolAssociation");
        transitiveFailedSubject.RootBinding.ColumnName.Should().Be("Student_DocumentId");

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

    private static RelationshipAuthorizationSubject[] StampNonPersonSubjects(
        RelationshipAuthorizationAuthObject authObject,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects
    ) =>
        [
            .. subjects.Select(subject =>
                subject.IsPersonSubject ? subject : subject with { AuthObject = authObject }
            ),
        ];

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
            .WithMessage("*only supports the EdOrg hierarchy auth object*");
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
                "*Single-record relationship authorization SQL only supports the EdOrg hierarchy auth object*auth.EducationOrganizationIdToStudentDocumentId*"
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
