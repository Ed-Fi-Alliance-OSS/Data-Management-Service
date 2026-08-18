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
public class DocumentCacheStatusContractTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Status_Response : DocumentCacheStatusContractTests
    {
        [Test]
        public void It_should_serialize_empty_targets_contract()
        {
            DocumentCacheStatusResponse response = new(ObservedAtWithOffset(milliseconds: 789), []);

            string json = JsonSerializer.Serialize(response, _jsonOptions);

            AssertJsonEquivalent(
                json,
                """
                {
                  "contractVersion": 1,
                  "observedAt": "2026-08-16T12:34:56.789Z",
                  "targets": []
                }
                """
            );
        }

        [Test]
        public void It_should_serialize_the_representative_populated_target_contract()
        {
            DocumentCacheStatusResponse response = new(
                ObservedAtWithOffset(milliseconds: 789),
                [PopulatedTarget()]
            );

            string json = JsonSerializer.Serialize(response, _jsonOptions);

            AssertJsonEquivalent(
                json,
                """
                {
                  "contractVersion": 1,
                  "observedAt": "2026-08-16T12:34:56.789Z",
                  "targets": [
                    {
                      "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                      "targetGeneration": 3,
                      "processObservedAt": "2026-08-16T12:34:56.789Z",
                      "durableObservedAt": "2026-08-16T12:34:56.900Z",
                      "provider": "postgresql",
                      "physicalSourceFingerprint": "opaque-fingerprint",
                      "resolution": {
                        "status": "resolved",
                        "reason": "none",
                        "observedAt": "2026-08-16T12:34:55Z",
                        "message": null
                      },
                      "eligibility": {
                        "status": "eligible",
                        "reason": "none",
                        "message": null
                      },
                      "inventory": {
                        "observedAt": "2026-08-16T12:34:55Z",
                        "state": { "status": "valid", "reason": "none", "message": null },
                        "work": { "status": "valid", "reason": "none", "message": null },
                        "cache": { "status": "valid", "reason": "none", "message": null },
                        "dataStoreIdentity": { "status": "valid", "reason": "none", "message": null },
                        "enqueueTrigger": { "status": "enabled", "reason": "none", "message": null }
                      },
                      "providerPrerequisites": {
                        "status": "satisfied",
                        "reason": "none",
                        "observedAt": "2026-08-16T12:34:55Z",
                        "sqlServerReadCommittedSnapshot": {
                          "status": "notApplicable",
                          "reason": "none",
                          "message": null
                        },
                        "sqlServerNestedTriggers": {
                          "status": "notApplicable",
                          "reason": "none",
                          "message": null
                        }
                      },
                      "lifecycle": {
                        "state": "tracking",
                        "availability": "available",
                        "message": null
                      },
                      "cacheAhead": {
                        "state": "clear",
                        "recoveryRequired": false,
                        "message": null
                      },
                      "operationalHealth": {
                        "status": "operational",
                        "reason": "none",
                        "message": null
                      },
                      "caughtUp": {
                        "status": "caughtUp",
                        "reason": "none",
                        "message": null
                      },
                      "queueSummary": {
                        "presence": "empty",
                        "oldestWorkFirstEnqueuedAt": null,
                        "oldestWorkAgeSeconds": null,
                        "backlogEstimate": { "kind": "unavailable", "value": null }
                      },
                      "executionState": {
                        "status": "waitingForPoll",
                        "observedAt": "2026-08-16T12:34:55Z",
                        "activeWorkers": 0,
                        "concurrencySlotsUsed": 0,
                        "targetBackoffUntil": null,
                        "lastSuccessfulWorkAt": "2026-08-16T12:34:40Z",
                        "lastFailureAt": null,
                        "message": null
                      },
                      "activeCommand": null,
                      "lastEndedDiagnostic": null,
                      "targetDiagnostics": { "recentEvents": [], "evictedCount": 0 },
                      "documentDiagnostics": { "recentEvents": [], "evictedCount": 0 },
                      "poisonTraversalDiagnostics": { "recentEvents": [], "evictedCount": 0 },
                      "effectiveSettings": {
                        "projector": {
                          "pollIntervalSeconds": 5,
                          "pageSize": 100,
                          "maxConcurrentTargets": 4,
                          "failureBackoffSeconds": 30,
                          "baselineHighWaterMark": 10000
                        },
                        "readAcceleration": {
                          "enabled": true,
                          "directFillTimeoutSeconds": 2
                        },
                        "status": {
                          "statusObservationTimeoutSeconds": 5,
                          "endpointTimeoutSeconds": 30
                        }
                      },
                      "enqueueFailures": {
                        "recentEvents": [],
                        "byCategory": [],
                        "evictedCount": 0
                      }
                    }
                  ]
                }
                """
            );
        }

        [Test]
        public void It_should_sort_targets_by_normalized_tenant_key_and_data_store_id()
        {
            DocumentCacheStatusResponse response = new(
                ObservedAtWithOffset(),
                [
                    PopulatedTarget(tenantKey: "z", dataStoreId: 3),
                    PopulatedTarget(tenantKey: "", dataStoreId: 2),
                    PopulatedTarget(tenantKey: "a", dataStoreId: 1),
                    PopulatedTarget(tenantKey: "", dataStoreId: 1),
                ]
            );

            JsonArray targets = JsonNode.Parse(JsonSerializer.Serialize(response))!["targets"]!.AsArray();

            targets
                .Select(target => target!["targetKey"]!["tenantKey"]!.GetValue<string>())
                .Should()
                .Equal("", "", "a", "z");
            targets
                .Select(target => target!["targetKey"]!["dataStoreId"]!.GetValue<long>())
                .Should()
                .Equal(1, 2, 1, 3);
        }

        [Test]
        public void It_should_serialize_public_enums_as_lower_camel_strings_and_reject_integer_values()
        {
            JsonSerializer
                .Serialize(DocumentCacheStatusExecutionState.WaitingForConcurrency)
                .Should()
                .Be("\"waitingForConcurrency\"");

            Action act = () => JsonSerializer.Deserialize<DocumentCacheStatusExecutionState>("1");

            act.Should().Throw<JsonException>();
        }

        [Test]
        public void It_should_advertise_only_reachable_execution_state_values()
        {
            Enum.GetValues<DocumentCacheStatusExecutionState>()
                .Select(value => JsonSerializer.Serialize(value))
                .Should()
                .Equal(
                    "\"notObserved\"",
                    "\"idle\"",
                    "\"waitingForPoll\"",
                    "\"waitingForConcurrency\"",
                    "\"active\"",
                    "\"targetBackoff\"",
                    "\"cancelling\"",
                    "\"cancelled\""
                );
        }

        [Test]
        public void It_should_serialize_effective_settings_without_required_role_or_administration_settings()
        {
            DocumentCacheTargetEffectiveSettings targetEffectiveSettings = new(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(2500),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 100,
                projectorMaxConcurrentTargets: 4,
                projectorFailureBackoff: TimeSpan.FromSeconds(30),
                projectorBaselineHighWaterMark: 10000,
                administrationWorkflowTimeout: TimeSpan.FromMinutes(10),
                statusObservationTimeout: TimeSpan.FromSeconds(5),
                statusEndpointTimeout: TimeSpan.FromSeconds(30)
            );

            string json = JsonSerializer.Serialize(
                DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(targetEffectiveSettings),
                _jsonOptions
            );

            json.Should().Contain("\"directFillTimeoutSeconds\": 2.5");
            json.Should().Contain("\"statusObservationTimeoutSeconds\": 5");
            json.Should().Contain("\"endpointTimeoutSeconds\": 30");
            json.Should().NotContain("requiredRole");
            json.Should().NotContain("administration");
            json.Should().NotContain("workflowTimeout");
        }

        [Test]
        public void It_should_serialize_current_generation_command_diagnostics_without_generation_fields()
        {
            DocumentCacheStatusTarget target = PopulatedTarget(
                activeCommand: new DocumentCacheStatusActiveCommand(
                    DocumentCacheAdministrativeCommand.OfflineActivation,
                    DocumentCacheAdministrativeCommandPhase.DrainWork,
                    DocumentCacheStatusActiveCommandStatus.Running,
                    WholeSecondWithOffset(hour: 7, minute: 34, second: 10),
                    WholeSecondWithOffset(hour: 7, minute: 34, second: 56),
                    "draining",
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
                    DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                    DocumentCacheAdministrativeCommandPhase.EnterTracking,
                    DocumentCacheStatusEndedCommandOutcome.TimedOut,
                    WholeSecondWithOffset(hour: 7, minute: 10, second: 0),
                    WholeSecondWithOffset(hour: 7, minute: 20, second: 0),
                    WholeSecondWithOffset(hour: 7, minute: 20, second: 1),
                    "timed out"
                )
            );

            string json = JsonSerializer.Serialize(target, _jsonOptions);
            JsonObject root = JsonNode.Parse(json)!.AsObject();

            root["activeCommand"]!["command"]!.GetValue<string>().Should().Be("offlineActivation");
            root["activeCommand"]!["phase"]!.GetValue<string>().Should().Be("drainWork");
            root["activeCommand"]!["status"]!.GetValue<string>().Should().Be("running");
            root["activeCommand"]!["phaseDiagnostics"]![0]!["diagnosticCategory"]!
                .GetValue<string>()
                .Should()
                .Be("providerCommandTimeout");
            root["lastEndedDiagnostic"]!["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
            root["lastEndedDiagnostic"]!["outcome"]!.GetValue<string>().Should().Be("timedOut");
            root["activeCommand"]!.ToJsonString().Should().NotContain("currentTargetGeneration");
            root["activeCommand"]!.ToJsonString().Should().NotContain("isCurrentGeneration");
            root["activeCommand"]!.ToJsonString().Should().NotContain("targetGeneration");
            root["lastEndedDiagnostic"]!.ToJsonString().Should().NotContain("currentTargetGeneration");
            root["lastEndedDiagnostic"]!.ToJsonString().Should().NotContain("isCurrentGeneration");
            root["lastEndedDiagnostic"]!.ToJsonString().Should().NotContain("targetGeneration");
        }

        private static DocumentCacheStatusTarget PopulatedTarget(
            string tenantKey = "",
            long dataStoreId = 1,
            DocumentCacheStatusActiveCommand? activeCommand = null,
            DocumentCacheStatusLastEndedDiagnostic? lastEndedDiagnostic = null
        ) =>
            new(
                new DocumentCacheStatusTargetKey(tenantKey, dataStoreId),
                targetGeneration: 3,
                processObservedAt: ObservedAtWithOffset(milliseconds: 789),
                durableObservedAt: ObservedAtWithOffset(milliseconds: 900),
                provider: "postgresql",
                physicalSourceFingerprint: "opaque-fingerprint",
                new DocumentCacheStatusResolutionComponent(
                    DocumentCacheStatusResolutionStatus.Resolved,
                    DocumentCacheStatusResolutionReason.None,
                    WholeSecondWithOffset(hour: 7, minute: 34, second: 55),
                    message: null
                ),
                new DocumentCacheStatusEligibilityComponent(
                    DocumentCacheStatusEligibilityStatus.Eligible,
                    DocumentCacheStatusReason.None,
                    message: null
                ),
                new DocumentCacheStatusInventoryComponentGroup(
                    WholeSecondWithOffset(hour: 7, minute: 34, second: 55),
                    ValidInventoryComponent(),
                    ValidInventoryComponent(),
                    ValidInventoryComponent(),
                    ValidInventoryComponent(),
                    new DocumentCacheStatusEnqueueTriggerComponent(
                        DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                        DocumentCacheStatusInventoryReason.None,
                        message: null
                    )
                ),
                new DocumentCacheStatusProviderPrerequisitesComponent(
                    DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                    DocumentCacheStatusProviderPrerequisiteReason.None,
                    WholeSecondWithOffset(hour: 7, minute: 34, second: 55),
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
                    WholeSecondWithOffset(hour: 7, minute: 34, second: 55),
                    activeWorkers: 0,
                    concurrencySlotsUsed: 0,
                    targetBackoffUntil: null,
                    lastSuccessfulWorkAt: WholeSecondWithOffset(hour: 7, minute: 34, second: 40),
                    lastFailureAt: null,
                    message: null
                ),
                activeCommand,
                lastEndedDiagnostic,
                EmptyWindow<DocumentCacheStatusTargetDiagnosticEvent>(),
                EmptyWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
                EmptyWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
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
                new DocumentCacheStatusEnqueueFailures()
            );

        private static DocumentCacheStatusInventoryComponent ValidInventoryComponent() =>
            new(
                DocumentCacheStatusInventoryStatus.Valid,
                DocumentCacheStatusInventoryReason.None,
                message: null
            );

        private static DocumentCacheStatusProviderPrerequisiteComponent NotApplicableProviderPrerequisite() =>
            new(
                DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                DocumentCacheStatusProviderPrerequisiteReason.None,
                message: null
            );

        private static DocumentCacheStatusDiagnosticWindow<TEvent> EmptyWindow<TEvent>() => new();

        private static DateTimeOffset ObservedAtWithOffset(int milliseconds = 0) =>
            new(2026, 8, 16, 7, 34, 56, milliseconds, TimeSpan.FromHours(-5));

        private static DateTimeOffset WholeSecondWithOffset(int hour, int minute, int second) =>
            new(2026, 8, 16, hour, minute, second, TimeSpan.FromHours(-5));

        private static void AssertJsonEquivalent(string actual, string expected)
        {
            JsonNode? actualNode = JsonNode.Parse(actual);
            JsonNode? expectedNode = JsonNode.Parse(expected);

            if (!JsonNode.DeepEquals(actualNode, expectedNode))
            {
                Assert.Fail(
                    $"JSON did not match expected fixture.\nActual:\n{actual}\nExpected:\n{expected}"
                );
            }
        }
    }
}
