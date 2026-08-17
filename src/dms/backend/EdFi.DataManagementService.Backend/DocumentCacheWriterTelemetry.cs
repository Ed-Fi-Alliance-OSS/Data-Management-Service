// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheWriterContentionParticipant
{
    CacheWriter = 1,
    CanonicalWriter = 2,
}

internal enum DocumentCacheWriterContentionPhase
{
    CacheDml = 1,
    Acknowledgement = 2,
    CanonicalPersist = 3,
}

internal static class DocumentCacheWriterTelemetryLabel
{
    public const string Unknown = DocumentCacheTelemetryLabel.Unknown;
    public const string CanonicalWrite = "CanonicalWrite";
    public const string AppliedWrite = "AppliedWrite";
    public const string Failed = "Failed";
}

internal sealed record DocumentCacheWriterMetricContext
{
    private const int MaxLabelLength = 128;

    private DocumentCacheWriterMetricContext(
        string provider,
        string targetKey,
        string purpose,
        string lifecycle,
        string outcome
    )
    {
        Provider = BoundSanitizedLabel(provider, nameof(provider));
        TargetKey = BoundSanitizedLabel(targetKey, nameof(targetKey));
        Purpose = BoundSanitizedLabel(purpose, nameof(purpose));
        Lifecycle = BoundSanitizedLabel(lifecycle, nameof(lifecycle));
        Outcome = BoundSanitizedLabel(outcome, nameof(outcome));
    }

    public string Provider { get; }

    public string TargetKey { get; }

    public string Purpose { get; }

    public string Lifecycle { get; }

    public string Outcome { get; }

    public static DocumentCacheWriterMetricContext ForCacheWriter(
        RelationalProviderToken providerToken,
        DocumentCacheProjectionTargetKey targetKey,
        DocumentCacheWriterPurpose purpose,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome
    ) =>
        new(
            ProviderLabel(providerToken),
            TargetKeyLabel(targetKey),
            purpose.ToString(),
            LifecycleLabel(lifecycleState),
            outcome.ToString()
        );

    public static DocumentCacheWriterMetricContext ForCacheWriter(
        DocumentCacheWriterRetryRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome
    ) =>
        new(
            request.SanitizedProvider,
            TargetKeyLabel(request.TargetKey),
            request.Purpose.ToString(),
            LifecycleLabel(lifecycleState),
            outcome.ToString()
        );

    public static DocumentCacheWriterMetricContext ForCanonicalWriter(
        RelationalProviderToken providerToken,
        string purpose,
        string outcome
    ) =>
        new(
            ProviderLabel(providerToken),
            DocumentCacheWriterTelemetryLabel.Unknown,
            purpose,
            DocumentCacheWriterTelemetryLabel.Unknown,
            outcome
        );

    public static DocumentCacheWriterMetricContext ForCanonicalWriter(
        SqlDialect dialect,
        string purpose,
        string outcome
    ) => ForCanonicalWriter(ProviderTokenForDialect(dialect), purpose, outcome);

    public TagList ToTags()
    {
        return
        [
            new("provider", Provider),
            new("target", TargetKey),
            new("purpose", Purpose),
            new("lifecycle", Lifecycle),
            new("outcome", Outcome),
        ];
    }

    private static string ProviderLabel(RelationalProviderToken providerToken) =>
        LoggingSanitizer.SanitizeForLogging(
            (providerToken ?? throw new ArgumentNullException(nameof(providerToken))).Value
        );

    private static string TargetKeyLabel(DocumentCacheProjectionTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return DocumentCacheTelemetryTargetLabel.FromProjectionTargetKey(targetKey);
    }

    private static RelationalProviderToken ProviderTokenForDialect(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => RelationalProviderToken.Postgresql,
            SqlDialect.Mssql => RelationalProviderToken.SqlServer,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    private static string LifecycleLabel(DocumentCacheLifecycleState? lifecycleState) =>
        lifecycleState?.ToString() ?? DocumentCacheWriterTelemetryLabel.Unknown;

    private static string BoundSanitizedLabel(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Metric label must be present.", parameterName);
        }

        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (sanitized.Length == 0)
        {
            sanitized = DocumentCacheWriterTelemetryLabel.Unknown;
        }

        return sanitized.Length <= MaxLabelLength ? sanitized : sanitized[..MaxLabelLength];
    }
}

