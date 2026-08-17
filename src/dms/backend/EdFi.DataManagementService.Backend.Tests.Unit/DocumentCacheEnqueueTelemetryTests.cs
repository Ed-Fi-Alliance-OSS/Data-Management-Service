// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheEnqueueTelemetry")]
public class Given_DocumentCacheEnqueueTelemetry
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 15, 10, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("", 1);

    [Test]
    public void It_retains_bounded_failures_by_current_target_oldest_to_newest()
    {
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 2);

        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Insert, "first"),
            DocumentCacheEnqueueFailureCategory.ProviderUnavailable
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "second"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "third"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );
        telemetry.RecordFailure(
            Context(
                DocumentCacheTargetKey.Create("other", 2),
                DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
                "other"
            ),
            DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
        );
        telemetry.RecordFailure(
            Context(targetKey: null, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "unknown"),
            DocumentCacheEnqueueFailureCategory.UnclassifiedProviderFailure
        );

        DocumentCacheEnqueueFailureSnapshot snapshot = telemetry.GetFailureSnapshot(TargetKey);

        snapshot.EvictedCount.Should().Be(1);
        snapshot
            .RecentEvents.Select(enqueueFailure => enqueueFailure.Message)
            .Should()
            .Equal("second", "third");
        snapshot
            .RecentEvents.Select(enqueueFailure => enqueueFailure.Category)
            .Should()
            .Equal(
                DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed,
                DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
            );
        telemetry
            .GetFailureSnapshot(DocumentCacheTargetKey.Create("missing", 3))
            .RecentEvents.Should()
            .BeEmpty();
    }

    [Test]
    public void It_drops_removed_configuration_buckets_and_starts_readded_targets_empty()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(TargetKey);
        DocumentCacheTargetObservation replacement = ResolvedTarget(
            DocumentCacheTargetKey.Create("other", 2)
        );
        var registry = new MutableTargetRegistry([target]);
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 10, registry);

        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "retained"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().ContainSingle();

        registry.ReplaceTargets([replacement]);

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();

        registry.ReplaceTargets([target]);

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();
    }

    [Test]
    public async Task It_maps_retained_failures_into_status_json_shape()
    {
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 10);
        DocumentCacheTargetObservation target = ResolvedTarget();
        DocumentCacheStatusService service = new(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt)),
            [new ScriptedStatusObserver()],
            new FixedTimeProvider(ObservedAt),
            telemetry
        );

        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Insert, "state missing"),
            DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "work failed"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.EnqueueFailures.EvictedCount.Should().Be(0);
        statusTarget
            .EnqueueFailures.RecentEvents.Select(enqueueFailure => enqueueFailure.Category)
            .Should()
            .Equal(
                DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid,
                DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed
            );
        statusTarget
            .EnqueueFailures.RecentEvents.Select(enqueueFailure => enqueueFailure.CanonicalOperation)
            .Should()
            .Equal(
                DocumentCacheStatusCanonicalOperation.Insert,
                DocumentCacheStatusCanonicalOperation.Update
            );
        statusTarget
            .EnqueueFailures.ByCategory.Select(categoryCount => (categoryCount.Category, categoryCount.Count))
            .Should()
            .Equal(
                (DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid, 1),
                (DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed, 1)
            );
    }

    [TestCase(
        "dms.DocumentCacheState singleton row is missing or unreadable for projection enqueue.",
        (int)DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
    )]
    [TestCase(
        "dms.DocumentCacheState.ProjectionLifecycleState has unsupported value Broken for projection enqueue.",
        (int)DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
    )]
    [TestCase(
        "permission denied for function TF_Document_EnqueueProjectionInsert",
        (int)DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable
    )]
    [TestCase(
        "insert or update on table DocumentProjectionWork violates foreign key",
        (int)DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
    )]
    public void It_classifies_canonical_enqueue_provider_failures(
        string providerMessage,
        int expectedCategory
    )
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException(providerMessage),
            new StubWriteExceptionClassifier(),
            out DocumentCacheEnqueueFailureCategory category,
            out string message
        );

        classified.Should().BeTrue();
        category.Should().Be((DocumentCacheEnqueueFailureCategory)expectedCategory);
        message.Should().NotContain("\r").And.NotContain("\n").And.NotContain("{").And.NotContain("}");
    }

    private static DocumentCacheEnqueueTelemetry CreateTelemetry(
        int pageSize,
        IDocumentCacheTargetRegistry? targetRegistry = null
    )
    {
        DocumentCacheOptions options = new();
        options.Projector.PageSize = pageSize;

        return new(
            Options.Create(options),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheEnqueueTelemetry>.Instance,
            targetRegistry
        );
    }

    private static DocumentCacheEnqueueTelemetryContext Context(
        DocumentCacheTargetKey? targetKey,
        DocumentCacheEnqueueTelemetryCanonicalOperation operation,
        string message
    ) =>
        new(
            targetKey,
            RelationalProviderToken.Postgresql,
            operation,
            DocumentCacheEnqueueTelemetryResourceKind.Resource,
            message
        );

    private static DocumentCacheTargetObservation ResolvedTarget() => ResolvedTarget(TargetKey);

    private static DocumentCacheTargetObservation ResolvedTarget(DocumentCacheTargetKey targetKey) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            targetKey,
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 10,
                projectorMaxConcurrentTargets: 1,
                projectorFailureBackoff: TimeSpan.FromSeconds(30),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetContextGeneration(1),
            RelationalProviderToken.Postgresql,
            new DocumentCachePhysicalSourceFingerprint(
                "sha256:1111111111111111111111111111111111111111111111111111111111111111"
            ),
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

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheTargetObservation target
    ) =>
        new(
            target.TargetKey,
            target.Generation!,
            target.EffectiveSettings,
            new DocumentCacheTargetDataStoreMetadata(target.TargetKey.DataStoreId, "PostgreSQL"),
            new DocumentCacheTargetConnectionInput(
                target.ProviderToken!,
                "Host=localhost;Database=document-cache-enqueue-telemetry"
            ),
            target.PhysicalSourceFingerprint!,
            target.Lifecycle!,
            target.Inventory!,
            target.EnqueueTrigger!,
            target.SqlServerPrerequisites
        );

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StaticTargetRegistry(
        ImmutableArray<DocumentCacheTargetObservation> targets,
        ImmutableArray<DocumentCacheTargetExecutionContext> executionContexts
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = new(targets, ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
            new(executionContexts, ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class MutableTargetRegistry(ImmutableArray<DocumentCacheTargetObservation> targets)
        : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; private set; } =
            new(targets, ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = new([], ObservedAt);

        public void ReplaceTargets(ImmutableArray<DocumentCacheTargetObservation> targets)
        {
            CurrentSnapshot = new DocumentCacheTargetRegistrySnapshot(targets, ObservedAt);
        }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class ScriptedStatusObserver : IDocumentCacheStatusCurrentSourceObserver
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheStatusCurrentSourceObservationResult> ObserveAsync(
            DocumentCacheStatusCurrentSourceObservationRequest request,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                DocumentCacheStatusCurrentSourceObservationResult.Success(
                    DocumentCacheLifecycleState.Tracking,
                    cacheAheadRecoveryRequired: false,
                    DocumentCacheStatusDurableQueuePresence.Empty,
                    oldestWorkFirstEnqueuedAt: null,
                    oldestWorkAgeSeconds: null,
                    ObservedAt
                )
            );
    }

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class StubWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            classification = RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance;
            return true;
        }

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

        public bool IsTransientFailure(DbException exception) => false;
    }
}
