// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>One completed offline handoff. Not a reusable readiness proof or a writer-start command.</summary>
public sealed record CdcWriterPublicationResult(Guid WorkflowId, DateTimeOffset AuthorizedAt);

/// <summary>
/// Owns and disposes the supplied temporary runtime, including on rejection and cancellation. The
/// caller keeps writers closed until success. The state-root lock covers the entire fresh admission
/// and shutdown. Any durable publication intent permanently closes the initial-enable route.
/// </summary>
public sealed class CdcInitialReadiness
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcProviderSetupService _provider;
    private readonly ICdcConnectorTemplateService _templates;
    private readonly ICdcKafkaAdminAdapter _kafka;
    private readonly ICdcConnectTransport _connect;
    private readonly ICdcWorkerInspectionTransport _worker;
    private readonly ICdcWorkerMetricsTransport _metrics;
    private readonly ICdcProviderSourcePositionAdapter _positions;
    private readonly TimeProvider _time;

    public CdcInitialReadiness(
        string stateRoot,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker,
        ICdcWorkerMetricsTransport metrics,
        ICdcProviderSourcePositionAdapter positions
    )
        : this(
            new(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            provider,
            templates,
            kafka,
            connect,
            worker,
            metrics,
            positions,
            TimeProvider.System
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
    }

    internal CdcInitialReadiness(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker,
        ICdcWorkerMetricsTransport metrics,
        ICdcProviderSourcePositionAdapter positions,
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
        _metrics = metrics;
        _positions = positions;
        _time = time;
    }

    public async Task<CdcTransportResult<CdcWriterPublicationResult>> PreparePublicationAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        long lagThresholdMilliseconds,
        CancellationToken cancellationToken = default,
        CancellationToken operationDeadline = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        var boundary = new Boundary();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationDeadline
        );
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var token = timeout.Token;
        bool disposed = false;
        try
        {
            try
            {
                await using var session = await _store.AcquireAsync(
                    request.Timing.CallTimeout,
                    request.Timing.PollInterval < request.Timing.CallTimeout
                        ? request.Timing.PollInterval
                        : request.Timing.CallTimeout,
                    token
                );
                try
                {
                    var journal = await session.ReadAsync(request.TargetIdentity, token);
                    Require(journal.Purpose == CdcWorkflowPurpose.InitialCdcProvisioning);
                    Require(_positions.Provider == request.Binding.Provider && lagThresholdMilliseconds >= 0);
                    Require(!journal.WriterPublicationAuthorized && !journal.HasPendingRecordSizeIncrease);
                    Require(
                        !journal.Operations.Any(o =>
                            o.Effect
                                is CdcWorkflowEffect.StopConnector
                                    or CdcWorkflowEffect.ResumeConnector
                                    or CdcWorkflowEffect.Retire
                                    or CdcWorkflowEffect.IncreaseRecordSize
                        )
                    );
                    foreach (
                        var effect in new[]
                        {
                            CdcWorkflowEffect.ReserveBinding,
                            CdcWorkflowEffect.ActivateProjection,
                            CdcWorkflowEffect.CreateProvider,
                            CdcWorkflowEffect.PrepareKafka,
                            CdcWorkflowEffect.RegisterConnector,
                            CdcWorkflowEffect.EstablishConnector,
                        }
                    )
                    {
                        Require(
                            journal.Operations.Count(o => o.Effect == effect) == 1
                                && journal.Operations.Single(o => o.Effect == effect).Completions.Length == 1
                        );
                    }
                    var established = (CdcWorkflowCompletion.Connector)
                        journal
                            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
                            .Completions[0]
                            .Evidence;
                    var history = await session.ReadSourcePublicationHistoryAsync(
                        request.TargetIdentity,
                        request.Binding.PhysicalSourceFingerprint,
                        token
                    );
                    Require(
                        history.WorkflowId == journal.WorkflowId
                            && history.CreationTarget == request.TargetIdentity
                            && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
                            && history.Transitions[^1].Status
                                == DocumentCacheDownstreamPublicationStatus.Active
                    );
                    var exact = await ExactAsync(request, token, cancellationToken);
                    string operation = Guid.NewGuid().ToString("D");
                    var proof = new InitialCdcProvisioningProof(
                        CdcJsonContract.CurrentContractVersion,
                        Guid.NewGuid().ToString("D"),
                        operation,
                        request.TargetIdentity,
                        request.Binding.Provider,
                        journal.WorkflowId.ToString("D"),
                        CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
                        CdcWriteAdmissionState.ClosedNeverOpened,
                        _time.GetUtcNow()
                    );
                    boundary.Component = CdcDeploymentComponent.Projection;
                    var initial = new CdcInitialEnablement(_store, _bindings, _time);
                    var eligibility = await CallAsync(
                        request,
                        ct =>
                            initial.ObserveAsync(
                                runtime,
                                request.Binding,
                                proof,
                                request.Timing.MaximumObservationAge,
                                ct
                            ),
                        token
                    );
                    Require(
                        initial
                            .Classify(request.Binding, proof, eligibility.Eligibility, exact)
                            .RetryClassification == CdcRetryClassification.ResumeProviderTopicConnectorSetup
                    );
                    await CallAsync(
                        request,
                        async ct =>
                        {
                            await runtime.StartProcessingAsync(ct);
                            return true;
                        },
                        token
                    );

                    while (true)
                    {
                        using var pass = new CdcTelemetryObservationPass(
                            request,
                            operation,
                            lagThresholdMilliseconds
                        );
                        boundary.Component = CdcDeploymentComponent.Worker;
                        var worker = Observed(
                            await CallAsync(request, ct => _worker.InspectAsync(request, ct), token)
                        );
                        CdcConnectorRegistration.RequireWorker(request, worker);
                        await RequireRunningAsync(request, worker, operation, boundary, token);
                        boundary.Component = CdcDeploymentComponent.Projection;
                        var first = await ProjectionAsync(request, runtime, operation, boundary, token);
                        if (!CaughtUp(first))
                        {
                            await DelayAsync(request, token);
                            continue;
                        }
                        boundary.Component = CdcDeploymentComponent.ProviderSetup;
                        var captured = await CallAsync(
                            request,
                            ct => runtime.CaptureBarrierAsync(request, _positions, ct),
                            token
                        );
                        Require(
                            captured.Succeeded
                                && captured.Provider == request.Binding.Provider
                                && captured.BarrierCapturedAt >= first.ProjectionObservedAt
                        );
                        CdcProviderBarrierObservation barrier = null!;
                        CdcProviderSetupHandoff provider;
                        CdcProviderSetupObservationMapping mapped;
                        CdcSourceHistoryClassificationResult continuity;
                        CdcConnectOffsetEvidence rawOffset;
                        CdcConnectorOffsetObservation offset;
                        while (true)
                        {
                            await RequireSameWorkerAsync(request, worker, boundary, token);
                            await RequireRunningAsync(request, worker, operation, boundary, token);
                            (provider, mapped, continuity, rawOffset, offset) = await InspectProviderAsync(
                                request,
                                runtime,
                                session,
                                operation,
                                c => boundary.Component = c,
                                token,
                                cancellationToken,
                                current =>
                                    barrier = _positions.ObserveProviderBarrier(
                                        new(
                                            operation,
                                            request.Binding,
                                            first.ProjectionObservedAt,
                                            captured,
                                            current,
                                            established.SourcePartitionHash
                                        )
                                    )
                            );
                            // Continuity is observed even while a valid offset is behind the barrier.
                            if (
                                barrier.BarrierState != CdcProviderBarrierState.NotReached
                                || continuity.Observation.Continuity != CdcSourceHistoryContinuity.Healthy
                            )
                            {
                                break;
                            }
                            Require(
                                CdcProviderBarrierObservationValidator
                                    .Validate(
                                        barrier,
                                        new(
                                            operation,
                                            request.TargetIdentity,
                                            request.Binding.PhysicalSourceFingerprint,
                                            _time.GetUtcNow()
                                        )
                                    )
                                    .Succeeded
                            );
                            await DelayAsync(request, token);
                        }
                        if (
                            request.Binding.Provider == CoreProvider.Postgresql
                            && continuity.Observation.Continuity == CdcSourceHistoryContinuity.Unknown
                        )
                        {
                            // Fresh provider and offset evidence on the next bounded admission pass.
                            await DelayAsync(request, token);
                            continue;
                        }
                        Require(continuity.Observation.Continuity == CdcSourceHistoryContinuity.Healthy);
                        Require(
                            rawOffset.State == CdcConnectOffsetState.Streaming
                                && rawOffset.SourcePartitionHash == established.SourcePartitionHash
                        );
                        Require(
                            barrier.BarrierState == CdcProviderBarrierState.Reached
                                && CdcProviderBarrierObservationValidator
                                    .Validate(
                                        barrier,
                                        new(
                                            operation,
                                            request.TargetIdentity,
                                            request.Binding.PhysicalSourceFingerprint,
                                            _time.GetUtcNow()
                                        )
                                    )
                                    .Succeeded
                        );
                        boundary.Component = CdcDeploymentComponent.Projection;
                        var second = await ProjectionAsync(request, runtime, operation, boundary, token);
                        if (!CaughtUp(second))
                        {
                            await DelayAsync(request, token);
                            continue;
                        }
                        boundary.Component = CdcDeploymentComponent.Connect;
                        var live = Observed(
                            await CallAsync(
                                request,
                                ct => _connect.ReadConfigurationAsync(request, ct),
                                token
                            )
                        );
                        Require(
                            _templates
                                .ValidateLiveReadBack(
                                    new(
                                        provider.TemplateRequest,
                                        live,
                                        provider.TemplateRequest.ProviderSetupEvidence,
                                        new(rawOffset.SourcePartition)
                                    )
                                )
                                .Outcome == CdcConnectorTemplateOutcome.Rendered
                        );
                        var registration = journal.Operations.Single(o =>
                            o.Effect == CdcWorkflowEffect.RegisterConnector
                        );
                        Require(
                            registration.ConnectorRegistration.Length == 1
                                && registration.ConnectorRegistration[0].ConfigSha256
                                    == provider.Template.ConfigSha256
                        );
                        boundary.Component = CdcDeploymentComponent.Kafka;
                        var policy = await CallAsync(
                            request,
                            ct =>
                                CdcKafkaProvisioning.InspectAsync(
                                    _kafka,
                                    new CdcConnectorRegistration.EffectiveProducer(live, worker),
                                    request,
                                    false,
                                    true,
                                    ct
                                ),
                            token
                        );
                        var policyAt = _time.GetUtcNow();
                        var kafka = CdcDeploymentKafkaPolicy.ObserveBinding(
                            request,
                            operation,
                            policyAt,
                            policy
                        );
                        var store = CdcDeploymentKafkaPolicy.ObserveOffsetStore(
                            request,
                            operation,
                            policyAt,
                            policy
                        );
                        boundary.Component = CdcDeploymentComponent.Metrics;
                        var telemetry = Observed(
                            await CallAsync(request, ct => _metrics.CollectAsync(request, pass, ct), token)
                        );
                        var status = await RequireRunningAsync(request, worker, operation, boundary, token);
                        await RequireSameWorkerAsync(request, worker, boundary, token);
                        boundary.Component = CdcDeploymentComponent.WorkflowState;
                        exact = await ExactAsync(request, token, cancellationToken);
                        var now = _time.GetUtcNow();
                        var input = new CdcInitialAdmissionEvaluationInput(
                            operation,
                            now,
                            now,
                            request.TargetIdentity,
                            request.Binding.PhysicalSourceFingerprint,
                            proof,
                            eligibility.Eligibility,
                            exact.State
                        )
                        {
                            FirstProjectionCaughtUp = first,
                            ProviderBarrier = barrier,
                            SourceHistory = continuity.Observation,
                            SecondProjectionCaughtUp = second,
                            ProviderSetup = mapped.ProviderSetup,
                            KafkaPolicy = kafka,
                            ConnectOffsetStore = store,
                            // REST status has no snapshot-completion field. The exact committed
                            // streaming offset that crossed this pass's provider barrier supplies
                            // that evidence; RUNNING status alone never does.
                            ConnectorRuntime = status with
                            {
                                SnapshotState =
                                    status.SnapshotState is CdcConnectorSnapshotState.Unknown
                                        ? CdcConnectorSnapshotState.Completed
                                        : status.SnapshotState,
                            },
                            ConnectorConfig = CdcControllerObservations.Configuration(
                                request,
                                operation,
                                now
                            ),
                            Lag = telemetry.ReadForEvaluation(pass),
                        };
                        // Old first observations/barriers are never reusable. Restart the entire sequence when
                        // this pass outlives its observation budget, including telemetry expiry at handoff.
                        if (
                            !Fresh(request, first.ObservedAt)
                            || !Fresh(request, provider.ObservedAt)
                            || input.Lag.LagState == CdcConnectorLagState.Unknown
                        )
                        {
                            await DelayAsync(request, token);
                            continue;
                        }
                        RequireAdmitted(input);
                        boundary.Component = CdcDeploymentComponent.Projection;
                        disposed = true;
                        await runtime.DisposeAsync();
                        await RequireSameWorkerAsync(request, worker, boundary, token);
                        await RequireRunningAsync(request, worker, operation, boundary, token);
                        // Shutdown is part of the handoff: slow/failed disposal must never publish stale readiness.
                        token.ThrowIfCancellationRequested();
                        now = _time.GetUtcNow();
                        boundary.Component = CdcDeploymentComponent.WriterPublication;
                        Require(Fresh(request, first.ObservedAt) && Fresh(request, provider.ObservedAt));
                        Require(
                            CdcInitialAdmissionEvaluator
                                .Evaluate(
                                    input with
                                    {
                                        ObservedAt = now,
                                        NowUtc = now,
                                        Lag = telemetry.ReadForEvaluation(pass),
                                    }
                                )
                                .AdmissionState == CdcAdmissionState.Admitted
                        );
                        journal = await session.RecordIntentAsync(
                            request.TargetIdentity,
                            journal.WorkflowId,
                            Guid.NewGuid(),
                            CdcWorkflowEffect.AuthorizeWriterPublication,
                            [],
                            token
                        );
                        // A lost response after the atomic intent already forbids initial retry. No ready bit
                        // or barrier is persisted, and an unfinished intent cannot be replayed as authorization.
                        token.ThrowIfCancellationRequested();
                        Require(
                            Fresh(request, first.ObservedAt)
                                && CdcInitialAdmissionEvaluator
                                    .Evaluate(
                                        input with
                                        {
                                            ObservedAt = _time.GetUtcNow(),
                                            NowUtc = _time.GetUtcNow(),
                                            Lag = telemetry.ReadForEvaluation(pass),
                                        }
                                    )
                                    .AdmissionState == CdcAdmissionState.Admitted
                        );
                        return new CdcTransportResult<CdcWriterPublicationResult>.Observed(
                            new(journal.WorkflowId, _time.GetUtcNow())
                        );
                    }
                }
                finally
                {
                    if (!disposed)
                    {
                        disposed = true;
                        await runtime.DisposeAsync();
                    }
                }
            }
            finally
            {
                if (!disposed)
                {
                    boundary.Component = CdcDeploymentComponent.Projection;
                    await runtime.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Failure(boundary.Component, CdcDeploymentFailure.Timeout);
        }
        catch (EvidenceException exception)
        {
            return CdcTransportResult<CdcWriterPublicationResult>.Unavailable.FromDiagnostics(
                exception.Diagnostics
            );
        }
        catch (CdcWorkflowStateException exception)
        {
            return Failure(
                boundary.Component,
                exception.Failure switch
                {
                    CdcWorkflowStateFailure.Missing or CdcWorkflowStateFailure.Unavailable =>
                        CdcDeploymentFailure.Unavailable,
                    CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                    _ => CdcDeploymentFailure.ValidationFailed,
                }
            );
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CdcTransportResult<CdcWriterPublicationResult>.Unavailable(
                CdcDeploymentDiagnostic.FromException(boundary.Component, exception)
            );
        }
    }

    private static void RequireAdmitted(CdcInitialAdmissionEvaluationInput input)
    {
        var admission = CdcInitialAdmissionEvaluator.Evaluate(input);
        if (admission.AdmissionState == CdcAdmissionState.Admitted)
        {
            return;
        }
        var steps = admission.Steps;
        (CdcComponent Evidence, CdcDeploymentComponent Component)[] components =
        [
            (steps.Binding, CdcDeploymentComponent.WorkflowState),
            (steps.GuardedTrackingActivation, CdcDeploymentComponent.Projection),
            (steps.ProviderSetup, CdcDeploymentComponent.ProviderSetup),
            (steps.ConnectorAndTopicValidation, CdcDeploymentComponent.Kafka),
            (steps.FirstProjectionCaughtUp, CdcDeploymentComponent.Projection),
            (steps.ProviderBarrier, CdcDeploymentComponent.ProviderSetup),
            (steps.SourceHistory, CdcDeploymentComponent.ProviderSetup),
            (steps.SecondProjectionCaughtUp, CdcDeploymentComponent.Projection),
            (steps.Lag, CdcDeploymentComponent.Metrics),
        ];
        var failed = Array.Find(components, c => c.Evidence.State != CdcComponentState.Satisfied);
        throw new EvidenceException([
            new(
                failed.Evidence is null ? CdcDeploymentComponent.WriterPublication : failed.Component,
                admission.AdmissionState == CdcAdmissionState.Unknown
                    ? CdcDeploymentFailure.Unavailable
                    : CdcDeploymentFailure.ValidationFailed
            ),
        ]);
    }

    private async Task<CdcBindingLifecycleResult> ExactAsync(
        CdcDeploymentRequest request,
        CancellationToken token,
        CancellationToken caller
    )
    {
        var exact = await CallAsync(
            request,
            ct => _bindings.ExactMatchBindingAsync(request.Binding, ct),
            token
        );
        Require(
            exact.Status == CdcControlPlaneOperationStatus.Succeeded
                && exact.State?.State is CdcBindingState.BindingPresent or CdcBindingState.IncidentLatched
        );
        if (exact.State!.Incident is not null)
        {
            // Retained loss is authoritative without projection or provider inputs. A failed earlier
            // stop or native recovery still requires shutdown; healthy samples cannot clear this latch.
            var retained = CdcSourceHistoryContinuityClassifier.Evaluate(
                new(Guid.NewGuid().ToString("D"), _time.GetUtcNow(), _time.GetUtcNow(), request.Binding)
                {
                    LatchedIncident = exact.State.Incident,
                }
            );
            if (retained.Observation.Continuity == CdcSourceHistoryContinuity.Lost)
            {
                await ContainLossAsync(request, retained, exact.State, caller);
            }
        }
        Require(exact.State.State == CdcBindingState.BindingPresent);
        return exact;
    }

    internal async Task<(
        CdcProviderSetupHandoff Provider,
        CdcProviderSetupObservationMapping Mapped,
        CdcSourceHistoryClassificationResult Continuity,
        CdcConnectOffsetEvidence RawOffset,
        CdcConnectorOffsetObservation Offset
    )> InspectProviderAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        LocalCdcWorkflowJournalStore.Session session,
        string operation,
        Action<CdcDeploymentComponent> setComponent,
        CancellationToken token,
        CancellationToken caller,
        Action<CdcConnectorOffsetObservation>? observeBarrier = null
    )
    {
        var journal = await session.ReadAsync(request.TargetIdentity, token);
        var established = (CdcWorkflowCompletion.Connector)
            journal
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
                .Completions.Single()
                .Evidence;
        var retained = (CdcWorkflowCompletion.Provider)
            journal
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider)
                .Completions.Single()
                .Evidence;
        var exact = await ExactAsync(request, token, caller);
        CdcProviderSetupObservationMapping mapped = null!;
        CdcSourceHistoryClassificationResult continuity = null!;
        CdcConnectOffsetEvidence rawOffset = null!;
        CdcConnectorOffsetObservation offset = null!;
        var provider = await new CdcProviderSetupOrchestration(
            _store,
            _bindings,
            _provider,
            _templates,
            _time
        ).SetupInSessionAsync(
            request,
            runtime,
            session,
            setComponent,
            token,
            observeProjection: false,
            observeProvider: async result =>
            {
                mapped = CdcProviderSetupOrchestration.MapRetainedProvider(
                    request,
                    result,
                    retained,
                    operation,
                    _time.GetUtcNow()
                );
                var schemaHistory = new CdcSqlServerSchemaHistoryEvidence(
                    CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
                    CdcSqlServerSchemaHistoryState.NotApplicable
                );
                async Task<CdcSourceHistoryClassificationResult> ClassifyAsync(
                    CdcConnectorOffsetObservation? current
                )
                {
                    setComponent(CdcDeploymentComponent.ProviderSetup);
                    var classification = await CallAsync(
                        request,
                        ct =>
                            _positions.ObserveSourceHistoryAsync(
                                new(
                                    operation,
                                    request.Binding,
                                    mapped.ProviderSetup,
                                    current,
                                    mapped.ProviderHistory
                                )
                                {
                                    ExpectedConnectSourcePartitionHash = established.SourcePartitionHash,
                                    SqlServerSchemaHistory =
                                        request.Binding.Provider == CoreProvider.SqlServer
                                            ? schemaHistory
                                            : null,
                                },
                                ct
                            ),
                        token
                    );
                    if (classification.Observation.Continuity == CdcSourceHistoryContinuity.Lost)
                    {
                        // Consume this exact classification under the held session. No full-status resample.
                        await ContainLossAsync(request, classification, exact.State!, caller);
                    }
                    return classification;
                }
                async Task ReadOffsetAsync()
                {
                    setComponent(CdcDeploymentComponent.Connect);
                    rawOffset = Observed(
                        await CallAsync(request, ct => _connect.ReadOffsetEvidenceAsync(request, ct), token)
                    );
                    offset = CdcControllerObservations.Offset(
                        request,
                        operation,
                        rawOffset,
                        established.SourcePartitionHash,
                        _time.GetUtcNow()
                    );
                    observeBarrier?.Invoke(offset);
                }
                // Missing/recreated provider artifacts must survive even an unavailable offset endpoint.
                await ClassifyAsync(null);
                await ReadOffsetAsync();
                continuity = await ClassifyAsync(offset);
                if (request.Binding.Provider == CoreProvider.SqlServer)
                {
                    setComponent(CdcDeploymentComponent.Kafka);
                    schemaHistory = schemaHistory with
                    {
                        State = Observed(
                            await CallAsync(
                                request,
                                ct => _kafka.InspectSchemaHistoryAsync(request, ct),
                                token
                            )
                        ),
                    };
                    continuity = await ClassifyAsync(offset);
                }
                if (
                    request.Binding.Provider == CoreProvider.Postgresql
                    && continuity.Observation.Continuity == CdcSourceHistoryContinuity.Unknown
                )
                {
                    // Preserve the fresh Connect read against this same slot observation.
                    await ReadOffsetAsync();
                    continuity = await ClassifyAsync(offset);
                }
            }
        );
        return (provider, mapped, continuity, rawOffset, offset);
    }

    private async Task ContainLossAsync(
        CdcDeploymentRequest request,
        CdcSourceHistoryClassificationResult classification,
        CdcBindingStateContract bindingState,
        CancellationToken caller
    )
    {
        var terminal = new CdcSourceHistoryContainment(_bindings, _connect, _time);
        List<CdcDeploymentDiagnostic> diagnostics = [];
        await terminal.PersistAsync(request, classification, bindingState, diagnostics, _ => { }, caller);
        await terminal.StopAsync(request, diagnostics, _ => { }, caller);
        throw new EvidenceException(
            diagnostics.Count > 0
                ? diagnostics
                : [new(CdcDeploymentComponent.ProviderSetup, CdcDeploymentFailure.ValidationFailed)]
        );
    }

    private async Task<CdcProjectionCorrelationObservation> ProjectionAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        string operation,
        Boundary boundary,
        CancellationToken token
    )
    {
        boundary.Component = CdcDeploymentComponent.Projection;
        var started = _time.GetUtcNow();
        var response = await CallAsync(request, ct => runtime.ObserveAsync(ct), token);
        var observation = CdcControllerObservations.Projection(
            request,
            response,
            operation,
            started,
            _time.GetUtcNow()
        );
        Require(observation.OperationalHealthStatus == DocumentCacheOperationalHealthStatus.Operational);
        return observation;
    }

    private async Task<CdcConnectorRuntimeObservation> RequireRunningAsync(
        CdcDeploymentRequest request,
        CdcWorkerInspection worker,
        string operation,
        Boundary boundary,
        CancellationToken token
    )
    {
        boundary.Component = CdcDeploymentComponent.Connect;
        var status = Observed(await CallAsync(request, ct => _connect.ReadStatusAsync(request, ct), token));
        Require(status.IsRunning && Fresh(request, status.Runtime.ObservedAt));
        Require(
            CdcConnectorRuntimeObservationValidator
                .ValidateForBinding(
                    status.Runtime,
                    request.Binding,
                    new(
                        status.Runtime.OperationId,
                        request.TargetIdentity,
                        request.Binding.PhysicalSourceFingerprint,
                        _time.GetUtcNow()
                    )
                )
                .Succeeded
        );
        CdcConnectorRegistration.RequireAssigned(worker, status);
        return status.Runtime with { OperationId = operation };
    }

    private async Task RequireSameWorkerAsync(
        CdcDeploymentRequest request,
        CdcWorkerInspection worker,
        Boundary boundary,
        CancellationToken token
    )
    {
        boundary.Component = CdcDeploymentComponent.Worker;
        CdcConnectorRegistration.RequireSameWorker(
            request,
            worker,
            Observed(await CallAsync(request, ct => _worker.InspectAsync(request, ct), token))
        );
    }

    private bool Fresh(CdcDeploymentRequest request, DateTimeOffset at) =>
        at <= _time.GetUtcNow() && _time.GetUtcNow() - at <= request.Timing.MaximumObservationAge;

    private static bool CaughtUp(CdcProjectionCorrelationObservation observation) =>
        observation.CaughtUpStatus == DocumentCacheCaughtUpStatus.CaughtUp;

    private Task DelayAsync(CdcDeploymentRequest request, CancellationToken token) =>
        Task.Delay(request.Timing.PollInterval, _time, token);

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
            var result = await action(timeout.Token).WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static T Observed<T>(CdcTransportResult<T> result)
        where T : notnull =>
        result switch
        {
            CdcTransportResult<T>.Observed value => value.Value,
            CdcTransportResult<T>.Unavailable unavailable => throw new EvidenceException(
                unavailable.Diagnostics
            ),
            _ => throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory),
        };

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);

    private static CdcTransportResult<CdcWriterPublicationResult> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new CdcTransportResult<CdcWriterPublicationResult>.Unavailable(new(component, failure));

    private sealed class Boundary
    {
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside the controller."
    )]
    internal sealed class EvidenceException(IReadOnlyList<CdcDeploymentDiagnostic> diagnostics) : Exception
    {
        public IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics { get; } = diagnostics;
    }
}
