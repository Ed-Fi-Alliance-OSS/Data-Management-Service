// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Core.Telemetry;

/// <summary>
/// The complete set of allowed dimension values for collection-paging metrics.
/// </summary>
/// <remarks>
/// Every tag value is one of these constants, which is what keeps tag-set cardinality bounded and keeps
/// request data out of the metric. Nothing derived from a request — resource name, tenant key, namespace,
/// client identity, filter names or values, page-token text, decoded cursor bounds, candidate
/// identifiers, or exception text — may become a label.
/// </remarks>
internal static class CollectionPagingTelemetryLabel
{
    // paging_mode
    public const string TraditionalPagingMode = "traditional";
    public const string CursorPagingMode = "cursor";
    public const string PartitionPagingMode = "partition";

    // command_category — the selection command shape a produced result was built around.
    public const string PageCommandCategory = "page";
    public const string PageWithCountCommandCategory = "page_with_count";
    public const string BoundaryCommandCategory = "boundary";
    public const string NoCommandCategory = "none";

    // provider
    public const string PostgresqlProvider = "postgresql";
    public const string SqlServerProvider = "sqlserver";
    public const string UnknownProvider = "unknown";

    // outcome
    public const string SuccessOutcome = "success";
    public const string TerminalPageOutcome = "terminal_page";
    public const string EarlyEmptyOutcome = "early_empty";
    public const string ValidationRejectedOutcome = "validation_rejected";
    public const string NotAuthorizedOutcome = "not_authorized";
    public const string NotImplementedOutcome = "not_implemented";
    public const string SecurityConfigurationOutcome = "security_configuration";
    public const string RetryExhaustedOutcome = "retry_exhausted";
    public const string UnknownFailureOutcome = "unknown_failure";
    public const string ExecutionExceptionOutcome = "execution_exception";
}

/// <summary>
/// The four dimensions carried by every collection-paging measurement.
/// </summary>
/// <remarks>
/// A single context type shared by regular resources and descriptors: their execution semantics are
/// equivalent, so nothing in the contract splits the two apart.
/// </remarks>
internal sealed record CollectionPagingTelemetryContext
{
    private const int MaxLabelLength = 128;

    private CollectionPagingTelemetryContext(
        string pagingMode,
        string commandCategory,
        string provider,
        string outcome
    )
    {
        PagingMode = BoundSanitizedLabel(pagingMode, nameof(pagingMode));
        CommandCategory = BoundSanitizedLabel(commandCategory, nameof(commandCategory));
        Provider = BoundSanitizedLabel(provider, nameof(provider));
        Outcome = BoundSanitizedLabel(outcome, nameof(outcome));
    }

    public string PagingMode { get; }

    public string CommandCategory { get; }

    public string Provider { get; }

    public string Outcome { get; }

    /// <summary>
    /// A context for a GET-many request whose paging mode was resolved.
    /// </summary>
    public static CollectionPagingTelemetryContext ForPaging(
        CollectionPaging paging,
        string commandCategory,
        SqlDialect? dialect,
        string outcome
    ) => ForPagingMode(PagingModeLabel(paging), commandCategory, dialect, outcome);

    /// <summary>
    /// A context built from an already-chosen paging-mode label, for the partition operation and for a
    /// rejected request whose paging mode was never assigned.
    /// </summary>
    public static CollectionPagingTelemetryContext ForPagingMode(
        string pagingMode,
        string commandCategory,
        SqlDialect? dialect,
        string outcome
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pagingMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        return new(pagingMode, commandCategory, ProviderLabel(dialect), outcome);
    }

    public TagList ToTags()
    {
        return
        [
            new("paging_mode", PagingMode),
            new("command_category", CommandCategory),
            new("provider", Provider),
            new("outcome", Outcome),
        ];
    }

    internal static string PagingModeLabel(CollectionPaging paging)
    {
        ArgumentNullException.ThrowIfNull(paging);

        return paging switch
        {
            CollectionPaging.Traditional => CollectionPagingTelemetryLabel.TraditionalPagingMode,
            CollectionPaging.Cursor => CollectionPagingTelemetryLabel.CursorPagingMode,
            _ => throw new ArgumentOutOfRangeException(
                nameof(paging),
                paging,
                "Unsupported collection paging mode."
            ),
        };
    }

