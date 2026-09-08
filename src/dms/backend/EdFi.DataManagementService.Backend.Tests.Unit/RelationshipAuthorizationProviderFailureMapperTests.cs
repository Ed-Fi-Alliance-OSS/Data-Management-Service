// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationProviderFailureMapper
{
    [Test]
    public void It_maps_people_auth1_provider_failures_to_relationship_failures()
    {
        var payloadText = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                12,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Pgsql,
            new StubDbException("provider exception"),
            new StubRelationshipAuthorizationProviderFailureExtractor(
                RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            ),
            expectedEmittedAuth1Index: 12,
            [CreateStoredPeopleCheckSpec()],
            [300L, 100L, 300L],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeTrue();
        invalidFailureDiagnostic.Should().BeNull();
        relationshipFailure.Should().NotBeNull();
        relationshipFailure!.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Stored);
        relationshipFailure.EmittedAuth1Index.Should().Be(12);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(100L, 300L);

        var failedSubject = relationshipFailure
            .FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.PathKind.Should().Be("DirectRootColumn");
    }

    [Test]
    public void It_ignores_provider_failures_that_are_not_relationship_authorization_failures()
    {
        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Pgsql,
            new StubDbException("unique constraint failure"),
            new StubRelationshipAuthorizationProviderFailureExtractor("23505", "duplicate key"),
            expectedEmittedAuth1Index: 12,
            [CreateStoredPeopleCheckSpec()],
            [100L],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
        invalidFailureDiagnostic.Should().BeNull();
    }

    [Test]
    public void It_reports_payload_parse_failures_for_malformed_auth1_provider_payloads()
    {
        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Mssql,
            new StubDbException("conversion failure"),
            new StubRelationshipAuthorizationProviderFailureExtractor(null, "AUTH1 - 2|12|1|0:0:n"),
            expectedEmittedAuth1Index: 12,
            [CreateStoredPeopleCheckSpec()],
            [100L],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
        invalidFailureDiagnostic.Should().NotBeNull();
        invalidFailureDiagnostic!.Dialect.Should().Be(SqlDialect.Mssql);
        invalidFailureDiagnostic.ExpectedEmittedAuth1Index.Should().Be(12);
        invalidFailureDiagnostic.ProviderErrorCode.Should().Be("none");
        invalidFailureDiagnostic.ProviderMessage.Should().Be("AUTH1 - 2|12|1|0:0:n");
        invalidFailureDiagnostic
            .MappingFailureCategory.Should()
            .Be(RelationshipAuthorizationProviderFailureMappingCategory.PayloadParseFailed);
    }

    [Test]
    public void It_reports_unexpected_emitted_auth1_indexes()
    {
        var payloadText = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                13,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Pgsql,
            new StubDbException("provider exception"),
            new StubRelationshipAuthorizationProviderFailureExtractor(
                RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            ),
            expectedEmittedAuth1Index: 12,
            [CreateStoredPeopleCheckSpec()],
            [100L],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
        invalidFailureDiagnostic.Should().NotBeNull();
        invalidFailureDiagnostic!
            .MappingFailureCategory.Should()
            .Be(RelationshipAuthorizationProviderFailureMappingCategory.EmittedAuth1IndexMismatch);
    }

    [Test]
    public void It_reports_payload_mapping_failures_for_unmappable_people_ordinals()
    {
        var payloadText = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                12,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        1,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Pgsql,
            new StubDbException("provider exception"),
            new StubRelationshipAuthorizationProviderFailureExtractor(
                RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            ),
            expectedEmittedAuth1Index: 12,
            [CreateStoredPeopleCheckSpec()],
            [100L],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
        invalidFailureDiagnostic.Should().NotBeNull();
        invalidFailureDiagnostic!
            .MappingFailureCategory.Should()
            .Be(RelationshipAuthorizationProviderFailureMappingCategory.PayloadMappingFailed);
    }

    private static RelationshipAuthorizationCheckSpec CreateStoredPeopleCheckSpec()
    {
        var subject = new RelationshipAuthorizationSubject(
            SchoolResource,
            RootTable,
            AuthNames.StudentDocumentId,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                    [
                        new ColumnPathStep(
                            RootTable,
                            AuthNames.StudentDocumentId,
                            StudentTable,
                            DocumentIdColumn
                        ),
                    ]
                ),
                new RelationshipAuthorizationPersonStoredAnchor(RootTable, DocumentIdColumn),
                ProposedAnchor: null
            )
        );

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RawConfiguredIndex: 0
            ),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Stored,
            [subject],
            new RelationshipAuthorizationCheckTarget.Stored(RootTable, DocumentIdColumn)
        );
    }

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "School");
    private static readonly DbTableName StudentTable = new(new DbSchemaName("edfi"), "Student");

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class StubRelationshipAuthorizationProviderFailureExtractor(
        string? providerErrorCode,
        string providerMessage
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new RelationshipAuthorizationProviderFailure(providerErrorCode, providerMessage);
        }
    }
}
