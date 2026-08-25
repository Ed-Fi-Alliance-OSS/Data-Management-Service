// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public partial class Given_DescriptorReadHandler
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly IServedEtagComposer _servedEtagComposer = new ServedEtagComposer();

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
    public async Task It_exposes_an_authorized_descriptor_get_candidate_to_read_acceleration()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-111111111111"));
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid.Value, documentId: 205L)),
            ]),
        ]);
        DocumentCacheReadAccelerationGetByIdRequest capturedRequest = null!;
        DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate capturedSelection = null!;

        A.CallTo(() =>
                readAccelerationCoordinator.GetByIdAsync(
                    A<DocumentCacheReadAccelerationGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationGetByIdRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                capturedRequest = request;
                var selectionResult = await request
                    .SelectAuthorizedCandidate(cancellationToken)
                    .ConfigureAwait(false);
                capturedSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate>()
                    .Subject;

                return new GetResult.GetSuccess(documentUuid, new JsonObject(), DateTime.UnixEpoch, null);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleGetByIdAsync(CreateRequest(SqlDialect.Pgsql, documentUuid));

        result.Should().BeOfType<GetResult.GetSuccess>();
        capturedRequest.ResourceKind.Should().Be(DocumentCacheReadAccelerationResourceKind.Descriptor);
        capturedRequest.SelectAuthorizedCandidate.Should().NotBeNull();
        capturedSelection
            .AuthorizedCandidate.Should()
            .Be(
                new DocumentCacheReadAccelerationCandidate(
                    205L,
                    documentUuid,
                    13,
                    42L,
                    new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero)
                )
            );
        commandExecutor.Commands.Should().ContainSingle();
        AssertDescriptorCandidateCommandOmitsBodyColumns(commandExecutor.Commands[0]);
        A.CallTo(() =>
                readAccelerationCoordinator.GetByIdAsync(
                    A<DocumentCacheReadAccelerationGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_reexecutes_descriptor_get_relational_fallback_after_cache_lookup_miss(
        SqlDialect dialect
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-121212121212"));
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        documentUuid.Value,
                        documentId: 205L,
                        shortDescription: "Before fallback",
                        contentVersion: 42L
                    )
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        documentUuid.Value,
                        documentId: 205L,
                        shortDescription: "After fallback",
                        contentVersion: 84L
                    )
                ),
            ]),
        ]);
        A.CallTo(() =>
                readAccelerationCoordinator.GetByIdAsync(
                    A<DocumentCacheReadAccelerationGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationGetByIdRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                var selectionResult = await request
                    .SelectAuthorizedCandidate(cancellationToken)
                    .ConfigureAwait(false);
                var candidateSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate>()
                    .Subject;

                return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleGetByIdAsync(CreateRequest(dialect, documentUuid));

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["shortDescription"]!.GetValue<string>().Should().Be("After fallback");
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be(ExpectedComposedDescriptorEtag(84L));
        commandExecutor.Commands.Should().HaveCount(2);
        AssertDescriptorCandidateCommandOmitsBodyColumns(commandExecutor.Commands[0]);
        AssertDescriptorMaterializationCommandSelectsBodyColumns(commandExecutor.Commands[1]);
        AssertDescriptorReadCommandsShareScaffolding(
            commandExecutor.Commands[0],
            commandExecutor.Commands[1],
            "document"
        );
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
                    "Relational descriptor GET authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or with 'NamespaceBased' and/or 'NoFurtherAuthorizationRequired' are currently supported."
                )
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_namespace_403_for_descriptor_get_by_id_when_no_prefixes_precede_an_unsupported_custom_view()
    {
        // The namespace 403 still outranks the custom view's own outcome, but the view is configured ahead of
        // that terminal, so it executes first and its auth view is validated before the 403 is reported.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            new DescriptorGetByIdRequest(
                CreateQueryMappingSet(SqlDialect.Pgsql, CreateSupportedDescriptorQueryCapability()),
                _descriptorResource,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-121212121212")),
                RelationalGetRequestReadMode.ExternalResponse,
                [
                    CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                    CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                ],
                readableProfileProjectionContext: null,
                new TraceId("descriptor-get-custom-view-no-prefixes"),
                new RelationalAuthorizationContext([], [])
            )
        );

        result
            .Should()
            .BeOfType<GetResult.GetFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        commandExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.CommandText.Should()
            .Contain("StudentWithCustomViewProviderTest");
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
        // The row reader treats Namespace as nullable so a stored null can flow into the
        // namespace-authorization stored-namespace-uninitialized 403; CodeValue is the next
        // required column, so the reader's invariant message names it when the LEFT JOIN finds
        // no descriptor row.
        failure.FailureMessage.Should().Contain("dms.Descriptor.CodeValue must not be null.");
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
    public async Task It_applies_readable_profile_projection_and_varies_the_etag_by_profile()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-111111111111"));
        var projectionContext = CreateReadableProfileProjectionContext();
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
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
        // The full/unprofiled representation still carries the profile-insensitive "_" etag.
        var unprofiledEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: false,
                ContentVersion: 42L
            )
        );
        // The profile-reduced representation the client actually sees must carry a distinct etag
        // (RFC 9110 §8.8.1 strong-validator semantics: distinct byte-representations, distinct etags).
        var profiledEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                projectionContext.ProfileName,
                LinksEnabled: false,
                ContentVersion: 42L
            )
        );
        profiledEtag.Should().NotBe(unprofiledEtag);
        var projectedDocument = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-111111111111",
              "_etag": "",
              "_lastModifiedDate": "2026-05-05T14:30:45Z",
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Alternative",
              "description": "Alternative school type"
            }
            """
        )!;
        // Mirrors real IReadableProfileProjector behavior: projection only drops/keeps business
        // fields, it never touches the _etag the materializer already composed.
        projectedDocument["_etag"] = profiledEtag;
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
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be(profiledEtag);
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().NotBe(unprofiledEtag);
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
    public async Task It_composes_a_profile_insensitive_etag_for_unprofiled_descriptor_reads()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-aaaaaaaaaaaa"));
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid.Value)),
            ]),
        ]);
        var expectedEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: false,
                ContentVersion: 42L
            )
        );
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(CreateRequest(SqlDialect.Pgsql, documentUuid));

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be(expectedEtag);
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().EndWith("._.n.i");
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

    // Descriptor selection and row retrieval are one statement, so the returned rows are the selected
    // keyset and their maximum is the page's boundary.
    [Test]
    public async Task It_reports_the_maximum_selected_document_id_from_descriptor_query_rows()
    {
        var sut = CreateHandler(
            CreateQueryRowsExecutor(
                CreateDescriptorRow(Guid.NewGuid(), documentId: 101L, codeValue: "Alternative"),
                CreateDescriptorRow(Guid.NewGuid(), documentId: 205L, codeValue: "Charter")
            )
        );

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().Be(205L);
        success.AllowsDocumentIdContinuation.Should().BeTrue();
    }

    // The page query orders ascending today, but a boundary taken from the last row rather than the
    // maximum would silently under-report if that ever changed.
    [Test]
    public async Task It_reports_the_maximum_selected_document_id_whatever_order_the_rows_arrive_in()
    {
        var sut = CreateHandler(
            CreateQueryRowsExecutor(
                CreateDescriptorRow(Guid.NewGuid(), documentId: 205L, codeValue: "Charter"),
                CreateDescriptorRow(Guid.NewGuid(), documentId: 101L, codeValue: "Alternative")
            )
        );

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql));

        result
            .Should()
            .BeOfType<QueryResult.QuerySuccess>()
            .Which.HighestSelectedDocumentId.Should()
            .Be(205L);
    }

    [Test]
    public async Task It_reports_no_boundary_when_descriptor_page_selection_returned_no_rows()
    {
        var sut = CreateHandler(CreateQueryRowsExecutor());

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().BeNull();
        success.EdfiDocs.Should().BeEmpty();
    }

    [Test]
    public async Task It_selects_a_descriptor_cursor_page_from_the_shared_candidate_plan()
    {
        var commandExecutor = CreateQueryRowsExecutor(
            CreateDescriptorRow(Guid.NewGuid(), documentId: 101L, codeValue: "Alternative")
        );
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                paging: new CollectionPaging.Cursor(new CursorRange(10, 2509), new PageSize(100))
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().Be(101L);
        success.AllowsDocumentIdContinuation.Should().BeTrue();
        success.TotalCount.Should().BeNull();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain("@cursorMin");
        commandExecutor.Commands[0].CommandText.Should().Contain("@cursorMax");
        commandExecutor.Commands[0].CommandText.Should().Contain("@pageSize");
        commandExecutor.Commands[0].CommandText.Should().NotContain("COUNT(1)");
    }

    // A traditional descriptor page whose request anchors on ContentVersion reports its real selected
    // maximum while withholding the continuation that maximum cannot anchor. The anchor arrives on the
    // request, so it is supplied here alongside the window Core resolved it from rather than inferred
    // from that window by the handler.
    [Test]
    public async Task It_keeps_the_descriptor_boundary_but_disallows_continuation_for_a_windowed_traditional_page()
    {
        var sut = CreateHandler(
            CreateQueryRowsExecutor(
                CreateDescriptorRow(Guid.NewGuid(), documentId: 205L, codeValue: "Charter")
            )
        );

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                changeVersionRange: new ChangeVersionRange(null, 900L),
                pageOrderingMode: PageOrderingMode.ContentVersion
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().Be(205L);
        success.AllowsDocumentIdContinuation.Should().BeFalse();
    }

    private static InMemoryRelationalCommandExecutor CreateQueryRowsExecutor(
        params IReadOnlyDictionary<string, object?>[] descriptorRows
    ) => new([new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create(descriptorRows)])]);

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
                    "Relational descriptor query authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET-many authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or with 'NamespaceBased' and/or 'NoFurtherAuthorizationRequired' are currently supported."
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
    public async Task It_emits_custom_view_filters_for_descriptor_get_many_queries()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(SqlDialect.Pgsql);
        var preprocessingResult = DescriptorQueryRequestPreprocessor.Preprocess(
            request.MappingSet,
            request.Resource,
            request.QueryElements
        );
        var authorizationSpec = new PageDocumentIdAuthorizationSpec(
            Strategies: [],
            CustomViewChecks: [CreateCustomViewCheck("StudentWithCustomViewProviderTest")]
        );

        var result = await sut.ReadQueryRowsAsync(request, preprocessingResult, authorizationSpec);

        result.Rows.Should().BeEmpty();
        commandExecutor.Commands.Should().ContainSingle();
        var commandText = commandExecutor.Commands[0].CommandText;
        commandText.Should().Contain("\"auth\".\"StudentWithCustomViewProviderTest\"");
        commandText.Should().Contain("\"DocumentId\"");
    }

    [Test]
    public async Task It_validates_descriptor_custom_view_before_executing_ordinary_get_many_query()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(Guid.NewGuid(), documentId: 101L)),
            ]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolTypeDescriptorWithCustomViewProviderTest"),
            ]
        );

        var result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        commandExecutor.Commands.Should().HaveCount(2);
        commandExecutor.Commands[0].CommandText.Should().Contain("LIMIT 0");
        commandExecutor
            .Commands[0]
            .CommandText.Should()
            .Contain("\"auth\".\"SchoolTypeDescriptorWithCustomViewProviderTest\"");
        commandExecutor
            .Commands[1]
            .CommandText.Should()
            .Contain("\"auth\".\"SchoolTypeDescriptorWithCustomViewProviderTest\"");
    }

    [Test]
    public async Task It_wraps_a_provider_error_raised_by_the_descriptor_custom_view_page_query()
    {
        // Validation and the page query are separate round trips against the same views, so a view that
        // is dropped, revoked, or broken in between raises only at execution. That failure must keep the
        // custom-view validation contract instead of escaping as an unhandled provider error.
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new StubDbException("custom view does not exist");
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<DescriptorQueryRowsPage>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolTypeDescriptorWithCustomViewProviderTest"),
            ]
        );

        var action = () => sut.HandleQueryAsync(request);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_preserves_namespace_and_custom_view_order_for_descriptor_get_many_queries()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(SqlDialect.Pgsql);
        var preprocessingResult = DescriptorQueryRequestPreprocessor.Preprocess(
            request.MappingSet,
            request.Resource,
            request.QueryElements
        );
        var authorizationSpec = new PageDocumentIdAuthorizationSpec(
            Strategies: [],
            NamespaceChecks: [CreateNamespaceCheck(rawConfiguredIndex: 0)],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            CustomViewChecks: [CreateCustomViewCheck("StudentWithCustomViewProviderTest", 1)]
        );

        var result = await sut.ReadQueryRowsAsync(request, preprocessingResult, authorizationSpec);

        result.Rows.Should().BeEmpty();
        commandExecutor.Commands.Should().ContainSingle();
        var commandText = commandExecutor.Commands[0].CommandText;
        commandText
            .Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        commandText.Should().Contain("\"auth\".\"StudentWithCustomViewProviderTest\"");
        commandText
            .IndexOf(
                "(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))",
                StringComparison.Ordinal
            )
            .Should()
            .BeLessThan(
                commandText.IndexOf(
                    "\"auth\".\"StudentWithCustomViewProviderTest\"",
                    StringComparison.Ordinal
                )
            );
    }

    [Test]
    public async Task It_validates_custom_views_before_a_later_invalid_relationship_strategy_configuration_error()
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new StubDbException("custom view does not exist");
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator("InvalidRelationshipStrategy"),
            ]
        );

        var action = () => sut.HandleQueryAsync(request);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_does_not_validate_a_later_descriptor_custom_view_after_an_earlier_classifier_failure()
    {
        // InvalidRelationshipStrategy (index 0) fails the classifier; the resolved custom view at index 1
        // executes after that terminal, so probing it would let a missing or non-conforming auth view mask
        // the earlier security-configuration failure.
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("InvalidRelationshipStrategy"),
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_validates_an_earlier_descriptor_custom_view_before_a_later_classifier_failure()
    {
        // The mirror case: the resolved custom view is configured first, so it is probed, and when that
        // probe succeeds the classifier's security-configuration failure is still the reported terminal.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator("InvalidRelationshipStrategy"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_validates_descriptor_custom_views_after_an_unsupported_relationship_before_returning_not_implemented()
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new StubDbException("custom view does not exist");
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
            ]
        );

        var action = () => sut.HandleQueryAsync(request);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_excludes_the_resolved_custom_view_from_the_descriptor_not_implemented_message_after_an_unsupported_relationship()
    {
        // Same strategy shape as the sibling above, but with a custom view that validates cleanly, so
        // the 501 is actually returned. A resolved custom view is a supported GET-many AND filter: it
        // must not be listed among the unsupported effective strategies, and the message must not claim
        // only NamespaceBased/NoFurtherAuthorizationRequired are supported. The unsupported relationship
        // strategy that caused the 501 stays in the list.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        var failureMessage = result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Subject.FailureMessage;
        failureMessage.Should().NotContain("StudentWithCustomViewProviderTest");
        failureMessage
            .Should()
            .Contain(
                $"Effective strategies: ['{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}']."
            );
        failureMessage
            .Should()
            .Contain("and/or a resolved custom view-based strategy are currently supported.");
        // The custom view was resolved and validated, which is what makes excluding it correct.
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_validates_a_descriptor_get_by_id_custom_view_configured_before_an_unsupported_relationship_terminal()
    {
        // The GET-many sibling of this case validates the view ahead of its 501. GET-by-id owes the same:
        // the unsupported relationship strategy is what makes the request not implemented, but a view
        // configured ahead of that terminal still executes, so a missing one keeps its own 500.
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new StubDbException("custom view does not exist");
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);
        var sut = CreateHandler(commandExecutor);

        var action = () =>
            sut.HandleGetByIdAsync(
                new DescriptorGetByIdRequest(
                    CreateQueryMappingSet(SqlDialect.Pgsql, CreateSupportedDescriptorQueryCapability()),
                    _descriptorResource,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-141414141414")),
                    RelationalGetRequestReadMode.ExternalResponse,
                    [
                        CreateAuthorizationStrategyEvaluator(
                            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                        ),
                        CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                    ],
                    readableProfileProjectionContext: null,
                    new TraceId("descriptor-get-custom-view-unsupported-relationship"),
                    new RelationalAuthorizationContext([], ["uri://ed-fi.org/"])
                )
            );

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_excludes_the_resolved_custom_view_from_the_descriptor_get_by_id_not_implemented_message()
    {
        // Same shape with a view that validates cleanly, so the 501 is returned. A resolved custom view is
        // supported on GET-by-id too, so it must not appear among the unsupported effective strategies and
        // the message must not claim only NamespaceBased/NoFurtherAuthorizationRequired are supported.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            new DescriptorGetByIdRequest(
                CreateQueryMappingSet(SqlDialect.Pgsql, CreateSupportedDescriptorQueryCapability()),
                _descriptorResource,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-151515151515")),
                RelationalGetRequestReadMode.ExternalResponse,
                [
                    CreateAuthorizationStrategyEvaluator(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    ),
                    CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                ],
                readableProfileProjectionContext: null,
                new TraceId("descriptor-get-custom-view-excluded-from-message"),
                new RelationalAuthorizationContext([], ["uri://ed-fi.org/"])
            )
        );

        var failureMessage = result
            .Should()
            .BeOfType<GetResult.GetFailureNotImplemented>()
            .Subject.FailureMessage;
        failureMessage.Should().NotContain("StudentWithCustomViewProviderTest");
        failureMessage
            .Should()
            .Contain(
                $"Effective strategies: ['{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}']."
            );
        failureMessage
            .Should()
            .Contain("and/or a resolved custom view-based strategy are currently supported.");
        // The view was resolved and validated, which is what makes excluding it correct.
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_validates_a_descriptor_get_by_id_custom_view_configured_before_a_no_usable_root_column_terminal()
    {
        // The no-usable-root-column 500 resolves before any row is fetched, but a custom view configured ahead
        // of it executes first, so a missing or non-conforming view keeps its own configuration failure rather
        // than being hidden by this terminal.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            new DescriptorGetByIdRequest(
                CreateQueryMappingSet(
                    SqlDialect.Pgsql,
                    CreateSupportedDescriptorQueryCapability(),
                    includeDescriptorMetadata: false
                ),
                _descriptorResource,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-131313131313")),
                RelationalGetRequestReadMode.ExternalResponse,
                [
                    CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                    CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                ],
                readableProfileProjectionContext: null,
                new TraceId("descriptor-get-custom-view-no-usable-root"),
                new RelationalAuthorizationContext([], ["uri://ed-fi.org/"])
            )
        );

        result
            .Should()
            .BeOfType<GetResult.GetFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no Namespace securable element resolves to a root table column");
        commandExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.CommandText.Should()
            .Contain("StudentWithCustomViewProviderTest");
    }

    [Test]
    public async Task It_returns_the_descriptor_namespace_no_usable_root_column_500_when_the_failing_strategy_is_configured_after_namespace()
    {
        // NamespaceBased is configured first, so its no-usable-root-column terminal is reported even
        // though a later custom-view strategy has an unresolvable basis resource.
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator("MissingBasisWithCustomViewProviderTest"),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"],
            includeDescriptorMetadata: false
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no Namespace securable element resolves to a root table column");
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_the_descriptor_not_implemented_terminal_when_OwnershipBased_accompanies_an_unsupported_relationship_strategy()
    {
        // OwnershipBased is known-but-not-enabled, so it cannot short-circuit ahead of the relationship
        // OR group with an empty page. The custom view configured ahead of it is still validated before
        // the 501 is returned.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        // The resolved custom view is a supported GET-many AND filter, so the 501 must neither report it
        // as an unsupported effective strategy nor claim only NamespaceBased/NoFurtherAuthorizationRequired
        // are supported. Only the genuinely unimplemented strategies belong in the effective list.
        var failureMessage = result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Subject.FailureMessage;
        failureMessage.Should().NotContain("StudentWithCustomViewProviderTest");
        failureMessage
            .Should()
            .Contain(
                $"Effective strategies: ['{AuthorizationStrategyNameConstants.OwnershipBased}', "
                    + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}']."
            );
        failureMessage
            .Should()
            .Contain("and/or a resolved custom view-based strategy are currently supported.");
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_returns_the_descriptor_security_configuration_terminal_when_OwnershipBased_accompanies_an_invalid_relationship_configuration()
    {
        // Same as the not-implemented sibling: OwnershipBased does not displace the relationship OR
        // group's security-configuration failure, and the earlier custom view is still validated.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("InvalidRelationshipStrategy"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_returns_the_descriptor_not_implemented_terminal_when_a_custom_view_accompanies_OwnershipBased()
    {
        // Custom view + OwnershipBased: Ownership is an AND term, so the request fails closed with 501
        // instead of letting the custom-view filter stand in for it. The custom view is still
        // validated first.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased)
            .And.NotContain("StudentWithCustomViewProviderTest");
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_excludes_the_resolved_custom_view_from_the_descriptor_get_by_id_OwnershipBased_message()
    {
        // The GET-by-id mirror of the sibling above. OwnershipBased is what fails the request closed, so it
        // belongs in the message; the resolved custom view is supported on this path too and must not be
        // named alongside it.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            new DescriptorGetByIdRequest(
                CreateQueryMappingSet(SqlDialect.Pgsql, CreateSupportedDescriptorQueryCapability()),
                _descriptorResource,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-161616161616")),
                RelationalGetRequestReadMode.ExternalResponse,
                [
                    CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                    CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                ],
                readableProfileProjectionContext: null,
                new TraceId("descriptor-get-custom-view-ownership"),
                new RelationalAuthorizationContext([], ["uri://ed-fi.org/"])
            )
        );

        result
            .Should()
            .BeOfType<GetResult.GetFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased)
            .And.NotContain("StudentWithCustomViewProviderTest");
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_validates_a_descriptor_custom_view_configured_after_OwnershipBased()
    {
        // The inverse configured order of the sibling above, with the same outcome: OwnershipBased executes
        // last per auth.md "Execution order" no matter where the CMS placed it, so the descriptor custom
        // view is still validated ahead of the 501.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased)
            .And.NotContain("StudentWithCustomViewProviderTest");
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [TestCase(
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        typeof(QueryResult.QueryFailureNotImplemented)
    )]
    [TestCase("InvalidRelationshipStrategy", typeof(QueryResult.QueryFailureSecurityConfiguration))]
    public async Task It_still_returns_the_descriptor_relationship_terminal_alongside_OwnershipBased(
        string relationshipStrategyName,
        Type expectedFailureType
    )
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator(relationshipStrategyName),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType(expectedFailureType);
    }

    [Test]
    public async Task It_reports_the_specific_no_join_path_error_for_a_descriptor_query_custom_view()
    {
        // Meeting is a known resource, so the basis resolves, but the descriptor has no reference path to
        // it. That is NoCustomViewJoinPath, which must report the specific join-path message the regular
        // resource GET-many path uses — not the generic unknown-strategy message.
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("MeetingWithCustomViewProviderTest"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("No DocumentId join path could be resolved");
        failure.Errors[0].Should().EndWith("Should a different authorization strategy be used?");
        failure.Errors[0].Should().Contain("auth.MeetingWithCustomViewProviderTest");
        failure.Errors[0].Should().Contain("MeetingWithCustomViewProviderTest");
        failure.Errors[0].Should().NotContain("is not a recognized built-in strategy");
        failure
            .Errors[0]
            .Should()
            .NotBe(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
                    "MeetingWithCustomViewProviderTest",
                ])
            );
        failure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be("RelationshipAuthorization.NoCustomViewJoinPath");
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_an_unknown_custom_view_basis_for_descriptor_query_ahead_of_OwnershipBased()
    {
        // Custom view-based executes before Ownership, so its configuration failure must not be hidden
        // by the OwnershipBased known-but-not-enabled terminal.
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("MissingBasisWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        commandExecutor.Commands.Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_short_circuits_invalid_descriptor_query_ids_to_an_empty_page_without_executing_sql(
        bool totalCount
    )
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        DocumentCacheReadAccelerationQuerySelectionResult selectionResult = null!;
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                return selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>()
                    .Subject.Result;
            });
        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                queryElements: [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
                totalCount: totalCount
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(totalCount ? 0 : null);
        success.SelectionSkipped.Should().BeTrue();
        selectionResult.Should().BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>();
        commandExecutor.Commands.Should().BeEmpty();
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_returns_zero_size_descriptor_query_pages_before_exposing_cache_candidate_pages(
        bool totalCount
    )
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution(
                totalCount
                    ?
                    [
                        InMemoryRelationalResultSet.Create(
                            RelationalAccessTestData.CreateRow(("TotalCount", 7L))
                        ),
                        InMemoryRelationalResultSet.Create(),
                    ]
                    : [InMemoryRelationalResultSet.Create()]
            ),
        ]);
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        DocumentCacheReadAccelerationQuerySelectionResult selectionResult = null!;
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                return selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>()
                    .Subject.Result;
            });
        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(SqlDialect.Pgsql, totalCount: totalCount, limit: 0)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(totalCount ? 7 : null);

        // The candidate command executed and selected nothing. An empty body alone must never be read as
        // a skipped selection, or a normal empty page would be reported as costing no database work.
        success.SelectionSkipped.Should().BeFalse();
        selectionResult.Should().BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>();
        RelationalCommand command = commandExecutor.Commands.Should().ContainSingle().Subject;
        AssertDescriptorCandidateCommandOmitsBodyColumns(command);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_validates_descriptor_custom_view_before_namespace_no_prefixes_when_custom_view_is_configured_first()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>();
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("ON", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_returns_descriptor_namespace_no_prefixes_without_custom_view_validation_when_namespace_is_configured_first()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
            ],
            namespacePrefixes: []
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>();
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .NotContain(sql => sql.Contains("1 = 0", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_validates_only_descriptor_custom_views_before_mssql_namespace_parameter_limit()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        string[] namespacePrefixes =
        [
            .. Enumerable.Range(1, 2096).Select(static index => $"uri://district-{index}.example/"),
        ];
        var request = CreateQueryRequest(
            SqlDialect.Mssql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator("StudentWithLaterCustomViewProviderTest"),
            ],
            namespacePrefixes: namespacePrefixes
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("StudentWithCustomViewProviderTest", StringComparison.Ordinal)
                && !sql.Contains("StudentWithLaterCustomViewProviderTest", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_validates_descriptor_custom_views_before_empty_page_preprocessing_success()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateHandler(commandExecutor);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            queryElements: [CreateQueryElement("id", "$.id", "not-a-valid-uuid", "string")],
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithCustomViewProviderTest"),
            ]
        );

        QueryResult result = await sut.HandleQueryAsync(request);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.SelectionSkipped.Should().BeTrue();
        commandExecutor
            .Commands.Select(command => command.CommandText)
            .Should()
            .ContainSingle(sql => sql.Contains("LIMIT 0", StringComparison.Ordinal));
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

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_uses_the_same_descriptor_query_scaffolding_for_candidate_and_full_row_projections(
        SqlDialect dialect
    )
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-292929292929");
        var fullRowCommandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 1))),
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid)),
            ]),
        ]);
        var candidateCommandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 1))),
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid)),
            ]),
        ]);
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
        var fullRowHandler = CreateHandler(fullRowCommandExecutor);

        QueryResult fullRowResult = await fullRowHandler.HandleQueryAsync(request);

        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var readAccelerationRequest = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                var selectionResult = await readAccelerationRequest
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>();

                return new QueryResult.QuerySuccess([], TotalCount: null);
            });
        var candidateHandler = CreateHandler(
            candidateCommandExecutor,
            readAccelerationCoordinator: readAccelerationCoordinator
        );

        QueryResult candidateResult = await candidateHandler.HandleQueryAsync(request);

        fullRowResult.Should().BeOfType<QueryResult.QuerySuccess>();
        candidateResult.Should().BeOfType<QueryResult.QuerySuccess>();
        RelationalCommand fullRowCommand = fullRowCommandExecutor.Commands.Should().ContainSingle().Subject;
        RelationalCommand candidateCommand = candidateCommandExecutor
            .Commands.Should()
            .ContainSingle()
            .Subject;
        candidateCommand.CommandText.Should().Contain("COUNT(1)");
        fullRowCommand.CommandText.Should().Contain("COUNT(1)");
        AssertDescriptorCandidateCommandOmitsBodyColumns(candidateCommand);
        AssertDescriptorMaterializationCommandSelectsBodyColumns(fullRowCommand);
        AssertDescriptorReadCommandsShareScaffolding(candidateCommand, fullRowCommand, "page_document_ids");
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
        // See sibling GET-by-id test: Namespace is read nullably so CodeValue is now the first
        // required column whose null value the reader trips on when the LEFT JOIN finds no row.
        failure.FailureMessage.Should().Contain("dms.Descriptor.CodeValue must not be null.");
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
        firstDocument["_etag"]!.GetValue<string>().Should().Be(ExpectedComposedDescriptorEtag(42L));
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
        secondDocument["_etag"]!.GetValue<string>().Should().Be(ExpectedComposedDescriptorEtag(42L));
        secondDocument["Uri"].Should().BeNull();
        secondDocument["Discriminator"].Should().BeNull();
        secondDocument["ChangeVersion"].Should().BeNull();
        commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_exposes_an_authorized_descriptor_query_candidate_page_to_read_acceleration()
    {
        var firstDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-999999999991");
        var secondDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-999999999992");
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 7))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(firstDocumentUuid, documentId: 101L, codeValue: "Alternative"),
                    CreateDescriptorRow(secondDocumentUuid, documentId: 205L, codeValue: "Charter")
                ),
            ]),
        ]);
        DocumentCacheReadAccelerationQueryRequest capturedRequest = null!;
        DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage capturedSelection = null!;

        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                capturedRequest = request;
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                capturedSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                    .Subject;

                return new QueryResult.QuerySuccess([], TotalCount: null);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql, totalCount: true));

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        capturedRequest.ResourceKind.Should().Be(DocumentCacheReadAccelerationResourceKind.Descriptor);
        capturedRequest.SelectAuthorizedCandidatePage.Should().NotBeNull();
        capturedSelection
            .AuthorizedCandidatePage.Should()
            .BeEquivalentTo(
                new DocumentCacheReadAccelerationCandidatePage(
                    [
                        new DocumentCacheReadAccelerationCandidate(
                            101L,
                            new DocumentUuid(firstDocumentUuid),
                            13,
                            42L,
                            new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero)
                        ),
                        new DocumentCacheReadAccelerationCandidate(
                            205L,
                            new DocumentUuid(secondDocumentUuid),
                            13,
                            42L,
                            new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero)
                        ),
                    ],
                    TotalCount: 7,
                    ContinuationBoundary: new PageContinuationBoundary(
                        205L,
                        AllowsDocumentIdContinuation: true
                    ),
                    IncludesTotalCount: true
                )
            );
        commandExecutor.Commands.Should().ContainSingle();
        AssertDescriptorCandidateCommandOmitsBodyColumns(commandExecutor.Commands[0]);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    // A traditional descriptor page anchored on ContentVersion shapes a cache-served response from a
    // candidate page that reports the selected maximum without the continuation eligibility that
    // maximum cannot carry.
    [Test]
    public async Task It_exposes_a_windowed_descriptor_candidate_page_that_cannot_anchor_a_continuation()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-999999999994");
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(documentUuid, documentId: 205L, codeValue: "Charter")
                ),
            ]),
        ]);
        DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage capturedSelection = null!;

        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(call.GetArgument<CancellationToken>(1))
                    .ConfigureAwait(false);
                capturedSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                    .Subject;

                return new QueryResult.QuerySuccess([], TotalCount: null);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                changeVersionRange: new ChangeVersionRange(null, 900L),
                pageOrderingMode: PageOrderingMode.ContentVersion
            )
        );

        capturedSelection
            .AuthorizedCandidatePage.ContinuationBoundary.Should()
            .Be(new PageContinuationBoundary(205L, AllowsDocumentIdContinuation: false));
    }

    // An accelerated selection that returned no rows still ran under an ordering, so it answers the
    // continuation question the same way a non-empty one does. Left on the permissive default, an empty
    // windowed page would tell Core a walk had ended that could never have been continued at all.
    [Test]
    public async Task It_withholds_continuation_from_an_empty_windowed_descriptor_candidate_selection()
    {
        var capturedSuccess = await SelectEmptyDescriptorCandidatePageAsync(
            new ChangeVersionRange(null, 900L),
            PageOrderingMode.ContentVersion
        );

        capturedSuccess.EdfiDocs.Should().BeEmpty();
        capturedSuccess.HighestSelectedDocumentId.Should().BeNull();
        capturedSuccess.AllowsDocumentIdContinuation.Should().BeFalse();
    }

    // The unwindowed page really is ordered by DocumentId, so an empty selection there ends the walk.
    // Withholding continuation from every empty page would erase that distinction.
    [Test]
    public async Task It_keeps_continuation_for_an_empty_unwindowed_descriptor_candidate_selection()
    {
        var capturedSuccess = await SelectEmptyDescriptorCandidatePageAsync(changeVersionRange: null);

        capturedSuccess.EdfiDocs.Should().BeEmpty();
        capturedSuccess.HighestSelectedDocumentId.Should().BeNull();
        capturedSuccess.AllowsDocumentIdContinuation.Should().BeTrue();
    }

    /// <summary>
    /// Drives the read-acceleration path to a candidate selection that returns no rows and hands back
    /// the short-circuit success the selection completed with.
    /// </summary>
    private static async Task<QueryResult.QuerySuccess> SelectEmptyDescriptorCandidatePageAsync(
        ChangeVersionRange? changeVersionRange,
        PageOrderingMode pageOrderingMode = PageOrderingMode.DocumentId
    )
    {
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        QueryResult.QuerySuccess capturedSuccess = null!;

        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(call.GetArgument<CancellationToken>(1))
                    .ConfigureAwait(false);
                capturedSuccess = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>()
                    .Which.Result.Should()
                    .BeOfType<QueryResult.QuerySuccess>()
                    .Subject;

                return capturedSuccess;
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Pgsql,
                changeVersionRange: changeVersionRange,
                pageOrderingMode: pageOrderingMode
            )
        );

        return capturedSuccess;
    }

    [Test]
    public async Task It_wraps_a_provider_error_raised_by_descriptor_custom_view_selected_page_fallback()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-999999999993");
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new StubDbException("custom view does not exist during fallback");

        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<IRelationalCommandReader, CancellationToken, Task<DescriptorQueryCandidatePage>>
                    >._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DescriptorQueryCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                    readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create(
                                CreateDescriptorRow(documentUuid, documentId: 101L)
                            ),
                        ]),
                        cancellationToken
                    )
            );
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<IReadOnlyList<DescriptorReadRow>>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                var candidateSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                    .Subject;

                return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
            });
        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);
        var request = CreateQueryRequest(
            SqlDialect.Pgsql,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolTypeDescriptorWithCustomViewProviderTest"),
            ]
        );

        var action = () => sut.HandleQueryAsync(request);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<DescriptorQueryRowsPage>>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(SqlDialect.Pgsql, """ORDER BY selected_document_ids."Ordinal" ASC""")]
    [TestCase(SqlDialect.Mssql, "ORDER BY selected_document_ids.[Ordinal] ASC")]
    public async Task It_preserves_selected_descriptor_query_order_when_fallback_hydration_returns_rows_out_of_order(
        SqlDialect dialect,
        string expectedOrderByFragment
    )
    {
        var firstDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-343434343434");
        var secondDocumentUuid = Guid.Parse("bbbbbbbb-1111-2222-3333-343434343434");
        var newlyMatchingDocumentUuid = Guid.Parse("cccccccc-1111-2222-3333-343434343434");
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 7))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        firstDocumentUuid,
                        documentId: 101L,
                        shortDescription: "First before fallback",
                        contentVersion: 42L
                    ),
                    CreateDescriptorRow(
                        secondDocumentUuid,
                        documentId: 205L,
                        shortDescription: "Second before fallback",
                        contentVersion: 43L
                    )
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        secondDocumentUuid,
                        documentId: 205L,
                        shortDescription: "Second after fallback",
                        contentVersion: 43L
                    ),
                    CreateDescriptorRow(
                        newlyMatchingDocumentUuid,
                        documentId: 999L,
                        shortDescription: "Newly matching row",
                        contentVersion: 999L
                    ),
                    CreateDescriptorRow(
                        firstDocumentUuid,
                        documentId: 101L,
                        shortDescription: "First after fallback",
                        contentVersion: 42L
                    )
                ),
            ]),
        ]);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                var candidateSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                    .Subject;
                candidateSelection.AuthorizedCandidatePage.IncludesTotalCount.Should().BeTrue();
                candidateSelection.AuthorizedCandidatePage.TotalCount.Should().Be(7);

                return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(CreateQueryRequest(dialect, totalCount: true));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(7);
        success.EdfiDocs.Should().HaveCount(2);
        success
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(firstDocumentUuid.ToString(), secondDocumentUuid.ToString());
        success
            .EdfiDocs.Select(document => document!["shortDescription"]!.GetValue<string>())
            .Should()
            .Equal("First after fallback", "Second after fallback");
        success.EdfiDocs[0]!["_etag"]!.GetValue<string>().Should().Be(ExpectedComposedDescriptorEtag(42L));
        success.EdfiDocs[1]!["_etag"]!.GetValue<string>().Should().Be(ExpectedComposedDescriptorEtag(43L));
        commandExecutor.Commands.Should().HaveCount(2);
        AssertDescriptorCandidateCommandOmitsBodyColumns(commandExecutor.Commands[0]);
        AssertDescriptorMaterializationCommandSelectsBodyColumns(commandExecutor.Commands[1]);
        commandExecutor.Commands[1].CommandText.Should().Contain(expectedOrderByFragment);
        commandExecutor.Commands[1].CommandText.Should().NotContain("COUNT(1)");

        if (dialect is SqlDialect.Pgsql)
        {
            commandExecutor.Commands[1].CommandText.Should().Contain("VALUES");
            commandExecutor.Commands[1].CommandText.Should().Contain("@selectedDocumentId0");
            commandExecutor.Commands[1].CommandText.Should().Contain("@selectedDocumentId1");
            commandExecutor
                .Commands[1]
                .Parameters.Select(parameter => parameter.Value)
                .Should()
                .Equal(101L, 205L);
        }
        else
        {
            commandExecutor.Commands[1].CommandText.Should().Contain("OPENJSON(@selectedDocumentIdsJson)");
            commandExecutor.Commands[1].CommandText.Should().Contain("[DocumentId] bigint '$.DocumentId'");
            commandExecutor.Commands[1].CommandText.Should().Contain("[Ordinal] int '$.Ordinal'");
            commandExecutor.Commands[1].CommandText.Should().NotContain("VALUES");
            commandExecutor.Commands[1].CommandText.Should().NotContain("@selectedDocumentId0");

            RelationalParameter parameter = commandExecutor
                .Commands[1]
                .Parameters.Should()
                .ContainSingle()
                .Subject;
            parameter.Name.Should().Be("@selectedDocumentIdsJson");
            parameter.Value.Should().BeOfType<string>();
            parameter.ConfigureParameter.Should().NotBeNull();

            using var jsonDocument = JsonDocument.Parse((string)parameter.Value!);
            jsonDocument.RootElement.GetArrayLength().Should().Be(2);
            jsonDocument.RootElement[0].GetProperty("DocumentId").GetInt64().Should().Be(101L);
            jsonDocument.RootElement[0].GetProperty("Ordinal").GetInt32().Should().Be(0);
            jsonDocument.RootElement[1].GetProperty("DocumentId").GetInt64().Should().Be(205L);
            jsonDocument.RootElement[1].GetProperty("Ordinal").GetInt32().Should().Be(1);
        }
    }

    [Test]
    public async Task It_reruns_descriptor_query_when_selected_page_fallback_metadata_drifts()
    {
        var selectedDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-343434343434");
        var rerunDocumentUuid = Guid.Parse("bbbbbbbb-1111-2222-3333-343434343434");
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 7))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        selectedDocumentUuid,
                        documentId: 101L,
                        shortDescription: "Before fallback",
                        contentVersion: 42L
                    )
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        selectedDocumentUuid,
                        documentId: 101L,
                        shortDescription: "Drifted selected row",
                        contentVersion: 84L
                    )
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 8))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        rerunDocumentUuid,
                        documentId: 205L,
                        shortDescription: "No-cache rerun row",
                        contentVersion: 95L
                    )
                ),
            ]),
        ]);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                var candidateSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                    .Subject;

                return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql, totalCount: true));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(8);
        success.EdfiDocs.Should().ContainSingle();
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(rerunDocumentUuid.ToString());
        success.EdfiDocs[0]!["shortDescription"]!.GetValue<string>().Should().Be("No-cache rerun row");
        commandExecutor.Commands.Should().HaveCount(3);
        commandExecutor.Commands[1].CommandText.Should().Contain("VALUES");
        commandExecutor.Commands[2].CommandText.Should().Contain("COUNT(1)");
    }

    [Test]
    public async Task It_reruns_descriptor_query_when_selected_page_fallback_returns_duplicate_document_ids()
    {
        var selectedDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-454545454545");
        var rerunDocumentUuid = Guid.Parse("bbbbbbbb-1111-2222-3333-454545454545");
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 7))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        selectedDocumentUuid,
                        documentId: 101L,
                        shortDescription: "Before fallback",
                        contentVersion: 42L
                    )
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        selectedDocumentUuid,
                        documentId: 101L,
                        shortDescription: "Duplicate fallback row",
                        contentVersion: 42L
                    ),
                    CreateDescriptorRow(
                        selectedDocumentUuid,
                        documentId: 101L,
                        shortDescription: "Duplicate fallback row again",
                        contentVersion: 42L
                    )
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("TotalCount", 8))),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        rerunDocumentUuid,
                        documentId: 205L,
                        shortDescription: "No-cache rerun row",
                        contentVersion: 95L
                    )
                ),
            ]),
        ]);
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                var selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                var candidateSelection = selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                    .Subject;

                return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
            });

        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(CreateQueryRequest(SqlDialect.Pgsql, totalCount: true));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(8);
        success.EdfiDocs.Should().ContainSingle();
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(rerunDocumentUuid.ToString());
        success.EdfiDocs[0]!["shortDescription"]!.GetValue<string>().Should().Be("No-cache rerun row");
        commandExecutor.Commands.Should().HaveCount(3);
        commandExecutor.Commands[1].CommandText.Should().Contain("VALUES");
        commandExecutor.Commands[2].CommandText.Should().Contain("COUNT(1)");
    }

    [Test]
    public async Task It_applies_readable_profile_projection_and_varies_the_etag_by_profile_for_query_items()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-888888888888");
        var projectionContext = CreateReadableProfileProjectionContext();
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
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
        var unprofiledEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: false,
                ContentVersion: 42L
            )
        );
        var profiledEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                projectionContext.ProfileName,
                LinksEnabled: false,
                ContentVersion: 42L
            )
        );
        profiledEtag.Should().NotBe(unprofiledEtag);
        var projectedDocument = JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-888888888888",
              "_etag": "",
              "_lastModifiedDate": "2026-05-05T14:30:45Z",
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Alternative",
              "description": "Alternative school type"
            }
            """
        )!;
        projectedDocument["_etag"] = profiledEtag;
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
        projectedItem["_etag"]!.GetValue<string>().Should().Be(profiledEtag);
        projectedItem["_etag"]!.GetValue<string>().Should().NotBe(unprofiledEtag);

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

        return new DescriptorGetByIdRequest(
            mappingSet,
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
        string[]? namespacePrefixes = null,
        DescriptorQueryCapability? descriptorQueryCapability = null,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null,
        int? limit = 25,
        int? offset = 0,
        bool includeDescriptorMetadata = true,
        CollectionPaging? paging = null,
        ChangeVersionRange? changeVersionRange = null,
        PageOrderingMode pageOrderingMode = PageOrderingMode.DocumentId
    )
    {
        var mappingSet = CreateQueryMappingSet(
            dialect,
            descriptorQueryCapability ?? CreateSupportedDescriptorQueryCapability(),
            includeDescriptorMetadata
        );

        return new DescriptorQueryRequest(
            mappingSet,
            _descriptorResource,
            queryElements ?? [],
            paging
                ?? new CollectionPaging.Traditional(
                    new PaginationParameters(
                        Limit: limit,
                        Offset: offset,
                        TotalCount: totalCount,
                        MaximumPageSize: 500
                    )
                ),
            authorizationStrategyEvaluators ?? [],
            readableProfileProjectionContext,
            new TraceId("descriptor-query-trace"),
            pageOrderingMode,
            new RelationalAuthorizationContext([], namespacePrefixes ?? []),
            changeVersionRange
        );
    }

    private static AuthorizationStrategyEvaluator CreateAuthorizationStrategyEvaluator(string strategyName) =>
        new(strategyName, [], FilterOperator.And);

    private static DescriptorReadHandler CreateHandler(
        IRelationalCommandExecutor commandExecutor,
        IReadableProfileProjector? readableProfileProjector = null,
        ICustomViewAuthorizationExecutor? customViewAuthorizationExecutor = null
    ) =>
        CreateHandler(
            commandExecutor,
            PassthroughDocumentCacheReadAccelerationCoordinator.Instance,
            readableProfileProjector,
            customViewAuthorizationExecutor
        );

    private static DescriptorReadHandler CreateHandler(
        IRelationalCommandExecutor commandExecutor,
        IDocumentCacheReadAccelerationCoordinator readAccelerationCoordinator,
        IReadableProfileProjector? readableProfileProjector = null,
        ICustomViewAuthorizationExecutor? customViewAuthorizationExecutor = null
    )
    {
        return new DescriptorReadHandler(
            commandExecutor,
            readableProfileProjector ?? A.Fake<IReadableProfileProjector>(),
            _servedEtagComposer,
            NullLogger<DescriptorReadHandler>.Instance,
            readAccelerationCoordinator,
            customViewAuthorizationExecutor ?? A.Fake<ICustomViewAuthorizationExecutor>()
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
        )
        {
            ProfileName = "Sample-Profile",
        };
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
        DescriptorQueryCapability descriptorQueryCapability,
        bool includeDescriptorMetadata = true
    )
    {
        var mappingSet = CreateMappingSet(dialect);

        // A SharedDescriptorTable resource with no descriptor metadata has no resolvable Namespace
        // column, which is the namespace planner's no-usable-root-column case.
        DescriptorMetadata? descriptorMetadata = includeDescriptorMetadata
            ? new DescriptorMetadata(
                new DescriptorColumnContract(
                    Namespace: new DbColumnName("Namespace"),
                    CodeValue: new DbColumnName("CodeValue"),
                    ShortDescription: null,
                    Description: null,
                    EffectiveBeginDate: null,
                    EffectiveEndDate: null,
                    Discriminator: null
                ),
                DiscriminatorStrategy.ResourceKeyId
            )
            : null;

        var concreteResources = mappingSet
            .Model.ConcreteResourcesInNameOrder.Select(resource =>
                resource.ResourceKey.Resource == _descriptorResource
                    ? resource with
                    {
                        DescriptorMetadata = descriptorMetadata,
                        RelationalModel = resource.RelationalModel with
                        {
                            DocumentReferenceBindings =
                            [
                                new DocumentReferenceBinding(
                                    IsIdentityComponent: false,
                                    JsonPathExpressionCompiler.Compile("$.studentReference"),
                                    resource.RelationalModel.Root.Table,
                                    new DbColumnName("DocumentId"),
                                    _requestResource,
                                    []
                                ),
                            ],
                        },
                    }
                    : resource
            )
            .ToArray();

        return mappingSet with
        {
            Model = mappingSet.Model with { ConcreteResourcesInNameOrder = concreteResources },
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
        long contentVersion = 42L,
        DateTimeOffset? contentLastModifiedAt = null,
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
            ("ContentVersion", contentVersion),
            (
                "ContentLastModifiedAt",
                contentLastModifiedAt ?? new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero)
            ),
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

    private static string ExpectedComposedDescriptorEtag(long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            DescriptorEtagTestSupport.NoProfileNoLinksJsonVariantKey(
                CreateMappingSet(SqlDialect.Pgsql).Key.EffectiveSchemaHash
            )
        );

    private static void AssertDescriptorCandidateCommandOmitsBodyColumns(RelationalCommand command)
    {
        AssertDescriptorProjectionSelectsColumns(
            command,
            [
                "DocumentId",
                "DocumentUuid",
                "ContentVersion",
                "ContentLastModifiedAt",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "Discriminator",
            ]
        );
        command.CommandText.Should().NotContain("\"ShortDescription\"");
        command.CommandText.Should().NotContain("[ShortDescription]");
        command.CommandText.Should().NotContain("descriptor.\"Description\"");
        command.CommandText.Should().NotContain("descriptor.[Description]");
        command.CommandText.Should().NotContain("\"EffectiveBeginDate\"");
        command.CommandText.Should().NotContain("[EffectiveBeginDate]");
        command.CommandText.Should().NotContain("\"EffectiveEndDate\"");
        command.CommandText.Should().NotContain("[EffectiveEndDate]");
    }

    private static void AssertDescriptorMaterializationCommandSelectsBodyColumns(RelationalCommand command)
    {
        AssertDescriptorProjectionSelectsColumns(
            command,
            [
                "DocumentId",
                "DocumentUuid",
                "ContentVersion",
                "ContentLastModifiedAt",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "EffectiveBeginDate",
                "EffectiveEndDate",
                "Discriminator",
            ]
        );
    }

    private static void AssertDescriptorProjectionSelectsColumns(
        RelationalCommand command,
        IReadOnlyList<string> columnNames
    )
    {
        foreach (string columnName in columnNames)
        {
            bool selectsColumn =
                command.CommandText.Contains($"AS \"{columnName}\"", StringComparison.Ordinal)
                || command.CommandText.Contains($"AS [{columnName}]", StringComparison.Ordinal);

            selectsColumn.Should().BeTrue($"the descriptor projection should select {columnName}");
        }
    }

    private static void AssertDescriptorReadCommandsShareScaffolding(
        RelationalCommand candidateCommand,
        RelationalCommand fullRowCommand,
        string documentIdSourceAlias
    )
    {
        RemoveDescriptorProjection(candidateCommand.CommandText, documentIdSourceAlias)
            .Should()
            .Be(RemoveDescriptorProjection(fullRowCommand.CommandText, documentIdSourceAlias));
        candidateCommand
            .Parameters.Select(parameter =>
                (parameter.Name, parameter.Value, HasConfiguration: parameter.ConfigureParameter is not null)
            )
            .Should()
            .Equal(
                fullRowCommand.Parameters.Select(parameter =>
                    (
                        parameter.Name,
                        parameter.Value,
                        HasConfiguration: parameter.ConfigureParameter is not null
                    )
                )
            );
    }

    private static string RemoveDescriptorProjection(string commandText, string documentIdSourceAlias)
    {
        string documentIdProjection = commandText.Contains(
            $"{documentIdSourceAlias}.\"DocumentId\" AS \"DocumentId\"",
            StringComparison.Ordinal
        )
            ? $"{documentIdSourceAlias}.\"DocumentId\" AS \"DocumentId\""
            : $"{documentIdSourceAlias}.[DocumentId] AS [DocumentId]";
        int documentIdProjectionIndex = commandText.IndexOf(documentIdProjection, StringComparison.Ordinal);
        documentIdProjectionIndex.Should().BeGreaterThanOrEqualTo(0);
        int selectIndex = commandText.LastIndexOf(
            "SELECT",
            documentIdProjectionIndex,
            StringComparison.Ordinal
        );
        selectIndex.Should().BeGreaterThanOrEqualTo(0);
        // Emitted SQL is canonicalized to Unix line endings, so the line break is matched literally
        // rather than through the platform's newline.
        int fromIndex = commandText.IndexOf("\nFROM ", documentIdProjectionIndex, StringComparison.Ordinal);
        fromIndex.Should().BeGreaterThan(documentIdProjectionIndex);

        return commandText[..(selectIndex + "SELECT".Length)] + commandText[fromIndex..];
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

    private static NamespaceAuthorizationCheckSpec CreateNamespaceCheck(int rawConfiguredIndex) =>
        new(
            0,
            NamespaceAuthorizationCheckValueSource.Stored,
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            new DbColumnName("Namespace"),
            RawConfiguredIndex: rawConfiguredIndex
        );

    private static PageDocumentIdAuthorizationCustomViewCheck CreateCustomViewCheck(
        string strategyName,
        int rawConfiguredIndex = 0
    ) =>
        new(
            strategyName,
            rawConfiguredIndex,
            new DbTableName(new DbSchemaName("auth"), strategyName),
            new DbColumnName("DocumentId"),
            [
                new ColumnPathStep(
                    new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                    new DbColumnName("DocumentId"),
                    null,
                    null
                ),
            ],
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            new DbColumnName("DocumentId")
        );

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

    private sealed class StubDbException(string message) : DbException(message);
}