    /// <summary>
    /// The provider label, <c>unknown</c> only when the request was answered before mapping-set
    /// resolution.
    /// </summary>
    internal static string ProviderLabel(SqlDialect? dialect) =>
        dialect switch
        {
            null => CollectionPagingTelemetryLabel.UnknownProvider,
            SqlDialect.Pgsql => CollectionPagingTelemetryLabel.PostgresqlProvider,
            SqlDialect.Mssql => CollectionPagingTelemetryLabel.SqlServerProvider,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    private static string BoundSanitizedLabel(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Metric label must be present.", parameterName);
        }

        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (sanitized.Length == 0)
        {
            sanitized = CollectionPagingTelemetryLabel.UnknownProvider;
        }

        return sanitized.Length <= MaxLabelLength ? sanitized : sanitized[..MaxLabelLength];
    }
}

/// <summary>
/// Records bounded metrics for the three live collection-read shapes: traditional paging, cursor paging,
/// and partition planning.
/// </summary>
/// <remarks>
/// Page-size instruments are recorded only for GET-many and partition-count instruments only for
/// <c>/partitions</c>, so neither histogram mixes two units of measure. Duration is recorded only where
/// backend execution was attempted, which is why a validation rejection has its own method rather than a
/// duration argument callers would have to zero out.
/// </remarks>
internal interface ICollectionPagingTelemetry
{
    /// <summary>
    /// Records a classified GET-many outcome.
    /// </summary>
    /// <param name="returnedPageSize">
    /// The number of documents in the response, or null when no page was produced. Null suppresses the
    /// returned-page-size measurement so a failure never contributes a zero to that histogram.
    /// </param>
    void RecordPage(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPageSize,
        int? returnedPageSize
    );

    /// <summary>
    /// Records a classified <c>/partitions</c> outcome.
    /// </summary>
    /// <param name="returnedPartitionCount">
    /// The number of boundary ranges produced, or null when no boundary set was produced.
    /// </param>
    void RecordPartitions(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPartitionCount,
        int? returnedPartitionCount
    );

    /// <summary>
    /// Records a request answered by parameter validation. No duration is recorded: nothing executed, so
    /// a microsecond-scale sample would distort the duration distribution.
    /// </summary>
    void RecordValidationRejected(CollectionPagingTelemetryContext context);
}

/// <summary>
/// Validates arguments exactly as the recording implementation does and emits nothing.
/// </summary>
/// <remarks>
/// Wired explicitly at the Change Query construction of the shared query-validation middleware, whose
/// endpoints do not page by cursor and are therefore not collection-paging events. Validating
/// identically is what keeps that wiring from hiding an argument fault that would surface in production.
/// </remarks>
internal sealed class NoOpCollectionPagingTelemetry : ICollectionPagingTelemetry
{
    public static NoOpCollectionPagingTelemetry Instance { get; } = new();

    private NoOpCollectionPagingTelemetry() { }

    public void RecordPage(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPageSize,
        int? returnedPageSize
    ) => ValidateMeasurement(context, duration, requestedPageSize, returnedPageSize);

    public void RecordPartitions(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPartitionCount,
        int? returnedPartitionCount
    ) => ValidateMeasurement(context, duration, requestedPartitionCount, returnedPartitionCount);

    public void RecordValidationRejected(CollectionPagingTelemetryContext context) =>
        ArgumentNullException.ThrowIfNull(context);

    private static void ValidateMeasurement(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requested,
        int? returned
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        CollectionPagingTelemetry.RequireNonNegativeMilliseconds(duration);
        CollectionPagingTelemetry.RequireNonNegativeCount(requested, nameof(requested));
        CollectionPagingTelemetry.RequireNonNegativeCount(returned, nameof(returned));
    }
}

internal sealed class CollectionPagingTelemetry : ICollectionPagingTelemetry
{
    internal const string MeterName = "EdFi.DataManagementService.CollectionPaging";
    internal const string RequestCounterName = "edfi.dms.collection_paging.requests";
    internal const string DurationName = "edfi.dms.collection_paging.duration";
    internal const string RequestedPageSizeName = "edfi.dms.collection_paging.page_size.requested";
    internal const string ReturnedPageSizeName = "edfi.dms.collection_paging.page_size.returned";
    internal const string RequestedPartitionCountName =
        "edfi.dms.collection_paging.partition_count.requested";
    internal const string ReturnedPartitionCountName = "edfi.dms.collection_paging.partition_count.returned";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly Counter<long> _requestCounter;
    private readonly Histogram<double> _duration;
    private readonly Histogram<int> _requestedPageSize;
    private readonly Histogram<int> _returnedPageSize;
    private readonly Histogram<int> _requestedPartitionCount;
    private readonly Histogram<int> _returnedPartitionCount;
    private readonly ILogger<CollectionPagingTelemetry> _logger;

