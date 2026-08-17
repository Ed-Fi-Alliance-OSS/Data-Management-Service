// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheStatus")]
public class Given_DocumentCacheStatusSerialization
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    [Test]
    public void It_serializes_diagnostics_with_public_lower_camel_values_and_bounded_safe_messages()
    {
        string noisyMessage = "{provider:\"raw\"}<unsafe>\r\n" + new string('x', 540);
        DocumentCacheStatusTarget target = Target(
            targetDiagnostics: new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(
                [
                    new DocumentCacheStatusTargetDiagnosticEvent(
                        ObservedAt,
                        DocumentCacheStatusTargetDiagnosticCategory.TargetInvariant,
                        noisyMessage
                    ),
                ],
                evictedCount: 2
            ),
            documentDiagnostics: new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(
                [
                    new DocumentCacheStatusDocumentDiagnosticEvent(
                        101,
                        ObservedAt,
                        DocumentCacheStatusDocumentDiagnosticCategory.WriterFailed,
                        ObservedAt.AddSeconds(30),
                        noisyMessage
                    ),
                ],
                evictedCount: 3
            ),
            poisonTraversalDiagnostics: new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(
                [
                    new DocumentCacheStatusPoisonTraversalDiagnosticEvent(
                        202,
                        ObservedAt,
                        DocumentCacheStatusPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
                        ObservedAt.AddSeconds(60),
                        noisyMessage
                    ),
                ],
                evictedCount: 4
            ),
            enqueueFailures: new DocumentCacheStatusEnqueueFailures(
                [
                    new DocumentCacheStatusEnqueueFailureEvent(
                        ObservedAt,
                        DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
                        DocumentCacheStatusCanonicalOperation.Update,
                        DocumentCacheStatusResourceKind.Descriptor,
                        noisyMessage
                    ),
                ],
                [
                    new DocumentCacheStatusEnqueueFailureCategoryCount(
                        DocumentCacheStatusEnqueueFailureCategory.ProviderUnavailable,
                        1
                    ),
                    new DocumentCacheStatusEnqueueFailureCategoryCount(
                        DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
                        2
                    ),
                ],
                evictedCount: 5
            )
        );

        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(target))!.AsObject();
        string json = root.ToJsonString();

        root["targetDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("targetInvariant");
        root["documentDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("writerFailed");
        root["poisonTraversalDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("skippedUntilRetry");
        root["enqueueFailures"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("workPersistenceFailed");
        root["enqueueFailures"]!["recentEvents"]![0]!["canonicalOperation"]!
            .GetValue<string>()
            .Should()
            .Be("update");
        root["enqueueFailures"]!["recentEvents"]![0]!["resourceKind"]!
            .GetValue<string>()
            .Should()
            .Be("descriptor");
        root["enqueueFailures"]!["byCategory"]!
            .AsArray()
            .Select(category => category!["category"]!.GetValue<string>())
            .Should()
            .Equal("workPersistenceFailed", "providerUnavailable");

        root["documentDiagnostics"]!["recentEvents"]![0]!.AsObject().Should().ContainKey("documentId");
        json.Should().NotContain("documentUuid");
        json.Should().NotContain("requestBody");
        json.Should().NotContain("subject");
        json.Should().NotContain("connectionString");
        json.Should().NotContain("DocumentCacheProjectionDocumentDiagnosticCategory");

        string sanitizedMessage = root["documentDiagnostics"]!["recentEvents"]![0]![
            "message"
        ]!.GetValue<string>();
        sanitizedMessage.Should().HaveLength(512);
        sanitizedMessage.Should().NotContain("\r");
        sanitizedMessage.Should().NotContain("\n");
        sanitizedMessage.Should().NotContain("{");
        sanitizedMessage.Should().NotContain("}");
        sanitizedMessage.Should().NotContain("<");
        sanitizedMessage.Should().NotContain(">");
    }

    [Test]
    public void It_serializes_shared_command_shapes_without_status_only_aliases_or_nested_eviction_counts()
    {
        DocumentCacheStatusTarget target = Target(
            activeCommand: new DocumentCacheStatusActiveCommand(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                DocumentCacheAdministrativeCommandPhase.DrainWork,
                DocumentCacheStatusActiveCommandStatus.Cancelling,
                ObservedAt.AddMinutes(-5),
                ObservedAt,
                "cancelling",
                [
                    new DocumentCacheAdministrativePhaseDiagnostic(
                        DocumentCacheAdministrativeCommandPhase.DrainWork,
                        DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                        retryable: true,
                        DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
                        affectedDocumentIds: [99],
                        "provider timeout"
                    ),
                ]
            ),
            lastEndedDiagnostic: new DocumentCacheStatusLastEndedDiagnostic(
                DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
                DocumentCacheAdministrativeCommandPhase.SetCacheAheadLatch,
                DocumentCacheStatusEndedCommandOutcome.TimedOut,
                ObservedAt.AddMinutes(-10),
                ObservedAt.AddMinutes(-1),
                ObservedAt,
                "timed out"
            )
        );

        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(target))!.AsObject();
        string json = root.ToJsonString();

        root["activeCommand"]!["command"]!.GetValue<string>().Should().Be("guardedNewEmptyActivation");
        root["activeCommand"]!["phase"]!.GetValue<string>().Should().Be("drainWork");
        root["activeCommand"]!["status"]!.GetValue<string>().Should().Be("cancelling");
        root["activeCommand"]!["phaseDiagnostics"]![0]!["diagnosticCategory"]!
            .GetValue<string>()
            .Should()
            .Be("providerCommandTimeout");
        root["lastEndedDiagnostic"]!["command"]!
            .GetValue<string>()
            .Should()
            .Be("internalOnlyCacheAheadRecovery");
        root["lastEndedDiagnostic"]!["phase"]!.GetValue<string>().Should().Be("setCacheAheadLatch");
        root["lastEndedDiagnostic"]!["outcome"]!.GetValue<string>().Should().Be("timedOut");
        root["activeCommand"]!["phaseDiagnostics"]![0]!.AsObject().Should().NotContainKey("evictedCount");

        json.Should().NotContain("activateNewEmpty");
        json.Should().NotContain("offlineActivate");
        json.Should().NotContain("onlineRebuild");
        json.Should().NotContain("cacheAheadRecovery");
        json.Should().NotContain("integrityScrub");
        json.Should().NotContain("currentTargetGeneration");
        json.Should().NotContain("isCurrentGeneration");
    }

    [Test]
    public void It_serializes_process_ineligible_unavailable_and_durable_unknown_shapes()
    {
        JsonObject unavailable = SerializeTarget(
            Target(
                targetGeneration: null,
                includeDurableObservedAt: false,
                provider: null,
                physicalSourceFingerprint: null,
                resolution: new DocumentCacheStatusResolutionComponent(
                    DocumentCacheStatusResolutionStatus.Unresolved,
                    DocumentCacheStatusResolutionReason.TargetNotFound,
                    ObservedAt,
                    "Configured target was not found."
                ),
                eligibility: new DocumentCacheStatusEligibilityComponent(
                    DocumentCacheStatusEligibilityStatus.Ineligible,
                    DocumentCacheStatusReason.InventoryInvalid,
                    "Inventory invalid."
                ),
                lifecycle: new DocumentCacheStatusLifecycleComponent(
                    DocumentCacheStatusLifecycleState.Unknown,
                    DocumentCacheStatusAvailability.Unavailable,
                    message: null
                ),
                cacheAhead: new DocumentCacheStatusCacheAheadComponent(
                    DocumentCacheStatusCacheAheadState.Unknown,
                    recoveryRequired: null,
                    message: null
                ),
                operationalHealth: new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.NonOperational,
                    DocumentCacheStatusReason.InventoryInvalid,
                    "Inventory invalid."
                ),
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.NotCaughtUp,
                    DocumentCacheStatusReason.InventoryInvalid,
                    "Inventory invalid."
                ),
                queueSummary: UnavailableQueueSummary(),
                executionState: NotObservedExecutionState()
            )
        );

        unavailable["targetGeneration"].Should().BeNull();
        unavailable["durableObservedAt"].Should().BeNull();
        unavailable["provider"].Should().BeNull();
        unavailable["physicalSourceFingerprint"].Should().BeNull();
        unavailable["lifecycle"]!["availability"]!.GetValue<string>().Should().Be("unavailable");
        unavailable["cacheAhead"]!["recoveryRequired"].Should().BeNull();
        unavailable["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("unavailable");
        unavailable["queueSummary"]!["oldestWorkFirstEnqueuedAt"].Should().BeNull();
        unavailable["operationalHealth"]!["status"]!.GetValue<string>().Should().Be("nonOperational");
        unavailable["caughtUp"]!["status"]!.GetValue<string>().Should().Be("notCaughtUp");
        unavailable["executionState"]!["status"]!.GetValue<string>().Should().Be("notObserved");
        unavailable["executionState"]!["observedAt"].Should().BeNull();

        JsonObject unknown = SerializeTarget(
            Target(
                includeDurableObservedAt: false,
                lifecycle: new DocumentCacheStatusLifecycleComponent(
                    DocumentCacheStatusLifecycleState.Unknown,
                    DocumentCacheStatusAvailability.Unknown,
                    "Provider statement failed."
                ),
                cacheAhead: new DocumentCacheStatusCacheAheadComponent(
                    DocumentCacheStatusCacheAheadState.Unknown,
                    recoveryRequired: null,
                    "Provider statement failed."
                ),
                operationalHealth: new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.Unknown,
                    DocumentCacheStatusReason.ProviderObservationFailed,
                    "Provider statement failed."
                ),
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.Unknown,
                    DocumentCacheStatusReason.ProviderObservationFailed,
                    "Provider statement failed."
                ),
                queueSummary: UnknownQueueSummary()
            )
        );

        unknown["durableObservedAt"].Should().BeNull();
        unknown["lifecycle"]!["availability"]!.GetValue<string>().Should().Be("unknown");
        unknown["lifecycle"]!["message"]!.GetValue<string>().Should().Be("Provider statement failed.");
        unknown["cacheAhead"]!["state"]!.GetValue<string>().Should().Be("unknown");
        unknown["cacheAhead"]!["recoveryRequired"].Should().BeNull();
        unknown["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("unknown");
        unknown["operationalHealth"]!["reason"]!.GetValue<string>().Should().Be("providerObservationFailed");
        unknown["caughtUp"]!["reason"]!.GetValue<string>().Should().Be("providerObservationFailed");
    }

    [TestCase(
        DocumentCacheStatusLifecycleState.Disabled,
        "disabled",
        DocumentCacheStatusReason.LifecycleDisabled,
        "lifecycleDisabled"
    )]
    [TestCase(
        DocumentCacheStatusLifecycleState.Resetting,
        "resetting",
        DocumentCacheStatusReason.LifecycleResetting,
        "lifecycleResetting"
    )]
    [TestCase(
        DocumentCacheStatusLifecycleState.Rebuilding,
        "rebuilding",
        DocumentCacheStatusReason.LifecycleRebuilding,
        "lifecycleRebuilding"
    )]
    public void It_serializes_non_operational_lifecycle_shapes(
        DocumentCacheStatusLifecycleState lifecycleState,
        string expectedLifecycleState,
        DocumentCacheStatusReason reason,
        string expectedReason
    )
    {
        JsonObject root = SerializeTarget(
            Target(
                lifecycle: new DocumentCacheStatusLifecycleComponent(
                    lifecycleState,
                    DocumentCacheStatusAvailability.Available,
                    "Lifecycle is not tracking."
                ),
                operationalHealth: new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.NonOperational,
                    reason,
                    "Lifecycle is not tracking."
                ),
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.NotCaughtUp,
                    reason,
                    "Lifecycle is not tracking."
                )
            )
        );

        root["lifecycle"]!["state"]!.GetValue<string>().Should().Be(expectedLifecycleState);
        root["lifecycle"]!["availability"]!.GetValue<string>().Should().Be("available");
        root["operationalHealth"]!["status"]!.GetValue<string>().Should().Be("nonOperational");
        root["operationalHealth"]!["reason"]!.GetValue<string>().Should().Be(expectedReason);
        root["caughtUp"]!["status"]!.GetValue<string>().Should().Be("notCaughtUp");
        root["caughtUp"]!["reason"]!.GetValue<string>().Should().Be(expectedReason);
    }

    [Test]
    public void It_serializes_queue_cache_ahead_and_runtime_diagnostic_shapes()
    {
        JsonObject queueNotEmpty = SerializeTarget(
            Target(
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.NotCaughtUp,
                    DocumentCacheStatusReason.QueueNotEmpty,
                    message: null
                ),
                queueSummary: new DocumentCacheStatusQueueSummary(
                    DocumentCacheStatusQueuePresence.NotEmpty,
                    ObservedAt.AddMinutes(-5),
                    oldestWorkAgeSeconds: 300.5,
                    DocumentCacheStatusBacklogEstimate.Unavailable
                )
            )
        );

        queueNotEmpty["operationalHealth"]!["status"]!.GetValue<string>().Should().Be("operational");
        queueNotEmpty["caughtUp"]!["status"]!.GetValue<string>().Should().Be("notCaughtUp");
        queueNotEmpty["caughtUp"]!["reason"]!.GetValue<string>().Should().Be("queueNotEmpty");
        queueNotEmpty["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("notEmpty");
        queueNotEmpty["queueSummary"]!["oldestWorkFirstEnqueuedAt"]!
            .GetValue<string>()
            .Should()
            .Be("2026-08-17T13:05:11Z");
        queueNotEmpty["queueSummary"]!["oldestWorkAgeSeconds"]!.GetValue<double>().Should().Be(300.5);
        queueNotEmpty["queueSummary"]!["backlogEstimate"]!["kind"]!
            .GetValue<string>()
            .Should()
            .Be("unavailable");

        JsonObject cacheAhead = SerializeTarget(
            Target(
                cacheAhead: new DocumentCacheStatusCacheAheadComponent(
                    DocumentCacheStatusCacheAheadState.RecoveryRequired,
                    recoveryRequired: true,
                    "Cache-ahead recovery is required."
                ),
                operationalHealth: new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.NonOperational,
                    DocumentCacheStatusReason.CacheAheadRecoveryRequired,
                    "Cache-ahead recovery is required."
                ),
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.NotCaughtUp,
                    DocumentCacheStatusReason.CacheAheadRecoveryRequired,
                    "Cache-ahead recovery is required."
                )
            )
        );

        cacheAhead["cacheAhead"]!["state"]!.GetValue<string>().Should().Be("recoveryRequired");
        cacheAhead["cacheAhead"]!["recoveryRequired"]!.GetValue<bool>().Should().BeTrue();
        cacheAhead["operationalHealth"]!["reason"]!
            .GetValue<string>()
            .Should()
            .Be("cacheAheadRecoveryRequired");

        JsonObject runtimeNotObserved = SerializeTarget(
            Target(
                includeDurableObservedAt: false,
                lifecycle: new DocumentCacheStatusLifecycleComponent(
                    DocumentCacheStatusLifecycleState.Unknown,
                    DocumentCacheStatusAvailability.Unavailable,
                    message: null
                ),
                cacheAhead: new DocumentCacheStatusCacheAheadComponent(
                    DocumentCacheStatusCacheAheadState.Unknown,
                    recoveryRequired: null,
                    message: null
                ),
                operationalHealth: new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.Unknown,
                    DocumentCacheStatusReason.RuntimeNotObserved,
                    "Runtime was not observed."
                ),
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.Unknown,
                    DocumentCacheStatusReason.RuntimeNotObserved,
                    "Runtime was not observed."
                ),
                queueSummary: UnavailableQueueSummary(),
                executionState: NotObservedExecutionState()
            )
        );

        runtimeNotObserved["executionState"]!["status"]!.GetValue<string>().Should().Be("notObserved");
        runtimeNotObserved["executionState"]!["observedAt"].Should().BeNull();
        runtimeNotObserved["operationalHealth"]!["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
        runtimeNotObserved["queueSummary"]!["presence"]!.GetValue<string>().Should().Be("unavailable");

        JsonObject targetBackoff = SerializeTarget(
            Target(
                includeDurableObservedAt: false,
                lifecycle: new DocumentCacheStatusLifecycleComponent(
                    DocumentCacheStatusLifecycleState.Unknown,
                    DocumentCacheStatusAvailability.Unavailable,
                    message: null
                ),
                operationalHealth: new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.NonOperational,
                    DocumentCacheStatusReason.TargetBackoff,
                    "Target is in backoff."
                ),
                caughtUp: new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.NotCaughtUp,
                    DocumentCacheStatusReason.TargetBackoff,
                    "Target is in backoff."
                ),
                queueSummary: UnavailableQueueSummary(),
                executionState: new DocumentCacheStatusExecutionStateComponent(
                    DocumentCacheStatusExecutionState.TargetBackoff,
                    ObservedAt,
                    activeWorkers: 0,
                    concurrencySlotsUsed: 0,
                    targetBackoffUntil: ObservedAt.AddSeconds(30),
                    lastSuccessfulWorkAt: null,
                    lastFailureAt: ObservedAt.AddSeconds(-10),
                    "Target is in backoff."
                )
            )
        );

        targetBackoff["executionState"]!["status"]!.GetValue<string>().Should().Be("targetBackoff");
        targetBackoff["executionState"]!["targetBackoffUntil"]!
            .GetValue<string>()
            .Should()
            .Be("2026-08-17T13:10:41Z");
        targetBackoff["operationalHealth"]!["reason"]!.GetValue<string>().Should().Be("targetBackoff");
        targetBackoff["caughtUp"]!["reason"]!.GetValue<string>().Should().Be("targetBackoff");
    }

    private static DocumentCacheStatusTarget Target(
        DocumentCacheStatusActiveCommand? activeCommand = null,
        DocumentCacheStatusLastEndedDiagnostic? lastEndedDiagnostic = null,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>? targetDiagnostics =
            null,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>? documentDiagnostics =
            null,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>? poisonTraversalDiagnostics =
            null,
        DocumentCacheStatusEnqueueFailures? enqueueFailures = null,
        DocumentCacheStatusTargetKey? targetKey = null,
        long? targetGeneration = 3,
        bool includeDurableObservedAt = true,
        string? provider = "postgresql",
        string? physicalSourceFingerprint = "opaque-fingerprint",
        DocumentCacheStatusResolutionComponent? resolution = null,
        DocumentCacheStatusEligibilityComponent? eligibility = null,
        DocumentCacheStatusInventoryComponentGroup? inventory = null,
        DocumentCacheStatusProviderPrerequisitesComponent? providerPrerequisites = null,
        DocumentCacheStatusLifecycleComponent? lifecycle = null,
        DocumentCacheStatusCacheAheadComponent? cacheAhead = null,
        DocumentCacheOperationalHealthComponent? operationalHealth = null,
        DocumentCacheCaughtUpComponent? caughtUp = null,
        DocumentCacheStatusQueueSummary? queueSummary = null,
        DocumentCacheStatusExecutionStateComponent? executionState = null
    ) =>
        new(
            targetKey ?? new DocumentCacheStatusTargetKey("", 1),
            targetGeneration,
            ObservedAt,
            includeDurableObservedAt ? ObservedAt.AddMilliseconds(50) : null,
            provider,
            physicalSourceFingerprint,
            resolution
                ?? new DocumentCacheStatusResolutionComponent(
                    DocumentCacheStatusResolutionStatus.Resolved,
                    DocumentCacheStatusResolutionReason.None,
                    ObservedAt,
                    message: null
                ),
            eligibility
                ?? new DocumentCacheStatusEligibilityComponent(
                    DocumentCacheStatusEligibilityStatus.Eligible,
                    DocumentCacheStatusReason.None,
                    message: null
                ),
            inventory
                ?? new DocumentCacheStatusInventoryComponentGroup(
                    ObservedAt,
                    ValidInventory(),
                    ValidInventory(),
                    ValidInventory(),
                    ValidInventory(),
                    new DocumentCacheStatusEnqueueTriggerComponent(
                        DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                        DocumentCacheStatusInventoryReason.None,
                        message: null
                    )
                ),
            providerPrerequisites
                ?? new DocumentCacheStatusProviderPrerequisitesComponent(
                    DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                    DocumentCacheStatusProviderPrerequisiteReason.None,
                    ObservedAt,
                    NotApplicableProviderPrerequisite(),
                    NotApplicableProviderPrerequisite()
                ),
            lifecycle
                ?? new DocumentCacheStatusLifecycleComponent(
                    DocumentCacheStatusLifecycleState.Tracking,
                    DocumentCacheStatusAvailability.Available,
                    message: null
                ),
            cacheAhead
                ?? new DocumentCacheStatusCacheAheadComponent(
                    DocumentCacheStatusCacheAheadState.Clear,
                    recoveryRequired: false,
                    message: null
                ),
            operationalHealth
                ?? new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.Operational,
                    DocumentCacheStatusReason.None,
                    message: null
                ),
            caughtUp
                ?? new DocumentCacheCaughtUpComponent(
                    DocumentCacheCaughtUpStatus.CaughtUp,
                    DocumentCacheStatusReason.None,
                    message: null
                ),
            queueSummary
                ?? new DocumentCacheStatusQueueSummary(
                    DocumentCacheStatusQueuePresence.Empty,
                    oldestWorkFirstEnqueuedAt: null,
                    oldestWorkAgeSeconds: null,
                    DocumentCacheStatusBacklogEstimate.Unavailable
                ),
            executionState
                ?? new DocumentCacheStatusExecutionStateComponent(
                    DocumentCacheStatusExecutionState.WaitingForPoll,
                    ObservedAt,
                    activeWorkers: 0,
                    concurrencySlotsUsed: 0,
                    targetBackoffUntil: null,
                    lastSuccessfulWorkAt: ObservedAt.AddSeconds(-1),
                    lastFailureAt: null,
                    message: null
                ),
            activeCommand,
            lastEndedDiagnostic,
            targetDiagnostics ?? new(),
            documentDiagnostics ?? new(),
            poisonTraversalDiagnostics ?? new(),
            new DocumentCacheStatusEffectiveSettings(
                new DocumentCacheStatusProjectorEffectiveSettings(
                    pollIntervalSeconds: 5,
                    pageSize: 100,
                    maxConcurrentTargets: 4,
                    failureBackoffSeconds: 30,
                    baselineHighWaterMark: 10000
                ),
                new DocumentCacheStatusReadAccelerationEffectiveSettings(
                    enabled: true,
                    directFillTimeoutSeconds: 2
                ),
                new DocumentCacheStatusTimingEffectiveSettings(
                    statusObservationTimeoutSeconds: 5,
                    endpointTimeoutSeconds: 30
                )
            ),
            enqueueFailures ?? new()
        );

    private static JsonObject SerializeTarget(DocumentCacheStatusTarget target) =>
        JsonNode.Parse(JsonSerializer.Serialize(target))!.AsObject();

    private static DocumentCacheStatusQueueSummary UnavailableQueueSummary() =>
        new(
            DocumentCacheStatusQueuePresence.Unavailable,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            DocumentCacheStatusBacklogEstimate.Unavailable
        );

    private static DocumentCacheStatusQueueSummary UnknownQueueSummary() =>
        new(
            DocumentCacheStatusQueuePresence.Unknown,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            DocumentCacheStatusBacklogEstimate.Unavailable
        );

    private static DocumentCacheStatusExecutionStateComponent NotObservedExecutionState() =>
        new(
            DocumentCacheStatusExecutionState.NotObserved,
            observedAt: null,
            activeWorkers: null,
            concurrencySlotsUsed: null,
            targetBackoffUntil: null,
            lastSuccessfulWorkAt: null,
            lastFailureAt: null,
            message: null
        );

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, null);

    private static DocumentCacheStatusProviderPrerequisiteComponent NotApplicableProviderPrerequisite() =>
        new(
            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            null
        );
}
