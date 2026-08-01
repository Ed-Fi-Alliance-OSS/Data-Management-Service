// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheBaselineSeeding")]
public class Given_DocumentCacheBaselineSeeding
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [Test]
    public async Task It_seeds_pages_from_a_fresh_captured_boundary_without_persisting_a_cursor()
    {
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeBaselineBoundaryResult(5, "boundary"),
            HighWaterBelow(),
            HighWaterBelow()
        );
        primitives.SeedPages.Enqueue(
            Page(
                boundaryDocumentId: 5,
                afterDocumentId: 0,
                Document(
                    1,
                    10,
                    previousRequiredContentVersion: null,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted
                ),
                Document(
                    2,
                    12,
                    previousRequiredContentVersion: 15,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.Lowered
                )
            )
        );
        primitives.SeedPages.Enqueue(
            Page(
                boundaryDocumentId: 5,
                afterDocumentId: 2,
                Document(
                    5,
                    20,
                    previousRequiredContentVersion: 20,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.None
                )
            )
        );
        var lease = new RecordingMutexLease();
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(primitives, lease);

        DocumentCacheBaselineSeedingResult result = await CreateSeeder().SeedAsync(context);

        result.BoundaryDocumentId.Should().Be(5);
        result.LastCommittedDocumentId.Should().Be(5);
        result.PagesSeeded.Should().Be(2);
        result.DocumentsVisited.Should().Be(3);
        result.WorkMutationCount.Should().Be(2);
        context.Mutated.Should().BeTrue();
        primitives.SeedRequests.Select(request => request.AfterDocumentId).Should().Equal(0, 2);
        lease
            .Sessions.Select(session => session.IsolationLevel)
            .Should()
            .Equal(
                IsolationLevel.ReadCommitted,
                IsolationLevel.ReadCommitted,
                IsolationLevel.Serializable,
                IsolationLevel.ReadCommitted,
                IsolationLevel.Serializable
            );
        lease.Sessions.Should().OnlyContain(session => session.Committed);
    }

    [Test]
    public async Task It_rolls_back_and_rereads_the_current_page_when_a_guarded_page_loses_a_race()
    {
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeBaselineBoundaryResult(4, "boundary"),
            HighWaterBelow(),
            HighWaterBelow(),
            HighWaterBelow()
        );
        primitives.SeedPages.Enqueue(
            new DocumentCacheAdministrativeBaselineSeedPageResult(
                DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey,
                boundaryDocumentId: 4,
                afterDocumentId: 0,
                pageSize: 2,
                [
                    Document(
                        1,
                        10,
                        previousRequiredContentVersion: 9,
                        DocumentCacheAdministrativeBaselineWorkMutationKind.Retry
                    ),
                ],
                "retry"
            )
        );
        primitives.SeedPages.Enqueue(
            Page(
                boundaryDocumentId: 4,
                afterDocumentId: 0,
                Document(
                    1,
                    10,
                    previousRequiredContentVersion: 9,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.Advanced
                ),
                Document(
                    2,
                    11,
                    previousRequiredContentVersion: null,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted
                )
            )
        );
        primitives.SeedPages.Enqueue(
            Page(
                boundaryDocumentId: 4,
                afterDocumentId: 2,
                Document(
                    4,
                    12,
                    previousRequiredContentVersion: 12,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.None
                )
            )
        );
        var lease = new RecordingMutexLease();
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(primitives, lease);

        DocumentCacheBaselineSeedingResult result = await CreateSeeder().SeedAsync(context);

        result.LastCommittedDocumentId.Should().Be(4);
        primitives.SeedRequests.Select(request => request.AfterDocumentId).Should().Equal(0, 0, 2);
        lease
            .Sessions.Where(session => session.IsolationLevel == IsolationLevel.Serializable)
            .Select(session => session.RolledBack)
            .Should()
            .Equal(true, false, false);
        context.PhaseDiagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task It_waits_with_bounded_diagnostics_when_work_is_at_the_baseline_high_water_mark()
    {
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeBaselineBoundaryResult(1, "boundary"),
            new DocumentCacheAdministrativeWorkHighWaterObservationResult(
                highWaterMark: 2,
                observedWorkRows: 3,
                diagnosticDocumentIds: [7, 9],
                "at high-water"
            ),
            HighWaterBelow()
        );
        primitives.SeedPages.Enqueue(
            Page(
                boundaryDocumentId: 1,
                afterDocumentId: 0,
                Document(
                    1,
                    10,
                    previousRequiredContentVersion: null,
                    DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted
                )
            )
        );
        var delay = new RecordingBaselineSeedDelay();
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease()
        );

        DocumentCacheBaselineSeedingResult result = await CreateSeeder(delay).SeedAsync(context);

        result.LastCommittedDocumentId.Should().Be(1);
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(5));
        context
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison
                && diagnostic.Retryable
                && diagnostic.AffectedDocumentIds.SequenceEqual(new long[] { 7L, 9L })
            );
    }

    private static DocumentCacheBaselineSeeder CreateSeeder(IDocumentCacheBaselineSeedDelay? delay = null) =>
        new(
            delay ?? new RecordingBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheBaselineSeeder>.Instance
        );

    private static DocumentCacheAdministrativeCommandExecutionContext CreateCommandContext(
        IDocumentCacheAdministrativePrimitives primitives,
        IDocumentCacheAdministrativeMutexLease lease
    )
    {
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext();
        DocumentCacheAdministrativeCommandRunnerRequest request = new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey(
                targetContext.TargetKey.TenantKey,
                targetContext.TargetKey.DataStoreId
            )
        );

        return new DocumentCacheAdministrativeCommandExecutionContext(
            DocumentCacheAdministrativeCommandExecutionId.New(),
            request,
            targetContext,
            lease,
            primitives,
            new NoOpObservationSink(),
            new FixedTimeProvider(ObservedAt),
            ObservedAt,
            CancellationToken.None
        );
    }

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        RelationalProviderToken providerToken = RelationalProviderToken.Postgresql;
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 2,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 2,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, providerToken.Value),
            new DocumentCacheTargetConnectionInput(providerToken, "connection"),
            Fingerprint,
            TrackingLifecycle,
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

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                providerToken,
                MaterializationTargetContext(targetKey, providerToken),
                new StubDocumentCacheMaterializer(),
                new StubDocumentCacheWriter()
            ),
            new NoOpObservationSink()
        );
    }

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken
    ) =>
        new(
            new DocumentCacheProjectionTargetKey(targetKey.TenantKey, new DataStoreId(targetKey.DataStoreId)),
            MappingSet(providerToken),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            "connection"
        );

    private static MappingSet MappingSet(RelationalProviderToken providerToken)
    {
        SqlDialect dialect =
            providerToken == RelationalProviderToken.SqlServer ? SqlDialect.Mssql : SqlDialect.Pgsql;
        EffectiveSchemaInfo effectiveSchema = new(
            ApiSchemaFormatVersion: "5.2.0",
            RelationalMappingVersion: "v2",
            EffectiveSchemaHash: "schema-hash",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        return new MappingSet(
            new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                dialect,
                effectiveSchema.RelationalMappingVersion
            ),
            new DerivedRelationalModelSet(effectiveSchema, dialect, [], [], [], [], [], []),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static DocumentCacheAdministrativeWorkHighWaterObservationResult HighWaterBelow() =>
        new(highWaterMark: 2, observedWorkRows: 0, diagnosticDocumentIds: [], "below");

    private static DocumentCacheAdministrativeBaselineSeedPageResult Page(
        long boundaryDocumentId,
        long afterDocumentId,
        params DocumentCacheAdministrativeBaselineSeededDocument[] documents
    ) =>
        new(
            DocumentCacheAdministrativeBaselineSeedPageStatus.PageSeeded,
            boundaryDocumentId,
            afterDocumentId,
            pageSize: 2,
            documents.ToImmutableArray(),
            "page"
        );

    private static DocumentCacheAdministrativeBaselineSeededDocument Document(
        long documentId,
        long sourceContentVersion,
        long? previousRequiredContentVersion,
        DocumentCacheAdministrativeBaselineWorkMutationKind mutationKind
    ) => new(documentId, sourceContentVersion, previousRequiredContentVersion, mutationKind);

    private sealed class RecordingAdministrativePrimitives(
        DocumentCacheAdministrativeBaselineBoundaryResult boundary,
        params DocumentCacheAdministrativeWorkHighWaterObservationResult[] highWaterObservations
    ) : IDocumentCacheAdministrativePrimitives
    {
        private readonly Queue<DocumentCacheAdministrativeWorkHighWaterObservationResult> _highWater = new(
            highWaterObservations
        );

        public Queue<DocumentCacheAdministrativeBaselineSeedPageResult> SeedPages { get; } = [];

        public List<DocumentCacheAdministrativeBaselineSeedPageRequest> SeedRequests { get; } = [];

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(boundary);

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            request.HighWaterMark.Should().Be(2);
            request.DiagnosticCapacity.Should().Be(2);
            return Task.FromResult(_highWater.Dequeue());
        }

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            SeedRequests.Add(request);
            return Task.FromResult(SeedPages.Dequeue());
        }

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingMutexLease : IDocumentCacheAdministrativeMutexLease
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen => true;

        public List<RecordingWriteSession> Sessions { get; } = [];

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        )
        {
            var session = new RecordingWriteSession(isolationLevel);
            Sessions.Add(session);
            return Task.FromResult<IRelationalWriteSession>(session);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWriteSession(IsolationLevel isolationLevel) : IRelationalWriteSession
    {
        public IsolationLevel IsolationLevel { get; } = isolationLevel;

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingBaselineSeedDelay : IDocumentCacheBaselineSeedDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpObservationSink : IDocumentCacheProjectionObservationSink
    {
        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => _ = (contextKey, endReason, endedAt);

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }

    private sealed class StubDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotImplementedException();
    }

    private sealed class StubDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotImplementedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
