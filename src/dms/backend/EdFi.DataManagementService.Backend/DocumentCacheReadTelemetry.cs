// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheReadAccelerationOperation
{
    GetById,
    Query,
}

internal static class DocumentCacheReadTelemetryLabel
{
    public const string Unknown = "unknown";
    public const string Primary = "Primary";
    public const string GetByIdOperation = "getById";
    public const string QueryOperation = "query";
    public const string ResourceKind = "resource";
    public const string DescriptorKind = "descriptor";
    public const string Attempted = "Attempted";
    public const string Hit = "Hit";
    public const string PageHit = "PageHit";
    public const string UnexpectedException = "UnexpectedException";
    public const string Skipped = "Skipped";
    public const string SkippedFallbackNotSuccessful = "SkippedFallbackNotSuccessful";
    public const string SkippedReadAccelerationDisabled = "SkippedReadAccelerationDisabled";
    public const string SkippedSelectedDataStoreUnavailable = "SkippedSelectedDataStoreUnavailable";
    public const string SkippedInvalidTargetKey = "SkippedInvalidTargetKey";
    public const string SkippedUnresolvedTarget = "SkippedUnresolvedTarget";
    public const string SkippedTargetRegistryUnavailable = "SkippedTargetRegistryUnavailable";
    public const string SkippedTargetReadAccelerationDisabled = "SkippedTargetReadAccelerationDisabled";
    public const string SkippedNoCandidates = "SkippedNoCandidates";
    public const string SkippedNoServedCandidate = "SkippedNoServedCandidate";
    public const string SkippedCacheUnavailable = "SkippedCacheUnavailable";
    public const string SkippedTargetMismatch = "SkippedTargetMismatch";
    public const string SkippedRequestCanceled = "SkippedRequestCanceled";
    public const string SkippedTargetIneligible = "SkippedTargetIneligible";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string TimedOut = "TimedOut";
    public const string CallerCanceled = "CallerCanceled";
    public const string Canceled = "Canceled";
    public const string Completed = "Completed";
}

internal sealed record DocumentCacheReadTelemetryContext
{
    private const int MaxLabelLength = 128;

    private DocumentCacheReadTelemetryContext(
        string provider,
        string targetKey,
        string effectiveTargetKind,
        string operation,
        string resourceKind,
        string outcome
    )
    {
        Provider = BoundSanitizedLabel(provider, nameof(provider));
        TargetKey = BoundSanitizedLabel(targetKey, nameof(targetKey));
        EffectiveTargetKind = BoundSanitizedLabel(effectiveTargetKind, nameof(effectiveTargetKind));
        Operation = BoundSanitizedLabel(operation, nameof(operation));
        ResourceKind = BoundSanitizedLabel(resourceKind, nameof(resourceKind));
        Outcome = BoundSanitizedLabel(outcome, nameof(outcome));
    }

    public string Provider { get; }

    public string TargetKey { get; }

    public string EffectiveTargetKind { get; }

    public string Operation { get; }

    public string ResourceKind { get; }

    public string Outcome { get; }

    public static DocumentCacheReadTelemetryContext ForTarget(
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        string outcome
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        return new(
            targetContext.ProviderToken.Value,
            targetContext.TargetKey.ToString(),
            DocumentCacheReadTelemetryLabel.Primary,
            OperationLabel(operation),
            ResourceKindLabel(resourceKind),
            outcome
        );
    }

    public static DocumentCacheReadTelemetryContext ForNoTarget(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        string outcome
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        return new(
            DocumentCacheReadTelemetryLabel.Unknown,
            DocumentCacheReadTelemetryLabel.Unknown,
            DocumentCacheReadTelemetryLabel.Unknown,
            OperationLabel(operation),
            ResourceKindLabel(resourceKind),
            outcome
        );
    }

    public TagList ToTags()
    {
        return
        [
            new("provider", Provider),
            new("target_key", TargetKey),
            new("effective_target_kind", EffectiveTargetKind),
            new("operation", Operation),
            new("resource_kind", ResourceKind),
            new("outcome", Outcome),
        ];
    }

    private static string BoundSanitizedLabel(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Metric label must be present.", parameterName);
        }

        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (sanitized.Length == 0)
        {
            sanitized = DocumentCacheReadTelemetryLabel.Unknown;
        }

        return sanitized.Length <= MaxLabelLength ? sanitized : sanitized[..MaxLabelLength];
    }

    internal static string OperationLabel(DocumentCacheReadAccelerationOperation operation) =>
        operation switch
        {
            DocumentCacheReadAccelerationOperation.GetById =>
                DocumentCacheReadTelemetryLabel.GetByIdOperation,
            DocumentCacheReadAccelerationOperation.Query => DocumentCacheReadTelemetryLabel.QueryOperation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported read operation."
            ),
        };

    internal static string ResourceKindLabel(DocumentCacheReadAccelerationResourceKind resourceKind) =>
        resourceKind switch
        {
            DocumentCacheReadAccelerationResourceKind.Resource =>
                DocumentCacheReadTelemetryLabel.ResourceKind,
            DocumentCacheReadAccelerationResourceKind.Descriptor =>
                DocumentCacheReadTelemetryLabel.DescriptorKind,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resourceKind),
                resourceKind,
                "Unsupported read resource kind."
            ),
        };
}

