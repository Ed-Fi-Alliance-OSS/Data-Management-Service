// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Establishment evidence for this invocation, never writer-publication readiness.</summary>
public sealed class CdcConnectorRegistrationReceipt(Guid workflowId, string sourcePartitionHash)
{
    public Guid WorkflowId { get; } = workflowId;

    [JsonIgnore]
    public string SourcePartitionHash { get; } = sourcePartitionHash;

    public override string ToString() => nameof(CdcConnectorRegistrationReceipt);
}

/// <summary>
/// Initial registration only. Holds one controller session across fresh provider inspection, policy,
/// preflight, exact registration reconciliation and streaming establishment. Never repairs or resumes
/// an established connector; public writer handoff and fresh admission belong to the readiness stage.
/// </summary>
public sealed class CdcConnectorRegistration
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcProviderSetupService _provider;
    private readonly ICdcConnectorTemplateService _templates;
    private readonly ICdcKafkaAdminAdapter _kafka;
    private readonly ICdcConnectTransport _connect;
    private readonly ICdcWorkerInspectionTransport _worker;
    private readonly TimeProvider _time;

    public CdcConnectorRegistration(
        string stateRoot,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker
    )
        : this(
            new(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            provider,
            templates,
            kafka,
            connect,
            worker,
            TimeProvider.System
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
    }

    internal CdcConnectorRegistration(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _provider = provider;
        _templates = templates;
        _kafka = kafka;
        _connect = connect;
        _worker = worker;
        _time = time;
    }

    public async Task<CdcTransportResult<CdcConnectorRegistrationReceipt>> RegisterAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        var boundary = new Boundary();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var token = timeout.Token;
        try
        {
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                token
            );
            var target = request.TargetIdentity;
            var journal = await session.ReadAsync(target, token);
            Require(
                !journal.Operations.Any(o =>
                    o.Effect
                        is CdcWorkflowEffect.AuthorizeWriterPublication
                            or CdcWorkflowEffect.StopConnector
                            or CdcWorkflowEffect.ResumeConnector
                            or CdcWorkflowEffect.IncreaseRecordSize
                            or CdcWorkflowEffect.Retire
                )
            );
            foreach (
                var effect in new[]
                {
                    CdcWorkflowEffect.ReserveBinding,
                    CdcWorkflowEffect.ActivateProjection,
                    CdcWorkflowEffect.CreateProvider,
                    CdcWorkflowEffect.PrepareKafka,
                }
            )
            {
                Require(
                    journal.Operations.Count(o => o.Effect == effect) == 1
                        && journal.Operations.Single(o => o.Effect == effect).Completions.Length == 1
                );
            }
            var registrations = journal
                .Operations.Where(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
                .ToArray();
            var establishments = journal
                .Operations.Where(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
                .ToArray();
            Require(registrations.Length <= 1 && establishments.Length <= 1);
            Require(
                establishments.Length == 0
                    || registrations.Length == 1 && registrations[0].Completions.Length == 1
            );
            if (registrations.Length == 1)
            {
                Require(registrations[0].ConnectorRegistration.Length == 1);
                Require(
                    Array.TrueForAll(
                        new[] { CdcWorkflowEffect.CreateProvider, CdcWorkflowEffect.PrepareKafka },
                        effect =>
                            journal.Operations.Single(o => o.Effect == effect).Completions[0].ReconciledAt
                            <= registrations[0].IntendedAt
                    )
                );
            }
            var history = await session.ReadSourcePublicationHistoryAsync(
                target,
                request.Binding.PhysicalSourceFingerprint,
                token
            );
            // Active history may precede the final establishment journal replacement in a crash.
            // That surviving evidence must never regain permission to await a missing offset.
            bool offsetPreviouslySeen =
                history.Transitions[^1].Status == DocumentCacheDownstreamPublicationStatus.Active
                || Array.Exists(establishments, o => !o.Completions.IsEmpty);
            Require(!offsetPreviouslySeen || establishments.Length == 1);

            var provider = new CdcProviderSetupOrchestration(_store, _bindings, _provider, _templates, _time);
            var handoff = await CallAsync(
                request,
                ct =>
                    provider.SetupInSessionAsync(request, runtime, session, c => boundary.Component = c, ct),
                token
            );
            var template = handoff.Template;
            Require(
                template.RegistrationPayload is not null
                    && CdcSha256ValueValidator.IsValid(template.ConfigSha256)
            );
            boundary.Component = CdcDeploymentComponent.WorkflowState;
            if (registrations.Length == 1)
            {
                Require(registrations[0].ConnectorRegistration[0].ConfigSha256 == template.ConfigSha256);
            }

            boundary.Component = CdcDeploymentComponent.Worker;
            var worker = Observed(await CallAsync(request, ct => _worker.InspectAsync(request, ct), token));
            RequireWorker(request, worker);
            boundary.Component = CdcDeploymentComponent.Connect;
            var preflight = Observed(
                await CallAsync(
                    request,
                    ct => _connect.ValidateConfigurationAsync(request, template.RegistrationPayload!, ct),
                    token
                )
            );
            Require(
                _templates
                    .ValidateRegistrationPreflight(
                        new(handoff.TemplateRequest, preflight, handoff.TemplateRequest.ProviderSetupEvidence)
                    )
                    .Outcome == CdcConnectorTemplateOutcome.Rendered
            );
            boundary.Component = CdcDeploymentComponent.Kafka;
            await RequireKafkaAsync(request, preflight, worker, token);
            RequireFresh(request, handoff.ObservedAt);
            boundary.Component = CdcDeploymentComponent.Connect;
            var configuration = await CallAsync(
                request,
                ct => _connect.ReadConfigurationAsync(request, ct),
                token
            );
            if (configuration is CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed current)
            {
                // Unjournaled live connectors are not adopted, even if their configuration looks healthy.
                Require(registrations.Length == 1);
                ValidateConfiguration(handoff, current.Value);
            }
            else if (configuration is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent)
            {
                Require(
                    !offsetPreviouslySeen
                        && establishments.Length == 0
                        && Array.TrueForAll(registrations, o => o.Completions.IsEmpty)
                );
                boundary.Component = CdcDeploymentComponent.WorkflowState;
                if (registrations.Length == 0)
                {
                    journal = await session.RecordConnectorRegistrationIntentAsync(
                        target,
                        journal.WorkflowId,
                        Guid.NewGuid(),
                        new(template.ConfigSha256!),
                        token
                    );
                }
                boundary.Component = CdcDeploymentComponent.Worker;
                var currentWorker = Observed(
                    await CallAsync(request, ct => _worker.InspectAsync(request, ct), token)
                );
                RequireSameWorker(request, worker, currentWorker);
                RequireFresh(request, handoff.ObservedAt);
                boundary.Component = CdcDeploymentComponent.Connect;
                // Exactly one attempt. Acknowledgement, conflict and a lost response all require an
                // independent live read. This code has no config PUT, reset or resnapshot fallback.
                try
                {
                    await CallAsync(
                        request,
                        ct => _connect.CreateAsync(request, template.RegistrationPayload!, ct),
                        token
                    );
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The independently bounded read below is the authority, including after a call timeout.
                }
            }
            else
            {
                _ = Observed(configuration);
            }
            boundary.Component = CdcDeploymentComponent.Connect;
            var live = Observed(
                await CallAsync(request, ct => _connect.ReadConfigurationAsync(request, ct), token)
            );
            ValidateConfiguration(handoff, live);
            boundary.Component = CdcDeploymentComponent.WorkflowState;
            var registration = journal.Operations.Single(o =>
                o.Effect == CdcWorkflowEffect.RegisterConnector
            );
            journal = await CompleteAsync(
                session,
                journal,
                registration,
                new CdcWorkflowCompletion.Reconciled(),
                token
            );
            if (establishments.Length == 0)
            {
                journal = await session.RecordIntentAsync(
                    target,
                    journal.WorkflowId,
                    Guid.NewGuid(),
                    CdcWorkflowEffect.EstablishConnector,
                    [],
                    token
                );
            }

            while (true)
            {
                var observationStartedAt = _time.GetUtcNow();
                boundary.Component = CdcDeploymentComponent.Connect;
                var statusEvidence = await CallAsync(
                    request,
                    ct => _connect.ReadStatusAsync(request, ct),
                    token
                );
                if (statusEvidence is CdcTransportResult<CdcConnectStatus>.Absent && !offsetPreviouslySeen)
                {
                    // The config store can acknowledge creation before the asynchronous status store
                    // publishes its first record. Absence never supplies readiness or permits recreation.
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                var status = Observed(statusEvidence);
                if (!IsRunningOrAwaitingAssignment(request, status))
                {
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                RequireAssigned(worker, status);
                var offset = Observed(
                    await CallAsync(request, ct => _connect.ReadOffsetEvidenceAsync(request, ct), token)
                );
                if (
                    offset.State
                    is CdcConnectOffsetState.Missing
                        or CdcConnectOffsetState.Snapshot
                        or CdcConnectOffsetState.AwaitingStreaming
                )
                {
                    Require(!offsetPreviouslySeen);
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                Require(offset.State == CdcConnectOffsetState.Streaming);
                var currentLive = Observed(
                    await CallAsync(request, ct => _connect.ReadConfigurationAsync(request, ct), token)
                );
                Require(
                    _templates
                        .ValidateLiveReadBack(
                            new(
                                handoff.TemplateRequest,
                                currentLive,
                                handoff.TemplateRequest.ProviderSetupEvidence,
                                new(offset.SourcePartition)
                            )
                        )
                        .Outcome == CdcConnectorTemplateOutcome.Rendered
                );
                // The existing parser owns partition hashing; cross-check its typed evidence against the actual partition.
                var partition = CdcSourcePartitionHashCalculator.Compute(
                    request.Binding.Provider,
                    offset.SourcePartition["server"],
                    request.Binding.Provider == CoreProvider.SqlServer
                        ? offset.SourcePartition["database"]
                        : null
                );
                Require(partition.Succeeded && partition.Hash == offset.SourcePartitionHash);
                boundary.Component = CdcDeploymentComponent.Worker;
                var afterWorker = Observed(
                    await CallAsync(request, ct => _worker.InspectAsync(request, ct), token)
                );
                RequireSameWorker(request, worker, afterWorker);
                boundary.Component = CdcDeploymentComponent.Connect;
                var afterStatusEvidence = await CallAsync(
                    request,
                    ct => _connect.ReadStatusAsync(request, ct),
                    token
                );
                if (
                    afterStatusEvidence is CdcTransportResult<CdcConnectStatus>.Absent
                    && !offsetPreviouslySeen
                )
                {
                    // The config store can acknowledge creation before the asynchronous status store
                    // publishes its first record. Absence never supplies readiness or permits recreation.
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                var afterStatus = Observed(afterStatusEvidence);
                if (!IsRunningOrAwaitingAssignment(request, afterStatus))
                {
                    // Assignment can change after the first RUNNING response. Discard the earlier
                    // offset/configuration observations and collect an entirely new startup pass.
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                RequireAssigned(afterWorker, afterStatus);
                boundary.Component = CdcDeploymentComponent.Kafka;
                await RequireKafkaAsync(request, currentLive, afterWorker, token);
                // Provider metadata is refreshed after potentially long RUNNING/offset waits, in ValidateOnly mode.
                boundary.Component = CdcDeploymentComponent.ProviderSetup;
                var finalHandoff = await CallAsync(
                    request,
                    ct =>
                        provider.SetupInSessionAsync(
                            request,
                            runtime,
                            session,
                            c => boundary.Component = c,
                            ct
                        ),
                    token
                );
                Require(finalHandoff.Template.ConfigSha256 == template.ConfigSha256);
                Require(
                    _templates
                        .ValidateLiveReadBack(
                            new(
                                finalHandoff.TemplateRequest,
                                currentLive,
                                finalHandoff.TemplateRequest.ProviderSetupEvidence,
                                new(offset.SourcePartition)
                            )
                        )
                        .Outcome == CdcConnectorTemplateOutcome.Rendered
                );
                RequireFresh(request, observationStartedAt);
                boundary.Component = CdcDeploymentComponent.Connect;
                var finalStatusEvidence = await CallAsync(
                    request,
                    ct => _connect.ReadStatusAsync(request, ct),
                    token
                );
                if (
                    finalStatusEvidence is CdcTransportResult<CdcConnectStatus>.Absent
                    && !offsetPreviouslySeen
                )
                {
                    // The config store can acknowledge creation before the asynchronous status store
                    // publishes its first record. Absence never supplies readiness or permits recreation.
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                var finalStatus = Observed(finalStatusEvidence);
                if (!IsRunningOrAwaitingAssignment(request, finalStatus))
                {
                    await Task.Delay(request.Timing.PollInterval, _time, token);
                    continue;
                }
                RequireAssigned(afterWorker, finalStatus);
                boundary.Component = CdcDeploymentComponent.Worker;
                var finalWorker = Observed(
                    await CallAsync(request, ct => _worker.InspectAsync(request, ct), token)
                );
                RequireSameWorker(request, worker, finalWorker);
                RequireFresh(request, observationStartedAt);
                boundary.Component = CdcDeploymentComponent.WorkflowState;
                var establishment = journal.Operations.Single(o =>
                    o.Effect == CdcWorkflowEffect.EstablishConnector
                );
                await CompleteAsync(
                    session,
                    journal,
                    establishment,
                    new CdcWorkflowCompletion.Connector(offset.SourcePartitionHash),
                    token
                );
                return new CdcTransportResult<CdcConnectorRegistrationReceipt>.Observed(
                    new(journal.WorkflowId, offset.SourcePartitionHash)
                );
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Failure(boundary.Component, CdcDeploymentFailure.Timeout);
        }
        catch (EvidenceException exception)
        {
            return new CdcTransportResult<CdcConnectorRegistrationReceipt>.Unavailable(exception.Diagnostic);
        }
        catch (CdcWorkflowStateException exception)
        {
            return Failure(
                boundary.Component,
                exception.Failure switch
                {
                    CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                    CdcWorkflowStateFailure.Missing or CdcWorkflowStateFailure.Unavailable =>
                        CdcDeploymentFailure.Unavailable,
                    _ => CdcDeploymentFailure.ValidationFailed,
                }
            );
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<CdcConnectorRegistrationReceipt>.Unavailable(
                CdcDeploymentDiagnostic.FromException(boundary.Component, exception)
            );
        }
    }

    private void ValidateConfiguration(
        CdcProviderSetupHandoff handoff,
        IReadOnlyDictionary<string, string> live
    ) =>
        Require(
            _templates
                .ValidateLiveConfigurationReadBack(
                    new(handoff.TemplateRequest, live, handoff.TemplateRequest.ProviderSetupEvidence)
                )
                .Outcome == CdcConnectorTemplateOutcome.Rendered
        );

    private async Task RequireKafkaAsync(
        CdcDeploymentRequest request,
        IReadOnlyDictionary<string, string> effective,
        CdcWorkerInspection worker,
        CancellationToken token
    )
    {
        // These values come from the validated worker preflight before creation and live config afterward.
        var producer = new EffectiveProducer(effective, worker);
        var evidence = await CdcKafkaProvisioning.InspectAsync(_kafka, producer, request, false, true, token);
        var now = _time.GetUtcNow();
        var operation = Guid.NewGuid().ToString("D");
        var offsets = CdcDeploymentKafkaPolicy.ObserveOffsetStore(request, operation, now, evidence);
        var binding = CdcDeploymentKafkaPolicy.ObserveBinding(request, operation, now, evidence);
        CdcWorkflowJournalValidation.Require(
            offsets.PolicyState == CdcConnectOffsetStorePolicyState.Satisfied
                && binding.PolicyState == CdcKafkaPolicyState.Satisfied,
            offsets.PolicyState == CdcConnectOffsetStorePolicyState.Unknown
            || binding.PolicyState == CdcKafkaPolicyState.Unknown
                ? CdcWorkflowStateFailure.Unavailable
                : CdcWorkflowStateFailure.Contradictory
        );
    }

    internal sealed class EffectiveProducer(
        IReadOnlyDictionary<string, string> configuration,
        CdcWorkerInspection worker
    ) : ICdcKafkaProducerInspection
    {
        public Task<CdcTransportResult<CdcKafkaProducerCapacityEvidence>> InspectAsync(
            CdcDeploymentRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CdcTransportResult<CdcKafkaProducerCapacityEvidence>>(
                new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed(
                    new(
                        long.Parse(
                            configuration["producer.override.max.request.size"],
                            CultureInfo.InvariantCulture
                        ),
                        long.Parse(
                            configuration["producer.override.buffer.memory"],
                            CultureInfo.InvariantCulture
                        ),
                        worker.HeapBytes
                    )
                )
            );
        }
    }

    private static Task<CdcWorkflowJournal> CompleteAsync(
        LocalCdcWorkflowJournalStore.Session session,
        CdcWorkflowJournal journal,
        CdcWorkflowOperation operation,
        CdcWorkflowCompletion evidence,
        CancellationToken token
    ) =>
        session.ReconcileCompletionAsync(
            journal.Target,
            journal.WorkflowId,
            operation.OperationId,
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(evidence)
                );
            },
            token
        );

    internal static void RequireWorker(CdcDeploymentRequest request, CdcWorkerInspection worker)
    {
        Require(
            !string.IsNullOrWhiteSpace(worker.ProcessIdentity)
                && !string.IsNullOrWhiteSpace(worker.ConnectWorkerId)
                && worker.MetricsEndpoint == request.WorkerMetricsEndpoint
                && CdcQualifiedWorkerImage.Digests.Contains(worker.ImageDigest)
                && worker.ImageDigest == request.WorkerPolicy.QualifiedImageDigest
                && worker.HeapBytes == request.WorkerPolicy.HeapBytes
                && worker.EffectiveConfiguration.GetValueOrDefault("bootstrap.servers")
                    == request.ConnectorPolicy.KafkaBootstrapServers
                && worker.EffectiveConfiguration.GetValueOrDefault("group.id")
                    == request.WorkerPolicy.WorkerKey.Value
                && worker.EffectiveConfiguration.GetValueOrDefault("offset.storage.topic")
                    == request.WorkerPolicy.OffsetStorageTopic.Value
                && worker.EffectiveConfiguration.GetValueOrDefault("connector.client.config.override.policy")
                    == "All"
        );
    }

    internal static void RequireSameWorker(
        CdcDeploymentRequest request,
        CdcWorkerInspection first,
        CdcWorkerInspection last
    )
    {
        RequireWorker(request, last);
        Require(
            first.ProcessIdentity == last.ProcessIdentity && first.ConnectWorkerId == last.ConnectWorkerId
        );
    }

    private bool IsRunningOrAwaitingAssignment(CdcDeploymentRequest request, CdcConnectStatus status)
    {
        RequireStatusIdentity(request, status);
        // A failed/stopped/paused task still requires the guarded lifecycle path. Only a valid
        // startup assignment transition can be awaited; it never establishes offset/readiness proof.
        Require(
            status.Runtime.ConnectorState
                is not (
                    CdcConnectorRuntimeState.Failed
                    or CdcConnectorRuntimeState.Stopped
                    or CdcConnectorRuntimeState.Paused
                )
                && status.Tasks.All(t =>
                    t.State
                        is not (
                            CdcConnectorRuntimeState.Failed
                            or CdcConnectorRuntimeState.Stopped
                            or CdcConnectorRuntimeState.Paused
                        )
                )
        );
        return status.IsRunning;
    }

    private void RequireStatusIdentity(CdcDeploymentRequest request, CdcConnectStatus status)
    {
        var context = new CdcObservationValidationContext(
            status.Runtime.OperationId,
            request.TargetIdentity,
            request.Binding.PhysicalSourceFingerprint,
            _time.GetUtcNow()
        );
        var validation = CdcConnectorRuntimeObservationValidator.ValidateForStartup(
            status.Runtime,
            request.Binding,
            context
        );
        Require(validation.Succeeded && status.Tasks.Count <= 1 && status.Tasks.All(t => t.Id == 0));
        RequireFresh(request, status.Runtime.ObservedAt);
    }

    internal static void RequireAssigned(CdcWorkerInspection worker, CdcConnectStatus status) =>
        Require(
            status.WorkerId == worker.ConnectWorkerId
                && status.Tasks[0].WorkerId == worker.ConnectWorkerId
                && status.Runtime.TaskCount == 1
                && status.Runtime.RunningTaskCount == 1
                && status.Runtime.SoleTaskState == CdcConnectorRuntimeState.Running
        );

    private void RequireFresh(CdcDeploymentRequest request, DateTimeOffset observedAt) =>
        Require(
            observedAt <= _time.GetUtcNow()
                && _time.GetUtcNow() - observedAt <= request.Timing.MaximumObservationAge
        );

    private static T Observed<T>(CdcTransportResult<T> result)
        where T : notnull =>
        result switch
        {
            CdcTransportResult<T>.Observed observed => observed.Value,
            CdcTransportResult<T>.Unavailable unavailable => throw new EvidenceException(
                unavailable.Diagnostic
            ),
            _ => throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory),
        };

    private static async Task<T> CallAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<T>> action,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timing.CallTimeout);
        try
        {
            return await action(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);

    private static CdcTransportResult<CdcConnectorRegistrationReceipt> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new CdcTransportResult<CdcConnectorRegistrationReceipt>.Unavailable(new(component, failure));

    private sealed class Boundary
    {
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside this controller; never escapes its API."
    )]
    private sealed class EvidenceException(CdcDeploymentDiagnostic diagnostic) : Exception
    {
        public CdcDeploymentDiagnostic Diagnostic { get; } = diagnostic;
    }
}
