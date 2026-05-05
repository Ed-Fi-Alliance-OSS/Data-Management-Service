// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DescriptorReadHandler
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"", "dms.\"Descriptor\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]", "[dms].[Descriptor]")]
    public async Task It_reads_descriptor_gets_directly_from_document_and_descriptor(
        SqlDialect dialect,
        string expectedDocumentTableFragment,
        string expectedDescriptorTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid.Value)),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(CreateRequest(dialect, documentUuid));

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(documentUuid);
        success.LastModifiedDate.Should().Be(new DateTime(2026, 5, 5, 14, 30, 45, DateTimeKind.Utc));
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(documentUuid.Value.ToString());
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        success.EdfiDoc["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        success.EdfiDoc["Discriminator"].Should().BeNull();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedDocumentTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedDescriptorTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain("LEFT JOIN");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)13);
    }

    [Test]
    public async Task It_returns_not_exists_when_document_uuid_is_missing_or_is_for_the_wrong_resource()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateRequest(
                SqlDialect.Pgsql,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc"))
            )
        );

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_fails_closed_for_descriptor_get_authorization_without_executing_sql()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateRequest(
                SqlDialect.Pgsql,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd")),
                authorizationStrategyEvaluators: [new("RelationshipsWithEdOrgsOnly", [], FilterOperator.And)]
            )
        );

        result
            .Should()
            .BeEquivalentTo(
                new GetResult.GetFailureNotImplemented(
                    "Relational descriptor GET authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or only 'NoFurtherAuthorizationRequired' are currently supported."
                )
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_an_unknown_failure_when_the_selected_document_has_no_descriptor_row()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        documentUuid.Value,
                        ns: null,
                        codeValue: null,
                        shortDescription: null,
                        description: null,
                        effectiveBeginDate: null,
                        effectiveEndDate: null,
                        discriminator: null
                    )
                ),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(CreateRequest(SqlDialect.Pgsql, documentUuid));

        var failure = result.Should().BeOfType<GetResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain("dms.Descriptor.Namespace must not be null.");
        failure.FailureMessage.Should().Contain("DocumentId 101");
        failure.FailureMessage.Should().Contain("ResourceKeyId=13");
    }

    [Test]
    public async Task It_treats_discriminator_as_diagnostic_only_when_the_document_resource_key_matches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-ffffffffffff"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(documentUuid.Value, discriminator: "OtherDescriptor")
                ),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(CreateRequest(SqlDialect.Pgsql, documentUuid));

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(documentUuid);
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be("Alternative");
    }

    [Test]
    public async Task It_applies_readable_profile_projection_to_external_descriptor_reads_and_refreshes_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-111111111111"));
        var projectionContext = CreateReadableProfileProjectionContext();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        documentUuid.Value,
                        description: "Alternative school type",
                        effectiveBeginDate: new DateOnly(2025, 1, 15)
                    )
                ),
            ]),
        ]);
        var unprojectedDocument = DescriptorDocumentMaterializer.Materialize(
            CreateDescriptorReadRow(
                documentUuid.Value,
                description: "Alternative school type",
                effectiveBeginDate: new DateOnly(2025, 1, 15)
            ),
            RelationalGetRequestReadMode.ExternalResponse
        );
        var projectedDocument = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-111111111111",
              "_etag": "stale",
              "_lastModifiedDate": "2026-05-05T14:30:45Z",
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Alternative",
              "description": "Alternative school type"
            }
            """
        )!;
        var readableProfileProjector = A.Fake<IReadableProfileProjector>();
        A.CallTo(() =>
                readableProfileProjector.Project(
                    A<JsonNode>._,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .Returns(projectedDocument);
        var sut = CreateHandler(commandExecutor, readableProfileProjector);

        var result = await sut.HandleGetByIdAsync(
            CreateRequest(SqlDialect.Pgsql, documentUuid, readableProfileProjectionContext: projectionContext)
        );

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["description"]!.GetValue<string>().Should().Be("Alternative school type");
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(documentUuid.Value.ToString());
        success.EdfiDoc["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        success.EdfiDoc["shortDescription"].Should().BeNull();
        success.EdfiDoc["effectiveBeginDate"].Should().BeNull();
        success.EdfiDoc["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(success.EdfiDoc));
        success.EdfiDoc["_etag"]!
            .GetValue<string>()
            .Should()
            .NotBe(unprojectedDocument["_etag"]!.GetValue<string>());
        A.CallTo(() =>
                readableProfileProjector.Project(
                    A<JsonNode>._,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_skips_readable_profile_projection_for_stored_descriptor_reads()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-222222222222"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid.Value)),
            ]),
        ]);
        var readableProfileProjector = A.Fake<IReadableProfileProjector>();
        var sut = CreateHandler(commandExecutor, readableProfileProjector);

        var result = await sut.HandleGetByIdAsync(
            CreateRequest(
                SqlDialect.Pgsql,
                documentUuid,
                readMode: RelationalGetRequestReadMode.StoredDocument,
                readableProfileProjectionContext: CreateReadableProfileProjectionContext()
            )
        );

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["id"].Should().BeNull();
        success.EdfiDoc["_etag"].Should().BeNull();
        success.EdfiDoc["_lastModifiedDate"].Should().BeNull();
        A.CallTo(() =>
                readableProfileProjector.Project(
                    A<JsonNode>._,
                    A<ContentTypeDefinition>._,
                    A<IReadOnlySet<string>>._
                )
            )
            .MustNotHaveHappened();
    }

    private static DescriptorGetByIdRequest CreateRequest(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        var mappingSet = CreateMappingSet(dialect);
        mappingSet
            .TryGetDescriptorResourceModel(_descriptorResource, out var descriptorResourceModel)
            .Should()
            .BeTrue();

        return new DescriptorGetByIdRequest(
            mappingSet,
            descriptorResourceModel!,
            _descriptorResource,
            documentUuid,
            readMode,
            authorizationStrategyEvaluators ?? [],
            readableProfileProjectionContext,
            new TraceId("descriptor-get-trace")
        );
    }

    private static DescriptorReadHandler CreateHandler(
        IRelationalCommandExecutor commandExecutor,
        IReadableProfileProjector? readableProfileProjector = null
    )
    {
        return new DescriptorReadHandler(
            commandExecutor,
            readableProfileProjector ?? A.Fake<IReadableProfileProjector>(),
            NullLogger<DescriptorReadHandler>.Instance
        );
    }

    private static ReadableProfileProjectionContext CreateReadableProfileProjectionContext()
    {
        return new ReadableProfileProjectionContext(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("description")],
                [],
                [],
                []
            ),
            new HashSet<string>(StringComparer.Ordinal) { "namespace", "codeValue" }
        );
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        return mappingSet with
        {
            Key = new MappingSetKey(
                mappingSet.Key.EffectiveSchemaHash,
                dialect,
                mappingSet.Key.RelationalMappingVersion
            ),
            Model = mappingSet.Model with { Dialect = dialect },
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateDescriptorRow(
        Guid documentUuid,
        string? ns = "uri://ed-fi.org/SchoolTypeDescriptor",
        string? codeValue = "Alternative",
        string? shortDescription = "Alternative",
        string? description = "Alternative school type",
        DateOnly? effectiveBeginDate = null,
        DateOnly? effectiveEndDate = null,
        string? discriminator = "SchoolTypeDescriptor"
    )
    {
        return RelationalAccessTestData.CreateRow(
            ("DocumentId", 101L),
            ("DocumentUuid", documentUuid),
            ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero)),
            ("ResourceKeyId", (short)13),
            ("Namespace", ns),
            ("CodeValue", codeValue),
            ("ShortDescription", shortDescription),
            ("Description", description),
            ("EffectiveBeginDate", effectiveBeginDate),
            ("EffectiveEndDate", effectiveEndDate),
            ("Discriminator", discriminator)
        );
    }

    private static DescriptorReadRow CreateDescriptorReadRow(
        Guid documentUuid,
        string? ns = "uri://ed-fi.org/SchoolTypeDescriptor",
        string? codeValue = "Alternative",
        string? shortDescription = "Alternative",
        string? description = "Alternative school type",
        DateOnly? effectiveBeginDate = null,
        DateOnly? effectiveEndDate = null,
        string? discriminator = "SchoolTypeDescriptor"
    )
    {
        return new DescriptorReadRow(
            DocumentId: 101L,
            DocumentUuid: documentUuid,
            ContentLastModifiedAt: new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero),
            ResourceKeyId: 13,
            Namespace: ns!,
            CodeValue: codeValue!,
            ShortDescription: shortDescription!,
            Description: description,
            EffectiveBeginDate: effectiveBeginDate,
            EffectiveEndDate: effectiveEndDate,
            Discriminator: discriminator
        );
    }
}
