// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcControllerObservations
{
    internal static CdcConnectorConfigurationObservation Configuration(
        CdcDeploymentRequest request,
        string operation,
        DateTimeOffset now
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            operation,
            now,
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            request.Binding.ConnectorName,
            CdcConnectorConfigurationState.Matched,
            CdcConnectorTemplateBindingArtifacts
                .From(request.Binding, nameof(request))
                .ArtifactInventory.TopicPrefix,
            1,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            request.Binding.Provider == CoreProvider.SqlServer
                ? CdcConnectorConfigurationItemState.Matched
                : CdcConnectorConfigurationItemState.NotApplicable,
            []
        );

    internal static CdcProjectionCorrelationObservation Projection(
        CdcDeploymentRequest request,
        DocumentCacheStatusResponse response,
        string operation,
        DateTimeOffset started,
        DateTimeOffset now
    )
    {
        Require(response.Targets.Length == 1);
        var target = response.Targets[0];
        Require(
            target.DurableObservedAt is not null
                && target.DurableObservedAt >= started
                && target.DurableObservedAt <= now
                && now - target.DurableObservedAt.Value <= request.Timing.MaximumObservationAge
                && CdcProviderToken.TryToRelationalProviderToken(request.Binding.Provider, out var provider)
                && target.Provider == provider.Value
                && target.PhysicalSourceFingerprint == request.Binding.PhysicalSourceFingerprint
                && target.Lifecycle.State == DocumentCacheStatusLifecycleState.Tracking
                && target.CacheAhead.RecoveryRequired is false
        );
        var observation = new CdcProjectionCorrelationObservation(
            CdcJsonContract.CurrentContractVersion,
            operation,
            now,
            request.TargetIdentity,
            request.Binding.Provider,
            target.PhysicalSourceFingerprint,
            target.DurableObservedAt!.Value,
            new(target.TargetKey.TenantKey.ToLowerInvariant(), target.TargetKey.DataStoreId),
            CdcProjectionCorrelationState.Matched,
            target.OperationalHealth.Status,
            target.OperationalHealth.Reason,
            target.CaughtUp.Status,
            target.CaughtUp.Reason,
            target.QueueSummary.Presence,
            target.EnqueueFailures.ByCategory.Select(c => c.Category).ToArray(),
            []
        );
        Require(
            CdcProjectionCorrelationObservationValidator
                .Validate(
                    observation,
                    new(operation, request.TargetIdentity, request.Binding.PhysicalSourceFingerprint, now)
                )
                .Succeeded
        );
        return observation;
    }

    internal static CdcConnectorOffsetObservation Offset(
        CdcDeploymentRequest request,
        string operation,
        CdcConnectOffsetEvidence offset,
        string expectedSourcePartitionHash,
        DateTimeOffset now
    )
    {
        bool postgres = request.Binding.Provider == CoreProvider.Postgresql;
        // The transport already parses provider positions. Preserve null/snapshot/malformed and
        // authoritative match outcomes so Core, rather than the controller, classifies history loss.
        return new(
            CdcJsonContract.CurrentContractVersion,
            operation,
            now,
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            request.Binding.ConnectorName,
            request.Binding.ConnectorName,
            postgres
                ? offset.Postgresql.SourcePartitionMatchResult
                : offset.SqlServer.SourcePartitionMatchResult,
            string.IsNullOrEmpty(offset.SourcePartitionHash)
            && offset.State != CdcConnectOffsetState.Streaming
                ? expectedSourcePartitionHash
                : offset.SourcePartitionHash,
            offset.State == CdcConnectOffsetState.Snapshot,
            offset.State == CdcConnectOffsetState.Null,
            postgres ? offset.Postgresql.LsnProc : null,
            postgres ? null : offset.SqlServer.CommitLsn,
            postgres ? null : offset.SqlServer.ChangeLsn,
            postgres ? null : offset.SqlServer.EventSerialNo,
            []
        );
    }

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);
}
