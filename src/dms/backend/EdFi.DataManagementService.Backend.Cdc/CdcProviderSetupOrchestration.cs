// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using DdlMode = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupMode;
using DdlOutcome = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupOutcome;
using DdlProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Current-pass in-memory handoff, never durable readiness or registration authorization.</summary>
public sealed class CdcProviderSetupHandoff(
    CdcConnectorTemplateRequest templateRequest,
    CdcConnectorTemplateResult template,
    DateTimeOffset observedAt
)
{
    [JsonIgnore]
    public CdcConnectorTemplateRequest TemplateRequest { get; } = templateRequest;

    [JsonIgnore]
    public CdcConnectorTemplateResult Template { get; } = template;
    public DateTimeOffset ObservedAt { get; } = observedAt;

    public override string ToString() => nameof(CdcProviderSetupHandoff);
}

/// <summary>
/// Owns post-activation capture setup and retained identity. Registration/continuity controllers must
/// obtain a fresh pass and enforce their own admission gates; this stage never registers a connector.
/// </summary>
public sealed class CdcProviderSetupOrchestration
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcProviderSetupService _provider;
    private readonly ICdcConnectorTemplateService _templates;
    private readonly TimeProvider _time;

    public CdcProviderSetupOrchestration(
        string stateRoot,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates
    )
        : this(
            new(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            provider,
            templates,
            TimeProvider.System
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
    }

    internal CdcProviderSetupOrchestration(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _provider = provider;
        _templates = templates;
        _time = time;
    }

    public async Task<CdcTransportResult<CdcProviderSetupHandoff>> SetupAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        var boundary = new OperationBoundary();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
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
            return new CdcTransportResult<CdcProviderSetupHandoff>.Observed(
                await SetupInSessionAsync(request, runtime, session, c => boundary.Component = c, token)
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Failure(boundary.Component, CdcDeploymentFailure.Timeout);
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
            return new CdcTransportResult<CdcProviderSetupHandoff>.Unavailable(
                CdcDeploymentDiagnostic.FromException(boundary.Component, exception)
            );
        }
    }

    // Caller owns the same state-root session for the complete registration operation.
    internal async Task<CdcProviderSetupHandoff> SetupInSessionAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        LocalCdcWorkflowJournalStore.Session session,
        Action<CdcDeploymentComponent> setComponent,
        CancellationToken token,
        bool observeProjection = true
    )
    {
        var binding = request.Binding;
        var target = binding.ToTargetIdentity();
        var journal = await session.ReadAsync(target, token);
        var history = await session.ReadSourcePublicationHistoryAsync(
            target,
            binding.PhysicalSourceFingerprint,
            token
        );
        Require(
            history.WorkflowId == journal.WorkflowId
                && history.CreationTarget == target
                && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
        );
        Require(
            history.Transitions[^1].Status
                is DocumentCacheDownstreamPublicationStatus.Possible
                    or DocumentCacheDownstreamPublicationStatus.Active
        );
        Require(!journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.Retire));
        RequireCompleted(journal, CdcWorkflowEffect.ReserveBinding);
        RequireCompleted(journal, CdcWorkflowEffect.ActivateProjection);
        var exact = await _bindings.ExactMatchBindingAsync(binding, token);
        Require(
            exact.Status == CdcControlPlaneOperationStatus.Succeeded
                && exact.State?.State == CdcBindingState.BindingPresent
        );

        var providerOperations = journal
            .Operations.Where(o => o.Effect == CdcWorkflowEffect.CreateProvider)
            .ToArray();
        Require(providerOperations.Length <= 1);
        Require(
            Array.TrueForAll(
                providerOperations,
                o =>
                    o.IntendedAt
                    >= journal
                        .Operations.Single(a => a.Effect == CdcWorkflowEffect.ActivateProjection)
                        .Completions[0]
                        .ReconciledAt
            )
        );
        var retained = providerOperations
            .SelectMany(o => o.Completions)
            .Select(c => c.Evidence)
            .OfType<CdcWorkflowCompletion.Provider>()
            .ToArray();
        bool consumptionPossible = journal.Operations.Any(o =>
            o.Effect == CdcWorkflowEffect.RegisterConnector
        );
        bool advanced = journal.Operations.Any(o =>
            o.Effect
                is CdcWorkflowEffect.EstablishConnector
                    or CdcWorkflowEffect.AuthorizeWriterPublication
                    or CdcWorkflowEffect.StopConnector
                    or CdcWorkflowEffect.ResumeConnector
                    or CdcWorkflowEffect.IncreaseRecordSize
        );
        Require(!advanced || consumptionPossible);
        Require(!consumptionPossible || retained.Length == 1);
        Require(
            !consumptionPossible
                || providerOperations[0].Completions[0].ReconciledAt
                    <= journal
                        .Operations.First(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
                        .IntendedAt
        );

        setComponent(CdcDeploymentComponent.Projection);
        // Established inspection must not manufacture a closed-never-opened initial proof.
        var initial = new CdcInitialEnablement(_store, _bindings, _time);
        if (!observeProjection)
        {
            Require(consumptionPossible && retained.Length == 1);
        }
        else if (consumptionPossible)
        {
            var current = await initial.ObserveCurrentDatabaseAsync(
                runtime,
                binding,
                _time.GetUtcNow(),
                request.Timing.MaximumObservationAge,
                token
            );
            Require(
                current.Lifecycle.State == DocumentCacheLifecycleState.Tracking
                    && !current.Lifecycle.CacheAheadRecoveryRequired
            );
        }
        else
        {
            var proof = new InitialCdcProvisioningProof(
                CdcJsonContract.CurrentContractVersion,
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                target,
                target.Provider,
                journal.WorkflowId.ToString("D"),
                CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
                CdcWriteAdmissionState.ClosedNeverOpened,
                _time.GetUtcNow()
            );
            var observation = await initial.ObserveAsync(
                runtime,
                binding,
                proof,
                request.Timing.MaximumObservationAge,
                token
            );
            Require(
                initial.Classify(binding, proof, observation.Eligibility, exact).RetryClassification
                    == CdcRetryClassification.ResumeProviderTopicConnectorSetup
            );
        }

        setComponent(CdcDeploymentComponent.WorkflowState);
        if (providerOperations.Length == 0)
        {
            Require(!journal.WriterPublicationAuthorized && !journal.HasPendingRecordSizeIncrease);
            journal = await session.RecordIntentAsync(
                target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.CreateProvider,
                [],
                token
            );
        }
        var operation = journal.Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider);
        setComponent(CdcDeploymentComponent.ProviderSetup);
        // A durable completed provider is inspect-only even before registration. This never repairs
        // missing slots/capture instances/jobs. The provider owns the unconsumed-slot guard.
        var slotProof = retained.SelectMany(p => p.InitialSlotProofs).SingleOrDefault();
        var mode = retained.Length == 0 ? DdlMode.InitialCreateOrExactMatch : DdlMode.ValidateOnly;
        var setup = CopyRequest(
            request.ProviderSetup,
            mode,
            slotProof,
            !consumptionPossible && retained.Length == 1
        );
        var result = await _provider.SetupAsync(setup, token);
        token.ThrowIfCancellationRequested();
        ValidateResult(request, setup, result);
        if (retained.Length == 0)
        {
            slotProof = result.InitialReplicationSlotProof;
            Require(setup.Provider != DdlProvider.Postgresql || slotProof is not null);
            // Re-read live capture after effects. A success response alone is not a completion receipt.
            setup = CopyRequest(request.ProviderSetup, DdlMode.ValidateOnly, slotProof, true);
            result = await _provider.SetupAsync(setup, token);
            token.ThrowIfCancellationRequested();
            ValidateResult(request, setup, result);
        }
        if (slotProof is not null)
        {
            var slot = result.ArtifactInventory.Single(a =>
                a.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
            );
            Require(
                slot.SafeArtifactName == slotProof.ReplicationSlotName
                    && slotProof.SourceFingerprint == result.ObservedSourceFingerprint
                    && slot.SafeObservedValues.TryGetValue(
                        "database_identity_token",
                        out var databaseIdentity
                    )
                    && databaseIdentity == slotProof.DatabaseIdentityToken.Value
            );
        }
        var completion = new CdcWorkflowCompletion.Provider(
            Identities(request, result),
            slotProof is null ? [] : [slotProof]
        );
        if (retained.Length == 1)
        {
            Require(completion.Artifacts.SequenceEqual(retained[0].Artifacts));
        }
        var observedAt = _time.GetUtcNow();
        setComponent(CdcDeploymentComponent.WorkflowState);
        await session.ReconcileCompletionAsync(
            target,
            journal.WorkflowId,
            operation.OperationId,
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(completion)
                );
            },
            token
        );
        setComponent(CdcDeploymentComponent.ProviderSetup);
        var templateRequest = request.CreateTemplateRequest(new(binding.Generation, result));
        var template = _templates.Render(templateRequest);
        Require(template.Outcome == CdcConnectorTemplateOutcome.Rendered);
        return new(templateRequest, template, observedAt);
    }

    internal static CdcProviderSetupRequest CopyRequest(
        CdcProviderSetupRequest request,
        DdlMode mode,
        CdcPostgresqlInitialReplicationSlotProof? proof,
        bool requireUnconsumed
    ) =>
        new(
            request.Provider,
            mode,
            request.BoundPhysicalSourceFingerprint,
            request.SetupPrincipal,
            request.ConnectorPrincipal,
            request.ArtifactNames,
            new(false),
            request.ExpectedSourceInventory,
            request.DmsManagedTableInventory,
            proof,
            request.ConnectorPrincipalProbeFactory,
            request.DatabaseExecutor,
            requireUnconsumed
        );

    private static void ValidateResult(
        CdcDeploymentRequest request,
        CdcProviderSetupRequest setup,
        CdcProviderSetupResult result
    )
    {
        if (
            result.Diagnostics.Any(d =>
                d.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                || d.Classification
                    is CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                        or CdcProviderRetryContinuityClassification.Retryable
            )
        )
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Unavailable);
        }
        Require(setup.Mode != DdlMode.ValidateOnly || result.Outcome == DdlOutcome.ExactMatch);
        Require(
            result.Provider == setup.Provider
                && result.Mode == setup.Mode
                && result.BoundPhysicalSourceFingerprint == setup.BoundPhysicalSourceFingerprint
                && result.ObservedSourceFingerprint == setup.BoundPhysicalSourceFingerprint
                && result.Outcome is DdlOutcome.CreatedOrMatched or DdlOutcome.ExactMatch
                && !result.Diagnostics.Any(d => d.Severity == CdcProviderDiagnosticSeverity.Error)
        );
        Require(
            CdcSourceInventoryValidator
                .ValidateLiveSourceInventory(
                    request.ProviderSetup.ExpectedSourceInventory,
                    result.SourceTableInventory
                )
                .Count == 0
        );
    }

    internal static ImmutableArray<CdcRetainedProviderIdentity> Identities(
        CdcDeploymentRequest request,
        CdcProviderSetupResult result
    )
    {
        var artifacts = CdcConnectorTemplateBindingArtifacts
            .From(request.Binding, nameof(request))
            .ArtifactInventory;
        return artifacts
            .GovernedArtifacts.Where(a =>
                a.Kind
                    is CdcGovernedArtifactKind.PostgresqlLogicalSlot
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
            )
            .Select(expected =>
            {
                var kind =
                    result.Provider == DdlProvider.Postgresql
                        ? CdcProviderArtifactKind.PostgresqlReplicationSlot
                        : CdcProviderArtifactKind.SqlServerCaptureInstance;
                var artifact = result.ArtifactInventory.Single(a =>
                    a.ArtifactKind == kind && a.SafeArtifactName.Value == expected.Name
                );
                Require(artifact.State == CdcProviderArtifactState.Matched);
                var key =
                    result.Provider == DdlProvider.Postgresql
                        ? "database_identity_token"
                        : "capture_identity_hash";
                CdcWorkflowJournalValidation.Require(
                    artifact.SafeObservedValues.TryGetValue(key, out var identity)
                        && !string.IsNullOrWhiteSpace(identity),
                    CdcWorkflowStateFailure.Unavailable
                );
                if (result.Provider == DdlProvider.Postgresql)
                {
                    // Validate the opaque token using its existing contract; never hash a sanitized database name.
                    _ = CdcPostgresqlInitialReplicationSlotProof.ValidateDatabaseIdentityToken(
                        new(identity!),
                        nameof(identity)
                    );
                }
                else
                {
                    Require(CdcSha256ValueValidator.IsValid("sha256:" + identity));
                }
                string hash =
                    "sha256:"
                    + Convert.ToHexStringLower(
                        SHA256.HashData(
                            JsonSerializer.SerializeToUtf8Bytes(
                                new[]
                                {
                                    "cdc-retained-provider-v1",
                                    request.Binding.PhysicalSourceFingerprint,
                                    expected.Name,
                                    identity!,
                                }
                            )
                        )
                    );
                return new CdcRetainedProviderIdentity(expected.Kind, expected.Name, hash);
            })
            .ToImmutableArray();
    }

    private sealed class OperationBoundary
    {
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }

    private static void RequireCompleted(CdcWorkflowJournal journal, CdcWorkflowEffect effect) =>
        Require(
            journal.Operations.Count(o => o.Effect == effect) == 1
                && journal.Operations.Single(o => o.Effect == effect).Completions.Length == 1
        );

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);

    private static CdcTransportResult<CdcProviderSetupHandoff> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new CdcTransportResult<CdcProviderSetupHandoff>.Unavailable(new(component, failure));
}
