// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

public sealed class DocumentCacheDirectFillTimeoutRecorder
{
    private readonly object _lock = new();
    private readonly List<DocumentCacheReadTelemetryRecord> _telemetryRecords = [];
    private int _materializationAttempts;
    private int _materializationCancellations;

    public int MaterializationAttempts
    {
        get
        {
            lock (_lock)
            {
                return _materializationAttempts;
            }
        }
    }

    public int MaterializationCancellations
    {
        get
        {
            lock (_lock)
            {
                return _materializationCancellations;
            }
        }
    }

    public IReadOnlyList<DocumentCacheReadTelemetryRecord> TelemetryRecords
    {
        get
        {
            lock (_lock)
            {
                return [.. _telemetryRecords];
            }
        }
    }

    internal void RecordMaterializationAttempt()
    {
        lock (_lock)
        {
            _materializationAttempts++;
        }
    }

    internal void RecordMaterializationCancellation()
    {
        lock (_lock)
        {
            _materializationCancellations++;
        }
    }

    internal void RecordTelemetry(string eventName, DocumentCacheReadTelemetryContext context)
    {
        lock (_lock)
        {
            _telemetryRecords.Add(
                new DocumentCacheReadTelemetryRecord(eventName, context.Operation, context.Outcome)
            );
        }
    }

    public int CountTelemetryRecords(string eventName, string outcome)
    {
        lock (_lock)
        {
            return _telemetryRecords.Count(record =>
                record.EventName == eventName && record.Outcome == outcome
            );
        }
    }
}

internal sealed class TimingOutDocumentCacheMaterializer(DocumentCacheDirectFillTimeoutRecorder recorder)
    : IDocumentCacheMaterializer
{
    private readonly DocumentCacheDirectFillTimeoutRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
        DocumentCacheMaterializationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        _recorder.RecordMaterializationAttempt();
        using var safetyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken,
            safetyTimeout.Token
        );

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedTimeout.Token);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            _recorder.RecordMaterializationCancellation();
            throw;
        }

        throw new InvalidOperationException("Direct-fill timeout test materializer was not canceled.");
    }
}

internal sealed class DirectFillTimeoutRecordingDocumentCacheReadTelemetry(
    DocumentCacheDirectFillTimeoutRecorder recorder
) : IDocumentCacheReadTelemetry
{
    private readonly DocumentCacheDirectFillTimeoutRecorder _recorder =
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
