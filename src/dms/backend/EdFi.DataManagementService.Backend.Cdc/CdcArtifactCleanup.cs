// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Immutable transport scope, not retirement authorization. The controller must hold its workflow lock,
/// validate durable provenance and journal intent before calling adapters. Exact inventory recovery
/// excludes shared topics, principals and group grants. No caller-supplied artifact names reach SQL.
/// </summary>
public sealed class CdcArtifactCleanupScope
{
    public CdcArtifactCleanupScope(
        CdcDeploymentRequest request,
        CoreCdc.CdcCompleteBindingIdentity identity,
        IReadOnlyList<CoreCdc.CdcGovernedArtifactName> inventory
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inventory);
        var recovered = CoreCdc.CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(identity);
        if (
            identity != request.Binding.ToCompleteBindingIdentity()
            || !recovered.Succeeded
            || recovered.Inventory is null
            || inventory.Any(item => item is null)
            || !inventory
                .OrderBy(item => item.Kind)
                .SequenceEqual(recovered.Inventory.GovernedArtifacts.OrderBy(item => item.Kind))
        )
        {
            throw new ArgumentException(
                "CDC cleanup requires the exact binding and complete governed inventory."
            );
        }
        Request = request;
        Inventory = Array.AsReadOnly(recovered.Inventory.GovernedArtifacts.ToArray());
    }

    [JsonIgnore]
    public CdcDeploymentRequest Request { get; }

    [JsonIgnore]
    public IReadOnlyList<CoreCdc.CdcGovernedArtifactName> Inventory { get; }

    internal CoreCdc.CdcGovernedArtifactName Artifact(CoreCdc.CdcGovernedArtifactKind kind) =>
        Inventory.Single(item => item.Kind == kind);

    public override string ToString() => nameof(CdcArtifactCleanupScope);
}

/// <summary>
/// One artifact per call lets the retirement controller persist partial progress before advancing.
/// Observed contains fresh absence evidence; Unavailable is resumable and never proves absence.
/// These adapters never delete binding, incident, journal, or source-publication history.
/// </summary>
public interface ICdcArtifactCleanupAdapter
{
    Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> DeleteAsync(
        CdcArtifactCleanupScope scope,
        CoreCdc.CdcGovernedArtifactKind kind,
        CancellationToken cancellationToken
    );
}

public interface ICdcProviderArtifactCleanupAdapter : ICdcArtifactCleanupAdapter
{
    Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteOwnedSqlServerJobsAsync(
        CdcArtifactCleanupScope scope,
        LocalCdcWorkflowJournalStore.Session session,
        CancellationToken cancellationToken
    );
}

internal static class CdcArtifactCleanup
{
    internal static CdcTransportResult<CoreCdc.CdcGovernedArtifact> Removed(
        CoreCdc.CdcGovernedArtifactName artifact,
        bool deleted
    ) =>
        new CdcTransportResult<CoreCdc.CdcGovernedArtifact>.Observed(
            new(
                artifact.Kind,
                artifact.Name,
                deleted ? CoreCdc.CdcCleanupState.Deleted : CoreCdc.CdcCleanupState.NotFound,
                "Authoritative live inspection verified artifact absence."
            )
        );

    internal static CdcTransportResult<CoreCdc.CdcGovernedArtifact> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure = CdcDeploymentFailure.Unavailable
    ) => new CdcTransportResult<CoreCdc.CdcGovernedArtifact>.Unavailable(new(component, failure));

    internal static async Task<CdcTransportResult<T>> GuardAsync<T>(
        CdcArtifactCleanupScope scope,
        CdcDeploymentComponent component,
        Func<CancellationToken, Task<CdcTransportResult<T>>> action,
        CancellationToken cancellationToken
    )
        where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(scope.Request.Timing.WaitTimeout);
        try
        {
            return await action(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failedComponent =
                exception is CdcWorkflowStateException ? CdcDeploymentComponent.WorkflowState : component;
            return exception is OperationCanceledException
                ? new CdcTransportResult<T>.Unavailable(new(component, CdcDeploymentFailure.Timeout))
                : new CdcTransportResult<T>.Unavailable(
                    CdcDeploymentDiagnostic.FromException(failedComponent, exception)
                );
        }
    }

    internal static async Task AttemptAsync(
        Func<CancellationToken, Task> effect,
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        try
        {
            await effect(timeout.Token).WaitAsync(timeout.Token);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        // Even a lost acknowledgement must be followed by independent live inspection.
    }
}

