// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
    public async Task It_returns_the_canonical_hash_etag_for_descriptor_post_creates()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            ),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, commandExecutor);
        var request = CreatePostRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
        );

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.InsertSuccess(request.DocumentUuid, ExpectedCanonicalHashEtag(request))
            );
        result
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPostCallCount.Should().Be(1);
        targetLookupService.ResolveForPutCallCount.Should().Be(0);
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor
            .Commands[0]
            .CommandText.Should()
            .NotContain("RETURNING \"DocumentId\", \"ContentVersion\"");
    }

    [Test]
    public async Task It_hashes_descriptor_write_responses_from_canonical_field_order()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            ),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, commandExecutor);
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
                new UpsertResult.InsertSuccess(request.DocumentUuid, ExpectedCanonicalHashEtag(request))
            );
    }

    [Test]
    public async Task It_returns_the_canonical_hash_etag_for_descriptor_post_as_update()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, commandExecutor);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, ExpectedCanonicalHashEtag(request)));
        result
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPostCallCount.Should().Be(1);
        targetLookupService.ResolveForPutCallCount.Should().Be(0);
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().NotContain("SELECT document.\"ContentVersion\"");
    }

    [Test]
    public async Task It_returns_the_canonical_hash_etag_for_descriptor_put_no_ops_without_an_update_command()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        commandExecutor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(
                new Dictionary<string, object?>
                {
                    ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                    ["ShortDescription"] = "Charter",
                    ["Description"] = "Charter",
                    ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                    ["EffectiveEndDate"] = null,
                }
            ),
        ]);
        var sut = CreateSut(targetLookupService, commandExecutor);
        var request = CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, ExpectedCanonicalHashEtag(request)));
        result
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPutCallCount.Should().Be(1);
        commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_returns_the_canonical_hash_etag_for_descriptor_put_updates()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new RecordingRelationalCommandExecutor(SqlDialect.Pgsql);
        commandExecutor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(
                new Dictionary<string, object?>
                {
                    ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                    ["ShortDescription"] = "Charter",
                    ["Description"] = "Previous Description",
                    ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                    ["EffectiveEndDate"] = null,
                }
            ),
        ]);
        var sut = CreateSut(targetLookupService, commandExecutor);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        );

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, ExpectedCanonicalHashEtag(request)));
        result
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotMatchRegex(StampStyleEtagPattern);
        targetLookupService.ResolveForPutCallCount.Should().Be(1);
        commandExecutor.Commands.Should().HaveCount(2);
        commandExecutor.Commands[1].CommandText.Should().NotContain("SELECT document.\"ContentVersion\"");
    }

    private static string ExpectedCanonicalHashEtag(DescriptorWriteRequest request) =>
        RelationalApiMetadataFormatter.FormatEtag(
            DescriptorWriteBodyExtractor.Extract(request.RequestBody, request.Resource)
        );

    private static DescriptorWriteHandler CreateSut(
        IRelationalWriteTargetLookupService targetLookupService,
        IRelationalCommandExecutor commandExecutor
    )
    {
        return new DescriptorWriteHandler(
            targetLookupService,
            commandExecutor,
            new NoOpRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
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
