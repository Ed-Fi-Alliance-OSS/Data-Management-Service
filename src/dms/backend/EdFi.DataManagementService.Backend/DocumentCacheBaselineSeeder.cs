// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheBaselineSeeder
{
    Task<DocumentCacheBaselineSeedingResult> SeedAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken = default
    );
}

internal interface IDocumentCacheBaselineSeedDelay
{
    Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken);
}

internal sealed class DocumentCacheBaselineSeedDelay : IDocumentCacheBaselineSeedDelay
{
    public Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Task.Delay(delay, timeProvider, cancellationToken);
    }
}

internal sealed record DocumentCacheBaselineSeedingResult
{
    public DocumentCacheBaselineSeedingResult(
        long? boundaryDocumentId,
        long lastCommittedDocumentId,
        int pagesSeeded,
        int documentsVisited,
        int workMutationCount,
        ImmutableArray<long> lastAffectedDocumentIds,
        DocumentCacheAdministrativeCommandResult? failureResult = null
    )
    {
        if (boundaryDocumentId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryDocumentId),
                boundaryDocumentId,
                "Baseline boundary document id must be positive when present."
            );
        }

        if (lastCommittedDocumentId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastCommittedDocumentId),
                lastCommittedDocumentId,
                "Last committed baseline document id cannot be negative."
            );
        }

        if (pagesSeeded < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pagesSeeded),
                pagesSeeded,
                "Baseline seeded page count cannot be negative."
            );
        }

        if (documentsVisited < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentsVisited),
                documentsVisited,
                "Baseline visited document count cannot be negative."
            );
        }

        if (workMutationCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workMutationCount),
                workMutationCount,
                "Baseline work mutation count cannot be negative."
            );
        }

        BoundaryDocumentId = boundaryDocumentId;
        LastCommittedDocumentId = lastCommittedDocumentId;
        PagesSeeded = pagesSeeded;
        DocumentsVisited = documentsVisited;
        WorkMutationCount = workMutationCount;
        LastAffectedDocumentIds = lastAffectedDocumentIds.IsDefault ? [] : lastAffectedDocumentIds;
        FailureResult = failureResult;
    }

    public bool Completed => FailureResult is null;

    public long? BoundaryDocumentId { get; }

    public long LastCommittedDocumentId { get; }

    public int PagesSeeded { get; }

    public int DocumentsVisited { get; }

    public int WorkMutationCount { get; }

    public ImmutableArray<long> LastAffectedDocumentIds { get; }

    public DocumentCacheAdministrativeCommandResult? FailureResult { get; }
}

