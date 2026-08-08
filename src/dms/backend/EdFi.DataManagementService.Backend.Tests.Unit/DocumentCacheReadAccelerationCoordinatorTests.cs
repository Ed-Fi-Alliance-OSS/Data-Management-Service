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

    private static DocumentCacheReadAccelerationCoordinator CreateCoordinator(
        bool readAccelerationEnabled,
        RecordingLookupAdapter lookupAdapter,
        IDocumentCacheTargetRegistry registry
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
            lookupAdapter
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
        Func<DocumentCacheReadAccelerationFallbackContext, CancellationToken, Task<QueryResult>> fallback
    ) =>
        new(
            TargetKey.TenantKey,
            MappingSet,
            Resource,
            DocumentCacheReadAccelerationResourceKind.Resource,
            lookupReadiness,
            fallback,
            lookupReadiness == DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate
                ? CandidatePage()
                : null
        );

    private static DocumentCacheReadAccelerationCandidate Candidate() =>
        new(345, DocumentUuid, ResourceKeyId: 1, ContentVersion: 91, ContentLastModifiedAt: ObservedAt);

    private static DocumentCacheReadAccelerationCandidatePage CandidatePage() =>
        new([Candidate()], TotalCount: 1, HighestSelectedDocumentId: null);

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

    private static DocumentCacheTargetExecutionContext ExecutionContext() =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
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
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Tracking,
                CacheAheadRecoveryRequired: false
            ),
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

        public CancellationToken LastGetByIdCancellationToken { get; private set; }

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
            LastQueryRequest = request;
            return Task.FromResult(QueryResult);
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
