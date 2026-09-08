// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using DdlMode = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupMode;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcEstablishedValidationMode
{
    PreStart,
    RunningPublication,
}

/// <summary>Negative deployment reports, not an attestation or an override for missing provenance.</summary>
public enum CdcDeploymentIntegrityReport
{
    NoReportedLoss,
    IncidentHistoryDeletion,
    StateRollback,
}

/// <summary>
/// A single observational pass, never reusable authorization or a new exact baseline. A lifecycle
/// controller must evaluate under its own retained session immediately before acting. Raw evidence
/// stays in memory; status/watch owns incident persistence and containment.
/// </summary>
public sealed record CdcEstablishedValidationObservation(
    DateTimeOffset ObservedAt,
    bool PreStartEligible,
    bool PublicationReady,
    CdcSourceHistoryContinuity Continuity,
    bool HasPendingRecordSizeIncrease,
    IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics
)
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> LiveConfiguration { get; init; } = null!;

    public CdcRecoveryObservation Recovery { get; init; } = new(CdcRecoveryBoundary.Unobserved, false);

    public override string ToString() => nameof(CdcEstablishedValidationObservation);

    [JsonIgnore]
    public CdcTargetStatusEvaluationInput Evidence { get; init; } = null!;

    [JsonIgnore]
    public CdcSourceHistoryClassificationResult SourceHistory { get; init; } = null!;

    [JsonIgnore]
    public CdcConnectStatus Connector { get; init; } = null!;

    [JsonIgnore]
    public CdcWorkerInspection Worker { get; init; } = null!;
}

/// <summary>
/// Reads intact established provenance and fresh external evidence without reconciling receipts,
/// capturing a write barrier, starting projection, repairing artifacts, or changing connector state.
/// The supplied runtime remains caller-owned. Local locking is the only coordination effect.
/// </summary>
public sealed partial class CdcEstablishedValidation
{
    private readonly CdcNativeRecoveryObserver _recovery = new();
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

