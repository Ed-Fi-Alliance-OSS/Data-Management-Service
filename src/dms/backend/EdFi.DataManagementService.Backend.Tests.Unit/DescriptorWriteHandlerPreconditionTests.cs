// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Write_Preconditions
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_post_creates_when_if_match_is_present()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, commandExecutor, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"", null),
        };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_post_as_update_when_if_match_mismatches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        var sut = CreateSut(targetLookupService, commandExecutor, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"", null),
        };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public async Task It_short_circuits_descriptor_post_as_update_overlap_when_if_match_exactly_matches_current_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);
        var currentState = CreatePersistedDescriptorBody();
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(currentState);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(targetLookupService, commandExecutor, sessionFactory);
        request = request with { WritePrecondition = new WritePrecondition.IfMatch(currentEtag, null) };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, currentEtag));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_updates_descriptor_put_when_if_match_exactly_matches_current_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentState = CreatePersistedDescriptorBody(description: "Current Charter");
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(currentState);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        var sut = CreateSut(targetLookupService, commandExecutor, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Charter"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag, null),
        };

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, ExpectedCanonicalHashEtag(request)));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory.Session.Executor.Commands[2].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_preserves_descriptor_put_immutable_identity_failures_after_exact_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentState = CreatePersistedDescriptorBody();
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(currentState);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(targetLookupService, commandExecutor, sessionFactory);
        var request = new DescriptorWriteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            JsonNode.Parse(
                """
                {
                  "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                  "codeValue": "Alternative",
                  "shortDescription": "Alternative",
                  "description": "Alternative",
                  "effectiveBeginDate": "2024-01-01"
                }
                """
            )!,
            documentUuid,
            null,
            new TraceId("descriptor-put-immutable-if-match")
        )
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag, null),
        };

        var result = await sut.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_delete_when_if_match_mismatches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), commandExecutor, sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"", null),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public async Task It_deletes_the_descriptor_when_delete_if_match_exactly_matches_the_current_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentState = CreatePersistedDescriptorBody();
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(currentState);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), commandExecutor, sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag, null),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory
            .Session.Executor.Commands[2]
            .CommandText.Should()
            .Contain("DELETE FROM dms.\"Document\"");
    }

    [Test]
    public async Task It_returns_not_exists_for_descriptor_delete_when_the_scoped_lookup_misses_under_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), commandExecutor, sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"current-etag\"", null),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_descriptor_delete_fk_conflict_mapping_after_an_exact_if_match()
    {
        const string constraintName = "FK_School_SchoolTypeDescriptor";
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var classifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsForeignKeyViolationToReturn = true,
            ClassificationToReturn = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                constraintName
            ),
        };
        var resolver = A.Fake<IRelationalDeleteConstraintResolver>();
        var referencingResource = new QualifiedResourceName("Ed-Fi", "School");
        A.CallTo(() => resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, constraintName))
            .Returns(referencingResource);

        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentState = CreatePersistedDescriptorBody();
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(currentState);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.CommandExceptionFactory = command =>
            command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
                ? new StubDbException("FK constraint violation")
                : null;

        var sut = CreateSut(
            new StubRelationalWriteTargetLookupService(),
            commandExecutor,
            sessionFactory,
            classifier,
            resolver
        );
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag, null),
        };

        var result = await sut.HandleDeleteAsync(request);

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([referencingResource.ResourceName]));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        A.CallTo(() => resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, constraintName))
            .MustHaveHappenedOnceExactly();
    }

    private static string ExpectedCanonicalHashEtag(DescriptorWriteRequest request) =>
        RelationalApiMetadataFormatter.FormatEtag(
            DescriptorWriteBodyExtractor.Extract(request.RequestBody, request.Resource)
        );

    private static ExtractedDescriptorBody CreatePersistedDescriptorBody(string description = "Charter")
    {
        return new ExtractedDescriptorBody(
            "uri://ed-fi.org/SchoolTypeDescriptor",
            "Charter",
            "Charter",
            description,
            new DateOnly(2024, 1, 1),
            null,
            "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
            _descriptorResource.ResourceName
        );
    }

    private static InMemoryRelationalResultSet CreateResolvedExistingDocumentRow(DocumentUuid documentUuid)
    {
        return InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["DocumentId"] = 345L,
                ["DocumentUuid"] = documentUuid.Value,
                ["ResourceKeyId"] = 1,
                ["ContentVersion"] = 44L,
            }
        );
    }

    private static InMemoryRelationalResultSet CreatePersistedDescriptorRow(string description = "Charter")
    {
        return InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["CodeValue"] = "Charter",
                ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                ["ShortDescription"] = "Charter",
                ["Description"] = description,
                ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                ["EffectiveEndDate"] = null,
            }
        );
    }

    private static DescriptorWriteHandler CreateSut(
        IRelationalWriteTargetLookupService targetLookupService,
        IRelationalCommandExecutor commandExecutor,
        RecordingRelationalWriteSessionFactory sessionFactory,
        IRelationalWriteExceptionClassifier? classifier = null,
        IRelationalDeleteConstraintResolver? deleteConstraintResolver = null
    )
    {
        return new DescriptorWriteHandler(
            targetLookupService,
            commandExecutor,
            classifier ?? new NoOpRelationalWriteExceptionClassifier(),
            deleteConstraintResolver ?? A.Fake<IRelationalDeleteConstraintResolver>(),
            sessionFactory,
            NullLogger<DescriptorWriteHandler>.Instance
        );
    }

    private static DescriptorWriteRequest CreatePostRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter"
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            _descriptorResource,
            CreateRequestBody(description),
            documentUuid,
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-precondition")
        );
    }

    private static DescriptorWriteRequest CreatePutRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter"
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            _descriptorResource,
            CreateRequestBody(description),
            documentUuid,
            null,
            new TraceId("descriptor-put-precondition")
        );
    }

    private static DescriptorDeleteRequest CreateDeleteRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid
    )
    {
        return new DescriptorDeleteRequest(
            mappingSet,
            _descriptorResource,
            documentUuid,
            new TraceId("descriptor-delete-precondition")
        );
    }

    private static JsonNode CreateRequestBody(string description)
    {
        return JsonNode.Parse(
            $$"""
            {
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Charter",
              "shortDescription": "Charter",
              "description": "{{description}}",
              "effectiveBeginDate": "2024-01-01"
            }
            """
        )!;
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var resourceKey = new ResourceKeyEntry(1, _descriptorResource, "1.0.0", true);
        var rootTable = CreateRootTable();
        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(resourceKey, resourceModel.StorageKind, resourceModel),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static DbTableModel CreateRootTable()
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolTypeDescriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_SchoolTypeDescriptor",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };
    }

    private sealed class RecordingRelationalWriteSessionFactory(SqlDialect dialect)
        : IRelationalWriteSessionFactory
    {
        public int CreateAsyncCallCount { get; private set; }

        public RecordingRelationalWriteSession Session { get; } = new(dialect);

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAsyncCallCount++;
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly RecordingDbConnection _connection = new(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        private readonly RecordingDbTransaction _transaction;

        public RecordingRelationalWriteSession(
            SqlDialect dialect,
            RecordingRelationalCommandExecutor? executor = null
        )
        {
            _transaction = new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted);
            Executor = executor ?? new RecordingRelationalCommandExecutor(dialect);
        }

        public DbConnection Connection => _connection;

        public DbTransaction Transaction => _transaction;

        public RecordingRelationalCommandExecutor Executor { get; }

        public Queue<object?> ScalarResults { get; } = [];

        public List<RelationalCommand> ScalarCommands { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            ScalarCommands.Add(command);

            var dbCommand = new RecordingDbCommand(new DataTable().CreateDataReader())
            {
                CommandText = command.CommandText,
                ScalarResult = ScalarResults.Count == 0 ? null : ScalarResults.Dequeue(),
            };

            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                parameter.ConfigureParameter?.Invoke(dbParameter);
                dbCommand.Parameters.Add((RecordingDbParameter)dbParameter);
            }

            return dbCommand;
        }

        public IRelationalCommandExecutor CreateCommandExecutor() => Executor;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCallCount++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRelationalCommandExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];

        public List<RelationalCommand> Commands { get; } = [];

        public Func<RelationalCommand, Exception?>? CommandExceptionFactory { get; set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);

            if (CommandExceptionFactory?.Invoke(command) is { } exception)
            {
                throw exception;
            }

            IReadOnlyList<InMemoryRelationalResultSet> resultSets =
                ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken);
        }
    }

    private sealed class StubRelationalWriteTargetLookupService : IRelationalWriteTargetLookupService
    {
        public RelationalWriteTargetLookupResult PostResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public RelationalWriteTargetLookupResult PutResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PostResult);
        }

        public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            DocumentUuid documentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PutResult);
        }
    }

    private sealed class StubDbException(string message) : DbException(message);
}
