// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_SingleRecordRelationshipAuthorizationExecutor
{
    [Test]
    public async Task It_returns_the_authorized_content_version_and_binds_postgresql_claims_as_one_array()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        CreateRow(("AuthorizationResult", 1), ("ContentVersion", 91L))
                    ),
                ]),
            ]
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 345L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [200L, 100L, 100L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 0
            )
        );

        result
            .Should()
            .BeEquivalentTo(new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L));
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain("\"dms\".\"Document\"");
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@DocumentId", "@ClaimEducationOrganizationIds");
        commandExecutor.Commands[0].Parameters[0].Value.Should().Be(345L);
        commandExecutor
            .Commands[0]
            .Parameters[1]
            .Value.Should()
            .BeAssignableTo<long[]>()
            .Which.Should()
            .Equal(100L, 200L);
    }

    [Test]
    public async Task It_returns_stale_target_when_authorization_reads_no_target_row()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])]
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 345L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [100L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 0
            )
        );

        result.Should().BeOfType<SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget>();
    }

    [Test]
    public async Task It_binds_sql_server_large_claim_sets_as_structured_parameters()
    {
        var claimEducationOrganizationIds = Enumerable.Range(1, 2000).Select(static id => (long)id).ToArray();
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Mssql,
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        CreateRow(("AuthorizationResult", 1), ("ContentVersion", 92L))
                    ),
                ]),
            ]
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(
            commandExecutor,
            new MssqlRelationalParameterConfigurator()
        );

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Mssql),
                DocumentId: 346L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Inverted)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    claimEducationOrganizationIds,
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 0
            )
        );

        result
            .Should()
            .BeEquivalentTo(new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(92L));
        commandExecutor.Commands.Should().ContainSingle();
        var claimParameter = commandExecutor.Commands[0].Parameters[1];
        claimParameter.Name.Should().Be("@ClaimEducationOrganizationIds");
        claimParameter.Value.Should().BeOfType<DataTable>();

        var claimTable = (DataTable)claimParameter.Value!;
        claimTable.Columns.Should().ContainSingle();
        claimTable.Columns[0].ColumnName.Should().Be("Id");
        claimTable.Columns[0].DataType.Should().Be(typeof(long));
        claimTable.Rows.Should().HaveCount(2000);
        claimTable.Rows[0].ItemArray.Should().Equal(1L);
        claimTable.Rows[^1].ItemArray.Should().Equal(2000L);

        var sqlParameter = new SqlParameter();
        claimParameter.ConfigureParameter.Should().NotBeNull();
        claimParameter.ConfigureParameter!(sqlParameter);
        sqlParameter.SqlDbType.Should().Be(SqlDbType.Structured);
        sqlParameter.TypeName.Should().Be("dms.BigIntTable");
    }

    [Test]
    public async Task It_maps_postgresql_auth1_failures_to_relationship_denials()
    {
        var payloadText = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                3,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(
            commandExecutor,
            providerFailureExtractor: new StubRelationshipAuthorizationProviderFailureExtractor(
                RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            )
        );

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 347L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [300L, 100L, 300L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 3
            )
        );

        result.Should().BeOfType<SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized>();
        var relationshipFailure = result
            .As<SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized>()
            .RelationshipFailure;
        relationshipFailure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Stored);
        relationshipFailure.EmittedAuth1Index.Should().Be(3);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(100L, 300L);
        relationshipFailure.FailedStrategies.Should().ContainSingle();
        relationshipFailure.FailedStrategies[0].FailedSubjects.Should().ContainSingle();
        relationshipFailure
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
    }

    [Test]
    public async Task It_fails_closed_when_auth1_provider_payload_uses_an_unexpected_emitted_check_index()
    {
        var payloadText = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                5,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var logger = new RecordingLogger<SingleRecordRelationshipAuthorizationExecutor>();
        var sut = new SingleRecordRelationshipAuthorizationExecutor(
            commandExecutor,
            providerFailureExtractor: new StubRelationshipAuthorizationProviderFailureExtractor(
                RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            ),
            logger: logger
        );

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 348L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [100L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 4
            )
        );

        result
            .Should()
            .BeOfType<SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.FailureMessage.Should()
            .Be(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );

        var logRecord = logger.Records.Should().ContainSingle().Subject;
        logRecord.Level.Should().Be(LogLevel.Error);
        logRecord.Message.Should().Contain("Dialect: Pgsql");
        logRecord.Message.Should().Contain("ExpectedEmittedAuth1Index: 4");
        logRecord.Message.Should().Contain("ProviderErrorCode: AUTH1");
        logRecord.Message.Should().Contain("ProviderMessageFragment: 1|5|1|0:0:n");
        logRecord.Message.Should().Contain("MappingFailureCategory: EmittedAuth1IndexMismatch");
    }

    [Test]
    public async Task It_fails_closed_when_auth1_provider_payload_cannot_be_mapped()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Mssql,
            exceptionToThrow: new StubDbException(
                "Conversion failed when converting the varchar value 'AUTH1 - 1|4|2|0:0:n' to data type int."
            )
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Mssql),
                DocumentId: 348L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    [100L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 4
            )
        );

        result
            .Should()
            .BeOfType<SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.FailureMessage.Should()
            .Be(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );
    }

    [Test]
    public async Task It_fails_closed_when_auth1_provider_payload_is_missing()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(
            commandExecutor,
            providerFailureExtractor: new StubRelationshipAuthorizationProviderFailureExtractor(
                RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                string.Empty
            )
        );

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 349L,
                [CreateStoredCheckSpec(RelationshipAuthorizationHierarchyDirection.Normal)],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [100L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 4
            )
        );

        result
            .Should()
            .BeOfType<SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.FailureMessage.Should()
            .Be(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );
    }

    [Test]
    public async Task It_executes_direct_people_relationship_specs()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        CreateRow(("AuthorizationResult", 1), ("ContentVersion", 93L))
                    ),
                ]),
            ]
        );
        var sut = new SingleRecordRelationshipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 349L,
                [CreatePeopleStoredCheckSpec()],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [100L],
                    "ClaimEducationOrganizationIds"
                ),
                EmittedAuth1Index: 0
            )
        );

        result
            .Should()
            .BeEquivalentTo(new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(93L));
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor
            .Commands[0]
            .CommandText.Should()
            .Contain("EducationOrganizationIdToStudentDocumentId");
    }

    [Test]
    public async Task It_rejects_transitive_people_proposed_specs_at_current_proposed_binding_validation()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sut = new SingleRecordRelationshipAuthorizationExecutor(commandExecutor);

        Func<Task> execute = async () =>
            await sut.ExecuteAsync(
                new SingleRecordRelationshipAuthorizationExecutionRequest(
                    CreateMappingSet(SqlDialect.Pgsql),
                    DocumentId: 350L,
                    [CreatePeopleTransitiveProposedCheckSpec()],
                    AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                        SqlDialect.Pgsql,
                        [100L],
                        "ClaimEducationOrganizationIds"
                    ),
                    EmittedAuth1Index: 0
                )
            );

        await execute
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage(
                "*targets column 'StudentSchoolAssociation_DocumentId', but the subject targets column 'Student_DocumentId'*"
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect) =>
        new(
            new MappingSetKey("schema-hash", dialect, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo("5.2.0", "v1", "schema-hash", 1, [], [], []),
                dialect,
                [],
                [],
                [],
                [],
                [],
                []
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

    private static RelationshipAuthorizationCheckSpec CreateStoredCheckSpec(
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(
                direction is RelationshipAuthorizationHierarchyDirection.Normal
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                RawConfiguredIndex: 0
            ),
            RelationshipLocalOrder: 0,
            direction,
            RelationshipAuthorizationValueSource.Stored,
            [
                new RelationshipAuthorizationSubject(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    new DbTableName(new DbSchemaName("edfi"), "School"),
                    new DbColumnName("SchoolId"),
                    RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
                    [
                        new RelationshipAuthorizationSubjectContributor(
                            SecurableElementKind.EducationOrganization,
                            "$.schoolReference.schoolId",
                            "SchoolId"
                        ),
                    ]
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Stored(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId")
            )
        );

    private static RelationshipAuthorizationCheckSpec CreatePeopleStoredCheckSpec()
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RawConfiguredIndex: 0
            ),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Stored,
            [
                new RelationshipAuthorizationSubject(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    rootTable,
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
                            [new ColumnPathStep(rootTable, AuthNames.StudentDocumentId, null, null)]
                        ),
                        new RelationshipAuthorizationPersonStoredAnchor(
                            rootTable,
                            new DbColumnName("DocumentId")
                        ),
                        ProposedAnchor: null
                    )
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Stored(rootTable, new DbColumnName("DocumentId"))
        );
    }

    private static RelationshipAuthorizationCheckSpec CreatePeopleTransitiveProposedCheckSpec()
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var courseTranscriptStudentSchoolAssociationIdColumn = new DbColumnName(
            "StudentSchoolAssociation_DocumentId"
        );
        var studentSchoolAssociationTable = new DbTableName(
            new DbSchemaName("edfi"),
            "StudentSchoolAssociation"
        );
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var proposedBinding = new RelationshipAuthorizationProposedValueBinding(
            rootTable,
            courseTranscriptStudentSchoolAssociationIdColumn,
            BindingIndex: 0,
            LogicalKey: courseTranscriptStudentSchoolAssociationIdColumn.Value,
            ParameterSeed: "studentSchoolAssociation_DocumentId"
        );

        return new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RawConfiguredIndex: 0
            ),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [
                new RelationshipAuthorizationSubject(
                    new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                    studentSchoolAssociationTable,
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
                            RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                            [
                                new ColumnPathStep(
                                    rootTable,
                                    courseTranscriptStudentSchoolAssociationIdColumn,
                                    studentSchoolAssociationTable,
                                    new DbColumnName("DocumentId")
                                ),
                                new ColumnPathStep(
                                    studentSchoolAssociationTable,
                                    AuthNames.StudentDocumentId,
                                    studentTable,
                                    AuthNames.StudentDocumentId
                                ),
                            ]
                        ),
                        new RelationshipAuthorizationPersonStoredAnchor(
                            rootTable,
                            new DbColumnName("DocumentId")
                        ),
                        new RelationshipAuthorizationPersonProposedAnchor(
                            RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                            proposedBinding
                        )
                    )
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Proposed(rootTable, [proposedBinding])
        );
    }

    private static IReadOnlyDictionary<string, object?> CreateRow(
        params (string ColumnName, object? Value)[] values
    ) => values.ToDictionary(static value => value.ColumnName, static value => value.Value);

    private sealed class RecordingRelationalCommandExecutor(
        SqlDialect dialect,
        IReadOnlyList<InMemoryRelationalCommandExecution>? executions = null,
        DbException? exceptionToThrow = null
    ) : IRelationalCommandExecutor
    {
        private readonly Queue<InMemoryRelationalCommandExecution> _executions = new(executions ?? []);
        private readonly DbException? _exceptionToThrow = exceptionToThrow;

        public SqlDialect Dialect { get; } = dialect;

        public List<RelationalCommand> Commands { get; } = [];

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(readAsync);

            Commands.Add(command);

            if (_exceptionToThrow is not null)
            {
                throw _exceptionToThrow;
            }

            if (!_executions.TryDequeue(out var execution))
            {
                throw new AssertionException(
                    "No in-memory relationship authorization execution was configured for this call."
                );
            }

            await using var reader = new InMemoryRelationalCommandReader(execution.ResultSets);
            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }

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