internal interface IDocumentCacheWriterTelemetry
{
    void RecordOutcome(DocumentCacheWriterMetricContext context);

    void RecordTransactionDuration(DocumentCacheWriterMetricContext context, TimeSpan duration);

    void RecordCacheDmlDuration(DocumentCacheWriterMetricContext context, TimeSpan duration);

    void RecordAcknowledgementDuration(DocumentCacheWriterMetricContext context, TimeSpan duration);

    void RecordRetry(DocumentCacheWriterMetricContext context, TimeSpan duration, int attemptCount);

    void RecordSameDocumentWait(
        DocumentCacheWriterMetricContext context,
        DocumentCacheWriterContentionParticipant participant,
        DocumentCacheWriterContentionPhase phase,
        TimeSpan duration
    );
}

internal sealed class NoOpDocumentCacheWriterTelemetry : IDocumentCacheWriterTelemetry
{
    public static NoOpDocumentCacheWriterTelemetry Instance { get; } = new();

    private NoOpDocumentCacheWriterTelemetry() { }

    public void RecordOutcome(DocumentCacheWriterMetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    public void RecordTransactionDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
    {
        ValidateDurationMetric(context, duration, nameof(RecordTransactionDuration));
    }

    public void RecordCacheDmlDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
    {
        ValidateDurationMetric(context, duration, nameof(RecordCacheDmlDuration));
    }

    public void RecordAcknowledgementDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
    {
        ValidateDurationMetric(context, duration, nameof(RecordAcknowledgementDuration));
    }

    public void RecordRetry(DocumentCacheWriterMetricContext context, TimeSpan duration, int attemptCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireNonNegativeDuration(duration);
        RequirePositiveAttemptCount(attemptCount);
    }

    public void RecordSameDocumentWait(
        DocumentCacheWriterMetricContext context,
        DocumentCacheWriterContentionParticipant participant,
        DocumentCacheWriterContentionPhase phase,
        TimeSpan duration
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        DocumentCacheMaterializerGuards.RequireDefined(
            participant,
            nameof(participant),
            "Unsupported DocumentCache writer contention participant."
        );
        DocumentCacheMaterializerGuards.RequireDefined(
            phase,
            nameof(phase),
            "Unsupported DocumentCache writer contention phase."
        );
        RequireNonNegativeDuration(duration);
    }

    private static void ValidateDurationMetric(
        DocumentCacheWriterMetricContext context,
        TimeSpan duration,
        string metricName
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        RequireNonNegativeDuration(duration);
    }

    private static void RequireNonNegativeDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Duration must be nonnegative."
            );
        }
    }

    private static void RequirePositiveAttemptCount(int attemptCount)
    {
        if (attemptCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                attemptCount,
                "Attempt count must be positive."
            );
        }
    }
}

internal sealed class DocumentCacheWriterTelemetry : IDocumentCacheWriterTelemetry
{
    internal const string MeterName = "EdFi.DataManagementService.DocumentCacheWriter";
    internal const string OutcomeCounterName = "edfi.dms.document_cache.writer.outcomes";
    internal const string TransactionDurationName = "edfi.dms.document_cache.writer.transaction.duration";
    internal const string CacheDmlDurationName = "edfi.dms.document_cache.writer.cache_dml.duration";
    internal const string AcknowledgementDurationName =
        "edfi.dms.document_cache.writer.acknowledgement.duration";
    internal const string RetryDurationName = "edfi.dms.document_cache.writer.retry.duration";
    internal const string RetryAttemptsName = "edfi.dms.document_cache.writer.retry.attempts";
    internal const string SameDocumentWaitName = "edfi.dms.document_cache.writer.same_document_wait";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly Counter<long> _outcomeCounter;
    private readonly Histogram<double> _transactionDuration;
    private readonly Histogram<double> _cacheDmlDuration;
    private readonly Histogram<double> _acknowledgementDuration;
    private readonly Histogram<double> _retryDuration;
    private readonly Histogram<int> _retryAttempts;
    private readonly Histogram<double> _sameDocumentWait;

