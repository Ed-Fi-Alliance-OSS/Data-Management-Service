// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal static class CdcAttachmentReadiness
{
    // Projection must come from the production correlation mapper, which checks fresh durable
    // Tracking state, clear cache-ahead latch, provider and physical-source identity.
    public static void RequireUnstarted(
        CdcControllerStatusResult result,
        CdcProjectionCorrelationObservation projection
    )
    {
        if (result.Targets.Count != 1)
        {
            throw new InvalidOperationException("CDC_API_ATTACHMENT_PREREQUISITES");
        }
        var target = result.Targets[0];
        var status = target.Status;
        CdcComponent[] prerequisites =
        [
            status.Binding,
            status.ProviderSetup,
            status.KafkaPolicy,
            status.ConnectOffsetStore,
            status.ConnectorConfig,
            status.ConnectorRuntime,
            status.Lag,
        ];
        if (
            projection.TargetIdentity != status.TargetIdentity
            || projection.CorrelationState != CdcProjectionCorrelationState.Matched
            || projection.OperationalHealthStatus != DocumentCacheOperationalHealthStatus.Unknown
            || projection.OperationalHealthReason != DocumentCacheStatusReason.RuntimeNotObserved
            || projection.CaughtUpStatus != DocumentCacheCaughtUpStatus.Unknown
            || projection.CaughtUpReason != DocumentCacheStatusReason.RuntimeNotObserved
            || projection.Diagnostics.Count != 0
            || status.Projection.State != CdcComponentState.Unknown
            || status.Projection.Category != CdcBlockingCategory.StatusObservationUnavailable
            || Array.Exists(prerequisites, p => p.State != CdcComponentState.Satisfied)
            || status.ProviderBarrier.State != CdcComponentState.NotApplicable
            || status.SourceHistory.State != CdcComponentState.Satisfied
            || status.SourceHistory.Continuity != CdcSourceHistoryContinuity.Healthy
            || status.SourceHistory.IncidentLatched
            // The admitted local profile emits these notices even when both Kafka policies
            // are satisfied. They report the profile's limits, not an unavailable prerequisite.
            || status.Diagnostics.Any(d =>
                d.Code != "authorizationDisabledLocal"
                || d.Category != CdcDiagnosticCategory.None
                || d.Severity != CdcDiagnosticSeverity.Info
                || d.Component
                    is not (CdcDiagnosticComponent.KafkaPolicy or CdcDiagnosticComponent.ConnectOffsetStore)
                || d.Retryable
            )
            || target.HasPendingRecordSizeIncrease
            || target.HasSharedOffsetStoreIssue
            || target.Recovery.RequiresFreshPass
            || target.IncidentPersistence != CdcIncidentPersistenceState.NotRequired
            || target.Containment != CdcConnectorContainmentState.NotRequired
            || target.Diagnostics.Any(d =>
                d.Component != CdcDeploymentComponent.Projection
                || d.Failure != CdcDeploymentFailure.Unavailable
            )
        )
        {
            throw new InvalidOperationException("CDC_API_ATTACHMENT_PREREQUISITES");
        }
    }

    // The caller arms its gate first. A fresh controller pass after explicit startup must be
    // ready before the caller can submit any domain mutation; attachment never claims readiness.
    public static async Task StartAndWaitAsync(
        ICdcProjectionRuntime runtime,
        Func<CancellationToken, Task<CdcControllerStatusResult>> observe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken token
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        await runtime.StartProcessingAsync(deadline.Token).WaitAsync(deadline.Token);
        while (true)
        {
            var status = await observe(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (status.Aggregate.Readiness == CdcReadiness.Ready)
            {
                return;
            }
            await Task.Delay(pollInterval, deadline.Token);
        }
    }
}
