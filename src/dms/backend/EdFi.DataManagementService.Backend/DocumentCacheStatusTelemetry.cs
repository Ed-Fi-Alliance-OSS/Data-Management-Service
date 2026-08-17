// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheStatusProviderObservationTelemetryOutcome
{
    Succeeded = 1,
    TimedOut = 2,
    Failed = 3,
}

internal interface IDocumentCacheStatusTelemetry
{
    void RecordStatusObservation(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheStatusTarget statusTarget
    );

    void RecordProviderObservation(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        DocumentCacheStatusProviderObservationTelemetryOutcome outcome,
        DocumentCacheStatusReason reason,
        TimeSpan duration,
        DocumentCacheLifecycleState? lifecycleState,
        double? oldestWorkAgeSeconds
    );
}

internal sealed class NoOpDocumentCacheStatusTelemetry : IDocumentCacheStatusTelemetry
{
    public static NoOpDocumentCacheStatusTelemetry Instance { get; } = new();

    private NoOpDocumentCacheStatusTelemetry() { }

    public void RecordStatusObservation(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheStatusTarget statusTarget
    )
    {
        ArgumentNullException.ThrowIfNull(targetObservation);
        ArgumentNullException.ThrowIfNull(statusTarget);
    }

    public void RecordProviderObservation(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        DocumentCacheStatusProviderObservationTelemetryOutcome outcome,
        DocumentCacheStatusReason reason,
        TimeSpan duration,
        DocumentCacheLifecycleState? lifecycleState,
        double? oldestWorkAgeSeconds
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(providerToken);
        DocumentCacheMaterializerGuards.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported status provider-observation telemetry outcome."
        );
        DocumentCacheMaterializerGuards.RequireDefined(
            reason,
            nameof(reason),
            "Unsupported DocumentCache status reason."
        );
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Provider-observation duration must be nonnegative."
            );
        }

        if (lifecycleState is not null)
        {
            DocumentCacheMaterializerGuards.RequireDefined(
                lifecycleState.Value,
                nameof(lifecycleState),
                "Unsupported DocumentCache lifecycle state."
            );
        }

        if (oldestWorkAgeSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(oldestWorkAgeSeconds),
                oldestWorkAgeSeconds,
                "Oldest work age must be nonnegative."
            );
        }
    }
}

internal sealed class DocumentCacheStatusTelemetry : IDocumentCacheStatusTelemetry
{
    internal const string MeterName = "EdFi.DataManagementService.DocumentCacheProjection";
    internal const string StatusObservationCounterName = "edfi.dms.document_cache.status.observations";
    internal const string ProviderObservationDurationName =
        "edfi.dms.document_cache.status.provider_observation.duration";
    internal const string OldestWorkAgeName = "edfi.dms.document_cache.status.oldest_work.age";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly Counter<long> _statusObservationCounter;
    private readonly Histogram<double> _providerObservationDuration;
    private readonly Histogram<double> _oldestWorkAge;
    private readonly ILogger<DocumentCacheStatusTelemetry> _logger;

    public DocumentCacheStatusTelemetry(ILogger<DocumentCacheStatusTelemetry>? logger = null)
        : this(SharedMeter, logger) { }

    internal DocumentCacheStatusTelemetry(Meter meter, ILogger<DocumentCacheStatusTelemetry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(meter);

        _logger = logger ?? NullLogger<DocumentCacheStatusTelemetry>.Instance;
        _statusObservationCounter = meter.CreateCounter<long>(
            StatusObservationCounterName,
            unit: "{observation}",
            description: "DocumentCache status target observations."
        );
        _providerObservationDuration = meter.CreateHistogram<double>(
            ProviderObservationDurationName,
            unit: "s",
            description: "DocumentCache status provider observation duration."
        );
        _oldestWorkAge = meter.CreateHistogram<double>(
            OldestWorkAgeName,
            unit: "s",
            description: "DocumentCache status oldest durable projection work age."
        );
    }