internal sealed class DocumentCacheBaselineSeeder(
    IDocumentCacheBaselineSeedDelay delay,
    TimeProvider timeProvider,
    ILogger<DocumentCacheBaselineSeeder> logger,
    IDocumentCacheAdministrativeDrainer? drainer = null
) : IDocumentCacheBaselineSeeder
{
    public async Task<DocumentCacheBaselineSeedingResult> SeedAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
            DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
        CancellationToken effectiveCancellationToken = cancellationScope.Token;
        int pageSize = context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize;
        int highWaterMark = context
            .TargetContext
            .TargetExecutionContext
            .EffectiveSettings
            .ProjectorBaselineHighWaterMark;

        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.CaptureBoundary);
        DocumentCacheAdministrativeBaselineBoundaryResult boundary = await DocumentCacheAdministrativeWorkflow
            .ExecuteInTransactionAsync(
                context.MutexLease,
                IsolationLevel.ReadCommitted,
                (session, transactionCancellationToken) =>
                    context.Primitives.CaptureBaselineBoundaryAsync(session, transactionCancellationToken),
                commit: true,
                effectiveCancellationToken
            )
            .ConfigureAwait(false);
        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.CaptureBoundary);

        if (boundary.BoundaryDocumentId is null)
        {
            return new DocumentCacheBaselineSeedingResult(
                boundary.BoundaryDocumentId,
                lastCommittedDocumentId: 0,
                pagesSeeded: 0,
                documentsVisited: 0,
                workMutationCount: 0,
                lastAffectedDocumentIds: []
            );
        }

        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);

        long afterDocumentId = 0;
        var pagesSeeded = 0;
        var documentsVisited = 0;
        var workMutationCount = 0;
        ImmutableArray<long> lastAffectedDocumentIds = [];

        while (afterDocumentId < boundary.BoundaryDocumentId.Value)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();

            DocumentCacheAdministrativeWorkHighWaterObservationResult highWater = await ObserveHighWaterAsync(
                    context,
                    highWaterMark,
                    pageSize,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);

            if (highWater.IsAtOrAboveHighWater)
            {
                context.AddPhaseDiagnostic(
                    DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                    highWater.Message,
                    retryable: true,
                    highWater.DiagnosticDocumentIds.Take(pageSize).ToImmutableArray()
                );

                logger.LogInformation(
                    "DocumentCache baseline seeding for target {TargetKey} is relieving high-water backpressure with {ObservedWorkRows} observed work rows.",
                    context.TargetContext.TargetKey,
                    highWater.ObservedWorkRows
                );

                DocumentCacheAdministrativeDrainSliceResult? reliefResult = null;
                if (drainer is not null)
                {
                    reliefResult = await drainer
                        .DrainBackpressureReliefSliceAsync(context, effectiveCancellationToken)
                        .ConfigureAwait(false);

                    if (!reliefResult.Completed)
                    {
                        return new DocumentCacheBaselineSeedingResult(
                            boundary.BoundaryDocumentId,
                            afterDocumentId,
                            pagesSeeded,
                            documentsVisited,
                            workMutationCount,
                            lastAffectedDocumentIds,
                            reliefResult.FailureResult
                        );
                    }

                    context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                }

                if (reliefResult?.AcknowledgedOrRemovedItemCount > 0)
                {
                    continue;
                }

                await delay
                    .DelayAsync(
                        BackpressureDelay(context, reliefResult),
                        timeProvider,
                        effectiveCancellationToken
                    )
                    .ConfigureAwait(false);
                continue;
            }

            DocumentCacheAdministrativeBaselineSeedPageResult page = await ExecuteSeedPageAsync(
                    context,
                    boundary.BoundaryDocumentId.Value,
                    afterDocumentId,
                    pageSize,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);

            if (page.Status == DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey)
            {
                continue;
            }

            if (page.Status == DocumentCacheAdministrativeBaselineSeedPageStatus.Empty)
            {
                break;
            }

            pagesSeeded++;
            documentsVisited += page.RowsVisited;
            workMutationCount += page.WorkMutationCount;
            afterDocumentId = page.LastVisitedDocumentId!.Value;
            lastAffectedDocumentIds = page.AffectedDocumentIds.Take(pageSize).ToImmutableArray();
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);

        return new DocumentCacheBaselineSeedingResult(
            boundary.BoundaryDocumentId,
            afterDocumentId,
            pagesSeeded,
            documentsVisited,
            workMutationCount,
            lastAffectedDocumentIds
        );
    }

    private TimeSpan BackpressureDelay(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeDrainSliceResult? reliefResult
    )
    {
        TimeSpan pollInterval = context
            .TargetContext
            .TargetExecutionContext
            .EffectiveSettings
            .ProjectorPollInterval;
        if (reliefResult?.NextRetryAt is null)
        {
            return pollInterval;
        }

        TimeSpan retryDelay = reliefResult.NextRetryAt.Value - timeProvider.GetUtcNow();
        if (retryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return retryDelay < pollInterval ? retryDelay : pollInterval;
    }

    private static Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveHighWaterAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        int highWaterMark,
        int pageSize,
        CancellationToken cancellationToken
    ) =>
        DocumentCacheAdministrativeWorkflow.ExecuteInTransactionAsync(
            context.MutexLease,
            IsolationLevel.ReadCommitted,
            (session, transactionCancellationToken) =>
                context.Primitives.ObserveWorkHighWaterAsync(
                    session,
                    new DocumentCacheAdministrativeWorkHighWaterObservationRequest(
                        highWaterMark,
                        diagnosticCapacity: pageSize
                    ),
                    transactionCancellationToken
                ),
            commit: true,
            cancellationToken
        );

    private static Task<DocumentCacheAdministrativeBaselineSeedPageResult> ExecuteSeedPageAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize,
        CancellationToken cancellationToken
    ) =>
        DocumentCacheAdministrativeWorkflow.ExecuteInTransactionAsync(
            context.MutexLease,
            IsolationLevel.Serializable,
            (session, transactionCancellationToken) =>
                context.Primitives.SeedBaselinePageAsync(
                    session,
                    new DocumentCacheAdministrativeBaselineSeedPageRequest(
                        boundaryDocumentId,
                        afterDocumentId,
                        pageSize
                    ),
                    transactionCancellationToken
                ),
            static page =>
                page.Status != DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey,
            cancellationToken,
            beforeCommit: page =>
            {
                if (page.Mutated)
                {
                    context.MarkMutated();
                }
            }
        );
}
