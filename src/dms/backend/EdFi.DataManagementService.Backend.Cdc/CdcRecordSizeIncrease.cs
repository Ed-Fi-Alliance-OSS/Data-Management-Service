// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcRecordSizeRollout;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed record CdcRecordSizeIncreaseResult(
    bool Succeeded,
    bool Ready,
    Guid OperationId,
    IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics
)
{
    public CdcControllerTargetStatus Observation { get; init; } = null!;
}

/// <summary>
/// Explicit, acknowledged size rollout. The previous request and scope are retained on retry. The
/// confirmation callback obtains fresh operator input for this invocation while the controller lock
/// is held. Projection remains caller-owned; success is observational, never writer authorization.
/// </summary>
public sealed class CdcRecordSizeIncrease
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcKafkaAdminAdapter _kafka;
    private readonly ICdcKafkaRecordSizeAdministration _sizes;
    private readonly ICdcConnectTransport _connect;
    private readonly CdcControllerStatus _status;
    private readonly TimeProvider _time;

    public CdcRecordSizeIncrease(
        string stateRoot,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcKafkaRecordSizeAdministration sizes,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker,
        ICdcWorkerMetricsTransport metrics,
        ICdcProviderSourcePositionAdapter positions
    )
        : this(
            new(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            kafka,
            sizes,
            connect,
            new(stateRoot, provider, templates, kafka, connect, worker, metrics, positions),
            TimeProvider.System
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
    }

    internal CdcRecordSizeIncrease(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcKafkaAdminAdapter kafka,
        ICdcKafkaRecordSizeAdministration sizes,
        ICdcConnectTransport connect,
        CdcEstablishedValidation validation,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _kafka = kafka;
        _sizes = sizes;
        _connect = connect;
        _time = time;
        _status = new(store, bindings, connect, _ => validation, time);
    }

    public async Task<CdcRecordSizeIncreaseResult> IncreaseAsync(
        CdcControllerStatusTarget previousTarget,
        CdcRecordSizeIncreaseScope scope,
        int requestedProducerBufferBytes,
        Func<
            CdcRecordSizeAcknowledgementInvocation,
            CancellationToken,
            Task<CdcRecordSizeIncreaseConfirmation>
        > confirm,
        CancellationToken cancellationToken = default,
        CancellationToken operationDeadline = default
    )
    {
        ArgumentNullException.ThrowIfNull(previousTarget);
        ArgumentNullException.ThrowIfNull(scope);
        var previous = previousTarget.Request;
        var component = CdcDeploymentComponent.WorkflowState;
        CdcControllerTargetStatus lastObservation = null!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationDeadline
        );
        timeout.CancelAfter(previous.Timing.WaitTimeout);
        var token = timeout.Token;
        try
        {
            CdcRecordSizeAcknowledgementInvocation.ValidateScope(scope);
            Require(scope.BindingIdentity == previous.Binding.ToCompleteBindingIdentity());
            Require(scope.PreviousMaxRecordBytes == previous.ConnectorPolicy.MaxRecordBytes);
            Require(requestedProducerBufferBytes >= previous.ConnectorPolicy.EffectiveProducerBufferBytes);
            var desired = WithPolicy(previous, scope.RequestedMaxRecordBytes, requestedProducerBufferBytes);
            await using var session = await _store.AcquireAsync(
                previous.Timing.CallTimeout,
                previous.Timing.PollInterval < previous.Timing.CallTimeout
                    ? previous.Timing.PollInterval
                    : previous.Timing.CallTimeout,
                token
            );
            var journal = await session.ReadAsync(previous.TargetIdentity, token);
            Require(!journal.RetirementIntended);
            var exact = await Call(ct => _bindings.ExactMatchBindingAsync(previous.Binding, ct));
            Require(exact.Status == CdcControlPlaneOperationStatus.Succeeded);
            var pending = journal.Operations.SingleOrDefault(o =>
                o.Effect == CdcWorkflowEffect.IncreaseRecordSize && o.Completions.IsEmpty
            );
            Require(pending is null || pending.OperationId == scope.OperationId);
            var lastCompleted = journal.Operations.LastOrDefault(o =>
                o.Effect == CdcWorkflowEffect.IncreaseRecordSize && !o.Completions.IsEmpty
            );
            Require(
                lastCompleted is null
                    || lastCompleted.RecordSizeIncrease.Single().RequestedMaxRecordBytes
                        == scope.PreviousMaxRecordBytes
            );
            if (pending is null)
            {
                // A new operation starts from the declared previous policy. Only retained pending
                // intent permits reconciliation of intermediate limits on a resumed invocation.
                var baseline = await Observe(previousTarget, session, token);
                if (baseline.Recovery.RequiresFreshPass)
                {
                    baseline = await Observe(previousTarget, session, token);
                }
                Require(baseline.PreStartEligible);
            }
            var invocation = session.BeginRecordSizeAcknowledgement(journal.WorkflowId, scope);
            var confirmation = await confirm(invocation, token).WaitAsync(token);
            return await invocation.ConfirmAndRunAsync(
                confirmation,
                async ct =>
                {
                    var rollout = new CdcRecordSizeRollout(invocation, previous, desired);
                    Guid resumeId = Guid.Empty;
                    CdcWorkerInspection worker = null!;
                    var current = await ValidateStage(false);
                    Require(current.PreStartEligible);
                    worker = current.Worker;

                    // Stop before any config PUT. Intent/read-back preserve the same lifecycle protocol.
                    var stopId = Guid.NewGuid();
                    await session.RecordIntentAsync(
                        previous.TargetIdentity,
                        journal.WorkflowId,
                        stopId,
                        CdcWorkflowEffect.StopConnector,
                        [],
                        ct
                    );
                    component = CdcDeploymentComponent.Connect;
                    await Attempt(() => Call(t => _connect.StopAsync(previous, t)));
                    await WaitStopped();
                    await session.ReconcileCompletionAsync(
                        previous.TargetIdentity,
                        journal.WorkflowId,
                        stopId,
                        async (_, t) =>
                        {
                            await RequireStopped(t);
                            return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                                new CdcWorkflowCompletion.Shutdown()
                            );
                        },
                        ct
                    );
                    await RequireEligible();

                    component = CdcDeploymentComponent.Kafka;
                    await Attempt(() => Call(t => _sizes.IncreaseBrokerLimitsAsync(desired, t)));
                    RequireBrokerCapacity(
                        Observed(await Call(t => _kafka.InspectBrokersAsync(desired, t))),
                        scope.RequestedMaxRecordBytes
                    );
                    await RequireEligible();
                    component = CdcDeploymentComponent.Kafka;
                    await Attempt(() =>
                        Call(t =>
                            _sizes.IncreasePublicTopicLimitAsync(desired, scope.PreviousMaxRecordBytes, t)
                        )
                    );
                    var publicTopic = await Call(t =>
                        _kafka.InspectTopicAsync(desired, desired.Binding.TopicName, t)
                    );
                    Require(
                        CdcDeploymentKafkaPolicy
                            .ObserveTopic(
                                desired,
                                CdcDeploymentKafkaPolicy.Build(desired).BindingTopics[0],
                                publicTopic
                            )
                            .State == CdcKafkaPolicyItemState.Satisfied
                    );
                    await RequireEligible();

                    // Two separate PUT/read-back steps. Build from freshly validated live config and replace
                    // only the size property; never replay rendered/masked credentials over the live payload.
                    await IncreaseProducer("producer.override.buffer.memory", requestedProducerBufferBytes);
                    await RequireEligible();
                    await IncreaseProducer(
                        "producer.override.max.request.size",
                        scope.RequestedMaxRecordBytes
                    );
                    await RequireEligible();

                    resumeId = Guid.NewGuid();
                    await session.RecordIntentAsync(
                        previous.TargetIdentity,
                        journal.WorkflowId,
                        resumeId,
                        CdcWorkflowEffect.ResumeConnector,
                        [],
                        ct
                    );
                    await RequireEligible();
                    component = CdcDeploymentComponent.Connect;
                    await Attempt(() => Call(t => _connect.ResumeAsync(desired, t)));
                    var ready = await ValidateStage(true);
                    while (!ready.PublicationReady)
                    {
                        Require(ready.PreStartEligible);
                        await Task.Delay(desired.Timing.PollInterval, _time, ct);
                        ready = await ValidateStage(true);
                    }
                    // Readiness is measured while the pending gate is still durable. Only this live
                    // acknowledged invocation may complete it; generic status/restart cannot enter here.
                    await session.ReconcileCompletionAsync(
                        previous.TargetIdentity,
                        journal.WorkflowId,
                        resumeId,
                        async (_, t) =>
                        {
                            var live = Observed(await Call(c => _connect.ReadStatusAsync(desired, c)));
                            Require(live.IsRunning && live.WorkerId == worker.ConnectWorkerId);
                            t.ThrowIfCancellationRequested();
                            return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                                new CdcWorkflowCompletion.Reconciled()
                            );
                        },
                        ct
                    );
                    ready = await ValidateStage(true);
                    Require(ready.PublicationReady);
                    component = CdcDeploymentComponent.WorkflowState;
                    await session.ReconcileCompletionAsync(
                        previous.TargetIdentity,
                        journal.WorkflowId,
                        scope.OperationId,
                        (operation, t) =>
                        {
                            Require(
                                operation.RecordSizeIncrease.Single().Acknowledgements[^1].InvocationId
                                    == invocation.InvocationId
                            );
                            Require(
                                ready.PublicationReady
                                    && _time.GetUtcNow() - ready.ObservedAt
                                        <= desired.Timing.MaximumObservationAge
                            );
                            t.ThrowIfCancellationRequested();
                            return Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                                new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                                    new CdcWorkflowCompletion.Reconciled()
                                )
                            );
                        },
                        ct
                    );
                    ct.ThrowIfCancellationRequested();
                    // Persistence is historical. Return success only after another ordinary, fresh pass.
                    var final = await Observe(
                        new(
                            desired,
                            previousTarget.Runtime,
                            previousTarget.LagThresholdMilliseconds,
                            previousTarget.Integrity
                        ),
                        session,
                        ct,
                        CdcEstablishedValidationMode.RunningPublication,
                        resumeId
                    );
                    Require(final.PublicationReady);
                    return new CdcRecordSizeIncreaseResult(true, true, scope.OperationId, []);

                    async Task<CdcEstablishedValidationObservation> ValidateStage(
                        bool publication,
                        bool retryRecovery = true
                    )
                    {
                        component = CdcDeploymentComponent.Connect;
                        var live = Observed(await Call(t => _connect.ReadConfigurationAsync(previous, t)));
                        var stage = rollout.Stage(live);
                        if (publication)
                        {
                            Require(stage.ConnectorPolicy == desired.ConnectorPolicy);
                        }

                        var observation = await Observe(
                            new(
                                stage,
                                previousTarget.Runtime,
                                previousTarget.LagThresholdMilliseconds,
                                previousTarget.Integrity
                            ),
                            session,
                            ct,
                            publication
                                ? CdcEstablishedValidationMode.RunningPublication
                                : CdcEstablishedValidationMode.PreStart,
                            resumeId,
                            rollout
                        );
                        if (observation.Worker is not null && worker is not null)
                        {
                            CdcConnectorRegistration.RequireSameWorker(desired, worker, observation.Worker);
                        }

                        if (observation.Recovery.RequiresFreshPass && retryRecovery)
                        {
                            return await ValidateStage(publication, false);
                        }
                        return observation;
                    }
                    async Task RequireEligible()
                    {
                        Require((await ValidateStage(false)).PreStartEligible);
                    }
                    async Task RequireStopped(CancellationToken t, bool afterConfigurationUpdate = false)
                    {
                        var started = _time.GetUtcNow();
                        var live = Observed(await Call(c => _connect.ReadStatusAsync(desired, c)));
                        // Config updates can briefly unassign the retained stopped connector. Wait
                        // only for fresh, task-free evidence from that same worker; never advance
                        // while unassigned or tolerate a running task or changed worker identity.
                        while (
                            afterConfigurationUpdate
                            && live.Runtime.ConnectorState == CdcConnectorRuntimeState.Unassigned
                            && live.Tasks.Count == 0
                            && live.Runtime.TaskCount == 0
                            && live.Runtime.RunningTaskCount == 0
                            && live.WorkerId == worker.ConnectWorkerId
                            && live.Runtime.ObservedAt >= started
                            && live.Runtime.ObservedAt <= _time.GetUtcNow()
                        )
                        {
                            await Task.Delay(desired.Timing.PollInterval, _time, t);
                            started = _time.GetUtcNow();
                            live = Observed(await Call(c => _connect.ReadStatusAsync(desired, c)));
                        }
                        Require(
                            live.IsStopped
                                && live.Tasks.Count == 0
                                && live.Runtime.TaskCount == 0
                                && live.WorkerId == worker.ConnectWorkerId
                                && live.Runtime.ObservedAt >= started
                                && live.Runtime.ObservedAt <= _time.GetUtcNow()
                        );
                        Require(
                            CdcConnectorRuntimeObservationValidator
                                .ValidateForLifecycle(
                                    live.Runtime,
                                    desired.Binding,
                                    new(
                                        live.Runtime.OperationId,
                                        desired.TargetIdentity,
                                        desired.Binding.PhysicalSourceFingerprint,
                                        _time.GetUtcNow()
                                    )
                                )
                                .Succeeded
                        );
                        t.ThrowIfCancellationRequested();
                    }
                    async Task WaitStopped()
                    {
                        while (true)
                        {
                            var live = Observed(await Call(t => _connect.ReadStatusAsync(desired, t)));
                            if (live.IsStopped)
                            {
                                await RequireStopped(ct);
                                return;
                            }
                            await Task.Delay(desired.Timing.PollInterval, _time, ct);
                        }
                    }
                    async Task IncreaseProducer(string key, int value)
                    {
                        component = CdcDeploymentComponent.Connect;
                        await RequireStopped(ct);
                        var validated = await ValidateStage(false);
                        Require(validated.PreStartEligible && validated.Connector.IsStopped);
                        var live = validated.LiveConfiguration;
                        rollout.Stage(live);
                        int old = Number(live[key]);
                        Require(old <= value);
                        if (old == value)
                        {
                            return;
                        }

                        Dictionary<string, string> updated = new(live)
                        {
                            [key] = value.ToString(CultureInfo.InvariantCulture),
                        };
                        // Masked values are observational only. Replace credentials with the explicitly
                        // supplied externalized references after full live-template validation above.
                        foreach (
                            var pair in desired
                                .ProviderConnectionProperties.Properties.Concat(
                                    desired.KafkaClientSecurityProperties.Properties
                                )
                                .Where(pair => updated.ContainsKey(pair.Key))
                        )
                        {
                            updated[pair.Key] = pair.Value;
                        }

                        CdcKafkaConnectRegistrationPayload payload = new(
                            new(desired.Binding.ConnectorName),
                            updated
                        );
                        Observed(await Call(t => _connect.ValidateConfigurationAsync(desired, payload, t)));
                        await Attempt(() =>
                            Call(t =>
                                _connect.UpdateConfigurationForRecordSizeIncreaseAsync(desired, payload, t)
                            )
                        );
                        var after = Observed(await Call(t => _connect.ReadConfigurationAsync(desired, t)));
                        Require(Number(after[key]) == value);
                        component = CdcDeploymentComponent.Connect;
                        await RequireStopped(ct, afterConfigurationUpdate: true);
                    }
                },
                token
            );
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = exception switch
            {
                CdcEstablishedValidation.EvidenceException evidence => evidence.Diagnostic,
                CdcWorkflowStateException state => new(
                    component,
                    state.Failure == CdcWorkflowStateFailure.LockTimeout
                        ? CdcDeploymentFailure.Timeout
                        : CdcDeploymentFailure.ValidationFailed
                ),
                OperationCanceledException or TimeoutException => new CdcDeploymentDiagnostic(
                    component,
                    CdcDeploymentFailure.Timeout
                ),
                _ => CdcDeploymentDiagnostic.FromException(component, exception),
            };
            return new(
                false,
                false,
                scope.OperationId,
                (lastObservation?.Diagnostics ?? [])
                    .Append(diagnostic)
                    .DistinctBy(d => (d.Component, d.Failure))
                    .ToArray()
            )
            {
                Observation = lastObservation!,
            };
        }

        async Task<CdcEstablishedValidationObservation> Observe(
            CdcControllerStatusTarget target,
            LocalCdcWorkflowJournalStore.Session session,
            CancellationToken deadline,
            CdcEstablishedValidationMode mode = CdcEstablishedValidationMode.PreStart,
            Guid resumeId = default,
            CdcRecordSizeRollout rollout = null!
        )
        {
            CdcEstablishedValidationObservation observation = null!;
            lastObservation = await _status.ObserveTargetAsync(
                target,
                cancellationToken,
                session,
                mode,
                value => observation = value,
                resumeId,
                deadline,
                rollout
            );
            Require(lastObservation.Status.SourceHistory.Continuity != CdcSourceHistoryContinuity.Lost);
            // Only the acknowledged publication wait may outlast known lag or queued work.
            // Status still performs terminal containment first; unavailable evidence is never catch-up.
            bool catchingUp =
                mode == CdcEstablishedValidationMode.RunningPublication
                && rollout is not null
                && observation is { Connector.IsRunning: true, PreStartEligible: true }
                && !lastObservation.Recovery.RequiresFreshPass
                && lastObservation.Status.PrimaryBlockingCategory
                    is CdcBlockingCategory.ProjectionBacklog
                        or CdcBlockingCategory.LagExceeded
                && lastObservation.Diagnostics.All(d =>
                    d.Failure == CdcDeploymentFailure.ValidationFailed
                    && d.Component is CdcDeploymentComponent.Projection or CdcDeploymentComponent.Metrics
                )
                && lastObservation.Status.ConnectorRuntime.State == CdcComponentState.Satisfied
                && CanCatchUp(lastObservation.Status.Projection, CdcBlockingCategory.ProjectionBacklog)
                && CanCatchUp(lastObservation.Status.Lag, CdcBlockingCategory.LagExceeded);
            if (!catchingUp && lastObservation.Diagnostics.FirstOrDefault() is { } diagnostic)
            {
                throw new CdcEstablishedValidation.EvidenceException(diagnostic);
            }
            Require(observation is not null);
            return observation;
        }

        static bool CanCatchUp(CdcComponent component, CdcBlockingCategory temporaryBlocker) =>
            component.State == CdcComponentState.Satisfied
            || component.State == CdcComponentState.NotSatisfied && component.Category == temporaryBlocker;

        async Task<T> Call<T>(Func<CancellationToken, Task<T>> action)
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
            call.CancelAfter(previous.Timing.CallTimeout);
            var result = await action(call.Token).WaitAsync(call.Token);
            call.Token.ThrowIfCancellationRequested();
            return result;
        }
        async Task Attempt<T>(Func<Task<T>> effect)
        {
            try
            {
                await effect();
            }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
            }
            // Even an acknowledged effect requires independent live inspection. Never retry a mutation
            // after an uncertain response in this invocation.
        }
    }
}