    public CdcEstablishedValidation(
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

    internal CdcEstablishedValidation(
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

    public async Task<CdcTransportResult<CdcEstablishedValidationObservation>> ValidateAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        CdcEstablishedValidationMode mode,
        long lagThresholdMilliseconds,
        CdcDeploymentIntegrityReport integrity = CdcDeploymentIntegrityReport.NoReportedLoss,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        var boundary = new Boundary();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        try
        {
            Require(integrity == CdcDeploymentIntegrityReport.NoReportedLoss);
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                timeout.Token
            );
            return new CdcTransportResult<CdcEstablishedValidationObservation>.Observed(
                await ObserveInSessionAsync(
                    request,
                    runtime,
                    mode,
                    lagThresholdMilliseconds,
                    integrity,
                    session,
                    c => boundary.Component = c,
                    timeout.Token
                )
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Failure(boundary.Component, CdcDeploymentFailure.Timeout);
        }
        catch (EvidenceException exception)
        {
            return new CdcTransportResult<CdcEstablishedValidationObservation>.Unavailable(
                exception.Diagnostic
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
            return new CdcTransportResult<CdcEstablishedValidationObservation>.Unavailable(
                CdcDeploymentDiagnostic.FromException(boundary.Component, exception)
            );
        }
    }

    // Later lifecycle/status controllers retain this session; no nested acquisition or durable writes.
    internal async Task<CdcEstablishedValidationObservation> ObserveInSessionAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        CdcEstablishedValidationMode mode,
        long lagThresholdMilliseconds,
        CdcDeploymentIntegrityReport integrity,
        LocalCdcWorkflowJournalStore.Session session,
        Action<CdcDeploymentComponent> setComponent,
        CancellationToken token,
        CdcEstablishedStatusProgress progress = null!,
        CdcNativeRecoveryObserver recovery = null!,
        Guid managedResumeId = default,
        CdcRecordSizeRollout rollout = null!
    )
    {
        recovery ??= _recovery;
        setComponent(CdcDeploymentComponent.WorkflowState);
        Require(
            Enum.IsDefined(mode)
                && integrity == CdcDeploymentIntegrityReport.NoReportedLoss
                && lagThresholdMilliseconds >= 0
                && _positions.Provider == request.Binding.Provider
        );
        var started = _time.GetUtcNow();
        var journal = await session.ReadAsync(request.TargetIdentity, token);
        Require(!journal.RetirementIntended);
        rollout?.Invocation.RequireActive(session, journal);
        var history = await session.ReadSourcePublicationHistoryAsync(
            request.TargetIdentity,
            request.Binding.PhysicalSourceFingerprint,
            token
        );
        ValidateProvenance(request, journal, history);
        var exact = await CallAsync(
            request,
            ct => _bindings.ExactMatchBindingAsync(request.Binding, ct),
            token
        );
        Require(
            exact.Status == CdcControlPlaneOperationStatus.Succeeded
                && exact.State?.State is CdcBindingState.BindingPresent or CdcBindingState.IncidentLatched
        );
        var retained = (CdcWorkflowCompletion.Provider)
            Completed(journal, CdcWorkflowEffect.CreateProvider).Evidence;
        var establishment = (CdcWorkflowCompletion.Connector)
            Completed(journal, CdcWorkflowEffect.EstablishConnector).Evidence;
        if (progress is null)
        {
            setComponent(CdcDeploymentComponent.Projection);
            await RequireEligibleProjectionAsync(request, runtime, started, token);
        }
        var operation = Guid.NewGuid().ToString("D");
        using var pass = new CdcTelemetryObservationPass(request, operation, lagThresholdMilliseconds);
        List<CdcDeploymentDiagnostic> diagnostics = [];

        var partialInput = new CdcTargetStatusEvaluationInput(
            operation,
            _time.GetUtcNow(),
            request.TargetIdentity,
            request.Binding.PhysicalSourceFingerprint
        )
        {
            BindingState = exact.State,
        };
        progress?.Capture(partialInput, journal.HasPendingRecordSizeIncrease);
        setComponent(CdcDeploymentComponent.ProviderSetup);
        var setup = CdcProviderSetupOrchestration.CopyRequest(
            request.ProviderSetup,
            DdlMode.ValidateOnly,
            retained.InitialSlotProofs.SingleOrDefault(),
            false
        );
        var provider = await CallAsync(request, ct => _provider.SetupAsync(setup, ct), token);
        var mapped = CdcProviderSetupResultMapper.MapValidateOnlyResult(
            operation,
            _time.GetUtcNow(),
            request.Binding,
            provider
        );
        // Keep authoritative loss available to the classifier even when setup is not successful.
        // Same-name artifact recreation cannot hide behind currently healthy retained ranges.
        if (
            provider.Outcome == Ddl.CdcProviderSetupOutcome.ExactMatch
            && mapped.ProviderSetup.PhysicalSourceFingerprint == request.Binding.PhysicalSourceFingerprint
        )
        {
            var identities = CdcProviderSetupOrchestration.Identities(request, provider);
            if (!identities.SequenceEqual(retained.Artifacts))
            {
                mapped = mapped with
                {
                    ProviderHistory = mapped.ProviderHistory with
                    {
                        ProviderArtifactState = CdcProviderArtifactContinuityState.Recreated,
                    },
                };
            }
        }

        partialInput = partialInput with { ProviderSetup = mapped.ProviderSetup };
        if (progress is not null)
        {
            // Classify already available provider loss before an offset/history endpoint can fail.
            var early = await CallAsync(
                request,
                ct =>
                    _positions.ObserveSourceHistoryAsync(
                        new(operation, request.Binding, mapped.ProviderSetup, null, mapped.ProviderHistory)
                        {
                            ExpectedConnectSourcePartitionHash = establishment.SourcePartitionHash,
                            LatchedIncident = exact.State!.Incident,
                        },
                        ct
                    ),
                token
            );
            partialInput = partialInput with
            {
                ObservedAt = _time.GetUtcNow(),
                SourceHistory = early.Observation,
            };
            progress.Capture(partialInput, journal.HasPendingRecordSizeIncrease, early);
            await progress.ContainTerminal();
        }
        setComponent(CdcDeploymentComponent.Connect);
        var rawOffset = await ReadAsync(
            request,
            ct => _connect.ReadOffsetEvidenceAsync(request, ct),
            CdcDeploymentComponent.Connect,
            token
        );
        // A REST endpoint failure/absence is unknown; only successful offset evidence proves loss.
        var offset = rawOffset is CdcTransportResult<CdcConnectOffsetEvidence>.Observed observed
            ? Offset(request, operation, observed.Value, establishment.SourcePartitionHash)
            : null;
        if (rawOffset is CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable unavailable)
        {
            diagnostics.Add(unavailable.Diagnostic);
        }
        if (progress is not null)
        {
            var offsetHistory = await CallAsync(
                request,
                ct =>
                    _positions.ObserveSourceHistoryAsync(
                        new(operation, request.Binding, mapped.ProviderSetup, offset, mapped.ProviderHistory)
                        {
                            ExpectedConnectSourcePartitionHash = establishment.SourcePartitionHash,
                            LatchedIncident = exact.State!.Incident,
                        },
                        ct
                    ),
                token
            );
            partialInput = partialInput with
            {
                ObservedAt = _time.GetUtcNow(),
                SourceHistory = offsetHistory.Observation,
            };
            progress.Capture(partialInput, journal.HasPendingRecordSizeIncrease, offsetHistory);
            await progress.ContainTerminal();
        }
        setComponent(CdcDeploymentComponent.Kafka);
        var schemaHistory = CdcSqlServerSchemaHistoryState.NotApplicable;
        if (request.Binding.Provider == Core.DocumentCache.Cdc.CdcProvider.SqlServer)
        {
            var rawHistory = await ReadAsync(
                request,
                ct => _kafka.InspectSchemaHistoryAsync(request, ct),
                CdcDeploymentComponent.Kafka,
                token
            );
            if (
                rawHistory
                is CdcTransportResult<CdcSqlServerSchemaHistoryState>.Unavailable historyUnavailable
            )
            {
                diagnostics.Add(historyUnavailable.Diagnostic);
            }
            schemaHistory = rawHistory switch
            {
                CdcTransportResult<CdcSqlServerSchemaHistoryState>.Observed value => value.Value,
                CdcTransportResult<CdcSqlServerSchemaHistoryState>.Absent =>
                    CdcSqlServerSchemaHistoryState.Missing,
                _ => CdcSqlServerSchemaHistoryState.Unknown,
            };
        }
        setComponent(CdcDeploymentComponent.ProviderSetup);
        var continuity = await CallAsync(
            request,
            ct =>
                _positions.ObserveSourceHistoryAsync(
                    new(operation, request.Binding, mapped.ProviderSetup, offset, mapped.ProviderHistory)
                    {
                        ExpectedConnectSourcePartitionHash = establishment.SourcePartitionHash,
                        LatchedIncident = exact.State!.Incident,
                        SqlServerSchemaHistory =
                            request.Binding.Provider == Core.DocumentCache.Cdc.CdcProvider.SqlServer
                                ? new(
                                    CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                                    schemaHistory
                                )
                                : null,
                    },
                    ct
                ),
            token
        );

        partialInput = partialInput with
        {
            ObservedAt = _time.GetUtcNow(),
            SourceHistory = continuity.Observation,
        };
        progress?.Capture(partialInput, journal.HasPendingRecordSizeIncrease, continuity);
        if (progress is not null)
        {
            await progress.ContainTerminal();
        }
        var downstream = new Boundary();
        var forwardComponent = setComponent;
        setComponent = component =>
        {
            downstream.Component = component;
            forwardComponent(component);
        };
        try
        {
            if (progress is not null)
            {
                setComponent(CdcDeploymentComponent.Projection);
                await RequireEligibleProjectionAsync(request, runtime, started, token);
            }
            setComponent(CdcDeploymentComponent.Worker);
            var worker = Observed(
                await CallAsync(request, ct => _worker.InspectAsync(request, ct), token),
                CdcDeploymentComponent.Worker
            );
            CdcConnectorRegistration.RequireWorker(request, worker);
            setComponent(CdcDeploymentComponent.Connect);
            var status = Observed(
                await CallAsync(request, ct => _connect.ReadStatusAsync(request, ct), token)
            );
            var recoveryObservation = recovery.Observe(
                request,
                journal,
                worker,
                status,
                pass,
                managedResumeId
            );
            if (progress is not null)
            {
                progress.Recovery = recoveryObservation;
            }
            RequireStatus(request, worker, status);
            partialInput = partialInput with
            {
                ObservedAt = _time.GetUtcNow(),
                ConnectorRuntime = status.Runtime with { OperationId = operation },
            };
            progress?.Capture(partialInput, journal.HasPendingRecordSizeIncrease, continuity);
            setComponent(CdcDeploymentComponent.Connect);
            var live = Observed(
                await CallAsync(request, ct => _connect.ReadConfigurationAsync(request, ct), token)
            );
            var templateRequest = request.CreateTemplateRequest(new(request.Binding.Generation, provider));
            bool configurationMatches =
                rawOffset is CdcTransportResult<CdcConnectOffsetEvidence>.Observed currentOffset
                && _templates
                    .ValidateLiveReadBack(
                        new(
                            templateRequest,
                            live,
                            templateRequest.ProviderSetupEvidence,
                            new(currentOffset.Value.SourcePartition)
                        )
                    )
                    .Outcome == CdcConnectorTemplateOutcome.Rendered;
            var configuration = CdcControllerObservations.Configuration(
                request,
                operation,
                _time.GetUtcNow()
            );
            if (!configurationMatches)
            {
                diagnostics.Add(new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.ValidationFailed));
                configuration = configuration with
                {
                    ConfigurationState = CdcConnectorConfigurationState.Invalid,
                };
            }
            setComponent(CdcDeploymentComponent.Kafka);
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
            var input = new CdcTargetStatusEvaluationInput(
                operation,
                policyAt,
                request.TargetIdentity,
                request.Binding.PhysicalSourceFingerprint
            )
            {
                BindingState = exact.State,
                ProviderSetup = mapped.ProviderSetup,
                SourceHistory = continuity.Observation,
                ConnectorConfig = configuration,
                ConnectorRuntime = status.Runtime with { OperationId = operation },
                KafkaPolicy = rollout is null
                    ? CdcDeploymentKafkaPolicy.ObserveBinding(request, operation, policyAt, policy)
                    : rollout.ObservePolicy(request, operation, policyAt, policy),
                ConnectOffsetStore = CdcDeploymentKafkaPolicy.ObserveOffsetStore(
                    request,
                    operation,
                    policyAt,
                    policy
                ),
            };
            progress?.Capture(input, journal.HasPendingRecordSizeIncrease, continuity);
            if (mode == CdcEstablishedValidationMode.RunningPublication)
            {
                setComponent(CdcDeploymentComponent.Projection);
                input = input with { Projection = await ProjectionAsync(request, runtime, operation, token) };
            }
            progress?.Capture(input, journal.HasPendingRecordSizeIncrease, continuity);
            // Pre-start must work without RUNNING task metrics. Publication always uses a new receipt.
            CdcConnectorTelemetryObservation telemetry = null!;
            if (mode == CdcEstablishedValidationMode.RunningPublication && status.IsRunning && pass.IsValid)
            {
                setComponent(CdcDeploymentComponent.Metrics);
                var collected = await CallAsync(
                    request,
                    ct => _metrics.CollectAsync(request, pass, ct),
                    token
                );
                if (collected is CdcTransportResult<CdcConnectorTelemetryObservation>.Observed sample)
                {
                    telemetry = sample.Value;
                }
                else if (collected is CdcTransportResult<CdcConnectorTelemetryObservation>.Unavailable failed)
                {
                    diagnostics.Add(failed.Diagnostic);
                }
            }
            setComponent(CdcDeploymentComponent.Worker);
            var finalWorker = Observed(
                await CallAsync(request, ct => _worker.InspectAsync(request, ct), token),
                CdcDeploymentComponent.Worker
            );
            setComponent(CdcDeploymentComponent.Connect);
            var finalStatus = Observed(
                await CallAsync(request, ct => _connect.ReadStatusAsync(request, ct), token)
            );
            recoveryObservation = recovery.Observe(
                request,
                journal,
                finalWorker,
                finalStatus,
                pass,
                managedResumeId
            );
            if (progress is not null)
            {
                progress.Recovery = recoveryObservation;
            }
            setComponent(CdcDeploymentComponent.Worker);
            CdcConnectorRegistration.RequireSameWorker(request, worker, finalWorker);
            setComponent(CdcDeploymentComponent.Connect);
            RequireStatus(request, worker, finalStatus);
            Require(
                status.Runtime.ConnectorState == finalStatus.Runtime.ConnectorState
                    && status.Runtime.SoleTaskState == finalStatus.Runtime.SoleTaskState
                    && status.Tasks.Count == finalStatus.Tasks.Count
            );
            setComponent(CdcDeploymentComponent.WorkflowState);
            token.ThrowIfCancellationRequested();
            Require(Fresh(request, started));
            input = input with
            {
                ObservedAt = _time.GetUtcNow(),
                ConnectorRuntime = finalStatus.Runtime with { OperationId = operation },
                Lag = telemetry?.ReadForEvaluation(pass),
            };
            var result = Evaluate(
                mode,
                journal,
                input,
                continuity,
                finalStatus,
                worker,
                diagnostics,
                rollout is not null
            ) with
            {
                Recovery = recoveryObservation,
                LiveConfiguration = new Dictionary<string, string>(live),
            };
            if (recoveryObservation.RequiresFreshPass)
            {
                result = result with { PreStartEligible = false, PublicationReady = false };
            }
            if (progress is not null)
            {
                input = input with
                {
                    ObservedAt = _time.GetUtcNow(),
                    Lag = telemetry?.ReadForEvaluation(pass),
                };
                progress.Capture(input, journal.HasPendingRecordSizeIncrease, continuity);
                progress.Complete();
            }
            return result;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                && continuity.Observation.Continuity == CdcSourceHistoryContinuity.Lost
            )
        {
            token.ThrowIfCancellationRequested();
            var diagnostic = exception switch
            {
                EvidenceException evidence => evidence.Diagnostic,
                CdcWorkflowStateException => new CdcDeploymentDiagnostic(
                    downstream.Component,
                    CdcDeploymentFailure.ValidationFailed
                ),
                _ => CdcDeploymentDiagnostic.FromException(downstream.Component, exception),
            };
            return new(
                _time.GetUtcNow(),
                false,
                false,
                CdcSourceHistoryContinuity.Lost,
                journal.HasPendingRecordSizeIncrease,
                [.. diagnostics, diagnostic]
            )
            {
                SourceHistory = continuity,
                Evidence = new(
                    operation,
                    _time.GetUtcNow(),
                    request.TargetIdentity,
                    request.Binding.PhysicalSourceFingerprint
                )
                {
                    BindingState = exact.State,
                    ProviderSetup = mapped.ProviderSetup,
                    SourceHistory = continuity.Observation,
                },
            };
        }
    }

