// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

public sealed record DocumentCacheReadLookupAttempt(string Operation, string ResourceKind, string TargetKey);

public sealed record DocumentCacheReadTelemetryRecord(string EventName, string Operation, string Outcome);

public sealed class DocumentCacheReadAcquisitionFailureRecorder
{
    private readonly object _sync = new();
    private readonly List<DocumentCacheReadLookupAttempt> _lookupAttempts = [];
    private readonly List<DocumentCacheReadTelemetryRecord> _telemetryRecords = [];

    public IReadOnlyList<DocumentCacheReadLookupAttempt> LookupAttempts
    {
        get
        {
            lock (_sync)
            {
                return [.. _lookupAttempts];
            }
        }
    }

    public IReadOnlyList<DocumentCacheReadTelemetryRecord> TelemetryRecords
    {
        get
        {
            lock (_sync)
            {
                return [.. _telemetryRecords];
            }
        }
    }

    internal void RecordLookupAttempt(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext
    )
    {
        lock (_sync)
        {
            _lookupAttempts.Add(
                new DocumentCacheReadLookupAttempt(
                    operation.ToString(),
                    resourceKind.ToString(),
                    targetContext.TargetKey.ToString()
                )
            );
        }
    }

    public int CountLookupAttempts(string operation) =>
        LookupAttempts.Count(attempt =>
            string.Equals(attempt.Operation, operation, StringComparison.Ordinal)
        );

    public int CountTelemetryRecords(string eventName, string outcome) =>
        TelemetryRecords.Count(record =>
            string.Equals(record.EventName, eventName, StringComparison.Ordinal)
            && string.Equals(record.Outcome, outcome, StringComparison.Ordinal)
        );

    internal void RecordTelemetry(string eventName, DocumentCacheReadTelemetryContext context)
    {
        lock (_sync)
        {
            _telemetryRecords.Add(
                new DocumentCacheReadTelemetryRecord(eventName, context.Operation, context.Outcome)
            );
        }
    }
}

internal sealed class RecordingDocumentCacheReadTelemetry(
    DocumentCacheReadAcquisitionFailureRecorder recorder
) : IDocumentCacheReadTelemetry
{
    private readonly DocumentCacheReadAcquisitionFailureRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public void RecordAttempt(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordAttempt), context);

    public void RecordHit(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordHit), context);

    public void RecordPageHit(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordPageHit), context);

    public void RecordMiss(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordMiss), context);

    public void RecordFallback(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordFallback), context);

    public void RecordCacheUnavailable(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordCacheUnavailable), context);

    public void RecordAdapterAcquisitionFailure(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(
            nameof(IDocumentCacheReadTelemetry.RecordAdapterAcquisitionFailure),
            context
        );

    public void RecordUnexpectedException(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordUnexpectedException), context);

    public void RecordDirectFill(DocumentCacheReadTelemetryContext context) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordDirectFill), context);

    public void RecordCacheLookupDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordCacheLookupDuration), context);

    public void RecordDirectFillDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
        _recorder.RecordTelemetry(nameof(IDocumentCacheReadTelemetry.RecordDirectFillDuration), context);
}

internal sealed class AcquisitionFailureDocumentCacheReadLookupAdapter(
    DocumentCacheReadAcquisitionFailureRecorder recorder
) : IDocumentCacheReadLookupAdapter
{
    private readonly DocumentCacheReadAcquisitionFailureRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public Task<DocumentCacheReadLookupResult<GetResult>> TryGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        _recorder.RecordLookupAttempt(
            DocumentCacheReadAccelerationOperation.GetById,
            request.ResourceKind,
            targetContext
        );

        return Task.FromResult(
            DocumentCacheReadLookupResult<GetResult>.FallbackFromLookupOutcome(
                DocumentCacheReadLookupOutcome.CacheUnavailable,
                isAdapterAcquisitionFailure: true
            )
        );
    }

    public Task<DocumentCacheReadLookupResult<QueryResult>> TryQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        _recorder.RecordLookupAttempt(
            DocumentCacheReadAccelerationOperation.Query,
            request.ResourceKind,
            targetContext
        );

        return Task.FromResult(
            DocumentCacheReadLookupResult<QueryResult>.FallbackFromLookupOutcome(
                DocumentCacheReadLookupOutcome.CacheUnavailable,
                isAdapterAcquisitionFailure: true
            )
        );
    }
}