internal interface IDocumentCacheReadTelemetry
{
    void RecordAttempt(DocumentCacheReadTelemetryContext context);

    void RecordHit(DocumentCacheReadTelemetryContext context);

    void RecordPageHit(DocumentCacheReadTelemetryContext context);

    void RecordMiss(DocumentCacheReadTelemetryContext context);

    void RecordFallback(DocumentCacheReadTelemetryContext context);

    void RecordCacheUnavailable(DocumentCacheReadTelemetryContext context);

    void RecordAdapterAcquisitionFailure(DocumentCacheReadTelemetryContext context);

    void RecordUnexpectedException(DocumentCacheReadTelemetryContext context);

    void RecordDirectFill(DocumentCacheReadTelemetryContext context);

    void RecordCacheLookupDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration);

    void RecordDirectFillDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration);
}

internal sealed class NoOpDocumentCacheReadTelemetry : IDocumentCacheReadTelemetry
{
    public static NoOpDocumentCacheReadTelemetry Instance { get; } = new();

    private NoOpDocumentCacheReadTelemetry() { }

    public void RecordAttempt(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordHit(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordPageHit(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordMiss(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordFallback(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordCacheUnavailable(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordAdapterAcquisitionFailure(DocumentCacheReadTelemetryContext context) =>
        ValidateContext(context);

    public void RecordUnexpectedException(DocumentCacheReadTelemetryContext context) =>
        ValidateContext(context);

    public void RecordDirectFill(DocumentCacheReadTelemetryContext context) => ValidateContext(context);

    public void RecordCacheLookupDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
        ValidateDuration(context, duration);

    public void RecordDirectFillDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
        ValidateDuration(context, duration);

    private static void ValidateContext(DocumentCacheReadTelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    private static void ValidateDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration)
    {
        ValidateContext(context);
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Duration must be nonnegative."
            );
        }
    }
}

internal sealed class DocumentCacheReadTelemetry : IDocumentCacheReadTelemetry
{
    internal const string MeterName = "EdFi.DataManagementService.DocumentCacheRead";
    internal const string AttemptCounterName = "edfi.dms.document_cache.read.attempts";
    internal const string HitCounterName = "edfi.dms.document_cache.read.hits";
    internal const string PageHitCounterName = "edfi.dms.document_cache.read.page_hits";
    internal const string MissCounterName = "edfi.dms.document_cache.read.misses";
    internal const string FallbackCounterName = "edfi.dms.document_cache.read.fallbacks";
    internal const string CacheUnavailableCounterName = "edfi.dms.document_cache.read.cache_unavailable";
    internal const string AdapterAcquisitionFailureCounterName =
        "edfi.dms.document_cache.read.adapter_acquisition_failures";
    internal const string UnexpectedExceptionCounterName =
        "edfi.dms.document_cache.read.unexpected_exceptions";
    internal const string DirectFillCounterName = "edfi.dms.document_cache.read.direct_fill";
    internal const string CacheLookupDurationName = "edfi.dms.document_cache.read.lookup.duration";
    internal const string DirectFillDurationName = "edfi.dms.document_cache.read.direct_fill.duration";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly Counter<long> _attemptCounter;
    private readonly Counter<long> _hitCounter;
    private readonly Counter<long> _pageHitCounter;
    private readonly Counter<long> _missCounter;
    private readonly Counter<long> _fallbackCounter;
    private readonly Counter<long> _cacheUnavailableCounter;
    private readonly Counter<long> _adapterAcquisitionFailureCounter;
    private readonly Counter<long> _unexpectedExceptionCounter;
    private readonly Counter<long> _directFillCounter;
    private readonly Histogram<double> _cacheLookupDuration;
    private readonly Histogram<double> _directFillDuration;
    private readonly ILogger<DocumentCacheReadTelemetry> _logger;

    public DocumentCacheReadTelemetry(ILogger<DocumentCacheReadTelemetry>? logger = null)
        : this(SharedMeter, logger) { }

    internal DocumentCacheReadTelemetry(Meter meter, ILogger<DocumentCacheReadTelemetry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(meter);

        _logger = logger ?? NullLogger<DocumentCacheReadTelemetry>.Instance;
        _attemptCounter = meter.CreateCounter<long>(
            AttemptCounterName,
            unit: "{attempt}",
            description: "DocumentCache read-acceleration cache lookup attempts."
        );
        _hitCounter = meter.CreateCounter<long>(
            HitCounterName,
            unit: "{hit}",
            description: "DocumentCache read-acceleration cache hits."
        );
        _pageHitCounter = meter.CreateCounter<long>(
            PageHitCounterName,
            unit: "{hit}",
            description: "DocumentCache read-acceleration all-or-nothing query page hits."
        );
        _missCounter = meter.CreateCounter<long>(
            MissCounterName,
            unit: "{miss}",
            description: "DocumentCache read-acceleration cache miss and non-hit reasons."
        );
        _fallbackCounter = meter.CreateCounter<long>(
            FallbackCounterName,
            unit: "{fallback}",
            description: "DocumentCache read-acceleration relational fallback reasons."
        );
        _cacheUnavailableCounter = meter.CreateCounter<long>(
            CacheUnavailableCounterName,
            unit: "{unavailable}",
            description: "DocumentCache read-acceleration cache-unavailable outcomes."
        );
        _adapterAcquisitionFailureCounter = meter.CreateCounter<long>(
            AdapterAcquisitionFailureCounterName,
            unit: "{failure}",
            description: "DocumentCache read-acceleration expected adapter acquisition failures."
        );
        _unexpectedExceptionCounter = meter.CreateCounter<long>(
            UnexpectedExceptionCounterName,
            unit: "{exception}",
            description: "DocumentCache read-acceleration unexpected exceptions that propagate."
        );
        _directFillCounter = meter.CreateCounter<long>(
            DirectFillCounterName,
            unit: "{outcome}",
            description: "DocumentCache read-acceleration direct-fill outcomes."
        );
        _cacheLookupDuration = meter.CreateHistogram<double>(
            CacheLookupDurationName,
            unit: "ms",
            description: "DocumentCache read-acceleration cache lookup duration."
        );
        _directFillDuration = meter.CreateHistogram<double>(
            DirectFillDurationName,
            unit: "ms",
            description: "DocumentCache read-acceleration direct-fill duration."
        );
    }

    public void RecordAttempt(DocumentCacheReadTelemetryContext context)
    {
        _attemptCounter.Add(1, context.ToTags());
        LogDebug("attempt", context);
    }

    public void RecordHit(DocumentCacheReadTelemetryContext context)
    {
        _hitCounter.Add(1, context.ToTags());
        LogDebug("hit", context);
    }

    public void RecordPageHit(DocumentCacheReadTelemetryContext context)
    {
        _pageHitCounter.Add(1, context.ToTags());
        LogDebug("page-hit", context);
    }

    public void RecordMiss(DocumentCacheReadTelemetryContext context)
    {
        _missCounter.Add(1, context.ToTags());
        LogDebug("miss", context);
    }

    public void RecordFallback(DocumentCacheReadTelemetryContext context)
    {
        _fallbackCounter.Add(1, context.ToTags());
        LogDebug("fallback", context);
    }

    public void RecordCacheUnavailable(DocumentCacheReadTelemetryContext context)
    {
        _cacheUnavailableCounter.Add(1, context.ToTags());
        LogDebug("cache-unavailable", context);
    }

    public void RecordAdapterAcquisitionFailure(DocumentCacheReadTelemetryContext context)
    {
        _adapterAcquisitionFailureCounter.Add(1, context.ToTags());
        LogDebug("adapter-acquisition-failure", context);
    }

    public void RecordUnexpectedException(DocumentCacheReadTelemetryContext context)
    {
        _unexpectedExceptionCounter.Add(1, context.ToTags());
        LogDebug("unexpected-exception", context);
    }

    public void RecordDirectFill(DocumentCacheReadTelemetryContext context)
    {
        _directFillCounter.Add(1, context.ToTags());
        LogDebug("direct-fill", context);
    }

    public void RecordCacheLookupDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
        _cacheLookupDuration.Record(RequireNonNegativeMilliseconds(duration), context.ToTags());

    public void RecordDirectFillDuration(DocumentCacheReadTelemetryContext context, TimeSpan duration) =>
        _directFillDuration.Record(RequireNonNegativeMilliseconds(duration), context.ToTags());

    internal static TimeSpan GetElapsedTime(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);

    private void LogDebug(string metric, DocumentCacheReadTelemetryContext context)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "DocumentCache read telemetry recorded {Metric}. Provider {Provider}; target {TargetKey}; effectiveTargetKind {EffectiveTargetKind}; operation {Operation}; resourceKind {ResourceKind}; outcome {Outcome}.",
            metric,
            context.Provider,
            context.TargetKey,
            context.EffectiveTargetKind,
            context.Operation,
            context.ResourceKind,
            context.Outcome
        );
    }

    private static double RequireNonNegativeMilliseconds(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Duration must be nonnegative."
            );
        }

        return duration.TotalMilliseconds;
    }
}