    private async Task RequireEligibleProjectionAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        DateTimeOffset started,
        CancellationToken token
    )
    {
        var database = await CallAsync(
            request,
            ct =>
                new CdcInitialEnablement(_store, _bindings, _time).ObserveCurrentDatabaseAsync(
                    runtime,
                    request.Binding,
                    started,
                    request.Timing.MaximumObservationAge,
                    ct
                ),
            token
        );
        Require(
            database.Lifecycle.State == DocumentCacheLifecycleState.Tracking
                && !database.Lifecycle.CacheAheadRecoveryRequired
        );
    }

    private static void ValidateProvenance(
        CdcDeploymentRequest request,
        CdcWorkflowJournal journal,
        CdcSourcePublicationHistory history
    )
    {
        Require(
            journal.Target == request.TargetIdentity
                && history.CreationTarget == journal.Target
                && history.WorkflowId == journal.WorkflowId
                && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
                && history.PhysicalSourceFingerprint == request.Binding.PhysicalSourceFingerprint
                && history.Transitions[^1].Status == DocumentCacheDownstreamPublicationStatus.Active
        );
        var database = (CdcWorkflowCompletion.Database)
            Completed(journal, CdcWorkflowEffect.CreateDatabase).Evidence;
        var source = (CdcWorkflowCompletion.Source)
            Completed(journal, CdcWorkflowEffect.AssociateSource).Evidence;
        Require(
            database.Receipt == history.CreationReceipt
                && source.PhysicalSourceFingerprint == request.Binding.PhysicalSourceFingerprint
        );
        DateTimeOffset previous = journal.CreatedAt;
        foreach (
            var effect in new[]
            {
                CdcWorkflowEffect.ReserveBinding,
                CdcWorkflowEffect.ActivateProjection,
                CdcWorkflowEffect.CreateProvider,
                CdcWorkflowEffect.RegisterConnector,
                CdcWorkflowEffect.EstablishConnector,
            }
        )
        {
            var completion = Completed(journal, effect);
            var operation = journal.Operations.Single(o => o.Effect == effect);
            Require(operation.IntendedAt >= previous);
            previous = completion.ReconciledAt;
        }
        var kafka = Completed(journal, CdcWorkflowEffect.PrepareKafka);
        Require(
            kafka.ReconciledAt
                <= journal.Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector).IntendedAt
        );
        Require(
            journal
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
                .ConnectorRegistration.Length == 1
        );
        Require(!journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.Retire));
        // Completed record-size rollouts update operational policy, not the immutable registration hash.
        // Live template validation below uses the current requested policy; it never replays old config.
    }

    private static CdcWorkflowCompletionRecord Completed(CdcWorkflowJournal journal, CdcWorkflowEffect effect)
    {
        Require(journal.Operations.Count(o => o.Effect == effect) == 1);
        var operation = journal.Operations.Single(o => o.Effect == effect);
        Require(operation.Completions.Length == 1);
        return operation.Completions[0];
    }

    private sealed class Boundary
    {
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);

    private bool Fresh(CdcDeploymentRequest request, DateTimeOffset at) =>
        at <= _time.GetUtcNow() && _time.GetUtcNow() - at <= request.Timing.MaximumObservationAge;

    private static CdcTransportResult<CdcEstablishedValidationObservation> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new CdcTransportResult<CdcEstablishedValidationObservation>.Unavailable(new(component, failure));

    private static T Observed<T>(
        CdcTransportResult<T> result,
        CdcDeploymentComponent component = CdcDeploymentComponent.Connect
    )
        where T : notnull =>
        result switch
        {
            CdcTransportResult<T>.Observed observed => observed.Value,
            CdcTransportResult<T>.Unavailable unavailable => throw new EvidenceException(
                unavailable.Diagnostic
            ),
            _ => throw new EvidenceException(new(component, CdcDeploymentFailure.ValidationFailed)),
        };

    private static async Task<CdcTransportResult<T>> ReadAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<CdcTransportResult<T>>> action,
        CdcDeploymentComponent component,
        CancellationToken token
    )
        where T : notnull
    {
        try
        {
            return await CallAsync(request, action, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CdcTransportResult<T>.Unavailable(
                CdcDeploymentDiagnostic.FromException(component, exception)
            );
        }
    }

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
            var value = await action(timeout.Token).WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return value;
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside the controller."
    )]
    internal sealed class EvidenceException(CdcDeploymentDiagnostic diagnostic) : Exception
    {
        public CdcDeploymentDiagnostic Diagnostic { get; } = diagnostic;
    }
}
