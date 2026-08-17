// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheEnqueueTelemetryCanonicalOperation
{
    Insert = 1,
    Update = 2,
}

internal enum DocumentCacheEnqueueTelemetryResourceKind
{
    Resource = 1,
    Descriptor = 2,
}

internal enum DocumentCacheEnqueueFailureCategory
{
    StateMissingOrInvalid = 1,
    EnqueueTriggerUnavailable = 2,
    WorkPersistenceFailed = 3,
    ProviderTimeout = 4,
    ProviderUnavailable = 5,
    UnclassifiedProviderFailure = 6,
}

internal sealed record DocumentCacheEnqueueTelemetryContext
{
    public DocumentCacheEnqueueTelemetryContext(
        DocumentCacheTargetKey? targetKey,
        RelationalProviderToken? providerToken,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DocumentCacheEnqueueTelemetryResourceKind resourceKind,
        string? message = null
    )
    {
        if (targetKey is not null)
        {
            TargetKey = targetKey;
        }

        ProviderToken = providerToken;
        CanonicalOperation = RequireDefined(
            canonicalOperation,
            nameof(canonicalOperation),
            "Unsupported canonical enqueue operation."
        );
        ResourceKind = RequireDefined(resourceKind, nameof(resourceKind), "Unsupported resource kind.");
        Message = DocumentCacheEnqueueTelemetryText.Sanitize(message ?? "DocumentCache enqueue telemetry.");
    }

    public DocumentCacheTargetKey? TargetKey { get; }

    public RelationalProviderToken? ProviderToken { get; }

    public DocumentCacheEnqueueTelemetryCanonicalOperation CanonicalOperation { get; }

    public DocumentCacheEnqueueTelemetryResourceKind ResourceKind { get; }

    public string Message { get; }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, message);
        }

        return value;
    }
}

internal sealed record DocumentCacheEnqueueFailureTelemetryEvent
{
    public DocumentCacheEnqueueFailureTelemetryEvent(
        DateTimeOffset observedAt,
        DocumentCacheTargetKey? targetKey,
        DocumentCacheEnqueueFailureCategory category,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DocumentCacheEnqueueTelemetryResourceKind resourceKind,
        string? message
    )
    {
        ObservedAt = observedAt.ToUniversalTime();
        TargetKey = targetKey;
        Category = RequireDefined(category, nameof(category), "Unsupported enqueue failure category.");
        CanonicalOperation = RequireDefined(
            canonicalOperation,
            nameof(canonicalOperation),
            "Unsupported canonical enqueue operation."
        );
        ResourceKind = RequireDefined(resourceKind, nameof(resourceKind), "Unsupported resource kind.");
        Message = DocumentCacheEnqueueTelemetryText.Sanitize(message ?? "DocumentCache enqueue failure.");
    }

    public DateTimeOffset ObservedAt { get; }

    public DocumentCacheTargetKey? TargetKey { get; }

    public DocumentCacheEnqueueFailureCategory Category { get; }

    public DocumentCacheEnqueueTelemetryCanonicalOperation CanonicalOperation { get; }

    public DocumentCacheEnqueueTelemetryResourceKind ResourceKind { get; }

    public string Message { get; }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, message);
        }

        return value;
    }
}

internal sealed record DocumentCacheEnqueueFailureSnapshot
{
    public DocumentCacheEnqueueFailureSnapshot(
        ImmutableArray<DocumentCacheEnqueueFailureTelemetryEvent> recentEvents = default,
        int evictedCount = 0
    )
    {
        if (evictedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evictedCount),
                "Evicted count must not be negative."
            );
        }

        RecentEvents = recentEvents.IsDefault ? [] : recentEvents;
        EvictedCount = evictedCount;
    }

    public ImmutableArray<DocumentCacheEnqueueFailureTelemetryEvent> RecentEvents { get; }

    public int EvictedCount { get; }
}

internal interface IDocumentCacheEnqueueTelemetry
{
    void RecordSuccess(DocumentCacheEnqueueTelemetryContext context);

    void RecordFailure(
        DocumentCacheEnqueueTelemetryContext context,
        DocumentCacheEnqueueFailureCategory category
    );
}

internal interface IDocumentCacheEnqueueFailureObservationProvider
{
    DocumentCacheEnqueueFailureSnapshot GetFailureSnapshot(DocumentCacheTargetKey targetKey);
}

internal sealed class NoOpDocumentCacheEnqueueTelemetry : IDocumentCacheEnqueueTelemetry
{
    public static NoOpDocumentCacheEnqueueTelemetry Instance { get; } = new();

    private NoOpDocumentCacheEnqueueTelemetry() { }

    public void RecordSuccess(DocumentCacheEnqueueTelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    public void RecordFailure(
        DocumentCacheEnqueueTelemetryContext context,
        DocumentCacheEnqueueFailureCategory category
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                "Unsupported enqueue failure category."
            );
        }
    }
}

