// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentProjectionWorkPager
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentProjectionWorkPage> ReadPageAsync(
        DocumentProjectionWorkPageRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed record DocumentProjectionWorkPageRequest
{
    public DocumentProjectionWorkPageRequest(
        DocumentCacheTargetExecutionContext targetExecutionContext,
        DocumentCacheProjectionCursorState cursor
    )
    {
        TargetExecutionContext =
            targetExecutionContext ?? throw new ArgumentNullException(nameof(targetExecutionContext));
        Cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
    }

    public DocumentCacheTargetExecutionContext TargetExecutionContext { get; }

    public DocumentCacheProjectionCursorState Cursor { get; }

    public int PageSize => TargetExecutionContext.EffectiveSettings.ProjectorPageSize;
}

internal sealed record DocumentProjectionWorkPageItem
{
    public DocumentProjectionWorkPageItem(
        long documentId,
        long requiredContentVersion,
        DateTimeOffset firstEnqueuedAt,
        DateTimeOffset lastEnqueuedAt
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        if (requiredContentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredContentVersion),
                "Required content version must be positive."
            );
        }

        DocumentId = documentId;
        RequiredContentVersion = requiredContentVersion;
        FirstEnqueuedAt = firstEnqueuedAt;
        LastEnqueuedAt = lastEnqueuedAt;
    }

    public long DocumentId { get; }

    public long RequiredContentVersion { get; }

    public DateTimeOffset FirstEnqueuedAt { get; }

    public DateTimeOffset LastEnqueuedAt { get; }
}

internal sealed record DocumentProjectionWorkPage
{
    public DocumentProjectionWorkPage(IEnumerable<DocumentProjectionWorkPageItem> items, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive.");
        }

        ImmutableArray<DocumentProjectionWorkPageItem> materializedItems = items.ToImmutableArray();
        if (materializedItems.Length > pageSize)
        {
            throw new ArgumentException("DocumentProjectionWork page contains more rows than PageSize.");
        }

        Items = materializedItems;
        PageSize = pageSize;
    }

    public ImmutableArray<DocumentProjectionWorkPageItem> Items { get; }

    public int PageSize { get; }

    public bool IsEmpty => Items.IsEmpty;
}