    public void RecordStatusObservation(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheStatusTarget statusTarget
    )
    {
        ArgumentNullException.ThrowIfNull(targetObservation);
        ArgumentNullException.ThrowIfNull(statusTarget);

        TagList tags =
        [
            new("provider", ProviderLabel(statusTarget.Provider ?? targetObservation.ProviderToken?.Value)),
            new("target", DocumentCacheTelemetryTargetLabel.FromTargetKey(targetObservation.TargetKey)),
            new("lifecycle", DocumentCacheTelemetryLabel.LowerCamel(statusTarget.Lifecycle.State)),
            new("queue_presence", DocumentCacheTelemetryLabel.LowerCamel(statusTarget.QueueSummary.Presence)),
            new(
                "operational_health_status",
                DocumentCacheTelemetryLabel.LowerCamel(statusTarget.OperationalHealth.Status)
            ),
            new(
                "operational_health_reason",
                DocumentCacheTelemetryLabel.LowerCamel(statusTarget.OperationalHealth.Reason)
            ),
            new("caught_up_status", DocumentCacheTelemetryLabel.LowerCamel(statusTarget.CaughtUp.Status)),
            new("caught_up_reason", DocumentCacheTelemetryLabel.LowerCamel(statusTarget.CaughtUp.Reason)),
        ];

        _statusObservationCounter.Add(1, tags);

        _logger.LogInformation(
            "DocumentCacheStatusObserved provider {Provider} target {Target} operationalHealthStatus {OperationalHealthStatus} operationalHealthReason {OperationalHealthReason} caughtUpStatus {CaughtUpStatus} caughtUpReason {CaughtUpReason} lifecycle {Lifecycle} queuePresence {QueuePresence}",
            ProviderLabel(statusTarget.Provider ?? targetObservation.ProviderToken?.Value),
            DocumentCacheTelemetryTargetLabel.FromTargetKey(targetObservation.TargetKey),
            DocumentCacheTelemetryLabel.LowerCamel(statusTarget.OperationalHealth.Status),
            DocumentCacheTelemetryLabel.LowerCamel(statusTarget.OperationalHealth.Reason),
            DocumentCacheTelemetryLabel.LowerCamel(statusTarget.CaughtUp.Status),
            DocumentCacheTelemetryLabel.LowerCamel(statusTarget.CaughtUp.Reason),
            DocumentCacheTelemetryLabel.LowerCamel(statusTarget.Lifecycle.State),
            DocumentCacheTelemetryLabel.LowerCamel(statusTarget.QueueSummary.Presence)
        );
    }

    public void RecordProviderObservation(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        DocumentCacheStatusProviderObservationTelemetryOutcome outcome,
        DocumentCacheStatusReason reason,
        TimeSpan duration,
        DocumentCacheLifecycleState? lifecycleState,
        double? oldestWorkAgeSeconds
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(providerToken);
        DocumentCacheMaterializerGuards.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported status provider-observation telemetry outcome."
        );
        DocumentCacheMaterializerGuards.RequireDefined(
            reason,
            nameof(reason),
            "Unsupported DocumentCache status reason."
        );

        TagList durationTags =
        [
            new("provider", providerToken.Value),
            new("target", DocumentCacheTelemetryTargetLabel.FromTargetKey(targetKey)),
            new("outcome", DocumentCacheTelemetryLabel.LowerCamel(outcome)),
            new("reason", DocumentCacheTelemetryLabel.LowerCamel(reason)),
        ];

        double durationSeconds = ClampToNonNegativeSeconds(duration);
        _providerObservationDuration.Record(durationSeconds, durationTags);

        if (oldestWorkAgeSeconds is not null && lifecycleState is not null)
        {
            if (oldestWorkAgeSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(oldestWorkAgeSeconds),
                    oldestWorkAgeSeconds,
                    "Oldest work age must be nonnegative."
                );
            }

            TagList oldestWorkTags =
            [
                new("provider", providerToken.Value),
                new("target", DocumentCacheTelemetryTargetLabel.FromTargetKey(targetKey)),
                new("lifecycle", DocumentCacheTelemetryLabel.LowerCamel(lifecycleState.Value)),
            ];
            _oldestWorkAge.Record(oldestWorkAgeSeconds.Value, oldestWorkTags);
        }

        if (outcome != DocumentCacheStatusProviderObservationTelemetryOutcome.Succeeded)
        {
            _logger.LogWarning(
                "DocumentCacheStatusProviderObservationFailed provider {Provider} target {Target} outcome {Outcome} reason {Reason} durationSeconds {DurationSeconds}",
                providerToken.Value,
                DocumentCacheTelemetryTargetLabel.FromTargetKey(targetKey),
                DocumentCacheTelemetryLabel.LowerCamel(outcome),
                DocumentCacheTelemetryLabel.LowerCamel(reason),
                durationSeconds
            );
        }
    }

    private static string ProviderLabel(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? DocumentCacheTelemetryLabel.Unknown : provider;

    private static double ClampToNonNegativeSeconds(TimeSpan duration) =>
        duration < TimeSpan.Zero ? 0 : duration.TotalSeconds;
}
