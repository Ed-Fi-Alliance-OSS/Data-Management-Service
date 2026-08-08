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
    public async Task It_bypasses_cache_when_the_selected_data_store_is_unavailable()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            selectDataStore: false
        );

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
        fallbackContext
            .Reason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.SelectedDataStoreUnavailable);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_bypasses_cache_when_the_target_registry_is_unavailable()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(readAccelerationEnabled: true, lookupAdapter, registry: null);

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
        fallbackContext
            .Reason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.TargetRegistryUnavailable);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_bypasses_cache_when_the_request_target_key_is_invalid()
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
                tenantKey: " TenantA"
            )
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext.Reason.Should().Be(DocumentCacheReadAccelerationFallbackReason.InvalidTargetKey);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_bypasses_cache_when_the_resolved_target_has_read_acceleration_disabled()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var fallbackResult = new GetResult.GetFailureNotExists();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext(targetReadAccelerationEnabled: false))
        );

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
        fallbackContext
            .Reason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.TargetReadAccelerationDisabled);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.GetByIdAttempts.Should().Be(0);
    }

    [TestCase("provider")]
    [TestCase("connection")]
    public async Task It_bypasses_cache_when_the_resolved_target_signature_does_not_match_the_selected_data_store(
        string mismatch
    )
    {
        var cachedResult = new GetResult.GetSuccess(
            DocumentUuid,
            JsonNode.Parse("""{"id":"old-cache"}""")!,
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            JsonNode.Parse("""{"id":"selected-store"}""")!,
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Hit(cachedResult),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        DataStore selectedDataStore =
            mismatch == "provider"
                ? SelectedDataStore(RelationalProviderToken.SqlServer, "Host=localhost")
                : SelectedDataStore(RelationalProviderToken.Postgresql, "Host=changed");
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext(lifecycleState: DocumentCacheLifecycleState.Rebuilding)),
            materializer,
            writer,
            readTelemetry: telemetry,
            selectedDataStore: selectedDataStore
        );

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
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry
            .Events.Should()
            .Contain(("directFill", DocumentCacheReadTelemetryLabel.SkippedTargetMismatch));
    }

    [Test]
    public async Task It_hydrates_query_fallback_when_the_resolved_target_connection_does_not_match_the_selected_data_store()
    {
        var cachedResult = new QueryResult.QuerySuccess(
            [JsonNode.Parse("""{"id":"old-cache"}""")!],
            1,
            HighestSelectedDocumentId: 345
        );
        var fallbackResult = new QueryResult.QuerySuccess(
            [JsonNode.Parse("""{"id":"selected-store"}""")!],
            1,
            HighestSelectedDocumentId: 345
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Hit(cachedResult),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext(lifecycleState: DocumentCacheLifecycleState.Rebuilding)),
            materializer,
            writer,
            readTelemetry: telemetry,
            selectedDataStore: SelectedDataStore(RelationalProviderToken.Postgresql, "Host=changed")
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<QueryResult>(fallbackResult);
                }
            )
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext.Reason.Should().Be(DocumentCacheReadAccelerationFallbackReason.UnresolvedTarget);
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.QueryAttempts.Should().Be(0);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry
            .Events.Should()
            .Contain(("directFill", DocumentCacheReadTelemetryLabel.SkippedTargetMismatch));
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

    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupStale)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupSourceDrift)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheHitResponseShapingUnavailable)]
    public async Task It_preserves_relational_get_results_for_cache_lookup_fallbacks(
        DocumentCacheReadAccelerationFallbackReason fallbackReason
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
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(fallbackReason),
        };
        var telemetry = new RecordingReadTelemetry();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext),
            readTelemetry: telemetry
        );

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
        fallbackContext.Reason.Should().Be(fallbackReason);
        fallbackContext.TargetContext.Should().BeSameAs(executionContext);
        lookupAdapter.GetByIdAttempts.Should().Be(1);
        telemetry.Events.Should().Contain(("fallback", fallbackReason.ToString()));
        if (fallbackReason == DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable)
        {
            telemetry.Events.Should().Contain(("cacheUnavailable", fallbackReason.ToString()));
            telemetry.Events.Should().NotContain(("adapterAcquisitionFailure", fallbackReason.ToString()));
        }
    }

    [Test]
    public async Task It_records_target_health_diagnostic_for_cache_lookup_invariant_fallback()
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
                DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure,
                invariantDiagnostic: DocumentCacheReadInvariantDiagnostic.CacheHitResponseShaping(
                    DocumentCacheReadResponseShapingFailureReason.InvalidDocumentJson
                )
            ),
        };
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext),
            projectionObservationSink: observationStore,
            projectionObservationProvider: observationStore,
            timeProvider: new FixedTimeProvider(ObservedAt)
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        DocumentCacheProjectionTargetHealthSnapshot? health =
            observationStore.CurrentSnapshot.GetCurrentTarget(executionContext.TargetKey);
        health.Should().NotBeNull();
        AssertTargetInvariantDiagnostic(health!, "InvalidCachedJson");
    }

    [Test]
    public async Task It_propagates_caller_cancellation_from_cache_lookup()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var lookupAdapter = new RecordingLookupAdapter
        {
            ExceptionToThrow = new OperationCanceledException(cancellationSource.Token),
        };
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext())
        );

        Func<Task> act = async () =>
            await sut.GetByIdAsync(
                CreateGetByIdRequest(
                    DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                    (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists())
                ),
                cancellationSource.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        lookupAdapter.GetByIdAttempts.Should().Be(1);
    }

    [Test]
    public async Task It_propagates_request_abort_disposal_from_cache_lookup()
    {
        var lookupAdapter = new RecordingLookupAdapter
        {
            ExceptionToThrow = new ObjectDisposedException("request"),
        };
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext())
        );
        var fallbackAttempts = 0;

        Func<Task> act = async () =>
            await sut.GetByIdAsync(
                CreateGetByIdRequest(
                    DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                    (_, _) =>
                    {
                        fallbackAttempts++;
                        return Task.FromResult<GetResult>(new GetResult.GetFailureNotExists());
                    }
                )
            );

        await act.Should().ThrowAsync<ObjectDisposedException>();
        lookupAdapter.GetByIdAttempts.Should().Be(1);
        fallbackAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_propagates_unexpected_programming_exceptions_from_cache_lookup()
    {
        var lookupAdapter = new RecordingLookupAdapter
        {
            ExceptionToThrow = new InvalidOperationException("programming failure"),
        };
        var telemetry = new RecordingReadTelemetry();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            readTelemetry: telemetry
        );

        Func<Task> act = async () =>
            await sut.GetByIdAsync(
                CreateGetByIdRequest(
                    DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                    (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists())
                )
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("programming failure");
        telemetry
            .Events.Should()
            .Contain(("unexpectedException", DocumentCacheReadTelemetryLabel.UnexpectedException));
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

    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupStale)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupSourceDrift)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure)]
    [TestCase(DocumentCacheReadAccelerationFallbackReason.CacheHitResponseShapingUnavailable)]
    public async Task It_preserves_relational_query_results_for_cache_lookup_fallbacks(
        DocumentCacheReadAccelerationFallbackReason fallbackReason
    )
    {
        var fallbackResult = new QueryResult.QuerySuccess(
            [new JsonObject { ["id"] = DocumentUuid.Value.ToString() }],
            1,
            HighestSelectedDocumentId: 345
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Fallback(fallbackReason, [Candidate()]),
        };
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(executionContext)
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<QueryResult>(fallbackResult);
                }
            )
        );

        result.Should().BeSameAs(fallbackResult);
        fallbackContext.Reason.Should().Be(fallbackReason);
        fallbackContext.TargetContext.Should().BeSameAs(executionContext);
        lookupAdapter.QueryAttempts.Should().Be(1);
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
        StaticTargetRegistry registry = CreateRegistry(ExecutionContext());
        var sut = CreateCoordinator(readAccelerationEnabled: true, lookupAdapter, registry);

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
        fallbackContext.TargetContext.Should().BeNull();
        lookupAdapter.QueryAttempts.Should().Be(0);
        registry.CurrentRuntimeSnapshotAccesses.Should().Be(0);
    }

    private static IEnumerable<TestCaseData> CompleteGetSelectionResults()
    {
        yield return new TestCaseData(new GetResult.GetFailureNotExists()).SetName("GET complete not-exists");
        yield return new TestCaseData(new GetResult.GetFailureNotAuthorized(["denied"])).SetName(
            "GET complete authorization-denied"
        );
        yield return new TestCaseData(
            new GetResult.GetFailureNotImplemented("unsupported authorization")
        ).SetName("GET complete unsupported-authorization");
        yield return new TestCaseData(new GetResult.GetFailureRetryable()).SetName("GET complete retry");
        yield return new TestCaseData(new GetResult.GetFailureSecurityConfiguration(["security"])).SetName(
            "GET complete security-configuration"
        );
    }

    [TestCaseSource(nameof(CompleteGetSelectionResults))]
    public async Task It_returns_complete_get_selection_result_before_target_resolution(
        GetResult completeResult
    )
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        var selectionAttempts = 0;
        var fallbackAttempts = 0;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            new ThrowingTargetRegistry(),
            materializer,
            writer,
            telemetry,
            dataStoreSelection: new ThrowingDataStoreSelection()
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) =>
                {
                    fallbackAttempts++;
                    return Task.FromResult<GetResult>(new GetResult.UnknownFailure("fallback"));
                },
                selectAuthorizedCandidate: _ =>
                {
                    selectionAttempts++;
                    return Task.FromResult<DocumentCacheReadAccelerationGetByIdSelectionResult>(
                        new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(completeResult)
                    );
                }
            )
        );

        result.Should().BeSameAs(completeResult);
        selectionAttempts.Should().Be(1);
        fallbackAttempts.Should().Be(0);
        lookupAdapter.GetByIdAttempts.Should().Be(0);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry.Events.Should().BeEmpty();
        telemetry.DurationEvents.Should().BeEmpty();
    }

    private static IEnumerable<TestCaseData> CompleteQuerySelectionResults()
    {
        yield return new TestCaseData(
            new QueryResult.QueryFailureNotImplemented("unsupported authorization")
        ).SetName("QUERY complete unsupported-authorization");
        yield return new TestCaseData(new QueryResult.QueryFailureRetryable()).SetName(
            "QUERY complete retry"
        );
        yield return new TestCaseData(
            new QueryResult.QueryFailureSecurityConfiguration(["security"])
        ).SetName("QUERY complete security-configuration");
    }

    [TestCaseSource(nameof(CompleteQuerySelectionResults))]
    public async Task It_returns_complete_query_selection_result_before_target_resolution(
        QueryResult completeResult
    )
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        var selectionAttempts = 0;
        var fallbackAttempts = 0;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            new ThrowingTargetRegistry(),
            materializer,
            writer,
            telemetry,
            dataStoreSelection: new ThrowingDataStoreSelection()
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) =>
                {
                    fallbackAttempts++;
                    return Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("fallback"));
                },
                selectAuthorizedCandidatePage: _ =>
                {
                    selectionAttempts++;
                    return Task.FromResult<DocumentCacheReadAccelerationQuerySelectionResult>(
                        new DocumentCacheReadAccelerationQuerySelectionResult.Complete(completeResult)
                    );
                }
            )
        );

        result.Should().BeSameAs(completeResult);
        selectionAttempts.Should().Be(1);
        fallbackAttempts.Should().Be(0);
        lookupAdapter.QueryAttempts.Should().Be(0);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry.Events.Should().BeEmpty();
        telemetry.DurationEvents.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_empty_query_selection_page_before_target_resolution()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        var selectionAttempts = 0;
        var fallbackAttempts = 0;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            new ThrowingTargetRegistry(),
            materializer,
            writer,
            telemetry,
            dataStoreSelection: new ThrowingDataStoreSelection()
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) =>
                {
                    fallbackAttempts++;
                    return Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("fallback"));
                },
                selectAuthorizedCandidatePage: _ =>
                {
                    selectionAttempts++;
                    return Task.FromResult<DocumentCacheReadAccelerationQuerySelectionResult>(
                        new DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage(
                            new DocumentCacheReadAccelerationCandidatePage(
                                [],
                                TotalCount: 0,
                                HighestSelectedDocumentId: null
                            ),
                            (_, _) =>
                            {
                                fallbackAttempts++;
                                return Task.FromResult<QueryResult>(
                                    new QueryResult.QueryFailureKnownError("selected fallback")
                                );
                            }
                        )
                    );
                }
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        success.HighestSelectedDocumentId.Should().BeNull();
        selectionAttempts.Should().Be(1);
        fallbackAttempts.Should().Be(0);
        lookupAdapter.QueryAttempts.Should().Be(0);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry.Events.Should().BeEmpty();
        telemetry.DurationEvents.Should().BeEmpty();
    }

    [Test]
    public async Task It_selects_an_authorized_get_candidate_before_target_resolution_and_lookup()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        StaticTargetRegistry registry = CreateRegistry(executionContext);
        var sut = CreateCoordinator(readAccelerationEnabled: true, lookupAdapter, registry);
        var selectedCandidate = Candidate() with { DocumentId = 987, ContentVersion = 654 };
        var selectionAttempts = 0;

        await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists()),
                selectAuthorizedCandidate: _ =>
                {
                    selectionAttempts++;
                    registry.CurrentRuntimeSnapshotAccesses.Should().Be(0);
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
    public async Task It_selects_an_authorized_query_candidate_page_before_target_resolution_and_lookup()
    {
        var lookupAdapter = new RecordingLookupAdapter();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        StaticTargetRegistry registry = CreateRegistry(executionContext);
        var sut = CreateCoordinator(readAccelerationEnabled: true, lookupAdapter, registry);
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
                    registry.CurrentRuntimeSnapshotAccesses.Should().Be(0);
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
    public async Task It_records_read_telemetry_for_lookup_fallback_and_direct_fill()
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
        var telemetry = new RecordingReadTelemetry();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            new RecordingMaterializer(),
            new RecordingCacheWriter(),
            telemetry
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        telemetry.Events.Should().Contain(("attempt", DocumentCacheReadTelemetryLabel.Attempted));
        telemetry
            .Events.Should()
            .Contain(("miss", nameof(DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss)));
        telemetry
            .Events.Should()
            .Contain(("fallback", nameof(DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss)));
        telemetry.Events.Should().Contain(("directFill", DocumentCacheReadTelemetryLabel.Attempted));
        telemetry.Events.Should().Contain(("directFill", DocumentCacheReadTelemetryLabel.Succeeded));
        string[] durationMetrics =
        [
            .. telemetry.DurationEvents.Select(durationEvent => durationEvent.Metric),
        ];
        durationMetrics.Should().Contain(DocumentCacheReadTelemetry.CacheLookupDurationName);
        durationMetrics.Should().Contain(DocumentCacheReadTelemetry.DirectFillDurationName);
    }

    [TestCase(nameof(DocumentCacheReadLookupOutcome.LifecycleDisabled))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.LifecycleResetting))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.LifecycleRebuilding))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.CacheAheadRecoveryRequired))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.MissingCacheRow))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.MissingSourceRow))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.SourceDrift))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.StaleCacheRow))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.MissingLifecycleState))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.InvalidLifecycleState))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.ProjectionTargetIneligible))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.ProviderPrerequisiteIneligible))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.CacheUnavailable))]
    [TestCase(nameof(DocumentCacheReadLookupOutcome.DeterministicInvariantFailure))]
    public async Task It_records_raw_lookup_outcomes_for_cache_miss_telemetry(string rawLookupOutcomeName)
    {
        var rawLookupOutcome = Enum.Parse<DocumentCacheReadLookupOutcome>(rawLookupOutcomeName);
        DocumentCacheReadAccelerationFallbackReason expectedFallbackReason =
            DocumentCacheReadLookupOutcomeMapper.MapFallbackReason(rawLookupOutcome);
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.FallbackFromLookupOutcome(
                rawLookupOutcome,
                [Candidate()]
            ),
        };
        var telemetry = new RecordingReadTelemetry();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            readTelemetry: telemetry
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (context, _) =>
                {
                    fallbackContext = context;
                    return Task.FromResult<GetResult>(new GetResult.GetFailureNotExists());
                }
            )
        );

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        fallbackContext.Reason.Should().Be(expectedFallbackReason);
        telemetry.Events.Should().Contain(("miss", rawLookupOutcome.ToString()));
        telemetry.Events.Should().Contain(("fallback", expectedFallbackReason.ToString()));
        telemetry
            .DurationEvents.Should()
            .Contain((DocumentCacheReadTelemetry.CacheLookupDurationName, rawLookupOutcome.ToString()));
    }

    [Test]
    public async Task It_records_adapter_acquisition_failure_only_for_cache_acquisition_failures()
    {
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.FallbackFromLookupOutcome(
                DocumentCacheReadLookupOutcome.CacheUnavailable,
                [Candidate()],
                isAdapterAcquisitionFailure: true
            ),
        };
        var telemetry = new RecordingReadTelemetry();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            readTelemetry: telemetry
        );

        await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists())
            )
        );

        telemetry.Events.Should().Contain(("miss", nameof(DocumentCacheReadLookupOutcome.CacheUnavailable)));
        telemetry
            .Events.Should()
            .Contain(("cacheUnavailable", nameof(DocumentCacheReadLookupOutcome.CacheUnavailable)));
        telemetry
            .Events.Should()
            .Contain(("adapterAcquisitionFailure", nameof(DocumentCacheReadLookupOutcome.CacheUnavailable)));
        telemetry
            .Events.Should()
            .Contain(
                ("fallback", nameof(DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable))
            );
    }

    [Test]
    public async Task It_records_cache_unavailable_direct_fill_skip_after_get_adapter_acquisition_failure()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            GetByIdResult = DocumentCacheReadLookupResult<GetResult>.FallbackFromLookupOutcome(
                DocumentCacheReadLookupOutcome.CacheUnavailable,
                [Candidate()],
                isAdapterAcquisitionFailure: true
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer,
            telemetry
        );

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
        fallbackContext
            .Reason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry
            .Events.Should()
            .Contain(("adapterAcquisitionFailure", nameof(DocumentCacheReadLookupOutcome.CacheUnavailable)));
        telemetry
            .Events.Should()
            .Contain(("directFill", DocumentCacheReadTelemetryLabel.SkippedCacheUnavailable));
        telemetry
            .Events.Should()
            .NotContain(("directFill", DocumentCacheReadTelemetryLabel.SkippedNoCandidates));
    }

    [Test]
    public async Task It_records_cache_unavailable_direct_fill_skip_after_query_adapter_acquisition_failure()
    {
        var fallbackResult = new QueryResult.QuerySuccess(
            [new JsonObject { ["id"] = DocumentUuid.Value.ToString() }],
            1,
            HighestSelectedDocumentId: 345
        );
        var lookupAdapter = new RecordingLookupAdapter
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.FallbackFromLookupOutcome(
                DocumentCacheReadLookupOutcome.CacheUnavailable,
                [Candidate()],
                isAdapterAcquisitionFailure: true
            ),
        };
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        DocumentCacheReadAccelerationFallbackContext fallbackContext = null!;
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            lookupAdapter,
            CreateRegistry(ExecutionContext()),
            materializer,
            writer,
            telemetry
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
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
            .Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable);
        materializer.Requests.Should().BeEmpty();
        writer.Requests.Should().BeEmpty();
        telemetry
            .Events.Should()
            .Contain(("adapterAcquisitionFailure", nameof(DocumentCacheReadLookupOutcome.CacheUnavailable)));
        telemetry
            .Events.Should()
            .Contain(("directFill", DocumentCacheReadTelemetryLabel.SkippedCacheUnavailable));
        telemetry
            .Events.Should()
            .NotContain(("directFill", DocumentCacheReadTelemetryLabel.SkippedNoCandidates));
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
    public async Task It_direct_fills_query_page_in_rebuilding_after_a_fenced_cache_read()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: SecondDocumentUuid
        );
        var fallbackResult = new QueryResult.QuerySuccess(
            [
                new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
                new JsonObject { ["id"] = SecondDocumentUuid.Value.ToString() },
            ],
            2,
            HighestSelectedDocumentId: 346
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
            CreateRegistry(ExecutionContext(lifecycleState: DocumentCacheLifecycleState.Rebuilding)),
            materializer,
            writer
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<QueryResult>(fallbackResult),
                CandidatePage([first, second], totalCount: 2, highestSelectedDocumentId: 346)
            )
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Select(request => request.DocumentId).Should().Equal(345, 346);
        writer.Requests.Select(request => request.DocumentId).Should().Equal(345, 346);
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
    public async Task It_records_target_health_diagnostic_for_direct_fill_materializer_target_failure()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var materializer = new RecordingMaterializer
        {
            ExceptionToThrow = new DocumentCacheTargetMappingException(
                DocumentCacheTargetMappingFailureReason.ResourceKeyMetadataMismatch,
                FailureMetadata(documentId: 345)
            ),
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
            CreateRegistry(executionContext),
            materializer,
            writer,
            projectionObservationSink: observationStore,
            projectionObservationProvider: observationStore,
            timeProvider: new FixedTimeProvider(ObservedAt)
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
        AssertTargetInvariantDiagnostic(
            observationStore.CurrentSnapshot.GetCurrentTarget(executionContext.TargetKey)!,
            nameof(DocumentCacheTargetMappingFailureReason.ResourceKeyMetadataMismatch)
        );
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
    public async Task It_records_target_health_diagnostic_for_direct_fill_writer_invariant_outcome()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        var materializer = new RecordingMaterializer();
        var writer = new RecordingCacheWriter
        {
            Result = new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                DocumentCacheWriterInvariantFailureReason.TargetMappingMismatch,
                currentContentVersion: 91,
                candidateContentVersion: 91
            ),
        };
        var telemetry = new RecordingReadTelemetry();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            new RecordingLookupAdapter
            {
                GetByIdResult = DocumentCacheReadLookupResult<GetResult>.Fallback(
                    DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
                ),
            },
            CreateRegistry(executionContext),
            materializer,
            writer,
            telemetry,
            projectionObservationSink: observationStore,
            projectionObservationProvider: observationStore,
            timeProvider: new FixedTimeProvider(ObservedAt)
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
        telemetry.Events.Should().Contain(("directFill", DocumentCacheReadTelemetryLabel.Failed));
        AssertTargetInvariantDiagnostic(
            observationStore.CurrentSnapshot.GetCurrentTarget(executionContext.TargetKey)!,
            nameof(DocumentCacheWriterInvariantFailureReason.TargetMappingMismatch)
        );
    }

    [Test]
    public async Task It_returns_relational_result_when_direct_fill_is_canceled_after_response_selection()
    {
        var fallbackResult = new GetResult.GetSuccess(
            DocumentUuid,
            new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            LastModifiedTraceId: null
        );
        using var cancellationSource = new CancellationTokenSource();
        var materializer = new RecordingMaterializer
        {
            OnMaterialize = _ => cancellationSource.Cancel(),
            ExceptionToThrow = new OperationCanceledException(cancellationSource.Token),
        };
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
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
            writer,
            telemetry
        );

        GetResult result = await sut.GetByIdAsync(
            CreateGetByIdRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<GetResult>(fallbackResult)
            ),
            cancellationSource.Token
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Should().ContainSingle();
        writer.Requests.Should().BeEmpty();
        telemetry.Events.Should().Contain(("directFill", DocumentCacheReadTelemetryLabel.Attempted));
        telemetry.Events.Should().Contain(("directFill", DocumentCacheReadTelemetryLabel.CallerCanceled));
    }

    [Test]
    public async Task It_records_caller_cancellation_when_query_direct_fill_is_canceled_between_candidates()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: SecondDocumentUuid
        );
        var fallbackResult = new QueryResult.QuerySuccess(
            [
                new JsonObject { ["id"] = DocumentUuid.Value.ToString() },
                new JsonObject { ["id"] = SecondDocumentUuid.Value.ToString() },
            ],
            2,
            HighestSelectedDocumentId: 346
        );
        using var cancellationSource = new CancellationTokenSource();
        var materializationAttempts = 0;
        var materializer = new RecordingMaterializer
        {
            OnMaterialize = _ =>
            {
                materializationAttempts++;
                if (materializationAttempts == 1)
                {
                    cancellationSource.Cancel();
                }
            },
        };
        var writer = new RecordingCacheWriter();
        var telemetry = new RecordingReadTelemetry();
        var sut = CreateCoordinator(
            readAccelerationEnabled: true,
            new RecordingLookupAdapter
            {
                QueryResult = DocumentCacheReadLookupResult<QueryResult>.FallbackFromLookupOutcome(
                    DocumentCacheReadLookupOutcome.MissingCacheRow,
                    [first, second]
                ),
            },
            CreateRegistry(ExecutionContext()),
            materializer,
            writer,
            telemetry
        );

        QueryResult result = await sut.QueryAsync(
            CreateQueryRequest(
                DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                (_, _) => Task.FromResult<QueryResult>(fallbackResult),
                CandidatePage([first, second], totalCount: 2, highestSelectedDocumentId: 346)
            ),
            cancellationSource.Token
        );

        result.Should().BeSameAs(fallbackResult);
        materializer.Requests.Select(request => request.DocumentId).Should().Equal(345);
        writer.Requests.Select(request => request.DocumentId).Should().Equal(345);
        telemetry.Events.Should().Contain(("directFill", DocumentCacheReadTelemetryLabel.CallerCanceled));
        telemetry.Events.Should().NotContain(("directFill", DocumentCacheReadTelemetryLabel.TimedOut));
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
        IDocumentCacheTargetRegistry? registry,
        IDocumentCacheMaterializer? materializer = null,
        IDocumentCacheWriter? cacheWriter = null,
        IDocumentCacheReadTelemetry? readTelemetry = null,
        bool selectDataStore = true,
        IDocumentCacheProjectionObservationSink? projectionObservationSink = null,
        IDocumentCacheProjectionObservationProvider? projectionObservationProvider = null,
        TimeProvider? timeProvider = null,
        DataStore? selectedDataStore = null,
        IDataStoreSelection? dataStoreSelection = null
    )
    {
        IDataStoreSelection requestDataStoreSelection = dataStoreSelection ?? CreateDataStoreSelection();

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
            requestDataStoreSelection,
            registry,
            lookupAdapter,
            materializer,
            cacheWriter,
            readTelemetry,
            projectionObservationSink,
            projectionObservationProvider,
            timeProvider
        );

        IDataStoreSelection CreateDataStoreSelection()
        {
            DataStoreSelection defaultDataStoreSelection = new();
            if (selectDataStore)
            {
                defaultDataStoreSelection.SetSelectedDataStore(selectedDataStore ?? SelectedDataStore());
            }

            return defaultDataStoreSelection;
        }
    }

    private static DataStore SelectedDataStore(
        RelationalProviderToken? providerToken = null,
        string connectionString = "Host=localhost"
    ) =>
        new(
            TargetKey.DataStoreId,
            providerToken == RelationalProviderToken.SqlServer ? "sqlserver" : "postgresql",
            "Primary",
            connectionString,
            [],
            providerToken ?? RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported
        );

    private static DocumentCacheMaterializerFailureMetadata FailureMetadata(long documentId) =>
        new(
            new DocumentCacheProjectionTargetKey(TargetKey.TenantKey, new DataStoreId(TargetKey.DataStoreId)),
            MappingSet.Key,
            DocumentCacheMaterializationPurpose.DirectFill,
            documentId
        )
        {
            ResourceKeyId = 1,
            ProjectName = Resource.ProjectName,
            ResourceName = Resource.ResourceName,
            ResourceVersion = "1.0",
        };

    private static void AssertTargetInvariantDiagnostic(
        DocumentCacheProjectionTargetHealthSnapshot health,
        string expectedReason
    )
    {
        DocumentCacheTargetDiagnostic diagnostic = health.TargetDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Category.Should().Be(DocumentCacheTargetDiagnosticCategory.DeterministicInvariantFailure);
        diagnostic.Message.Should().Contain(expectedReason);
        diagnostic.Message.Should().NotContain(DocumentUuid.Value.ToString());
        diagnostic.Message.Should().NotContain(SecondDocumentUuid.Value.ToString());
        diagnostic.Message.Should().NotContain("DocumentId");
        diagnostic.Message.Should().NotContain("DocumentJson");
        diagnostic.Message.Should().NotContain("Host=localhost");
        health.FailureDiagnostics.DocumentIds.Should().BeEmpty();
    }

    private static DocumentCacheReadAccelerationGetByIdRequest CreateGetByIdRequest(
        DocumentCacheReadAccelerationLookupReadiness lookupReadiness,
        Func<DocumentCacheReadAccelerationFallbackContext, CancellationToken, Task<GetResult>> fallback,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        Func<
            CancellationToken,
            Task<DocumentCacheReadAccelerationGetByIdSelectionResult>
        >? selectAuthorizedCandidate = null,
        string? tenantKey = null
    ) =>
        new(
            tenantKey ?? TargetKey.TenantKey,
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
        TimeSpan? directFillTimeout = null,
        bool targetReadAccelerationEnabled = true
    ) =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: targetReadAccelerationEnabled,
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

        public Exception? ExceptionToThrow { get; init; }

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
            if (ExceptionToThrow is not null)
            {
                return Task.FromException<DocumentCacheReadLookupResult<GetResult>>(ExceptionToThrow);
            }

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
            if (ExceptionToThrow is not null)
            {
                return Task.FromException<DocumentCacheReadLookupResult<QueryResult>>(ExceptionToThrow);
            }

            return Task.FromResult(QueryResult);
        }
    }

    private sealed class RecordingMaterializer : IDocumentCacheMaterializer
    {
        public List<DocumentCacheMaterializationRequest> Requests { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public Action<DocumentCacheMaterializationRequest>? OnMaterialize { get; init; }

        public TimeSpan? DelayUntilCancellation { get; init; }

        public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            Requests.Add(request);
            OnMaterialize?.Invoke(request);

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

        public DocumentCacheWriterResult? Result { get; init; }

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
        {
            Requests.Add(request);
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult<DocumentCacheWriterResult>(
                Result
                    ?? new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
                        request.Candidate!,
                        request.Candidate!.ContentVersion
                    )
            );
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingReadTelemetry : IDocumentCacheReadTelemetry
    {
        public List<(string Metric, string Outcome)> Events { get; } = [];

        public List<(string Metric, string Outcome)> DurationEvents { get; } = [];

        public void RecordAttempt(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("attempt", context.Outcome));

        public void RecordHit(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("hit", context.Outcome));

        public void RecordPageHit(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("pageHit", context.Outcome));

        public void RecordMiss(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("miss", context.Outcome));

        public void RecordFallback(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("fallback", context.Outcome));

        public void RecordCacheUnavailable(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("cacheUnavailable", context.Outcome));

        public void RecordAdapterAcquisitionFailure(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("adapterAcquisitionFailure", context.Outcome));

        public void RecordUnexpectedException(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("unexpectedException", context.Outcome));

        public void RecordDirectFill(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("directFill", context.Outcome));

        public void RecordCacheLookupDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
            DurationEvents.Add((DocumentCacheReadTelemetry.CacheLookupDurationName, context.Outcome));

        public void RecordDirectFillDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
            DurationEvents.Add((DocumentCacheReadTelemetry.DirectFillDurationName, context.Outcome));

        public void RecordDerivativeTargetBypass(DocumentCacheReadTelemetryContext context) =>
            Events.Add(("derivativeTargetBypass", context.Outcome));
    }

    private sealed class ThrowingDataStoreSelection : IDataStoreSelection
    {
        public bool IsSet => throw new InvalidOperationException("DataStoreSelection should not be read.");

        public void SetSelectedDataStore(DataStore dataStore) =>
            throw new InvalidOperationException("DataStoreSelection should not be written.");

        public DataStore GetSelectedDataStore() =>
            throw new InvalidOperationException("DataStoreSelection should not be read.");
    }

    private sealed class ThrowingTargetRegistry : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot =>
            throw new InvalidOperationException("Target registry should not be read.");

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot =>
            throw new InvalidOperationException("Target registry should not be read.");

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Target registry should not be refreshed.");
    }

    private sealed class StaticTargetRegistry(
        DocumentCacheTargetRegistrySnapshot snapshot,
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public int CurrentSnapshotAccesses { get; private set; }

        public int CurrentRuntimeSnapshotAccesses { get; private set; }

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot
        {
            get
            {
                CurrentSnapshotAccesses++;
                return snapshot;
            }
        }

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot
        {
            get
            {
                CurrentRuntimeSnapshotAccesses++;
                return runtimeSnapshot;
            }
        }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(snapshot);
    }
}