internal sealed class DocumentCacheProjectionDrainPageProcessor(
    IDocumentProjectionWorkPager workPager,
    IDocumentCacheProjectionItemProcessor itemProcessor,
    ILogger<DocumentCacheProjectionDrainPageProcessor> logger,
    TimeProvider timeProvider
) : IDocumentCacheProjectionDrainPageProcessor
{
    private readonly IDocumentProjectionWorkPager _workPager =
        workPager ?? throw new ArgumentNullException(nameof(workPager));
    private readonly IDocumentCacheProjectionItemProcessor _itemProcessor =
        itemProcessor ?? throw new ArgumentNullException(nameof(itemProcessor));
    private readonly ILogger<DocumentCacheProjectionDrainPageProcessor> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DocumentCacheProjectionDrainPageResult> ProcessPageAsync(
        DocumentCacheProjectionDrainPageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        DocumentCacheProjectionTargetRuntimeContext targetContext = request.TargetContext;
        RequireProviderMatch(targetContext.TargetExecutionContext);

        using CancellationTokenSource linkedCancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                targetContext.CancellationToken
            );
        CancellationToken effectiveCancellationToken = linkedCancellationSource.Token;

        effectiveCancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        DocumentProjectionWorkPage page = await ReadPageAsync(targetContext, effectiveCancellationToken)
            .ConfigureAwait(false);
        if (page.IsEmpty && targetContext.Cursor.HasValue)
        {
            DocumentCacheProjectionCursorPassCompletion cursorPassCompletion =
                targetContext.FailureBackoffState.CompleteCursorPass(observedAt);
            targetContext.Cursor.Clear();

            if (
                !cursorPassCompletion.ProcessedEligibleWork
                && cursorPassCompletion.EarliestSuppressedRetryAt is not null
            )
            {
                ObserveTarget(targetContext, observedAt);
                return DocumentCacheProjectionDrainPageResult.NoEligibleWorkWithRetry(
                    cursorPassCompletion.EarliestSuppressedRetryAt.Value
                );
            }

            effectiveCancellationToken.ThrowIfCancellationRequested();
            page = await ReadPageAsync(targetContext, effectiveCancellationToken).ConfigureAwait(false);
        }

        if (page.IsEmpty)
        {
            targetContext.FailureBackoffState.RecordSuppressedTraversal([], observedAt);
            ObserveTarget(targetContext, observedAt);
            return DocumentCacheProjectionDrainPageResult.NoEligibleWork;
        }

        List<long> suppressedDocumentIds = [];
        List<long> documentScopedFailureIds = [];
        int processedItemCount = 0;
        int acknowledgedOrRemovedItemCount = 0;
        int documentScopedFailureCount = 0;
        foreach (DocumentProjectionWorkPageItem item in page.Items)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            targetContext.Cursor.Advance(item.FirstEnqueuedAt, item.DocumentId);
            if (targetContext.FailureBackoffState.IsSuppressed(item.DocumentId, observedAt))
            {
                suppressedDocumentIds.Add(item.DocumentId);
                continue;
            }

            targetContext.FailureBackoffState.RecordEligibleWorkProcessed();
            processedItemCount++;
            DocumentCacheProjectionItemProcessResult itemResult = await _itemProcessor
                .ProcessItemAsync(
                    new DocumentCacheProjectionItemProcessRequest(
                        targetContext,
                        item,
                        request.InvocationKind
                    ),
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (itemResult.AcknowledgedOrRemovedDurableWork)
            {
                acknowledgedOrRemovedItemCount++;
                targetContext.SchedulingState.RecordProjectionSuccess(
                    item.DocumentId,
                    item.RequiredContentVersion,
                    _timeProvider.GetUtcNow()
                );
            }

            if (itemResult.DocumentScopedFailureRecorded)
            {
                documentScopedFailureCount++;
                documentScopedFailureIds.Add(item.DocumentId);
            }

            if (itemResult.AdministrativeFailure is not null)
            {
                targetContext.FailureBackoffState.RecordSuppressedTraversal(
                    suppressedDocumentIds,
                    observedAt,
                    PageCapacityExhaustedBySuppressedWork(page, suppressedDocumentIds)
                );
                ObserveTarget(targetContext, observedAt);
                return DocumentCacheProjectionDrainPageResult.AdministrativeFailureResult(
                    processedItemCount,
                    acknowledgedOrRemovedItemCount,
                    documentScopedFailureCount,
                    documentScopedFailureIds.Take(page.PageSize).ToImmutableArray(),
                    itemResult.AdministrativeFailure
                );
            }

            if (itemResult.Outcome == DocumentCacheProjectionItemProcessOutcome.Continue)
            {
                continue;
            }

            targetContext.FailureBackoffState.RecordSuppressedTraversal(
                suppressedDocumentIds,
                observedAt,
                PageCapacityExhaustedBySuppressedWork(page, suppressedDocumentIds)
            );
            ObserveTarget(targetContext, observedAt);
            return itemResult.Outcome switch
            {
                DocumentCacheProjectionItemProcessOutcome.LifecycleFenced =>
                    DocumentCacheProjectionDrainPageResult.LifecycleFenced,
                DocumentCacheProjectionItemProcessOutcome.TargetBackoff =>
                    DocumentCacheProjectionDrainPageResult.TargetBackoff(itemResult.BackoffUntil!.Value),
                DocumentCacheProjectionItemProcessOutcome.TargetPaused =>
                    DocumentCacheProjectionDrainPageResult.TargetPaused(
                        processedItemCount,
                        acknowledgedOrRemovedItemCount,
                        documentScopedFailureCount,
                        documentScopedFailureIds.Take(page.PageSize).ToImmutableArray()
                    ),
                _ => throw new InvalidOperationException(
                    $"Unsupported DocumentCache projection item outcome '{itemResult.Outcome}'."
                ),
            };
        }

        targetContext.FailureBackoffState.RecordSuppressedTraversal(
            suppressedDocumentIds,
            observedAt,
            PageCapacityExhaustedBySuppressedWork(page, suppressedDocumentIds)
        );
        ObserveTarget(targetContext, observedAt);

        _logger.LogDebug(
            "DocumentCache projection paged {WorkItemCount} durable work rows and selected {SelectedWorkItemCount} eligible rows for target {TargetKey}.",
            page.Items.Length,
            processedItemCount,
            LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString())
        );

        return DocumentCacheProjectionDrainPageResult.PageProcessed(
            processedItemCount,
            acknowledgedOrRemovedItemCount,
            documentScopedFailureCount,
            documentScopedFailureIds.Take(page.PageSize).ToImmutableArray()
        );
    }

    private Task<DocumentProjectionWorkPage> ReadPageAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        CancellationToken cancellationToken
    ) =>
        _workPager.ReadPageAsync(
            new DocumentProjectionWorkPageRequest(targetContext.TargetExecutionContext, targetContext.Cursor),
            cancellationToken
        );

    private static bool PageCapacityExhaustedBySuppressedWork(
        DocumentProjectionWorkPage page,
        IReadOnlyCollection<long> suppressedDocumentIds
    ) => page.Items.Length == page.PageSize && suppressedDocumentIds.Count > 0;

    private void RequireProviderMatch(DocumentCacheTargetExecutionContext executionContext)
    {
        if (_workPager.ProviderToken != executionContext.ProviderToken)
        {
            throw new InvalidOperationException(
                "DocumentProjectionWork pager provider "
                    + $"'{_workPager.ProviderToken}' does not match target provider "
                    + $"'{executionContext.ProviderToken}'."
            );
        }
    }

    private static void ObserveTarget(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DateTimeOffset observedAt
    ) =>
        targetContext.ObservationSink.ObserveTarget(
            DocumentCacheProjectionTargetHealthSnapshotFactory.Create(
                targetContext,
                observedAt,
                executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                    isRunning: true,
                    isActivelyProcessing: true,
                    isWaitingForWorkerGate: false,
                    isInBackoff: false,
                    backoffUntil: null,
                    cancellationRequested: targetContext.CancellationRequested,
                    cancellationObservedAt: targetContext.CancellationRequested ? observedAt : null
                )
            )
        );
}