/// <summary>
/// Offset removal precedes connector deletion. Persist its result before deleting the connector;
/// an absent connector alone cannot certify its offsets. The existing REST adapter owns supported
/// stop/reset/delete APIs. Controller-owned retries use durable offset-removal evidence after deletion.
/// </summary>
public sealed class CdcConnectArtifactCleanupAdapter(ICdcConnectTransport connect)
    : ICdcArtifactCleanupAdapter
{
    public Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> DeleteAsync(
        CdcArtifactCleanupScope scope,
        CoreCdc.CdcGovernedArtifactKind kind,
        CancellationToken cancellationToken
    ) =>
        CdcArtifactCleanup.GuardAsync(
            scope,
            CdcDeploymentComponent.Connect,
            async token =>
            {
                if (
                    kind
                    is not (
                        CoreCdc.CdcGovernedArtifactKind.ConnectSourceOffsets
                        or CoreCdc.CdcGovernedArtifactKind.KafkaConnectConnector
                    )
                )
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Connect,
                        CdcDeploymentFailure.InvalidInput
                    );
                }

                var artifact = scope.Artifact(kind);
                var request = scope.Request;
                var config = await connect
                    .ReadConfigurationAsync(request, token)
                    .WaitAsync(request.Timing.CallTimeout, token);
                if (config is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent)
                {
                    return kind == CoreCdc.CdcGovernedArtifactKind.KafkaConnectConnector
                        ? CdcArtifactCleanup.Removed(artifact, false)
                        : CdcArtifactCleanup.Failure(CdcDeploymentComponent.Connect);
                }

                if (config is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Connect,
                        config.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                    );
                }

                var stop = await connect
                    .StopAsync(request, token)
                    .WaitAsync(request.Timing.WaitTimeout, token);
                var statusStarted = DateTimeOffset.UtcNow;
                var status = await connect
                    .ReadStatusAsync(request, token)
                    .WaitAsync(request.Timing.CallTimeout, token);
                if (!IsFreshStopped(scope, status, statusStarted))
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Connect,
                        stop.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.ValidationFailed
                    );
                }

                if (kind == CoreCdc.CdcGovernedArtifactKind.ConnectSourceOffsets)
                {
                    await CdcArtifactCleanup.AttemptAsync(
                        async callToken =>
                        {
                            await connect.DeleteOffsetsAsync(request, callToken);
                        },
                        request,
                        token
                    );
                }
                var offsets = await connect
                    .ReadOffsetEvidenceAsync(request, token)
                    .WaitAsync(request.Timing.CallTimeout, token);
                var afterStarted = DateTimeOffset.UtcNow;
                var after = await connect
                    .ReadStatusAsync(request, token)
                    .WaitAsync(request.Timing.CallTimeout, token);
                if (
                    offsets is not CdcTransportResult<CdcConnectOffsetEvidence>.Observed observed
                    || observed.Value.State != CdcConnectOffsetState.Missing
                    || !IsFreshStopped(scope, after, afterStarted)
                )
                {
                    return CdcArtifactCleanup.Failure(CdcDeploymentComponent.Connect);
                }

                if (kind == CoreCdc.CdcGovernedArtifactKind.ConnectSourceOffsets)
                {
                    return CdcArtifactCleanup.Removed(artifact, true);
                }

                await CdcArtifactCleanup.AttemptAsync(
                    async callToken =>
                    {
                        await connect.DeleteAsync(request, callToken);
                    },
                    request,
                    token
                );
                var absent = await connect
                    .ReadConfigurationAsync(request, token)
                    .WaitAsync(request.Timing.CallTimeout, token);
                return absent is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent
                    ? CdcArtifactCleanup.Removed(artifact, true)
                    : CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Connect,
                        absent.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                    );
            },
            cancellationToken
        );

    private static bool IsFreshStopped(
        CdcArtifactCleanupScope scope,
        CdcTransportResult<CdcConnectStatus> result,
        DateTimeOffset started
    )
    {
        if (result is not CdcTransportResult<CdcConnectStatus>.Observed observed)
        {
            return false;
        }
        var status = observed.Value;
        var now = DateTimeOffset.UtcNow;
        return status.IsStopped
            && status.Runtime.TaskCount == 0
            && status.Runtime.RunningTaskCount == 0
            && status.Runtime.ObservedAt >= started
            && status.Runtime.ObservedAt <= now
            && now - status.Runtime.ObservedAt <= scope.Request.Timing.MaximumObservationAge
            && CoreCdc
                .CdcConnectorRuntimeObservationValidator.ValidateForLifecycle(
                    status.Runtime,
                    scope.Request.Binding,
                    new(
                        status.Runtime.OperationId,
                        scope.Request.TargetIdentity,
                        scope.Request.Binding.PhysicalSourceFingerprint,
                        now
                    )
                )
                .Succeeded;
    }
}