    public DocumentCacheWriterTelemetry()
        : this(SharedMeter) { }

    internal DocumentCacheWriterTelemetry(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        _outcomeCounter = meter.CreateCounter<long>(
            OutcomeCounterName,
            unit: "{outcome}",
            description: "DocumentCache writer outcomes."
        );
        _transactionDuration = meter.CreateHistogram<double>(
            TransactionDurationName,
            unit: "ms",
            description: "DocumentCache writer provider transaction duration."
        );
        _cacheDmlDuration = meter.CreateHistogram<double>(
            CacheDmlDurationName,
            unit: "ms",
            description: "DocumentCache writer cache DML duration."
        );
        _acknowledgementDuration = meter.CreateHistogram<double>(
            AcknowledgementDurationName,
            unit: "ms",
            description: "DocumentCache writer work acknowledgement duration."
        );
        _retryDuration = meter.CreateHistogram<double>(
            RetryDurationName,
            unit: "ms",
            description: "DocumentCache writer retry execution duration."
        );
        _retryAttempts = meter.CreateHistogram<int>(
            RetryAttemptsName,
            unit: "{attempt}",
            description: "DocumentCache writer retry attempt count."
        );
        _sameDocumentWait = meter.CreateHistogram<double>(
            SameDocumentWaitName,
            unit: "ms",
            description: "Same-document canonical writer and cache-writer database wait duration."
        );
    }

    public void RecordOutcome(DocumentCacheWriterMetricContext context)
    {
        TagList tags = context.ToTags();
        _outcomeCounter.Add(1, tags);
    }

    public void RecordTransactionDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
    {
        TagList tags = context.ToTags();
        _transactionDuration.Record(RequireNonNegativeMilliseconds(duration), tags);
    }

    public void RecordCacheDmlDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
    {
        TagList tags = context.ToTags();
        _cacheDmlDuration.Record(RequireNonNegativeMilliseconds(duration), tags);
    }

    public void RecordAcknowledgementDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
    {
        TagList tags = context.ToTags();
        _acknowledgementDuration.Record(RequireNonNegativeMilliseconds(duration), tags);
    }

    public void RecordRetry(DocumentCacheWriterMetricContext context, TimeSpan duration, int attemptCount)
    {
        if (attemptCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                attemptCount,
                "Attempt count must be positive."
            );
        }

        TagList tags = context.ToTags();
        _retryDuration.Record(RequireNonNegativeMilliseconds(duration), tags);
        _retryAttempts.Record(attemptCount, tags);
    }

    public void RecordSameDocumentWait(
        DocumentCacheWriterMetricContext context,
        DocumentCacheWriterContentionParticipant participant,
        DocumentCacheWriterContentionPhase phase,
        TimeSpan duration
    )
    {
        DocumentCacheMaterializerGuards.RequireDefined(
            participant,
            nameof(participant),
            "Unsupported DocumentCache writer contention participant."
        );
        DocumentCacheMaterializerGuards.RequireDefined(
            phase,
            nameof(phase),
            "Unsupported DocumentCache writer contention phase."
        );

        TagList tags = context.ToTags();
        tags.Add("participant", participant.ToString());
        tags.Add("phase", phase.ToString());
        _sameDocumentWait.Record(RequireNonNegativeMilliseconds(duration), tags);
    }

    internal static TimeSpan GetElapsedTime(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);

    internal static DocumentCacheLifecycleState? TryGetLifecycle(DocumentCacheWriterResult result) =>
        result switch
        {
            DocumentCacheWriterResult.LifecycleOrLatchFenced fence => fence.LifecycleState,
            DocumentCacheWriterResult.WorkAnomaly anomaly => anomaly.LifecycleState,
            _ => null,
        };

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
