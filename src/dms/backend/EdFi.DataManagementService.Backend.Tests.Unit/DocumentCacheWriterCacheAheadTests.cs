// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheWriterCacheAhead
{
    private static readonly DocumentCacheProjectionTargetKey TargetKey = new(
        "tenant-cache-ahead",
        new DataStoreId(7)
    );

    [Test]
    public async Task It_executes_the_incident_with_an_uncanceled_bounded_token()
    {
        var observedTokenWasCanceled = true;

        DocumentCacheWriterResult result = await DocumentCacheWriterCacheAheadIncidentFlow.ExecuteAsync(
            CreateRequest(),
            cancellationToken =>
            {
                observedTokenWasCanceled = cancellationToken.IsCancellationRequested;
                return Task.FromResult<DocumentCacheWriterResult>(
                    DocumentCacheWriterResult.CacheAheadDisappeared.Instance
                );
            },
            new NoOpLogger()
        );

        result.Should().BeSameAs(DocumentCacheWriterResult.CacheAheadDisappeared.Instance);
        observedTokenWasCanceled.Should().BeFalse();
    }

    [Test]
    public async Task It_returns_unconfirmed_when_the_incident_transaction_is_canceled()
    {
        DocumentCacheWriterResult result = await DocumentCacheWriterCacheAheadIncidentFlow.ExecuteAsync(
            CreateRequest(),
            _ => throw new OperationCanceledException(),
            new NoOpLogger()
        );

        result.Should().BeSameAs(DocumentCacheWriterResult.CacheAheadUnconfirmedCallerAbort.Instance);
    }

    [Test]
    public async Task It_returns_unconfirmed_when_the_incident_timeout_expires()
    {
        DocumentCacheWriterResult result = await DocumentCacheWriterCacheAheadIncidentFlow.ExecuteAsync(
            CreateRequest(TimeSpan.FromMilliseconds(1)),
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return DocumentCacheWriterResult.CacheAheadDisappeared.Instance;
            },
            new NoOpLogger()
        );

        result.Should().BeSameAs(DocumentCacheWriterResult.CacheAheadUnconfirmedCallerAbort.Instance);
    }

    [Test]
    public void It_selects_latch_update_only_after_lifecycle_and_current_cache_ahead_recheck()
    {
        DocumentCacheWriterCacheAheadIncidentDecision decision = SelectRecheck(
            Lifecycle(DocumentCacheLifecycleState.Rebuilding),
            sourceContentVersion: 10,
            cacheContentVersion: 11,
            workRequiredContentVersion: 10
        );

        decision.Action.Should().Be(DocumentCacheWriterCacheAheadIncidentAction.SetCacheAheadLatch);
        decision.SourceContentVersion.Should().Be(10);
        decision.CacheContentVersion.Should().Be(11);
        decision.LifecycleState.Should().Be(DocumentCacheLifecycleState.Rebuilding);
        decision.TerminalResult.Should().BeNull();
    }

    [Test]
    public void It_returns_lifecycle_or_latch_fence_when_revalidation_fails()
    {
        DocumentCacheWriterCacheAheadIncidentDecision disabled = SelectRecheck(
            Lifecycle(DocumentCacheLifecycleState.Disabled),
            sourceContentVersion: 10,
            cacheContentVersion: 11,
            workRequiredContentVersion: 10
        );
        DocumentCacheWriterCacheAheadIncidentDecision setLatch = SelectRecheck(
            Lifecycle(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true),
            sourceContentVersion: 10,
            cacheContentVersion: 11,
            workRequiredContentVersion: 10
        );
        DocumentCacheWriterCacheAheadIncidentDecision missingState = SelectRecheck(
            DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "state row missing"
            ),
            sourceContentVersion: 10,
            cacheContentVersion: 11,
            workRequiredContentVersion: 10
        );
        DocumentCacheWriterCacheAheadIncidentDecision unreadableState = SelectRecheck(
            DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "state row unreadable"
            ),
            sourceContentVersion: 10,
            cacheContentVersion: 11,
            workRequiredContentVersion: 10
        );

        disabled.Action.Should().Be(DocumentCacheWriterCacheAheadIncidentAction.ReturnLifecycleOrLatchFence);
        setLatch.Action.Should().Be(DocumentCacheWriterCacheAheadIncidentAction.ReturnLifecycleOrLatchFence);
        missingState
            .Action.Should()
            .Be(DocumentCacheWriterCacheAheadIncidentAction.ReturnLifecycleOrLatchFence);
        unreadableState
            .Action.Should()
            .Be(DocumentCacheWriterCacheAheadIncidentAction.ReturnLifecycleOrLatchFence);
        disabled
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.LifecycleNotEligible);
        setLatch
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired);
        missingState
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.StateMissing);
        unreadableState
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.StateUnreadable);
    }

    [Test]
    public void It_returns_cache_ahead_disappeared_for_non_cache_ahead_rechecks()
    {
        DocumentCacheWriterCacheAheadIncidentDecision currentCache = SelectRecheck(
            Lifecycle(),
            sourceContentVersion: 10,
            cacheContentVersion: 10,
            workRequiredContentVersion: 10
        );
        DocumentCacheWriterCacheAheadIncidentDecision staleCandidate = SelectRecheck(
            Lifecycle(),
            sourceContentVersion: 10,
            cacheContentVersion: 9,
            workRequiredContentVersion: 10
        );
        DocumentCacheWriterCacheAheadIncidentDecision missingWork = SelectRecheck(
            Lifecycle(),
            sourceContentVersion: 10,
            cacheContentVersion: 9,
            workRequiredContentVersion: null
        );
        DocumentCacheWriterCacheAheadIncidentDecision missingSource = SelectRecheck(
            Lifecycle(),
            sourceContentVersion: null,
            cacheContentVersion: 11,
            workRequiredContentVersion: 11
        );

        new[] { currentCache, staleCandidate, missingWork, missingSource }
            .Should()
            .OnlyContain(decision =>
                decision.Action == DocumentCacheWriterCacheAheadIncidentAction.ReturnCacheAheadDisappeared
            );
        new[] { currentCache, staleCandidate, missingWork, missingSource }
            .Select(decision => decision.TerminalResult)
            .Should()
            .OnlyContain(result => result == DocumentCacheWriterResult.CacheAheadDisappeared.Instance);
    }

    [Test]
    public void It_shapes_latch_update_outcomes_from_the_recheck_decision()
    {
        DocumentCacheWriterCacheAheadIncidentDecision decision = SelectRecheck(
            Lifecycle(),
            sourceContentVersion: 10,
            cacheContentVersion: 11,
            workRequiredContentVersion: 10
        );

        DocumentCacheWriterResult latchSet = DocumentCacheWriterCacheAheadIncidentFlow.CompleteLatchUpdate(
            decision,
            DocumentCacheWriterCacheAheadLatchUpdateResult.LatchSet()
        );
        DocumentCacheWriterResult lifecycleOrLatchFence =
            DocumentCacheWriterCacheAheadIncidentFlow.CompleteLatchUpdate(
                decision,
                DocumentCacheWriterCacheAheadLatchUpdateResult.LifecycleOrLatchFenced()
            );
        DocumentCacheWriterResult disappeared = DocumentCacheWriterCacheAheadIncidentFlow.CompleteLatchUpdate(
            decision,
            DocumentCacheWriterCacheAheadLatchUpdateResult.CacheAheadDisappeared()
        );

        latchSet
            .Should()
            .BeOfType<DocumentCacheWriterResult.CacheAheadLatchSet>()
            .Which.CacheContentVersion.Should()
            .Be(11);
        lifecycleOrLatchFence
            .Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired);
        disappeared.Should().BeSameAs(DocumentCacheWriterResult.CacheAheadDisappeared.Instance);
    }

    private static DocumentCacheWriterCacheAheadIncidentRequest CreateRequest(TimeSpan? timeout = null) =>
        new(
            RelationalProviderToken.Postgresql,
            TargetKey,
            DocumentCacheWriterPurpose.DurableWorkProjection,
            timeout ?? DocumentCacheWriterCacheAheadIncidentFlow.DefaultIncidentTimeout
        );

    private static DocumentCacheWriterCacheAheadIncidentDecision SelectRecheck(
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        long? sourceContentVersion,
        long? cacheContentVersion,
        long? workRequiredContentVersion
    ) =>
        DocumentCacheWriterCacheAheadIncidentFlow.SelectRecheckDecision(
            DocumentCacheWriterPurpose.DurableWorkProjection,
            lifecycleReadResult,
            new DocumentCacheWriterCurrentStateObservation(
                sourceContentVersion,
                cacheContentVersion,
                workRequiredContentVersion
            ),
            DocumentCacheWriterCandidateObservation.Absent
        );

    private static DocumentCacheLifecycleReadResult Lifecycle(
        DocumentCacheLifecycleState lifecycleState = DocumentCacheLifecycleState.Tracking,
        bool cacheAheadRecoveryRequired = false
    ) =>
        DocumentCacheLifecycleReadResult.Success(
            new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired)
        );

    private sealed class NoOpLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            _ = formatter;
        }

        private sealed class NoOpScope : IDisposable
        {
            public static NoOpScope Instance { get; } = new();

            public void Dispose()
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