internal static class DocumentCacheEnqueueTelemetryWriteBoundary
{
    public static void RecordSuccessIfEnqueueEnabled(
        IDocumentCacheEnqueueTelemetry telemetry,
        IDataStoreSelection? dataStoreSelection,
        IDocumentCacheTargetRegistry? targetRegistry,
        string tenantKey,
        SqlDialect dialect,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DocumentCacheEnqueueTelemetryResourceKind resourceKind,
        string message
    )
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        if (
            !TryCreateContext(
                dataStoreSelection,
                targetRegistry,
                tenantKey,
                dialect,
                canonicalOperation,
                resourceKind,
                message,
                requireEnqueueEnabled: true,
                out DocumentCacheEnqueueTelemetryContext? context
            )
        )
        {
            return;
        }

        telemetry.RecordSuccess(context!);
    }

    public static void RecordFailureIfClassified(
        IDocumentCacheEnqueueTelemetry telemetry,
        IRelationalWriteExceptionClassifier writeExceptionClassifier,
        IDataStoreSelection? dataStoreSelection,
        IDocumentCacheTargetRegistry? targetRegistry,
        string tenantKey,
        SqlDialect dialect,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DocumentCacheEnqueueTelemetryResourceKind resourceKind,
        DbException exception
    )
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(writeExceptionClassifier);
        ArgumentNullException.ThrowIfNull(exception);

        DocumentCacheTargetKey? targetKey = TryCreateTelemetryTargetKey(dataStoreSelection, tenantKey);
        if (targetKey is null)
        {
            return;
        }

        if (
            !DocumentCacheEnqueueFailureClassifier.TryClassify(
                exception,
                writeExceptionClassifier,
                out DocumentCacheEnqueueFailureCategory category,
                out string message
            )
        )
        {
            return;
        }

        DocumentCacheTargetObservation? targetObservation = TryGetCurrentTarget(targetRegistry, targetKey);
        var context = new DocumentCacheEnqueueTelemetryContext(
            targetKey,
            targetObservation?.ProviderToken ?? ProviderTokenForDialect(dialect),
            canonicalOperation,
            resourceKind,
            message
        );

        telemetry.RecordFailure(context, category);
    }

    private static bool TryCreateContext(
        IDataStoreSelection? dataStoreSelection,
        IDocumentCacheTargetRegistry? targetRegistry,
        string tenantKey,
        SqlDialect dialect,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DocumentCacheEnqueueTelemetryResourceKind resourceKind,
        string message,
        bool requireEnqueueEnabled,
        out DocumentCacheEnqueueTelemetryContext? context
    )
    {
        context = null;

        DocumentCacheTargetKey? targetKey = TryCreateTelemetryTargetKey(dataStoreSelection, tenantKey);
        if (targetKey is null)
        {
            return false;
        }

        DocumentCacheTargetObservation? targetObservation = TryGetCurrentTarget(targetRegistry, targetKey);
        if (requireEnqueueEnabled && (targetObservation is null || !IsEnqueueEnabled(targetObservation)))
        {
            return false;
        }

        context = new DocumentCacheEnqueueTelemetryContext(
            targetKey,
            targetObservation?.ProviderToken ?? ProviderTokenForDialect(dialect),
            canonicalOperation,
            resourceKind,
            message
        );
        return true;
    }

    private static DocumentCacheTargetKey? TryCreateTelemetryTargetKey(
        IDataStoreSelection? dataStoreSelection,
        string tenantKey
    )
    {
        if (dataStoreSelection?.IsSet != true)
        {
            return null;
        }

        long dataStoreId;
        try
        {
            dataStoreId = dataStoreSelection.GetSelectedDataStore().Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return DocumentCacheTargetKey.TryCreate(
            tenantKey,
            dataStoreId,
            out DocumentCacheTargetKey? targetKey,
            out _
        )
            ? targetKey
            : null;
    }

    private static DocumentCacheTargetObservation? TryGetCurrentTarget(
        IDocumentCacheTargetRegistry? targetRegistry,
        DocumentCacheTargetKey targetKey
    ) =>
        targetRegistry?.CurrentSnapshot.Targets.SingleOrDefault(target => target.TargetKey.Equals(targetKey));

    private static bool IsEnqueueEnabled(DocumentCacheTargetObservation targetObservation) =>
        targetObservation.EnqueueTrigger?.Status == DocumentCacheEnqueueTriggerStatus.Satisfied
        && targetObservation.Lifecycle?.State
            is DocumentCacheLifecycleState.Resetting
                or DocumentCacheLifecycleState.Rebuilding
                or DocumentCacheLifecycleState.Tracking;

    private static RelationalProviderToken ProviderTokenForDialect(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => RelationalProviderToken.Postgresql,
            SqlDialect.Mssql => RelationalProviderToken.SqlServer,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
}

internal sealed class DocumentCacheEnqueueTelemetry(
    IOptions<DocumentCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<DocumentCacheEnqueueTelemetry>? logger = null,
    IDocumentCacheTargetRegistry? targetRegistry = null,
    Meter? meter = null
) : IDocumentCacheEnqueueTelemetry, IDocumentCacheEnqueueFailureObservationProvider
{
    internal const string MeterName = "EdFi.DataManagementService.DocumentCacheProjection";
    internal const string SuccessCounterName = "edfi.dms.document_cache.enqueue.successes";
    internal const string FailureCounterName = "edfi.dms.document_cache.enqueue.failures";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly int _retentionLimit = Math.Max(
        1,
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Projector.PageSize
    );
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<DocumentCacheEnqueueTelemetry> _logger =
        logger ?? NullLogger<DocumentCacheEnqueueTelemetry>.Instance;
    private readonly IDocumentCacheTargetRegistry? _targetRegistry = targetRegistry;
    private readonly ConcurrentDictionary<DocumentCacheTargetKey, RetainedFailureWindow> _failureWindows =
        new();
    private readonly Counter<long> _successCounter = (meter ?? SharedMeter).CreateCounter<long>(
        SuccessCounterName,
        unit: "{success}",
        description: "DocumentCache canonical write enqueue successes."
    );
    private readonly Counter<long> _failureCounter = (meter ?? SharedMeter).CreateCounter<long>(
        FailureCounterName,
        unit: "{failure}",
        description: "DocumentCache canonical write enqueue failures."
    );

    public void RecordSuccess(DocumentCacheEnqueueTelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _successCounter.Add(1, ToTags(context, category: null));

        _logger.LogInformation(
            "DocumentCacheEnqueueSucceeded provider {Provider} target {Target} canonicalOperation {CanonicalOperation} resourceKind {ResourceKind} outcome {Outcome}",
            context.ProviderToken?.Value ?? DocumentCacheTelemetryLabel.Unknown,
            ToLogTarget(context.TargetKey),
            DocumentCacheTelemetryLabel.LowerCamel(context.CanonicalOperation),
            DocumentCacheTelemetryLabel.LowerCamel(context.ResourceKind),
            "committed"
        );
    }

    public void RecordFailure(
        DocumentCacheEnqueueTelemetryContext context,
        DocumentCacheEnqueueFailureCategory category
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                "Unsupported enqueue failure category."
            );
        }

        _failureCounter.Add(1, ToTags(context, category));

        var failureEvent = new DocumentCacheEnqueueFailureTelemetryEvent(
            _timeProvider.GetUtcNow(),
            context.TargetKey,
            category,
            context.CanonicalOperation,
            context.ResourceKind,
            context.Message
        );

        ImmutableHashSet<DocumentCacheTargetKey>? currentTargetKeys = PruneRemovedTargetBuckets();

        if (
            context.TargetKey is not null
            && (currentTargetKeys is null || currentTargetKeys.Contains(context.TargetKey))
        )
        {
            _failureWindows
                .GetOrAdd(context.TargetKey, static _ => new RetainedFailureWindow())
                .Append(failureEvent, _retentionLimit);
        }

        _logger.LogWarning(
            "DocumentCacheEnqueueFailed provider {Provider} target {Target} category {Category} canonicalOperation {CanonicalOperation} resourceKind {ResourceKind} message {Message}",
            context.ProviderToken?.Value ?? DocumentCacheTelemetryLabel.Unknown,
            ToLogTarget(context.TargetKey),
            DocumentCacheTelemetryLabel.LowerCamel(category),
            DocumentCacheTelemetryLabel.LowerCamel(context.CanonicalOperation),
            DocumentCacheTelemetryLabel.LowerCamel(context.ResourceKind),
            failureEvent.Message
        );
    }

    public DocumentCacheEnqueueFailureSnapshot GetFailureSnapshot(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        ImmutableHashSet<DocumentCacheTargetKey>? currentTargetKeys = PruneRemovedTargetBuckets();
        if (currentTargetKeys is not null && !currentTargetKeys.Contains(targetKey))
        {
            return new DocumentCacheEnqueueFailureSnapshot();
        }

        return _failureWindows.TryGetValue(targetKey, out RetainedFailureWindow? window)
            ? window.Snapshot()
            : new DocumentCacheEnqueueFailureSnapshot();
    }

    private ImmutableHashSet<DocumentCacheTargetKey>? PruneRemovedTargetBuckets()
    {
        if (_targetRegistry is null)
        {
            return null;
        }

        ImmutableHashSet<DocumentCacheTargetKey> currentTargetKeys = _targetRegistry
            .CurrentSnapshot.Targets.Select(target => target.TargetKey)
            .ToImmutableHashSet();

        foreach (DocumentCacheTargetKey retainedTargetKey in _failureWindows.Keys)
        {
            if (!currentTargetKeys.Contains(retainedTargetKey))
            {
                _failureWindows.TryRemove(retainedTargetKey, out _);
            }
        }

        return currentTargetKeys;
    }

    private static string ToLogTarget(DocumentCacheTargetKey? targetKey) =>
        DocumentCacheTelemetryTargetLabel.FromTargetKey(targetKey);

    private static TagList ToTags(
        DocumentCacheEnqueueTelemetryContext context,
        DocumentCacheEnqueueFailureCategory? category
    )
    {
        TagList tags =
        [
            new("provider", context.ProviderToken?.Value ?? DocumentCacheTelemetryLabel.Unknown),
            new("target", DocumentCacheTelemetryTargetLabel.FromTargetKey(context.TargetKey)),
            new("canonical_operation", DocumentCacheTelemetryLabel.LowerCamel(context.CanonicalOperation)),
            new("resource_kind", DocumentCacheTelemetryLabel.LowerCamel(context.ResourceKind)),
        ];

        if (category is not null)
        {
            tags.Add("category", DocumentCacheTelemetryLabel.LowerCamel(category.Value));
        }
        else
        {
            tags.Add("outcome", "committed");
        }

        return tags;
    }

    private sealed class RetainedFailureWindow
    {
        private readonly object _sync = new();
        private readonly Queue<DocumentCacheEnqueueFailureTelemetryEvent> _events = new();
        private int _evictedCount;

        public void Append(DocumentCacheEnqueueFailureTelemetryEvent failureEvent, int retentionLimit)
        {
            lock (_sync)
            {
                _events.Enqueue(failureEvent);
                while (_events.Count > retentionLimit)
                {
                    _events.Dequeue();
                    _evictedCount++;
                }
            }
        }

        public DocumentCacheEnqueueFailureSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new DocumentCacheEnqueueFailureSnapshot([.. _events], _evictedCount);
            }
        }
    }
}

