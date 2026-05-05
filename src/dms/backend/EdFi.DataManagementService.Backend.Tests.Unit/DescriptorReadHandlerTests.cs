// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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

    [Test]
    public async Task It_fails_closed_for_descriptor_query_authorization_without_executing_sql()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                authorizationStrategyEvaluators: [new("RelationshipsWithEdOrgsOnly", [], FilterOperator.And)]
            )
        );

        result
            .Should()
            .BeEquivalentTo(
                new QueryResult.QueryFailureNotImplemented(
                    "Relational descriptor query authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET-many authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or only 'NoFurtherAuthorizationRequired' are currently supported."
                )
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_descriptor_query_capability_omission_diagnostics_without_executing_sql()
    {
        const string omissionReason =
            "descriptor query support was intentionally omitted for the test fixture.";
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                descriptorQueryCapability: CreateOmittedDescriptorQueryCapability(omissionReason)
            )
        );

        result
            .Should()
            .BeEquivalentTo(
                new QueryResult.QueryFailureNotImplemented(
                    "Descriptor query capability for resource 'Ed-Fi.SchoolTypeDescriptor' was intentionally omitted: "
                        + omissionReason
                )
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_short_circuits_invalid_descriptor_query_ids_to_an_empty_page_without_executing_sql()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                queryElements: [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
                totalCount: true
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"", "dms.\"Descriptor\"", "page_document_ids.\"DocumentId\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]", "[dms].[Descriptor]", "page_document_ids.[DocumentId]")]
    public async Task It_reads_descriptor_query_rows_in_document_id_order_and_honors_total_count(
        SqlDialect dialect,
        string expectedDocumentTableFragment,
        string expectedDescriptorTableFragment,
        string expectedOrderByFragment
    )
    {
        var firstDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-111111111111");
        var secondDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-222222222222");
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 7))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(firstDocumentUuid, documentId: 101L, codeValue: "Alternative"),
                    CreateDescriptorRow(secondDocumentUuid, documentId: 205L, codeValue: "Charter")
                ),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            dialect,
            queryElements:
            [
                CreateQueryElement(
                    "namespace",
                    "$.namespace",
                    "uri://ed-fi.org/SchoolTypeDescriptor",
                    "string"
                ),
            ],
            totalCount: true
        );

        var result = await ReadQueryRowsAsync(sut, request);

        result.TotalCount.Should().Be(7);
        result.Rows.Select(row => row.DocumentId).Should().Equal(101L, 205L);
        result.Rows.Select(row => row.DocumentUuid).Should().Equal(firstDocumentUuid, secondDocumentUuid);
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain("COUNT(1)");
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedDocumentTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedDescriptorTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain("LEFT JOIN");
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedOrderByFragment);
    }

    [Test]
    public async Task It_does_not_fail_when_total_count_is_requested_and_a_corrupt_descriptor_document_is_outside_the_selected_page()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-333333333333");
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 2))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(documentUuid, documentId: 101L, codeValue: "Alternative")
                ),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(SqlDialect.Pgsql, totalCount: true, limit: 1, offset: 0);

        var result = await ReadQueryRowsAsync(sut, request);

        result.TotalCount.Should().Be(2);
        result.Rows.Select(row => row.DocumentId).Should().Equal(101L);
        commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_returns_an_unknown_failure_when_the_selected_descriptor_query_document_has_no_descriptor_row()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
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

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql));

        var failure = result.Should().BeOfType<QueryResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain("dms.Descriptor.Namespace must not be null.");
        failure.FailureMessage.Should().Contain("DocumentId 101");
        failure.FailureMessage.Should().Contain("ResourceKeyId=13");
    }

    [Test]
    public async Task It_returns_an_unknown_failure_when_a_selected_descriptor_query_row_has_a_required_field_null()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        Guid.Parse("aaaaaaaa-1111-2222-3333-555555555555"),
                        shortDescription: null
                    )
                ),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql));

        var failure = result.Should().BeOfType<QueryResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain("dms.Descriptor.ShortDescription must not be null.");
        failure.FailureMessage.Should().Contain("DocumentId 101");
        failure.FailureMessage.Should().Contain("ResourceKeyId=13");
    }

    [Test]
    public async Task It_materializes_descriptor_query_pages_into_external_response_items_with_metadata_and_total_count()
    {
        var firstDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-666666666666");
        var secondDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-777777777777");
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 7))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        firstDocumentUuid,
                        documentId: 101L,
                        description: null,
                        effectiveBeginDate: new DateOnly(2025, 1, 15),
                        effectiveEndDate: null
                    ),
                    CreateDescriptorRow(
                        secondDocumentUuid,
                        documentId: 205L,
                        codeValue: "Charter",
                        shortDescription: "Charter",
                        description: "Charter school type",
                        effectiveBeginDate: null,
                        effectiveEndDate: new DateOnly(2025, 12, 31)
                    )
                ),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                queryElements:
                [
                    CreateQueryElement(
                        "namespace",
                        "$.namespace",
                        "uri://ed-fi.org/SchoolTypeDescriptor",
                        "string"
                    ),
                ],
                totalCount: true
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(7);
        success.EdfiDocs.Should().HaveCount(2);

        var firstDocument = success.EdfiDocs[0]!.AsObject();
        firstDocument["id"]!.GetValue<string>().Should().Be(firstDocumentUuid.ToString());
        firstDocument["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        firstDocument["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        firstDocument["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        firstDocument["description"].Should().BeNull();
        firstDocument["effectiveBeginDate"]!.GetValue<string>().Should().Be("2025-01-15");
        firstDocument["effectiveEndDate"].Should().BeNull();
        firstDocument["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        firstDocument["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(firstDocument));
        firstDocument["Uri"].Should().BeNull();
        firstDocument["Discriminator"].Should().BeNull();
        firstDocument["ChangeVersion"].Should().BeNull();

        var secondDocument = success.EdfiDocs[1]!.AsObject();
        secondDocument["id"]!.GetValue<string>().Should().Be(secondDocumentUuid.ToString());
        secondDocument["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        secondDocument["codeValue"]!.GetValue<string>().Should().Be("Charter");
        secondDocument["shortDescription"]!.GetValue<string>().Should().Be("Charter");
        secondDocument["description"]!.GetValue<string>().Should().Be("Charter school type");
        secondDocument["effectiveBeginDate"].Should().BeNull();
        secondDocument["effectiveEndDate"]!.GetValue<string>().Should().Be("2025-12-31");
        secondDocument["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        secondDocument["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(secondDocument));
        secondDocument["Uri"].Should().BeNull();
        secondDocument["Discriminator"].Should().BeNull();
        secondDocument["ChangeVersion"].Should().BeNull();
        commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_applies_readable_profile_projection_to_descriptor_query_items_and_refreshes_each_etag()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-888888888888");
        var projectionContext = CreateReadableProfileProjectionContext();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        documentUuid,
                        description: "Alternative school type",
                        effectiveBeginDate: new DateOnly(2025, 1, 15)
                    )
                ),
            ]),
        ]);
        var unprojectedDocument = DescriptorDocumentMaterializer.Materialize(
            CreateDescriptorReadRow(
                documentUuid,
                description: "Alternative school type",
                effectiveBeginDate: new DateOnly(2025, 1, 15)
            ),
            RelationalGetRequestReadMode.ExternalResponse
        );
        var projectedDocument = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-888888888888",
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

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(SqlDialect.Pgsql, readableProfileProjectionContext: projectionContext)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().BeNull();
        success.EdfiDocs.Should().HaveCount(1);

        var projectedItem = success.EdfiDocs[0]!.AsObject();
        projectedItem["id"]!.GetValue<string>().Should().Be(documentUuid.ToString());
        projectedItem["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        projectedItem["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        projectedItem["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        projectedItem["description"]!.GetValue<string>().Should().Be("Alternative school type");
        projectedItem["shortDescription"].Should().BeNull();
        projectedItem["effectiveBeginDate"].Should().BeNull();
        projectedItem["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(projectedItem));
        projectedItem["_etag"]!
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

    private static DescriptorQueryRequest CreateQueryRequest(
        SqlDialect dialect,
        QueryElement[]? queryElements = null,
        bool totalCount = false,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        DescriptorQueryCapability? descriptorQueryCapability = null,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null,
        int? limit = 25,
        int? offset = 0
    )
    {
        var mappingSet = CreateQueryMappingSet(
            dialect,
            descriptorQueryCapability ?? CreateSupportedDescriptorQueryCapability()
        );
        mappingSet
            .TryGetDescriptorResourceModel(_descriptorResource, out var descriptorResourceModel)
            .Should()
            .BeTrue();

        return new DescriptorQueryRequest(
            mappingSet,
            descriptorResourceModel!,
            _descriptorResource,
            queryElements ?? [],
            new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: 500
            ),
            authorizationStrategyEvaluators ?? [],
            readableProfileProjectionContext,
            new TraceId("descriptor-query-trace")
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

    private static MappingSet CreateQueryMappingSet(
        SqlDialect dialect,
        DescriptorQueryCapability descriptorQueryCapability
    )
    {
        return CreateMappingSet(dialect) with
        {
            DescriptorQueryCapabilitiesByResource = new Dictionary<
                QualifiedResourceName,
                DescriptorQueryCapability
            >
            {
                [_descriptorResource] = descriptorQueryCapability,
            },
        };
    }

    private static DescriptorQueryCapability CreateSupportedDescriptorQueryCapability()
    {
        return new DescriptorQueryCapability(
            new DescriptorQuerySupport.Supported(),
            new Dictionary<string, SupportedDescriptorQueryField>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = CreateSupportedField("id", new DescriptorQueryFieldTarget.DocumentUuid()),
                ["namespace"] = CreateSupportedField(
                    "namespace",
                    new DescriptorQueryFieldTarget.Namespace(new DbColumnName("Namespace"))
                ),
                ["codeValue"] = CreateSupportedField(
                    "codeValue",
                    new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue"))
                ),
                ["shortDescription"] = CreateSupportedField(
                    "shortDescription",
                    new DescriptorQueryFieldTarget.ShortDescription(new DbColumnName("ShortDescription"))
                ),
                ["description"] = CreateSupportedField(
                    "description",
                    new DescriptorQueryFieldTarget.Description(new DbColumnName("Description"))
                ),
                ["effectiveBeginDate"] = CreateSupportedField(
                    "effectiveBeginDate",
                    new DescriptorQueryFieldTarget.EffectiveBeginDate(new DbColumnName("EffectiveBeginDate"))
                ),
                ["effectiveEndDate"] = CreateSupportedField(
                    "effectiveEndDate",
                    new DescriptorQueryFieldTarget.EffectiveEndDate(new DbColumnName("EffectiveEndDate"))
                ),
            }
        );
    }

    private static DescriptorQueryCapability CreateOmittedDescriptorQueryCapability(string omissionReason)
    {
        return new DescriptorQueryCapability(
            new DescriptorQuerySupport.Omitted(
                new DescriptorQueryCapabilityOmission(
                    DescriptorQueryCapabilityOmissionKind.ApiSchemaMismatch,
                    omissionReason
                )
            ),
            new Dictionary<string, SupportedDescriptorQueryField>(StringComparer.OrdinalIgnoreCase)
        );
    }

    private static IReadOnlyDictionary<string, object?> CreateDescriptorRow(
        Guid documentUuid,
        long documentId = 101L,
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
            ("DocumentId", documentId),
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
        long documentId = 101L,
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
            DocumentId: documentId,
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

    private static QueryElement CreateQueryElement(
        string queryFieldName,
        string documentPath,
        string value,
        string type
    )
    {
        return new QueryElement(queryFieldName, [new JsonPath(documentPath)], value, type);
    }

    private static SupportedDescriptorQueryField CreateSupportedField(
        string queryFieldName,
        DescriptorQueryFieldTarget target
    )
    {
        return new SupportedDescriptorQueryField(queryFieldName, target);
    }

    private static async Task<DescriptorQueryRowsPage> ReadQueryRowsAsync(
        DescriptorReadHandler sut,
        DescriptorQueryRequest request
    )
    {
        var preprocessingResult = DescriptorQueryRequestPreprocessor.Preprocess(
            request.MappingSet,
            request.Resource,
            request.QueryElements
        );
        preprocessingResult.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();

        return await sut.ReadQueryRowsAsync(request, preprocessingResult);
    }
}
