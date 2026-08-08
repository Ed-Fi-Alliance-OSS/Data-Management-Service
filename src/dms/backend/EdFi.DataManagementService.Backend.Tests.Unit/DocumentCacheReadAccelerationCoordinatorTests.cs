// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheReadAccelerationCoordinator")]
public class Given_DocumentCacheReadAccelerationCoordinator
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );
    private static readonly DocumentUuid SecondDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );
    private static readonly QualifiedResourceName Resource = new("Ed-Fi", "Student");
    private static readonly MappingSet MappingSet = RelationalAccessTestData.CreateMappingSet(Resource);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [Test]
    public async Task It_bypasses_cache_when_read_acceleration_is_disabled()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        using var cancellationSource = new CancellationTokenSource();
        var sut = CreateCoordinator(
            readAccelerationEnabled: false,
            lookupAdapter,
            CreateRegistry(ExecutionContext())
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, cancellationToken) =>
                {
                    fallbackContext = context;
                    cancellationToken.Should().Be(cancellationSource.Token);
                    return Task.FromResult<GetResult>(fallbackResult);
                }
            ),
            cancellationSource.Token
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext
            .Reason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.ReadAccelerationDisabled);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_bypasses_cache_for_stored_document_gets()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext())
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<GetResult>(fallbackResult);
                },
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext.Reason.Should().Be(DocumentCacheReadAccelerationFallbackReason.NotExternalRead);
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_bypasses_cache_when_the_selected_target_is_unresolved()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(readAccelerationEnabled: true, lookupAdapter, CreateRegistry());

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<GetResult>(fallbackResult);
                }
            )
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext.Reason.Should().Be(DocumentCacheReadAccelerationFallbackReason.UnresolvedTarget);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_uses_the_cache_lookup_adapter_for_an_authorized_external_get_on_an_exact_target()
    {
        var cachedResult = new GetResult.GetSuccess(
            DocumentUuid,
            JsonNode.Parse("""{"id":"cached"}""")!,
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Hit(cachedResult),
        };
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );
        using var cancellationSource = new CancellationTokenSource();

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists())
            ),
            cancellationSource.Token
        );

        result.Should().BeSameAs(cachedResult);
        lookupAdapter.GetByIdAttempts.Should().Be(1);
        lookupAdapter.LastGetByIdTargetContext.Should().BeSameAs(executionContext);
        lookupAdapter.LastGetByIdCancellationToken.Should().Be(cancellationSource.Token);
    }

    [Test]
    public async Task It_uses_one_cache_lookup_for_an_authorized_query_page_on_an_exact_target()
    {
        var cachedResult = new QueryResult.QuerySuccess(
            [JsonNode.Parse("""{"id":"cached"}""")!],
            2,
            HighestSelectedDocumentId: 346
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Hit(cachedResult),
        };
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );
        using var cancellationSource = new CancellationTokenSource();
        var candidatePage = CandidatePage(
            [Candidate(documentId: 345, contentVersion: 91), Candidate(documentId: 346, contentVersion: 92)],
            totalCount: 2,
            highestSelectedDocumentId: 346
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("fallback")),
                candidatePage
            ),
            cancellationSource.Token
        );

        result.Should().BeSameAs(cachedResult);
        lookupAdapter.QueryAttempts.Should().Be(1);
        lookupAdapter.LastQueryRequest!.AuthorizedCandidatePage.Should().Be(candidatePage);
        lookupAdapter.LastQueryTargetContext.Should().BeSameAs(executionContext);
        lookupAdapter.LastQueryCancellationToken.Should().Be(cancellationSource.Token);
    }

    [Test]
    public async Task It_hydrates_the_complete_authorized_query_page_when_lookup_falls_back()
    {
        var fallbackResult = new QueryResult.QuerySuccess(
            [JsonNode.Parse("""{"id":"relational"}""")!],
            2,
            HighestSelectedDocumentId: 346
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupStale
            ),
        };
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );
        var candidatePage = CandidatePage(
            [Candidate(documentId: 345, contentVersion: 91), Candidate(documentId: 346, contentVersion: 92)],
            totalCount: 2,
            highestSelectedDocumentId: 346
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<QueryResult>(fallbackResult);
                },
                candidatePage
            )
        );

        result.Should().BeSameAs(fallbackResult);
        lookupAdapter.QueryAttempts.Should().Be(1);
        fallbackContext.Reason.Should().Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupStale);
        fallbackContext.TargetContext.Should().BeSameAs(executionContext);
    }

    [Test]
    public async Task It_returns_an_empty_authorized_query_page_without_lookup_or_relational_fallback()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackAttempts = 0;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext())
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) =>
                {
                    fallbackAttempts++;
                    return Task.FromResult<QueryResult>(
                        new QueryResult.QueryFailureKnownError("fallback should not run")
                    );
                },
                new DocumentCacheReadAccelerationCandidatePage(
                    [],
                    TotalCount: 0,
                    HighestSelectedDocumentId: null
                )
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        success.HighestSelectedDocumentId.Should().BeNull();
        lookupAdapter.QueryAttempts.Should().Be(0);
        fallbackAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_preserves_relational_fallback_when_lookup_is_not_candidate_ready()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new QueryResult.QuerySuccess([], 0);
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<QueryResult>(fallbackResult);
                }
            )
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext
            .Reason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable);
        fallbackContext.TargetContext.Should().BeSameAs(executionContext);
        lookupAdapter.QueryAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_selects_an_authorized_get_candidate_after_target_resolution_before_lookup()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );
        var selectedCandidate = Candidate() with { DocumentId = 987, ContentVersion = 654 };
        var selectionAttempts = 0;

        await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists()),
                selectAuthorizedCandidate: _ =>
                {
                    selectionAttempts++;
                    return Task.FromResult<DocumentCacheReadAccelerationGetByIdSelectionResult>(
                        new DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate(
                            selectedCandidate,
                            (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists())
                        )
                    );
                }
            )
        );

        selectionAttempts.Should().Be(1);
        lookupAdapter.GetByIdAttempts.Should().Be(1);
        lookupAdapter
            .LastGetByIdRequest!.LookupReadiness.Should()
            .Be(DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate);
        lookupAdapter.LastGetByIdRequest.AuthorizedCandidate.Should().Be(selectedCandidate);
        lookupAdapter.LastGetByIdTargetContext.Should().BeSameAs(executionContext);
    }

    [Test]
    public async Task It_selects_an_authorized_query_candidate_page_after_target_resolution_before_lookup()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );
        var selectedCandidatePage = CandidatePage(
            [Candidate(documentId: 987, contentVersion: 654)],
            totalCount: 1,
            highestSelectedDocumentId: 987
        );
        var selectionAttempts = 0;

        await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) => Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("fallback")),
                selectAuthorizedCandidatePage: _ =>
                {
                    selectionAttempts++;
                    return Task.FromResult<DocumentCacheReadAccelerationQuerySelectionResult>(
                        new DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage(
                            selectedCandidatePage,
                            (_, _) =>
                                Task.FromResult<QueryResult>(
                                    new QueryResult.QueryFailureKnownError("fallback")
                                )
                        )
                    );
                }
            )
        );

        selectionAttempts.Should().Be(1);
        lookupAdapter.QueryAttempts.Should().Be(1);
        lookupAdapter
            .LastQueryRequest!.LookupReadiness.Should()
            .Be(DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate);
        lookupAdapter.LastQueryRequest.AuthorizedCandidatePage.Should().Be(selectedCandidatePage);
        lookupAdapter.LastQueryTargetContext.Should().BeSameAs(executionContext);
    }

    [Test]
    public async Task It_does_not_select_a_candidate_when_read_acceleration_is_disabled()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        var selectionAttempts = 0;
        var sut = CreateCoordinator(
            readAccelerationEnabled: false,
            lookupAdapter,
            CreateRegistry(ExecutionContext())
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) => Task.FromResult<GetResult>(fallbackResult),
                selectAuthorizedCandidate: _ =>
                {
                    selectionAttempts++;
                    return Task.FromResult<DocumentCacheReadAccelerationGetByIdSelectionResult>(
                        new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                            new GetResult.UnknownFailure("should not select")
                        )
                    );
                }
            )
        );

        result.Should().BeSameAs(fallbackResult);
        selectionAttempts.Should().Be(0);
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_direct_fills_get_by_id_after_successful_relational_fallback_for_a_document_miss()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        DocumentCacheMaterializationRequest materializationRequest = materializer
            .Requests.Should()
            .ContainSingle()
            .Which;
        materializationRequest.DocumentId.Should().Be(345);
        materializationRequest.Purpose.Should().Be(DocumentCacheMaterializationPurpose.DirectFill);
        materializationRequest.SelectedRequiredContentVersion.Should().Be(91);

        DocumentCacheWriterRequest writerRequest = writer.Requests.Should().ContainSingle().Which;
        writerRequest.DocumentId.Should().Be(345);
        writerRequest.Purpose.Should().Be(DocumentCacheWriterPurpose.DirectFill);
        writerRequest.Candidate.Should().NotBeNull();
        writerRequest.Candidate!.DocumentId.Should().Be(345);
    }

    [Test]
    public async Task It_skips_direct_fill_when_relational_get_fallback_is_not_successful()
    {
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists())
            )
        );

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_direct_fills_only_query_miss_candidates_that_survive_relational_fallback()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: SecondDocumentUuid
        );
        var candidatePage = CandidatePage([first, second], totalCount: 2, highestSelectedDocumentId: 346);
        var fallbackResult = new QueryResult.QuerySuccess(
            [new JsonObject { ["id"] = SecondDocumentUuid.Value.ToString() }],
            2,
            HighestSelectedDocumentId: 346
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss,
                [first, second]
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<QueryResult>(fallbackResult),
                candidatePage
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Select(request => request.DocumentId).Should().Equal(346);
        writer.Requests.Select(request => request.DocumentId).Should().Equal(346);
    }

    [Test]
    public async Task It_skips_tracking_query_direct_fill_for_page_level_fallback()
    {
        var fallbackResult = new QueryResult.QuerySuccess(
            [new JsonObject { ["id"] = DocumentUuid.Value.ToString() }],
            1,
            HighestSelectedDocumentId: 345
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<QueryResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_direct_fills_get_by_id_in_rebuilding_after_a_fenced_cache_read()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext(lifecycleState: DocumentCacheLifecycleState.Rebuilding)),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Select(request => request.DocumentId).Should().Equal(345);
        writer.Requests.Select(request => request.DocumentId).Should().Equal(345);
    }

    [Test]
    public async Task It_skips_direct_fill_after_cache_unavailable_fallback()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
    }

    [TestCase(DocumentCacheLifecycleState.Disabled, false)]
    [TestCase(DocumentCacheLifecycleState.Resetting, false)]
    [TestCase(DocumentCacheLifecycleState.Tracking, true)]
    public async Task It_skips_direct_fill_when_target_lifecycle_is_not_write_eligible(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(
                ExecutionContext(
                    lifecycleState: lifecycleState,
                    cacheAheadRecoveryRequired: cacheAheadRecoveryRequired
                )
            ),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_relational_result_when_direct_fill_materializer_fails()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var materializer = new RecordingMaterializer
        {
            ExceptionToThrow = new InvalidOperationException("projection failure"),
        };
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            new RecordingLookupAdapter
            {
                GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                    DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
                ),
            },
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().ContainSingle();
        writer.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_relational_result_when_direct_fill_writer_fails()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter
        {
            ExceptionToThrow = new InvalidOperationException("writer failure"),
        };
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            new RecordingLookupAdapter
            {
                GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                    DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
                ),
            },
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().ContainSingle();
        writer.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task It_stops_query_direct_fill_when_timeout_budget_is_exhausted()
    {
        var fallbackResult = new QueryResult.QuerySuccess(
            [new JsonObject { ["id"] = DocumentUuid.Value.ToString() }],
            1,
            HighestSelectedDocumentId: 345
        );
        var materializer = new RecordingMaterializer { DelayUntilCancellation = TimeSpan.FromSeconds(30) };
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            new RecordingLookupAdapter
            {
                QueryResult = DocumentCacheReadLookupResult<QueryResult>.Fallback(
                    DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss,
                    [Candidate()]
                ),
            },
            CreateRegistry(ExecutionContext(directFillTimeout: TimeSpan.FromMilliseconds(10))),
            materializer,
            writer
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<QueryResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().ContainSingle();
        writer.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_skips_direct_fill_when_request_is_canceled_before_fill_starts()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        using var cancellationSource = new CancellationTokenSource();
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            new RecordingLookupAdapter
            {
                GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                    DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
                ),
            },
            CreateRegistry(ExecutionContext()),
            materializer,
            writer
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) =>
                {
                    cancellationSource.Cancel();
                    return Task.FromResult<GetResult>(fallbackResult);
                }
            ),
            cancellationSource.Token
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
    }

    private static DocumentCacheReadAccelerationCoordinator CreateCoordinator(
        bool readAccelerationEnabled,
        RecordingLookupAdapter lookupAdapter,
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheMaterializer? materializer = null,
        IDocumentCacheWriter? cacheWriter = null
    )
    {
        DataStoreSelection dataStoreSelection = new();
        dataStoreSelection.SetSelectedDataStore(
            new DataStore(
                TargetKey.DataStoreId,
                "postgresql",
                "Primary",
                "Host=localhost",
                [],
                RelationalProviderToken.Postgresql,
                RelationalProviderMetadataStatus.Supported
            )
        );

        DocumentCacheOptions options = new()
        {
            Targets =
            [
                new DocumentCacheTargetOptions
                {
                    TenantKey = TargetKey.TenantKey,
                    DataStoreId = TargetKey.DataStoreId,
                },
            ],
            ReadAcceleration = new DocumentCacheReadAccelerationOptions { Enabled = readAccelerationEnabled },
        };

        return new DocumentCacheReadAccelerationCoordinator(
            Options.Create(options),
            dataStoreSelection,
            registry,
            lookupAdapter,
            materializer,
            cacheWriter
        );
    }

    private static DocumentCacheReadAccelerationGetByIdRequest CreateGetByIdRequest(
        DocumentCacheReadAccelerationLookupReadiness lookupReadiness,
        Func<DocumentCacheReadAccelerationFallbackContext, CancellationToken, Task<GetResult>> fallback,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        Func<
            CancellationToken,
            Task<DocumentCacheReadAccelerationGetByIdSelectionResult>
        >? selectAuthorizedCandidate = null
    ) =>
        new(
            TargetKey.TenantKey,
            MappingSet,
            Resource,
            DocumentUuid,
            readMode,
            DocumentCacheReadAccelerationResourceKind.Resource,
            lookupReadiness,
            fallback,
            lookupReadiness == DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate
                ? Candidate()
                : null,
            selectAuthorizedCandidate
        );

    private static DocumentCacheReadAccelerationQueryRequest CreateQueryRequest(
        DocumentCacheReadAccelerationLookupReadiness lookupReadiness,
        Func<DocumentCacheReadAccelerationFallbackContext, CancellationToken, Task<QueryResult>> fallback,
        DocumentCacheReadAccelerationCandidatePage? candidatePage = null,
        Func<
            CancellationToken,
            Task<DocumentCacheReadAccelerationQuerySelectionResult>
        >? selectAuthorizedCandidatePage = null
    ) =>
        new(
            TargetKey.TenantKey,
            MappingSet,
            Resource,
            DocumentCacheReadAccelerationResourceKind.Resource,
            lookupReadiness,
            fallback,
            lookupReadiness == DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate
                ? candidatePage ?? CandidatePage()
                : null,
            selectAuthorizedCandidatePage
        );

    private static DocumentCacheReadAccelerationCandidate Candidate(
        long documentId = 345,
        long contentVersion = 91,
        DocumentUuid? documentUuid = null
    ) =>
        new(
            documentId,
            documentUuid ?? DocumentUuid,
            ResourceKeyId: 1,
            ContentVersion: contentVersion,
            ContentLastModifiedAt: ObservedAt
        );

    private static DocumentCacheReadAccelerationCandidatePage CandidatePage(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate>? candidates = null,
        long? totalCount = 1,
        long? highestSelectedDocumentId = null
    ) => new(candidates ?? [Candidate()], totalCount, highestSelectedDocumentId);

    private static StaticTargetRegistry CreateRegistry(
        DocumentCacheTargetExecutionContext? executionContext = null
    )
    {
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = executionContext is null
            ? new DocumentCacheTargetRuntimeSnapshot([], ObservedAt)
            : new DocumentCacheTargetRuntimeSnapshot([executionContext], ObservedAt);

        DocumentCacheTargetRegistrySnapshot snapshot = executionContext is null
            ? new DocumentCacheTargetRegistrySnapshot([], ObservedAt)
            : new DocumentCacheTargetRegistrySnapshot(
                [
                    DocumentCacheTargetObservation.ResolvedEligible(
                        executionContext.TargetKey,
                        executionContext.EffectiveSettings,
                        executionContext.Generation,
                        executionContext.ProviderToken,
                        executionContext.PhysicalSourceFingerprint,
                        executionContext.Lifecycle,
                        executionContext.Inventory,
                        executionContext.EnqueueTrigger,
                        executionContext.SqlServerPrerequisites
                    ),
                ],
                ObservedAt
            );

        return new StaticTargetRegistry(snapshot, runtimeSnapshot);
    }

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheLifecycleState lifecycleState = DocumentCacheLifecycleState.Tracking,
        bool cacheAheadRecoveryRequired = false,
        TimeSpan? directFillTimeout = null
    ) =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: directFillTimeout ?? TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 3,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "Host=localhost"),
            Fingerprint,
            new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private sealed class RecordingLookupAdapter : IDocumentCacheReadLookupAdapter
    {
        public int GetByIdAttempts { get; private set; }

        public int QueryAttempts { get; private set; }

        public DocumentCacheTargetExecutionContext? LastGetByIdTargetContext { get; private set; }

        public DocumentCacheTargetExecutionContext? LastQueryTargetContext { get; private set; }

        public CancellationToken LastGetByIdCancellationToken { get; private set; }

        public CancellationToken LastQueryCancellationToken { get; private set; }

        public DocumentCacheReadAccelerationGetByIdRequest? LastGetByIdRequest { get; private set; }

        public DocumentCacheReadAccelerationQueryRequest? LastQueryRequest { get; private set; }

        public DocumentCacheReadLookupResult<GetResult> GetByIdResult { get; init; } =
            DocumentCacheReadLookupResult<GetResult>.Fallback();

        public DocumentCacheReadLookupResult<QueryResult> QueryResult { get; init; } =
            DocumentCacheReadLookupResult<QueryResult>.Fallback();

        public Task<DocumentCacheReadLookupResult<GetResult>> TryGetByIdAsync(
            DocumentCacheReadAccelerationGetByIdRequest request,
            DocumentCacheTargetExecutionContext targetContext,
            CancellationToken cancellationToken = default
        )
        {
            GetByIdAttempts++;
            LastGetByIdTargetContext = targetContext;
            LastGetByIdCancellationToken = cancellationToken;
            LastGetByIdRequest = request;
            return Task.FromResult(GetByIdResult);
        }

        public Task<DocumentCacheReadLookupResult<QueryResult>> TryQueryAsync(
            DocumentCacheReadAccelerationQueryRequest request,
            DocumentCacheTargetExecutionContext targetContext,
            CancellationToken cancellationToken = default
        )
        {
            QueryAttempts++;
            LastQueryTargetContext = targetContext;
            LastQueryCancellationToken = cancellationToken;
            LastQueryRequest = request;
            return Task.FromResult(QueryResult);
        }
    }

    private sealed class RecordingMaterializer : IDocumentCacheMaterializer
    {
        public List<DocumentCacheMaterializationRequest> Requests { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public TimeSpan? DelayUntilCancellation { get; init; }

        public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            Requests.Add(request);
            if (DelayUntilCancellation is { } delay)
            {
                await Task.Delay(delay, request.CancellationToken).ConfigureAwait(false);
                return DocumentCacheMaterializationResult.MissingSource.Instance;
            }

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return new DocumentCacheMaterializationResult.Success(MaterializedCandidate(request));
        }

        private static DocumentCacheMaterializationCandidate MaterializedCandidate(
            DocumentCacheMaterializationRequest request
        ) =>
            new(
                request.DocumentId,
                DocumentUuid,
                Resource.ProjectName,
                Resource.ResourceName,
                "1.0",
                request.SelectedRequiredContentVersion ?? 91,
                ObservedAt,
                $"stream-{request.DocumentId}",
                new JsonObject { ["id"] = DocumentUuid.Value.ToString() }
            );
    }

    private sealed class RecordingCacheWriter : IDocumentCacheWriter
    {
        public List<DocumentCacheWriterRequest> Requests { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
        {
            Requests.Add(request);
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult<DocumentCacheWriterResult>(
                new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
                    request.Candidate!,
                    request.Candidate!.ContentVersion
                )
            );
        }
    }

    private sealed class StaticTargetRegistry(
        DocumentCacheTargetRegistrySnapshot snapshot,
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = snapshot;

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = runtimeSnapshot;

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }
}
