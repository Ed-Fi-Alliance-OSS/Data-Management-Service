// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed record CdcRecordSizeIncreaseScope(
    Guid OperationId,
    CdcCompleteBindingIdentity BindingIdentity,
    int PreviousMaxRecordBytes,
    int RequestedMaxRecordBytes
);

/// <summary>
/// Explicit input from the authorized deployment operator. Confirmation covers every affected
/// consumer's deployed fetch limits, tested deserialization capacity, and preservation of that
/// capacity through rollout and consumption. References are credential-free opaque evidence IDs,
/// using the journal's safe-token contract; the controller neither fetches nor certifies them.
/// </summary>
public sealed record CdcRecordSizeIncreaseConfirmation(
    [property: JsonIgnore] CdcRecordSizeIncreaseScope Scope,
    [property: JsonIgnore] CdcRecordSizeAcknowledgement Acknowledgement,
    bool CompleteInventoryAndCapacityConfirmed
)
{
    public override string ToString() => nameof(CdcRecordSizeIncreaseConfirmation);
}

/// <summary>
/// One explicit increase invocation under the caller's controller lock. Obtain fresh operator
/// confirmation for InvocationId after StartedAt, then enter the rollout once. Persisted history
/// cannot construct this invocation or authorize its callback. The callback owns live eligibility,
/// infrastructure reconciliation and readiness; acknowledgement alone proves none of those.
/// </summary>
public sealed class CdcRecordSizeAcknowledgementInvocation
{
    private readonly LocalCdcWorkflowJournalStore.Session _session;
    private readonly Guid _workflowId;
    private readonly CdcRecordSizeIncreaseScope _scope;
    private readonly TimeProvider _time;
    private int _entered;

    internal CdcRecordSizeAcknowledgementInvocation(
        LocalCdcWorkflowJournalStore.Session session,
        Guid workflowId,
        CdcRecordSizeIncreaseScope scope,
        TimeProvider time
    )
    {
        Require(workflowId != Guid.Empty);
        ValidateScope(scope);
        _session = session;
        _workflowId = workflowId;
        _scope = scope;
        _time = time;
        InvocationId = Guid.NewGuid();
        StartedAt = time.GetUtcNow();
    }

    public Guid InvocationId { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Retain the session until this call returns. The rollout callback may use its journal APIs;
    /// do not dispose the session concurrently. Failure/cancellation consumes this invocation and
    /// preserves any durable intent/confirmation; another invocation requires renewed confirmation.
    /// </summary>
    public async Task<T> ConfirmAndRunAsync<T>(
        CdcRecordSizeIncreaseConfirmation confirmation,
        Func<CancellationToken, Task<T>> advance,
        CancellationToken cancellationToken
    )
    {
        Require(Interlocked.Exchange(ref _entered, 1) == 0, CdcWorkflowStateFailure.Contradictory);
        cancellationToken.ThrowIfCancellationRequested();
        Require(advance is not null && confirmation is not null);
        Require(confirmation!.CompleteInventoryAndCapacityConfirmed);
        Require(confirmation.Scope == _scope, CdcWorkflowStateFailure.Contradictory);
        Require(confirmation.Acknowledgement is not null);
        Require(
            confirmation.Acknowledgement.InvocationId == InvocationId,
            CdcWorkflowStateFailure.Contradictory
        );
        ValidateAcknowledgement(confirmation.Acknowledgement, StartedAt, _time.GetUtcNow());
        await _session.RecordConfirmedIncreaseAsync(
            _workflowId,
            _scope,
            confirmation.Acknowledgement,
            cancellationToken
        );
        cancellationToken.ThrowIfCancellationRequested();
        return await advance!(cancellationToken);
    }

    internal static void ValidateScope(CdcRecordSizeIncreaseScope scope)
    {
        Require(scope is not null && scope.BindingIdentity is not null);
        Require(scope!.OperationId != Guid.Empty);
        Require(CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(scope.BindingIdentity).Succeeded);
        Require(
            scope.PreviousMaxRecordBytes > 0 && scope.RequestedMaxRecordBytes > scope.PreviousMaxRecordBytes
        );
    }
}