internal static class DocumentProjectionWorkPagingGuards
{
    public static string RequireConnectionString(
        DocumentProjectionWorkPageRequest request,
        RelationalProviderToken providerToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providerToken);

        DocumentCacheTargetExecutionContext executionContext = request.TargetExecutionContext;
        if (executionContext.ProviderToken != providerToken)
        {
            throw new InvalidOperationException(
                "DocumentProjectionWork pager provider "
                    + $"'{providerToken}' does not match target provider "
                    + $"'{executionContext.ProviderToken}'."
            );
        }

        if (executionContext.ConnectionInput.ProviderToken != providerToken)
        {
            throw new InvalidOperationException(
                "DocumentProjectionWork pager connection provider "
                    + $"'{executionContext.ConnectionInput.ProviderToken}' does not match adapter provider "
                    + $"'{providerToken}'."
            );
        }

        return executionContext.ConnectionInput.Value;
    }

    public static DateTimeOffset NormalizeUtcTimestamp(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Utc
                    ? dateTime
                    : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            ),
            _ => DateTimeOffset
                .Parse(
                    value.ToString()
                        ?? throw new InvalidOperationException("DocumentProjectionWork timestamp was null."),
                    System.Globalization.CultureInfo.InvariantCulture
                )
                .ToUniversalTime(),
        };
}