    public CollectionPagingTelemetry(ILogger<CollectionPagingTelemetry>? logger = null)
        : this(SharedMeter, logger) { }

    internal CollectionPagingTelemetry(Meter meter, ILogger<CollectionPagingTelemetry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(meter);

        _logger = logger ?? NullLogger<CollectionPagingTelemetry>.Instance;
        _requestCounter = meter.CreateCounter<long>(
            RequestCounterName,
            unit: "{request}",
            description: "Collection read requests by paging mode, command category, provider, and outcome."
        );
        _duration = meter.CreateHistogram<double>(
            DurationName,
            unit: "ms",
            description: "Collection read duration where backend execution was attempted."
        );
        _requestedPageSize = meter.CreateHistogram<int>(
            RequestedPageSizeName,
            unit: "{item}",
            description: "Page size a collection read requested."
        );
        _returnedPageSize = meter.CreateHistogram<int>(
            ReturnedPageSizeName,
            unit: "{item}",
            description: "Page size a collection read returned."
        );
        _requestedPartitionCount = meter.CreateHistogram<int>(
            RequestedPartitionCountName,
            unit: "{partition}",
            description: "Partition count a partition request asked for."
        );
        _returnedPartitionCount = meter.CreateHistogram<int>(
            ReturnedPartitionCountName,
            unit: "{partition}",
            description: "Partition count a partition request returned."
        );
    }

    public void RecordPage(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPageSize,
        int? returnedPageSize
    )
    {
        // Every argument is validated before the first instrument is touched: a fault discovered halfway
        // through would leave the request counter incremented for a measurement set that was never
        // completed, which is worse than the bad call it reports.
        ArgumentNullException.ThrowIfNull(context);

        double milliseconds = RequireNonNegativeMilliseconds(duration);
        RequireNonNegativeCount(requestedPageSize, nameof(requestedPageSize));
        RequireNonNegativeCount(returnedPageSize, nameof(returnedPageSize));

        TagList tags = context.ToTags();

        _requestCounter.Add(1, tags);
        _duration.Record(milliseconds, tags);
        _requestedPageSize.Record(requestedPageSize, tags);

        if (returnedPageSize is { } returned)
        {
            _returnedPageSize.Record(returned, tags);
        }

        LogDebug("page", context);
    }

    public void RecordPartitions(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPartitionCount,
        int? returnedPartitionCount
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        double milliseconds = RequireNonNegativeMilliseconds(duration);
        RequireNonNegativeCount(requestedPartitionCount, nameof(requestedPartitionCount));
        RequireNonNegativeCount(returnedPartitionCount, nameof(returnedPartitionCount));

        TagList tags = context.ToTags();

        _requestCounter.Add(1, tags);
        _duration.Record(milliseconds, tags);
        _requestedPartitionCount.Record(requestedPartitionCount, tags);

        if (returnedPartitionCount is { } returned)
        {
            _returnedPartitionCount.Record(returned, tags);
        }

        LogDebug("partitions", context);
    }

    public void RecordValidationRejected(CollectionPagingTelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _requestCounter.Add(1, context.ToTags());
        LogDebug("validation-rejected", context);
    }

    private void LogDebug(string metric, CollectionPagingTelemetryContext context)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "Collection paging telemetry recorded {Metric}. PagingMode {PagingMode}; commandCategory {CommandCategory}; provider {Provider}; outcome {Outcome}.",
            metric,
            context.PagingMode,
            context.CommandCategory,
            context.Provider,
            context.Outcome
        );
    }

    internal static double RequireNonNegativeMilliseconds(TimeSpan duration)
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

    internal static void RequireNonNegativeCount(int count, string parameterName)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, count, "Count must be nonnegative.");
        }
    }

    /// <summary>
    /// Validates an optional count. Null means the measurement is not recorded, which is a legitimate
    /// state rather than a missing argument.
    /// </summary>
    internal static void RequireNonNegativeCount(int? count, string parameterName)
    {
        if (count is { } value)
        {
            RequireNonNegativeCount(value, parameterName);
        }
    }
}