internal static class DocumentCacheEnqueueFailureClassifier
{
    private const string StateMissingMessage =
        "dms.DocumentCacheState singleton row is missing or unreadable for projection enqueue.";
    private const string UnsupportedLifecycleValueMessage =
        "dms.DocumentCacheState.ProjectionLifecycleState has unsupported value";
    private const string DocumentProjectionWorkName = "DocumentProjectionWork";
    private const string EnqueueFunctionPrefix = "TF_Document_EnqueueProjection";
    private const string EnqueueTriggerName = "TR_Document_EnqueueProjectionWork";

    public static bool TryClassify(
        DbException exception,
        IRelationalWriteExceptionClassifier writeExceptionClassifier,
        out DocumentCacheEnqueueFailureCategory category,
        out string message
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(writeExceptionClassifier);

        string sanitizedMessage = DocumentCacheEnqueueTelemetryText.Sanitize(
            string.IsNullOrWhiteSpace(exception.Message)
                ? "DocumentCache enqueue provider failure."
                : exception.Message
        );
        message = sanitizedMessage;

        if (
            sanitizedMessage.Contains(StateMissingMessage, StringComparison.Ordinal)
            || sanitizedMessage.Contains(UnsupportedLifecycleValueMessage, StringComparison.Ordinal)
        )
        {
            category = DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid;
            return true;
        }

        if (
            sanitizedMessage.Contains(EnqueueFunctionPrefix, StringComparison.OrdinalIgnoreCase)
            || sanitizedMessage.Contains(EnqueueTriggerName, StringComparison.OrdinalIgnoreCase)
        )
        {
            category = DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable;
            return true;
        }

        if (sanitizedMessage.Contains(DocumentProjectionWorkName, StringComparison.OrdinalIgnoreCase))
        {
            category = DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed;
            return true;
        }

        if (writeExceptionClassifier.IsTransientFailure(exception))
        {
            category = DocumentCacheEnqueueFailureCategory.ProviderTimeout;
            return true;
        }

        if (writeExceptionClassifier.TryClassify(exception, out _))
        {
            category = DocumentCacheEnqueueFailureCategory.UnclassifiedProviderFailure;
            return true;
        }

        category = DocumentCacheEnqueueFailureCategory.ProviderUnavailable;
        return true;
    }
}

internal static class DocumentCacheEnqueueTelemetryText
{
    private const int MaxLength = 512;

    public static string Sanitize(string value)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (sanitized.Length == 0)
        {
            return "DocumentCache enqueue telemetry.";
        }

        return sanitized.Length <= MaxLength ? sanitized : sanitized[..MaxLength];
    }
}
