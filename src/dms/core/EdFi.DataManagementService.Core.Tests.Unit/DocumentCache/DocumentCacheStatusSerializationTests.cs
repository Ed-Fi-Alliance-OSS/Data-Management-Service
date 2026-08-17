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

    private static DocumentCacheStatusTarget Target(
        DocumentCacheStatusActiveCommand? activeCommand = null,
        DocumentCacheStatusLastEndedDiagnostic? lastEndedDiagnostic = null,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>? targetDiagnostics =
            null,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>? documentDiagnostics =
            null,
        DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>? poisonTraversalDiagnostics =
            null,
        DocumentCacheStatusEnqueueFailures? enqueueFailures = null
    ) =>
        new(
            new DocumentCacheStatusTargetKey("", 1),
            targetGeneration: 3,
            ObservedAt,
            ObservedAt.AddMilliseconds(50),
            provider: "postgresql",
            physicalSourceFingerprint: "opaque-fingerprint",
            new DocumentCacheStatusResolutionComponent(
                DocumentCacheStatusResolutionStatus.Resolved,
                DocumentCacheStatusResolutionReason.None,
                ObservedAt,
                message: null
            ),
            new DocumentCacheStatusEligibilityComponent(
                DocumentCacheStatusEligibilityStatus.Eligible,
                DocumentCacheStatusReason.None,
                message: null
            ),
            new DocumentCacheStatusInventoryComponentGroup(
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
            new DocumentCacheStatusProviderPrerequisitesComponent(
                DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                DocumentCacheStatusProviderPrerequisiteReason.None,
                ObservedAt,
                NotApplicableProviderPrerequisite(),
                NotApplicableProviderPrerequisite()
            ),
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Tracking,
                DocumentCacheStatusAvailability.Available,
                message: null
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Clear,
                recoveryRequired: false,
                message: null
            ),
            new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.Operational,
                DocumentCacheStatusReason.None,
                message: null
            ),
            new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.CaughtUp,
                DocumentCacheStatusReason.None,
                message: null
            ),
            new DocumentCacheStatusQueueSummary(
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DocumentCacheStatusBacklogEstimate.Unavailable
            ),
            new DocumentCacheStatusExecutionStateComponent(
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

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, null);

    private static DocumentCacheStatusProviderPrerequisiteComponent NotApplicableProviderPrerequisite() =>
        new(
            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            null
        );
}
