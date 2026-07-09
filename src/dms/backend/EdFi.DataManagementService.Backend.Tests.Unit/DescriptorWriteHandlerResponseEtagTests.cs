// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Write_Response_Etags
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private const string StampStyleEtagPattern = "^\"\\d+\"$";

    [Test]
    public async Task It_returns_the_composed_etag_for_descriptor_post_creates()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            ),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(42L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
        );

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.InsertSuccess(request.DocumentUuid, ExpectedComposedDescriptorEtag(42L))
            );
        result
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPostCallCount.Should().Be(1);
        targetLookupService.ResolveForPutCallCount.Should().Be(0);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory
            .Session.Executor.Commands[0]
            .CommandText.Should()
            .Contain("RETURNING \"DocumentId\", \"ContentVersion\"");
    }

    [Test]
    public async Task It_composes_descriptor_write_responses_independent_of_request_field_order()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            ),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(42L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = new DescriptorWriteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            CreateRequestBodyInNonCanonicalOrder(),
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-trace")
        );

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.InsertSuccess(request.DocumentUuid, ExpectedComposedDescriptorEtag(42L))
            );
    }

    [Test]
    public async Task It_returns_the_composed_etag_for_descriptor_post_as_update()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(description: "Previous"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        result
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPostCallCount.Should().Be(1);
        targetLookupService.ResolveForPutCallCount.Should().Be(0);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
        sessionFactory.Session.Executor.Commands[1].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_returns_the_current_composed_etag_for_descriptor_post_as_update_no_ops_without_an_update_command()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorResultSet()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(44L))
            );
        result
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPostCallCount.Should().Be(1);
        targetLookupService.ResolveForPutCallCount.Should().Be(0);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_returns_the_current_composed_etag_for_descriptor_put_no_ops_without_an_update_command()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorResultSet()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(44L))
            );
        result
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPutCallCount.Should().Be(1);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_returns_the_composed_etag_for_descriptor_put_updates()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(description: "Previous Description"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        );

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        result
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPutCallCount.Should().Be(1);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
        sessionFactory.Session.Executor.Commands[1].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_returns_a_profile_coded_etag_for_a_profiled_descriptor_post_create()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            ),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(42L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
        ) with
        {
            ProfileName = ProfileName,
        };

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.InsertSuccess(
                    request.DocumentUuid,
                    ExpectedProfiledDescriptorEtag(42L, ProfileName)
                )
            );
        // A profiled write etag differs from the unprofiled one for the same ContentVersion.
        result
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Which.ETag.Should()
            .NotBe(ExpectedComposedDescriptorEtag(42L));
    }

    [Test]
    public async Task It_returns_a_profile_coded_etag_for_a_profiled_descriptor_put_update()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(description: "Previous Description"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        ) with
        {
            ProfileName = ProfileName,
        };

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedProfiledDescriptorEtag(45L, ProfileName))
            );
    }

    [Test]
    public async Task It_returns_a_profile_coded_current_etag_for_a_profiled_descriptor_put_no_op()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorResultSet()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            ProfileName = ProfileName,
        };

        var result = await sut.HandlePutAsync(request);

        // A no-op profiled PUT returns the current representation's etag, which must carry the
        // profile code just as the changed-write path (and a profiled GET) would.
        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedProfiledDescriptorEtag(44L, ProfileName))
            );
    }

    private const string ProfileName = "E2E-Test-SchoolTypeDescriptor-Profile";

    private static string ExpectedComposedDescriptorEtag(long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            DescriptorEtagTestSupport.NoProfileNoLinksJsonVariantKey(
                CreateMappingSet(SqlDialect.Pgsql).Key.EffectiveSchemaHash
            )
        );

    private static string ExpectedProfiledDescriptorEtag(long contentVersion, string profileName) =>
        EtagComposer.Compose(
            contentVersion,
            VariantKeyFactory.Create(
                CreateMappingSet(SqlDialect.Pgsql).Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(profileName),
                linksEnabled: false
            )
        );

    private static InMemoryRelationalResultSet CreateContentVersionResultSet(long contentVersion) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?> { ["ContentVersion"] = contentVersion }
        );

    private static DescriptorWriteHandler CreateSut(
        IRelationalWriteTargetLookupService targetLookupService,
        IRelationalWriteSessionFactory? writeSessionFactory = null
    )
    {
        return new DescriptorWriteHandler(
            targetLookupService,
            new NoOpRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            writeSessionFactory ?? A.Fake<IRelationalWriteSessionFactory>(),
            NullLogger<DescriptorWriteHandler>.Instance,
            new ServedEtagComposer(),
            new IfMatchEvaluator()
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
            new TraceId("descriptor-post-trace")
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
            new TraceId("descriptor-put-trace")
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

    private static JsonNode CreateRequestBodyInNonCanonicalOrder()
    {
        return JsonNode.Parse(
            """
            {
              "description": "Charter",
              "effectiveBeginDate": "2024-01-01",
              "shortDescription": "Charter",
              "codeValue": "Charter",
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor"
            }
            """
        )!;
    }

    private static InMemoryRelationalResultSet CreatePersistedDescriptorResultSet(
        string description = "Charter"
    )
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

    private sealed class RecordingRelationalCommandExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];

        public List<RelationalCommand> Commands { get; } = [];

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);

            IReadOnlyList<InMemoryRelationalResultSet> resultSets =
                ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken);
        }
    }

    private sealed class RecordingRelationalWriteSessionFactory(SqlDialect dialect)
        : IRelationalWriteSessionFactory
    {
        public RecordingRelationalWriteSession Session { get; } = new(dialect);

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly RecordingDbConnection _connection = new(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        private readonly RecordingDbTransaction _transaction;

        public RecordingRelationalWriteSession(SqlDialect dialect)
        {
            _transaction = new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted);
            Executor = new RecordingRelationalCommandExecutor(dialect);
        }

        public DbConnection Connection => _connection;

        public DbTransaction Transaction => _transaction;

        public RecordingRelationalCommandExecutor Executor { get; }

        public Queue<object?> ScalarResults { get; } = [];

        public List<RelationalCommand> ScalarCommands { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubRelationalWriteTargetLookupService : IRelationalWriteTargetLookupService
    {
        public RelationalWriteTargetLookupResult PostResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public RelationalWriteTargetLookupResult PutResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public int ResolveForPostCallCount { get; private set; }

        public int ResolveForPutCallCount { get; private set; }

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveForPostCallCount++;
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
            ResolveForPutCallCount++;
            return Task.FromResult(PutResult);
        }
    }
}
