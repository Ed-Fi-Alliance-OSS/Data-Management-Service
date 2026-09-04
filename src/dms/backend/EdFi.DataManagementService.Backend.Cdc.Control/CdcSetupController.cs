// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using DdlCdc = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// The provider-setup inputs the control plane cannot derive for itself: the principals the
/// deployment provisioned, and the source inventory the instance database's own schema emission
/// describes. The caller supplies them because both are properties of the deployed database rather
/// than of the CDC policy.
/// </summary>
public sealed record CdcProviderSetupInputs(
    string SetupPrincipal,
    string ConnectorPrincipal,
    IReadOnlyList<CdcSourceTableInventory> ExpectedSourceInventory,
    IReadOnlyList<CdcDmsManagedTableInventory> DmsManagedTableInventory
);

/// <summary>
/// One operator request to enable CDC on a target. The tenant key and data store id are the
/// projector's own target coordinates; everything else about the binding comes from
/// <see cref="CdcControlOptions"/>.
/// </summary>
public sealed record CdcEnableRequest(
    string OperationId,
    string TenantKey,
    long DataStoreId,
    string ConnectionString,
    CdcProvisioningProofEvidence ProvisioningEvidence,
    CdcProviderSetupInputs ProviderSetup
);

/// <summary>
/// One operator request against a target that has already been enabled. It names the projector's own
/// target coordinates and the instance database, and carries the same provider-setup inputs the
/// validate-only inspection needs; everything else about the binding is read from the durable binding
/// record rather than supplied again.
/// </summary>
public sealed record CdcTargetOperationRequest(
    string OperationId,
    string TenantKey,
    long DataStoreId,
    string ConnectionString,
    CdcProviderSetupInputs ProviderSetup
)
{
    /// <summary>
    /// The operator's assertion that the connector the binding record names is already gone, which
    /// retirement alone cannot establish.
    /// </summary>
    /// <remarks>
    /// Only retirement reads this. A missing connector leaves the committed source offsets unobservable
    /// — they outlive the configuration, and Kafka Connect answers the same <c>404</c> for a connector
    /// that never existed as for one deleted out from under the record — so retirement refuses by
    /// default. This is how an operator accepts that ambiguity for a generation whose connector was
    /// never registered, or whose earlier retirement removed the connector before it was interrupted.
    /// The resulting proof records the assertion as the operator's rather than the worker's.
    /// </remarks>
    public bool ConnectorAlreadyAbsent { get; init; }
}

/// <summary>
/// One operator request to replace the physical source behind an already enabled target. The generation
/// being replaced is named explicitly; the generation replacing it is the one
/// <see cref="CdcControlOptions"/> configures, and it must advance past it.
/// </summary>
public sealed record CdcReplaceSourceRequest(
    string OperationId,
    string TenantKey,
    long DataStoreId,
    string ConnectionString,
    long PreviousGeneration,
    CdcProvisioningProofEvidence ProvisioningEvidence,
    CdcProviderSetupInputs ProviderSetup
);

/// <summary>
/// One operator request to adopt an existing governed-artifact set. The binding record is supplied in
/// full by the operator and is never inferred from the topic names or the connector configuration that
/// happen to exist: adoption repairs missing deployment state around an already complete artifact set,
/// and every fact in the supplied record is verified live before any of it becomes durable.
/// </summary>
public sealed record CdcAdoptRequest(
    string OperationId,
    CdcBinding Binding,
    string ConnectionString,
    CdcProviderSetupInputs ProviderSetup
);

/// <summary>
/// Opens a connection to the instance database a binding captures. The control plane holds the
/// connection open across the provider-setup passes, so opening it is a seam of its own rather than
/// something the provider-setup service is handed.
/// </summary>
public interface ICdcInstanceDatabaseConnectionFactory
{
    DbConnection Create(CoreCdc.CdcProvider provider, string connectionString);
}

internal sealed class CdcInstanceDatabaseConnectionFactory : ICdcInstanceDatabaseConnectionFactory
{
    public DbConnection Create(CoreCdc.CdcProvider provider, string connectionString) =>
        provider == CoreCdc.CdcProvider.Postgresql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);
}

/// <summary>
/// The operator operations of the CDC control plane. Every operation returns an existing shared
/// contract rather than a result shape of its own.
/// </summary>
public interface ICdcSetupController
{
    /// <summary>
    /// Runs the initial enablement sequence and reports the admission its collected evidence
    /// supports. Write admission is opened only by evidence: a step that cannot produce its evidence
    /// ends the sequence and the admission reports what was and was not observed.
    /// </summary>
    Task<CdcAdmission> EnableAsync(CdcEnableRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Collects every observation the target's combined status is evaluated from and reports it. The
    /// source-history continuity check runs on each interval, and a continuity loss it proves is
    /// latched durably before the status is reported.
    /// </summary>
    Task<CdcStatus> StatusAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Restarts the binding's connector, but only against affirmative source-history continuity
    /// evidence. Continuity that is unknown leaves the connector as it is rather than starting or
    /// resuming it, and a proved loss stops it. No offset is ever reset and nothing is re-snapshotted
    /// into the existing public topic.
    /// </summary>
    Task<CdcStatus> RestartAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Adopts an existing governed-artifact set under the binding record the operator supplied, after
    /// live-verifying every one of that record's claims. The proof is issued, and the record becomes
    /// durable, only when every verification is an exact match; a failed or incomplete adoption changes
    /// nothing and reports what did not match.
    /// </summary>
    Task<CdcContractReadResult<CdcAdoptionProof>> AdoptAsync(
        CdcAdoptRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Replaces the physical source behind an enabled target by fencing the outgoing generation's
    /// connector and running the enablement sequence for a new generation, whose connector, topics and
    /// provider artifacts are all its own. The outgoing generation's record and artifacts are retained
    /// until they are explicitly retired.
    /// </summary>
    Task<CdcAdmission> ReplaceSourceAsync(
        CdcReplaceSourceRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Retires one binding generation, removing every governed artifact it owns in the order that keeps
    /// each removal decidable, and deleting the binding record last. A partial teardown issues no proof
    /// and leaves the record intact, so the retry stays idempotent.
    /// </summary>
    Task<CdcContractReadResult<CdcCleanupProof>> RetireAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class CdcSetupController(
    IOptions<CdcControlOptions> options,
    CdcExplicitProjectionTargetProof projectionTargetProof,
    ICdcProjectionCorrelationCollector projectionCorrelation,
    ICdcEligibilityProbe eligibilityProbe,
    ICdcBindingLifecycleService bindingLifecycle,
    IDocumentCacheGuardedNewEmptyActivationCommand guardedActivation,
    ICdcProviderSetupService providerSetup,
    ICdcInstanceDatabaseConnectionFactory connectionFactory,
    ICdcKafkaAdmin kafkaAdmin,
    ICdcConnectorTemplateService templateService,
    ICdcConnectClient connectClient,
    ICdcConnectorObservationMapper observationMapper,
    ICdcConnectorLagReader lagReader,
    ICdcProviderSourcePositionAdapter sourcePositions,
    ICdcProviderArtifactTeardown providerArtifactTeardown,
    TimeProvider timeProvider,
    ILogger<CdcSetupController> logger
) : ICdcSetupController
{
    private const string ConnectorClassPropertyName = "connector.class";

    /// <summary>
    /// The SQL Server Connect source partition includes the catalog the connector reads, so it is taken
    /// from the deployment's own provider connection properties rather than derived.
    /// </summary>
    private const string SqlServerCatalogPropertyName = "database.names";

    /// <summary>
    /// The full enablement sequence: prove the target, preflight the projector's status endpoint,
    /// classify eligibility, make the binding durable, run the guarded activation, provision the
    /// provider and Kafka artifacts, register the connector, and then collect the caught-up, provider
    /// barrier, source-history, and lag evidence write admission is decided from.
    /// </summary>
    /// <remarks>
    /// The binding record is made durable before any external artifact exists, so an interrupted
    /// enablement always leaves something that names what was provisioned. Every step reports the
    /// evidence it observed and nothing more: a step whose evidence does not arrive within its budget
    /// ends the sequence with what was observed, so an admission is opened by evidence rather than by
    /// elapsed time.
    /// </remarks>
    public Task<CdcAdmission> EnableAsync(
        CdcEnableRequest request,
        CancellationToken cancellationToken = default
    ) => EnableCoreAsync(request, fencedPreviousGeneration: null, cancellationToken);

    /// <summary>
    /// The enablement sequence, told which generation of this target the caller has already fenced.
    /// </summary>
    /// <param name="fencedPreviousGeneration">
    /// The generation <see cref="ReplaceSourceAsync"/> stopped before entering the sequence, and the
    /// one live generation of this target the enablement may run alongside. Null for a plain enable,
    /// which never starts a second publisher for a target this deployment already binds.
    /// </param>
    private async Task<CdcAdmission> EnableCoreAsync(
        CdcEnableRequest request,
        long? fencedPreviousGeneration,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProvisioningEvidence);
        ArgumentNullException.ThrowIfNull(request.ProviderSetup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcControlOptions controlOptions = options.Value;

        // When the sequence started. It seeds the admission and stamps the steps that run before the
        // first await. A step diagnostic raised after one is stamped with a fresh read instead: the
        // waiting steps here are budgeted in minutes, and a diagnostic that named this instant would
        // report an observation the step had not yet made.
        //
        // It is deliberately NOT the classifier inputs' clock. Every classifier input carries evidence
        // read after this instant - the durable binding state and the eligibility observation are both
        // stamped by their own live reads - and the contract validators reject a timestamp later than
        // the `nowUtc` they are given. Classifying against this instant would report freshly collected
        // evidence as future-dated and refuse the enablement on evidence that is in fact current.
        DateTimeOffset now = timeProvider.GetUtcNow();
        CoreCdc.CdcProvider provider = eligibilityProbe.Provider;
        string dataStoreId = request.DataStoreId.ToString(CultureInfo.InvariantCulture);

        CdcInitialAdmissionEvaluationInput evaluation = new(
            request.OperationId,
            now,
            now,
            UnvalidatedTargetIdentity(controlOptions, request.TenantKey, dataStoreId, provider),
            null,
            null,
            null,
            null
        );

        // The admission is stamped when it is composed rather than when the sequence started: the later
        // steps wait on evidence, and an observation is never in the future of the admission that
        // reports it.
        CdcAdmission Evaluate()
        {
            DateTimeOffset evaluatedAt = timeProvider.GetUtcNow();

            return CdcInitialAdmissionEvaluator.Evaluate(
                evaluation with
                {
                    ObservedAt = evaluatedAt,
                    NowUtc = evaluatedAt,
                }
            );
        }

        CdcAdmission Blocked(CdcDiagnostic stepDiagnostic, IReadOnlyList<CdcDiagnostic> diagnostics)
        {
            evaluation = evaluation with { StateStoreDiagnostics = [stepDiagnostic, .. diagnostics] };

            return Evaluate();
        }

        // Step 1: the target must be a valid CDC target and an operator-configured projection target.
        CdcTargetValidationResult targetValidation = CdcTargetValidator.Validate(
            TargetInput(controlOptions, request.TenantKey, dataStoreId, provider)
        );
        if (targetValidation.Target is not { } target)
        {
            return Blocked(
                Step(
                    "enableTargetInvalid",
                    CdcDiagnosticCategory.TargetMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC enablement rejected the requested target.",
                    "an invalid target",
                    now
                ),
                targetValidation.Diagnostics
            );
        }

        evaluation = evaluation with { TargetIdentity = target.ToTargetIdentity() };

        CdcExplicitProjectionTargetProofResult projectionTarget = projectionTargetProof.Prove(target, now);
        if (!projectionTarget.Succeeded)
        {
            return Blocked(
                Step(
                    "enableProjectionTargetUnproven",
                    CdcDiagnosticCategory.TargetMismatch,
                    CdcDiagnosticComponent.Projection,
                    "CDC enablement requires the target to be configured on the DMS projector itself.",
                    projectionTarget.State.ToString(),
                    now
                ),
                projectionTarget.Diagnostics
            );
        }

        // Preflight: the caught-up evidence steps 7 and 9 depend on is read from the running DMS, so a
        // deployment whose status endpoint is unmapped or unauthorized fails here — before a binding
        // or any external artifact exists — rather than after provisioning everything.
        CdcProjectionCorrelationObservation preflight = await projectionCorrelation
            .CollectAsync(new(request.OperationId, target.ToTargetIdentity(), null), cancellationToken)
            .ConfigureAwait(false);
        if (preflight.CorrelationState == CdcProjectionCorrelationState.Unavailable)
        {
            return Blocked(
                Step(
                    "enableProjectionStatusUnavailable",
                    CdcDiagnosticCategory.StatusObservationUnavailable,
                    CdcDiagnosticComponent.Projection,
                    "CDC enablement could not read the running DMS projection status it must observe "
                        + "caught-up evidence from.",
                    preflight.CorrelationState.ToString(),
                    timeProvider.GetUtcNow()
                ),
                preflight.Diagnostics
            );
        }

        CdcArtifactNameResult artifactNames = CdcArtifactNameGenerator.Render(
            new(target.DeploymentKey, target.TopicPrefix, target.InstanceKey, target.Generation, provider)
        );
        if (artifactNames.Inventory is not { } inventory)
        {
            return Blocked(
                Step(
                    "enableArtifactNamesInvalid",
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC enablement could not render the governed artifact names for the target.",
                    "unrenderable",
                    now
                ),
                artifactNames.Diagnostics
            );
        }

        // Step 2: the operator's provisioning evidence, the durable binding state, and one read-only
        // eligibility observation, classified together.
        CdcContractReadResult<InitialCdcProvisioningProof> issuedProof = CdcProvisioningProofFactory.Issue(
            new(request.OperationId, target.ToTargetIdentity(), null),
            request.ProvisioningEvidence,
            now
        );
        if (issuedProof.Contract is not { } provisioningProof)
        {
            return Blocked(
                Step(
                    "enableProvisioningEvidenceRefused",
                    CdcDiagnosticCategory.MalformedProof,
                    CdcDiagnosticComponent.ProofValidation,
                    "CDC enablement requires the operator's explicit provisioning evidence.",
                    "refused",
                    now
                ),
                issuedProof.Diagnostics
            );
        }

        evaluation = evaluation with { ProvisioningProof = provisioningProof };

        CdcBindingLifecycleResult bindingRead = await bindingLifecycle
            .ReadBindingAsync(target.ToBindingIdentity(), cancellationToken)
            .ConfigureAwait(false);
        if (
            bindingRead.Status
            is CdcControlPlaneOperationStatus.StateStoreUnavailable
                or CdcControlPlaneOperationStatus.InvalidOperation
        )
        {
            return Blocked(
                Step(
                    "enableBindingStateUnavailable",
                    CdcDiagnosticCategory.LocalStateUnavailable,
                    CdcDiagnosticComponent.StateStore,
                    "CDC enablement could not read the durable binding state.",
                    bindingRead.Status.ToString(),
                    now
                ),
                bindingRead.Diagnostics
            );
        }

        bool firstAttempt = bindingRead.Status == CdcControlPlaneOperationStatus.BindingMissing;
        evaluation = evaluation with { BindingState = bindingRead.State };

        InitialCdcEligibilityObservation eligibility = await eligibilityProbe
            .ProbeAsync(
                EligibilityProbeRequest(request, target, provisioningProof, controlOptions),
                cancellationToken
            )
            .ConfigureAwait(false);

        evaluation = evaluation with
        {
            EligibilityObservation = eligibility,
            PhysicalSourceFingerprint = eligibility.PhysicalSourceFingerprint,
        };

        // The classification's own clock, read after the evidence it classifies rather than at the
        // sequence's start. The probe stamps its observation when the database answered, and the
        // binding read stamps its state when the store answered; both are later than `now`, and the
        // shared validators reject an observation later than the `nowUtc` they are handed. Source
        // replacement reads its own clock in the same position for the same reason.
        DateTimeOffset probedAt = timeProvider.GetUtcNow();

        CdcRetryClassification? retryClassification = null;
        if (firstAttempt)
        {
            CdcInitialEnablePreBindingEligibilityResult preBinding =
                CdcInitialEnableRetryClassifier.EvaluatePreBindingEligibility(
                    new(
                        request.OperationId,
                        probedAt,
                        probedAt,
                        target.ToTargetIdentity(),
                        eligibility.PhysicalSourceFingerprint,
                        provisioningProof,
                        eligibility
                    )
                );
            if (!preBinding.CanCreateBinding)
            {
                return Blocked(
                    RejectionStep(preBinding.Rejection, timeProvider.GetUtcNow()),
                    preBinding.Rejection?.Diagnostics ?? preBinding.Diagnostics
                );
            }
        }
        else
        {
            CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
                new(
                    request.OperationId,
                    probedAt,
                    probedAt,
                    target.ToTargetIdentity(),
                    eligibility.PhysicalSourceFingerprint,
                    provisioningProof,
                    eligibility,
                    bindingRead.State
                )
            );
            if (retry.Action != CdcRetryAction.Proceed)
            {
                return Blocked(RejectionStep(retry, timeProvider.GetUtcNow()), retry.Diagnostics);
            }

            retryClassification = retry.RetryClassification;
        }

        if (eligibility.PhysicalSourceFingerprint is not { } physicalSourceFingerprint)
        {
            return Blocked(
                Step(
                    "enablePhysicalSourceUnidentified",
                    CdcDiagnosticCategory.SourceMismatch,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC enablement could not identify the physical source to bind against.",
                    "absent",
                    timeProvider.GetUtcNow()
                ),
                eligibility.Diagnostics
            );
        }

        // Step 3: the binding record is durable before any external artifact is created, so nothing is
        // ever provisioned that the control plane cannot name afterwards.
        CdcBinding binding = Binding(target, provider, physicalSourceFingerprint, inventory);

        CdcBindingConflict conflict = await FindBindingConflictAsync(
                binding,
                CdcSecondGenerationRule.RefusedUnlessFenced(fencedPreviousGeneration),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (conflict.Blocked)
        {
            return Blocked(
                Step(
                    conflict.Code,
                    conflict.Category,
                    CdcDiagnosticComponent.Binding,
                    conflict.Message,
                    conflict.Observed,
                    timeProvider.GetUtcNow()
                ),
                conflict.Diagnostics
            );
        }

        // An unbound attempt must be the first to provision these names. Nothing downstream can hold
        // that line: the topic pass creates what is absent and accepts what already matches, and the
        // registration is a configuration PUT that overwrites whatever the worker was holding under
        // this name. So a deployment that lost its binding state store and enabled a fresh database
        // would attach it to the previous life's topics and its consumers' committed offsets, and
        // report a clean enablement of artifacts it had silently adopted.
        //
        // Only the unbound attempt asks. A retry legitimately finds the artifacts its own earlier run
        // created, and a source replacement is unbound at a generation whose every governed name is
        // new. Recovering deployment state around an artifact set that already exists is what `cdc
        // adopt` is for, and it verifies each artifact against an operator-supplied record rather than
        // inferring one from the names it finds.
        if (firstAttempt)
        {
            CdcKafkaGovernedTopicPresence topicPresence = await kafkaAdmin
                .FindExistingGovernedTopicsAsync(inventory, cancellationToken)
                .ConfigureAwait(false);
            CdcConnectResult<IReadOnlyDictionary<string, string>> existingConnector = await connectClient
                .GetConnectorConfigAsync(inventory.ConnectorName, cancellationToken)
                .ConfigureAwait(false);

            if (
                UnboundGovernedArtifactRefusal(inventory, topicPresence, existingConnector) is
                { } unboundRefusal
            )
            {
                return Blocked(
                    Step(
                        "enableGovernedArtifactAlreadyExists",
                        unboundRefusal.Category,
                        CdcDiagnosticComponent.Binding,
                        "CDC enablement never infers a binding from artifacts it finds. A governed "
                            + "artifact that exists without its binding record requires explicit "
                            + "adoption or cleanup.",
                        unboundRefusal.Observed,
                        timeProvider.GetUtcNow()
                    ),
                    []
                );
            }
        }

        CdcBindingLifecycleResult bindingWrite = firstAttempt
            ? await bindingLifecycle
                .CreateBindingIfAbsentAsync(binding, cancellationToken)
                .ConfigureAwait(false)
            : await bindingLifecycle.ExactMatchBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        if (bindingWrite.Status != CdcControlPlaneOperationStatus.Succeeded)
        {
            return Blocked(
                Step(
                    "enableBindingNotDurable",
                    bindingWrite.Status == CdcControlPlaneOperationStatus.BindingMismatch
                        ? CdcDiagnosticCategory.BindingMismatch
                        : CdcDiagnosticCategory.LocalStateUnavailable,
                    CdcDiagnosticComponent.Binding,
                    "CDC enablement could not make the binding durable.",
                    bindingWrite.Status.ToString(),
                    now
                ),
                bindingWrite.Diagnostics
            );
        }

        evaluation = evaluation with { BindingState = bindingWrite.State ?? bindingRead.State };
        logger.LogDebug(
            "CDC enablement made the binding durable for generation {Generation}.",
            target.Generation
        );

        // Step 4: guarded tracking activation. A committed activation is recognized only from the
        // classifier's resume decision — never inferred from a lifecycle the control plane itself read.
        if (retryClassification != CdcRetryClassification.ResumeProviderTopicConnectorSetup)
        {
            DocumentCacheAdministrativeCommandResult activation = await guardedActivation
                .ExecuteAsync(
                    new(
                        new DocumentCacheAdministrativeTargetKey(request.TenantKey, request.DataStoreId),
                        new DocumentCachePhysicalSourceFingerprint(physicalSourceFingerprint)
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (activation.Status != DocumentCacheAdministrativeCommandStatus.Completed)
            {
                return Blocked(
                    Step(
                        "enableGuardedActivationIncomplete",
                        CdcDiagnosticCategory.ProjectionNonOperational,
                        CdcDiagnosticComponent.Projection,
                        "CDC enablement could not complete the guarded new-empty tracking activation.",
                        $"{activation.Status} / {activation.Classification}",
                        timeProvider.GetUtcNow()
                    ),
                    []
                );
            }

            // The activation is carried into the admission as a fresh read-only observation of the
            // instance database rather than as the command's own report of itself: every later step is
            // classified against the observed lifecycle, and a command that answered without leaving the
            // database tracking is not an activation the sequence may build on.
            eligibility = await eligibilityProbe
                .ProbeAsync(
                    EligibilityProbeRequest(request, target, provisioningProof, controlOptions),
                    cancellationToken
                )
                .ConfigureAwait(false);
            evaluation = evaluation with { EligibilityObservation = eligibility };

            if (eligibility.LifecycleState != CdcLifecycleState.Tracking)
            {
                return Blocked(
                    Step(
                        "enableGuardedActivationNotObserved",
                        CdcDiagnosticCategory.ProjectionNonOperational,
                        CdcDiagnosticComponent.Projection,
                        "CDC enablement did not observe the tracking lifecycle the guarded activation reported.",
                        eligibility.LifecycleState.ToString(),
                        timeProvider.GetUtcNow()
                    ),
                    eligibility.Diagnostics
                );
            }
        }

        // Step 5: provider artifacts first, then the shared Connect offset store, then the binding's
        // own topics and ACLs.
        (DbConnection? openedConnection, string? connectionRefusal) =
            await OpenProviderConnectionWithinBudgetAsync(
                    provider,
                    request.ConnectionString,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (openedConnection is null)
        {
            return Blocked(
                Step(
                    "enableProviderConnectionUnavailable",
                    CdcDiagnosticCategory.ProviderSetupInvalid,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC enablement could not reach the instance database its provider setup runs against.",
                    connectionRefusal ?? "unavailable",
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        await using DbConnection connection = openedConnection;

        CdcProviderSetupResult created = await SetupProviderWithinBudgetAsync(
                ProviderSetupRequest(
                    request.ProviderSetup,
                    provider,
                    physicalSourceFingerprint,
                    inventory,
                    connection,
                    DdlCdc.CdcProviderSetupMode.InitialCreateOrExactMatch
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (created.Outcome == DdlCdc.CdcProviderSetupOutcome.Failed)
        {
            // The pass's own diagnostics are carried, not just its outcome: a refused grant, an absent
            // connector principal, and an exhausted step budget are all this one outcome, and they are
            // told apart only by what the provider reported.
            DateTimeOffset providerSetupFailedAt = timeProvider.GetUtcNow();

            return Blocked(
                Step(
                    "enableProviderSetupFailed",
                    CdcDiagnosticCategory.ProviderSetupInvalid,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC enablement could not create or exact-match the provider capture artifacts.",
                    created.Outcome.ToString(),
                    providerSetupFailedAt
                ),
                CdcProviderSetupResultMapper.MapResultDiagnostics(created, providerSetupFailedAt)
            );
        }

        // The shared observation is composed from validate-only evidence, so the artifacts just created
        // are read back through the same inspection every later status check uses.
        CdcProviderSetupResult validated = await SetupProviderWithinBudgetAsync(
                ProviderSetupRequest(
                    request.ProviderSetup,
                    provider,
                    physicalSourceFingerprint,
                    inventory,
                    connection,
                    DdlCdc.CdcProviderSetupMode.ValidateOnly
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcProviderSetupObservationMapping providerSetupObservation =
            CdcProviderSetupResultMapper.MapValidateOnlyResult(
                request.OperationId,
                timeProvider.GetUtcNow(),
                binding,
                validated
            );
        evaluation = evaluation with { ProviderSetup = providerSetupObservation.ProviderSetup };

        // Gated here for the same reason the two Kafka observations below are: this validate-only pass
        // is the evidence every later status check reads the provider artifacts through, and a connector
        // registered against nonconforming ones would already be capturing from them by the time the
        // final evaluation rejected the state. A failed or non-exact-match pass is not evidence that the
        // artifacts conform - the mapper reports it as invalid or unavailable - so the sequence ends
        // here, before any Kafka or Connect side effect.
        if (!CdcTargetStatusEvaluator.IsProviderSetupSatisfied(providerSetupObservation.ProviderSetup))
        {
            return Blocked(
                Step(
                    "enableProviderSetupNotSatisfied",
                    CdcDiagnosticCategory.ProviderSetupInvalid,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC enablement requires conforming provider capture artifacts before it registers a "
                        + "connector.",
                    providerSetupObservation.ProviderSetup.SetupOutcome.ToString(),
                    timeProvider.GetUtcNow()
                ),
                providerSetupObservation.ProviderSetup.Diagnostics
            );
        }

        CdcObservationContext context = new(
            request.OperationId,
            target.ToTargetIdentity(),
            physicalSourceFingerprint
        );

        // Both Kafka observations are gated here rather than only carried into the admission. The
        // shared offset store and the binding's topics, grants, and record-size budget are what a
        // registered connector immediately publishes through, so a nonconforming one must end the
        // sequence before registration: an admission that reported the nonconformance afterwards would
        // report it about a connector that was already running against it.
        CdcConnectOffsetStorePolicyObservation connectOffsetStore = await kafkaAdmin
            .EnsureConnectOffsetStoreAsync(context, cancellationToken)
            .ConfigureAwait(false);
        evaluation = evaluation with { ConnectOffsetStore = connectOffsetStore };
        if (!CdcTargetStatusEvaluator.IsConnectOffsetStorePolicySatisfied(connectOffsetStore))
        {
            return Blocked(
                Step(
                    "enableConnectOffsetStoreNotSatisfied",
                    CdcDiagnosticCategory.ConnectOffsetStoreInvalid,
                    CdcDiagnosticComponent.ConnectOffsetStore,
                    "CDC enablement requires a conforming shared Connect offset store before it registers "
                        + "a connector.",
                    $"{connectOffsetStore.PolicyState} / {connectOffsetStore.AclState}",
                    timeProvider.GetUtcNow()
                ),
                connectOffsetStore.Diagnostics
            );
        }

        CdcKafkaPolicyObservation kafkaPolicy = await kafkaAdmin
            .EnsureBindingKafkaPolicyAsync(context, inventory, cancellationToken)
            .ConfigureAwait(false);
        evaluation = evaluation with { KafkaPolicy = kafkaPolicy };
        if (!CdcTargetStatusEvaluator.IsKafkaPolicySatisfied(kafkaPolicy))
        {
            return Blocked(
                Step(
                    "enableKafkaPolicyNotSatisfied",
                    CdcDiagnosticCategory.KafkaPolicyInvalid,
                    CdcDiagnosticComponent.KafkaPolicy,
                    "CDC enablement requires the binding's governed Kafka topics, grants, and record-size "
                        + "budget to be conforming before it registers a connector.",
                    kafkaPolicy.PolicyState.ToString(),
                    timeProvider.GetUtcNow()
                ),
                kafkaPolicy.Diagnostics
            );
        }

        logger.LogDebug("CDC enablement provisioned the provider and Kafka artifacts for the binding.");

        // Step 6: render the connector, validate it before it is registered, register it, and validate
        // what the worker actually holds against the same template rules.
        if (
            !TryComposeConnectorTemplate(
                binding,
                new CdcConnectorProviderSetupEvidence(target.Generation, created),
                controlOptions,
                provider,
                out CdcConnectorTemplateRequest? templateRequest,
                out string? templateRejection
            )
        )
        {
            return Blocked(
                Step(
                    "enableConnectorInputsInvalid",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC enablement could not compose the connector template inputs.",
                    templateRejection,
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        CdcConnectorTemplateResult rendered = templateService.Render(templateRequest);
        if (rendered.Outcome != CdcConnectorTemplateOutcome.Rendered)
        {
            return Blocked(
                Step(
                    "enableConnectorRenderRejected",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC enablement could not render the connector configuration for the binding.",
                    rendered.Outcome.ToString(),
                    timeProvider.GetUtcNow()
                ),
                TemplateDiagnostics(rendered, timeProvider.GetUtcNow())
            );
        }

        CdcConnectorTemplateResult registrationPreflight = templateService.ValidateRegistrationPreflight(
            new(templateRequest, rendered.Config, templateRequest.ProviderSetupEvidence)
        );
        if (registrationPreflight.Outcome != CdcConnectorTemplateOutcome.Rendered)
        {
            return Blocked(
                Step(
                    "enableConnectorPreflightRejected",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC enablement rejected the rendered connector configuration before registering it.",
                    registrationPreflight.Outcome.ToString(),
                    timeProvider.GetUtcNow()
                ),
                TemplateDiagnostics(registrationPreflight, timeProvider.GetUtcNow())
            );
        }

        if (!rendered.Config.TryGetValue(ConnectorClassPropertyName, out string? connectorClass))
        {
            return Blocked(
                Step(
                    "enableConnectorClassAbsent",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC enablement could not identify the connector plugin to register.",
                    "absent",
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        // The worker validates the configuration against the plugin itself before anything is
        // registered, so a configuration the plugin refuses never becomes a registered connector.
        CdcConnectResult<CdcConnectConfigValidation> pluginValidation = await connectClient
            .ValidateConnectorPluginConfigAsync(connectorClass, rendered.Config, cancellationToken)
            .ConfigureAwait(false);
        if (!pluginValidation.Succeeded || pluginValidation.Value is not { ErrorCount: 0 })
        {
            return Blocked(
                Step(
                    "enableConnectorPluginValidationRejected",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC enablement could not confirm the connector plugin accepts the rendered configuration.",
                    PluginValidationSummary(pluginValidation),
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        CdcConnectResult registration = await connectClient
            .PutConnectorConfigAsync(inventory.ConnectorName, rendered.Config, cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Succeeded)
        {
            return Blocked(
                Step(
                    "enableConnectorRegistrationFailed",
                    CdcDiagnosticCategory.ConnectorNotRunning,
                    CdcDiagnosticComponent.ConnectorRuntime,
                    "CDC enablement could not register the connector with the Kafka Connect worker.",
                    registration.Outcome.ToString(),
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack = await connectClient
            .GetConnectorConfigAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);

        evaluation = evaluation with
        {
            ConnectorConfig = observationMapper.MapConfiguration(
                context,
                templateRequest,
                new CdcConnectorProviderSetupEvidence(target.Generation, validated),
                SourcePartitionEvidence(readBack.Value, provider),
                readBack
            ),
        };
        if (evaluation.ConnectorConfig?.ConfigurationState != CdcConnectorConfigurationState.Matched)
        {
            return Evaluate();
        }

        // Step 7: the first caught-up observation, read from the running DMS projector.
        CdcProjectionCorrelationObservation firstCaughtUp = await WaitForCaughtUpAsync(
                context,
                controlOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        evaluation = evaluation with { FirstProjectionCaughtUp = firstCaughtUp };
        if (!IsCaughtUp(firstCaughtUp))
        {
            return Evaluate();
        }

        // Step 8: capture the provider barrier and wait for the connector to commit past it. The barrier
        // is captured after the projector reported caught-up, so the position it names is one the
        // projector had already drained.
        CdcProviderBarrierCaptureResult capturedBarrier = await sourcePositions
            .CaptureBarrierAsync(
                BarrierCapture(request.ConnectionString, binding, controlOptions),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!capturedBarrier.Succeeded)
        {
            return Blocked(
                Step(
                    "enableProviderBarrierNotCaptured",
                    CdcDiagnosticCategory.StatusObservationUnavailable,
                    CdcDiagnosticComponent.ProviderBarrier,
                    "CDC enablement could not capture the provider barrier position.",
                    "uncaptured",
                    timeProvider.GetUtcNow()
                ),
                capturedBarrier.Diagnostics
            );
        }

        string? sqlServerCatalogName = SqlServerCatalogName(templateRequest, provider);

        // The Connect source partition the binding's connector commits under. SQL Server's includes the
        // catalog the connector reads, which neither the provider adapter nor the continuity classifier
        // can derive for itself, so the control plane supplies it to both the barrier and the
        // source-history steps rather than letting either fall back to absent evidence.
        string? expectedSourcePartitionHash = CdcSourcePartitionHashCalculator
            .Compute(provider, inventory.ConnectorName, sqlServerCatalogName)
            .Hash;

        CdcConnectorOffsetObservation? offsetObservation = null;
        CdcProviderBarrierObservation barrier = await PollAsync(
                async token =>
                {
                    CdcConnectResult<CdcConnectorOffsets> committedOffsets = await connectClient
                        .GetConnectorOffsetsAsync(inventory.ConnectorName, token)
                        .ConfigureAwait(false);
                    offsetObservation = observationMapper.MapOffset(
                        context,
                        binding,
                        sqlServerCatalogName,
                        committedOffsets
                    );

                    return sourcePositions.ObserveProviderBarrier(
                        new(
                            request.OperationId,
                            binding,
                            firstCaughtUp.ProjectionObservedAt,
                            capturedBarrier,
                            offsetObservation,
                            expectedSourcePartitionHash
                        )
                    );
                },
                observation => observation.BarrierState == CdcProviderBarrierState.Reached,
                controlOptions.Timeouts.ProviderBarrier,
                controlOptions.Timeouts.PollInterval,
                cancellationToken
            )
            .ConfigureAwait(false);
        evaluation = evaluation with { ProviderBarrier = barrier };

        // Connector runtime evidence is collected whatever the barrier reported, so a barrier that was
        // never reached is reported alongside the connector state that explains it.
        evaluation = evaluation with
        {
            ConnectorRuntime = observationMapper.MapRuntime(
                context,
                binding,
                await connectClient
                    .GetConnectorStatusAsync(inventory.ConnectorName, cancellationToken)
                    .ConfigureAwait(false),
                await connectClient
                    .GetConnectorOffsetsAsync(inventory.ConnectorName, cancellationToken)
                    .ConfigureAwait(false)
            ),
        };

        if (barrier.BarrierState != CdcProviderBarrierState.Reached)
        {
            return Evaluate();
        }

        // Step 9: source-history continuity, a second caught-up observation taken after it, and the
        // connector's own source lag.
        //
        // The SQL Server schema-history topic is evidence the classifier requires for that provider and
        // reports unknown continuity without. The phase reported is the first enablement's, so a state
        // that is not yet continuous leaves continuity unknown and latches no incident: the connector
        // writes its history during the snapshot, and this is the run that produced it.
        CdcSqlServerSchemaHistoryEvidence? schemaHistory = await kafkaAdmin
            .ReadSqlServerSchemaHistoryAsync(
                inventory,
                CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
                HasCommittedStreamingOffset(offsetObservation),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcSourceHistoryClassificationResult sourceHistory = await sourcePositions
            .ObserveSourceHistoryAsync(
                new(
                    request.OperationId,
                    binding,
                    providerSetupObservation.ProviderSetup,
                    offsetObservation,
                    providerSetupObservation.ProviderHistory
                )
                {
                    SqlServerSchemaHistory = schemaHistory,
                    ExpectedConnectSourcePartitionHash = expectedSourcePartitionHash,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        evaluation = evaluation with { SourceHistory = sourceHistory.Observation };
        if (sourceHistory.Observation.Continuity != CdcSourceHistoryContinuity.Healthy)
        {
            return Evaluate();
        }

        CdcProjectionCorrelationObservation secondCaughtUp = await WaitForCaughtUpAsync(
                context,
                controlOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        evaluation = evaluation with { SecondProjectionCaughtUp = secondCaughtUp };
        if (!IsCaughtUp(secondCaughtUp))
        {
            return Evaluate();
        }

        CdcConnectorLagReadResult lagReading = await lagReader
            .ReadAsync(provider, inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        evaluation = evaluation with
        {
            Lag = CdcConnectorLagObservationMapper.Map(
                context,
                lagReading,
                controlOptions.LagThreshold,
                timeProvider.GetUtcNow()
            ),
        };

        logger.LogDebug("CDC enablement collected the initial readiness evidence for the binding.");

        return Evaluate();
    }

    /// <summary>
    /// The target's combined status: every observation the shared evaluators decide readiness from,
    /// collected once and reported as it was observed.
    /// </summary>
    /// <remarks>
    /// Source-history continuity is checked on this interval like every other one, and a loss it proves
    /// is latched durably before the status is composed, so the status that reports the loss is already
    /// the latched one. The latch is written once: a later interval reads the incident back from the
    /// binding record, which keeps continuity lost whatever the artifacts, offsets, or lag then look
    /// like.
    /// </remarks>
    public async Task<CdcStatus> StatusAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProviderSetup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcCollectedTargetObservations collected = await CollectTargetObservationsAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        return Compose(collected.Evaluation);
    }

    /// <summary>
    /// Restarts the binding's connector against affirmative source-history continuity evidence, and
    /// reports the target's status either way.
    /// </summary>
    /// <remarks>
    /// Continuity is proved before the connector is started or resumed, never after. Continuity that is
    /// unknown is absent evidence rather than a healthy source, so the connector is left exactly as it
    /// is — a stopped or failed connector stays stopped — and a proved loss has already stopped it. No
    /// committed offset is deleted and nothing is re-snapshotted into the existing public topic: a
    /// current-state snapshot cannot emit tombstones for documents deleted before it, so it would leave
    /// stale state in that topic's consumers.
    ///
    /// Against affirmative continuity the request depends on what the worker is holding. A connector in
    /// <c>STOPPED</c> or <c>PAUSED</c> is resumed, because those are worker-owned target states that a
    /// restart does not clear; anything else is restarted with its tasks. A request the worker refuses
    /// is reported as its own diagnostic on the connector runtime rather than being left to be inferred
    /// from the state that is read back afterwards.
    /// </remarks>
    public async Task<CdcStatus> RestartAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProviderSetup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcCollectedTargetObservations collected = await CollectTargetObservationsAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (
            collected.Continuity != CdcSourceHistoryContinuity.Healthy
            || collected is not { BindingRecord: { } binding, Inventory: { } inventory, Context: { } context }
        )
        {
            logger.LogDebug(
                "CDC restart did not start the connector: source-history continuity is {Continuity}.",
                collected.Continuity
            );

            // The status this returns describes a connector the restart never asked the worker about,
            // which reads exactly like one it asked about and failed to start. The step says which,
            // because only this one leaves the deployment as the operator left it.
            return Compose(
                collected.Evaluation with
                {
                    StateStoreDiagnostics =
                    [
                        StatusStep(
                            CdcRestartDiagnosticCodes.NotAttempted,
                            collected.Continuity == CdcSourceHistoryContinuity.Lost
                                ? CdcDiagnosticCategory.SourceHistoryLost
                                : CdcDiagnosticCategory.ProviderHistoryUnknown,
                            CdcDiagnosticComponent.SourceHistory,
                            "CDC restart issued no connector request: source-history continuity is proved "
                                + "before a connector is started, never after.",
                            collected.Continuity.ToString(),
                            timeProvider.GetUtcNow()
                        ),
                        .. collected.Evaluation.StateStoreDiagnostics,
                    ],
                }
            );
        }

        // The artifacts a connector publishes through, proved before it is put back to work. These are
        // the same four the enablement sequence requires before it will register a connector at all,
        // read from the observations this collection already made rather than re-collected here. A
        // restart re-derives none of them: a resume issued over a mismatched transform configuration,
        // a nonconforming shared offset store, a missing grant, or a provider capture artifact that no
        // longer conforms starts publishing through it immediately, and the unhealthy status this would
        // compose afterwards cannot recall what was already produced. `cdc-streaming.md` requires a
        // post-admission restart to exact-match the binding and validate existing artifacts; this is
        // that validation.
        if (UnsatisfiedRestartPrerequisite(collected.Evaluation) is { } prerequisite)
        {
            logger.LogDebug(
                "CDC restart did not start the connector: {Component} is not satisfied.",
                prerequisite.Component
            );

            return Compose(
                collected.Evaluation with
                {
                    StateStoreDiagnostics =
                    [
                        StatusStep(
                            CdcRestartDiagnosticCodes.NotAttempted,
                            prerequisite.Category,
                            prerequisite.Component,
                            "CDC restart issued no connector request: the artifacts the connector "
                                + "publishes through are proved before it is started, never after.",
                            prerequisite.Observed,
                            timeProvider.GetUtcNow()
                        ),
                        .. collected.Evaluation.StateStoreDiagnostics,
                    ],
                }
            );
        }

        // A connector the worker is holding fenced is resumed rather than restarted. STOPPED and
        // PAUSED are target states the worker owns, and a restart clears neither: it re-creates the
        // connector and task instances, and a stopped connector has no tasks to re-create. Resuming is
        // what lets this verb start a connector, rather than only re-run one that was already running.
        bool fenced =
            collected.Evaluation.ConnectorRuntime?.ConnectorState
            is CdcConnectorRuntimeState.Stopped
                or CdcConnectorRuntimeState.Paused;

        CdcConnectResult connectorAction = fenced
            ? await connectClient
                .ResumeConnectorAsync(inventory.ConnectorName, cancellationToken)
                .ConfigureAwait(false)
            : await connectClient
                .RestartConnectorAsync(inventory.ConnectorName, cancellationToken)
                .ConfigureAwait(false);
        logger.LogDebug(
            "CDC restart asked the worker to {ConnectorAction} the connector: {Outcome}.",
            fenced ? "resume" : "restart",
            connectorAction.Outcome
        );

        // The runtime evidence is re-read so the reported status describes the connector the restart
        // left behind rather than the one that was observed before it.
        CdcTargetStatusEvaluationInput evaluation = collected.Evaluation with
        {
            ConnectorRuntime = observationMapper.MapRuntime(
                context,
                binding,
                await connectClient
                    .GetConnectorStatusAsync(inventory.ConnectorName, cancellationToken)
                    .ConfigureAwait(false),
                await connectClient
                    .GetConnectorOffsetsAsync(inventory.ConnectorName, cancellationToken)
                    .ConfigureAwait(false)
            ),
        };

        if (connectorAction.Succeeded)
        {
            return Compose(evaluation);
        }

        // A request the worker refused is reported rather than left to be inferred from the re-read
        // state: a connector still not running reads identically whether the worker acted on it and it
        // failed, or never accepted the request at all, and only the second is worth reissuing.
        string refusedMessage = fenced
            ? "CDC restart could not resume the connector the worker is holding fenced."
            : "CDC restart could not restart the connector.";
        string refusedExpectation = fenced
            ? "the connector resumed to its running target state"
            : "the connector restarted";

        return Compose(
            WithUnappliedConnectorAction(
                evaluation,
                inventory,
                connectorAction,
                CdcRestartDiagnosticCodes.NotApplied,
                CdcDiagnosticCategory.ConnectorNotRunning,
                refusedMessage,
                refusedExpectation
            )
        );
    }

    /// <summary>
    /// The first artifact prerequisite a restart may not proceed without, or null when all of them are
    /// satisfied.
    /// </summary>
    /// <remarks>
    /// One entry per gate the enablement sequence applies before it registers a connector, in the order
    /// that sequence proves them: the provider capture artifacts, the shared Connect offset store, the
    /// binding's own Kafka topics and grants, and the registered connector configuration. An absent
    /// observation is unsatisfied rather than skipped — a prerequisite that could not be observed is
    /// not one that was proved.
    /// </remarks>
    private static CdcRestartPrerequisite? UnsatisfiedRestartPrerequisite(
        CdcTargetStatusEvaluationInput evaluation
    )
    {
        if (
            evaluation.ProviderSetup is not { } providerSetup
            || !CdcTargetStatusEvaluator.IsProviderSetupSatisfied(providerSetup)
        )
        {
            return new(
                CdcDiagnosticComponent.ProviderSetup,
                CdcDiagnosticCategory.ProviderSetupInvalid,
                evaluation.ProviderSetup?.SetupOutcome.ToString() ?? "unobserved"
            );
        }

        if (
            evaluation.ConnectOffsetStore is not { } connectOffsetStore
            || !CdcTargetStatusEvaluator.IsConnectOffsetStorePolicySatisfied(connectOffsetStore)
        )
        {
            return new(
                CdcDiagnosticComponent.ConnectOffsetStore,
                CdcDiagnosticCategory.ConnectOffsetStoreInvalid,
                evaluation.ConnectOffsetStore is { } observed
                    ? $"{observed.PolicyState} / {observed.AclState}"
                    : "unobserved"
            );
        }

        if (
            evaluation.KafkaPolicy is not { } kafkaPolicy
            || !CdcTargetStatusEvaluator.IsKafkaPolicySatisfied(kafkaPolicy)
        )
        {
            return new(
                CdcDiagnosticComponent.KafkaPolicy,
                CdcDiagnosticCategory.KafkaPolicyInvalid,
                evaluation.KafkaPolicy?.PolicyState.ToString() ?? "unobserved"
            );
        }

        if (
            evaluation.ConnectorConfig is not { } connectorConfig
            || !CdcTargetStatusEvaluator.IsConnectorConfigSatisfied(connectorConfig)
        )
        {
            return new(
                CdcDiagnosticComponent.ConnectorConfig,
                CdcDiagnosticCategory.ConnectorConfigInvalid,
                evaluation.ConnectorConfig?.ConfigurationState.ToString() ?? "unobserved"
            );
        }

        return null;
    }

    /// <summary>The component that refused a restart, and what it was observed to be.</summary>
    private readonly record struct CdcRestartPrerequisite(
        CdcDiagnosticComponent Component,
        CdcDiagnosticCategory Category,
        string Observed
    );

    /// <summary>
    /// Retires one binding generation and every governed artifact it owns.
    /// </summary>
    /// <remarks>
    /// The order is operational rather than cosmetic. The connector is stopped first so it commits no
    /// further offsets; its committed offsets are deleted while it is stopped and still exists, because
    /// the worker accepts that deletion only then and deleting the connector configuration does not
    /// remove them — deleting the connector first would orphan them in the shared store forever and
    /// break a later registration of the same name. The connector configuration goes next, then the
    /// binding's own topics and ACLs, then the provider capture artifacts, and the binding record last,
    /// through the verified-cleanup operation that removes the terminal incident state with it.
    ///
    /// The shared cluster-scoped Connect offset store is never touched and never appears in the proof:
    /// it is worker state for every binding, not a binding artifact. A step that fails ends the
    /// retirement with no proof and the binding record intact, so the retry finds the record that names
    /// what is left. An artifact that is already gone is reported as not found rather than as a failure,
    /// which is also how a binding whose artifacts were never created proves that none of them exists.
    /// </remarks>
    public async Task<CdcContractReadResult<CdcCleanupProof>> RetireAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProviderSetup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        CoreCdc.CdcProvider provider = eligibilityProbe.Provider;
        string dataStoreId = request.DataStoreId.ToString(CultureInfo.InvariantCulture);

        CdcTargetValidationResult targetValidation = CdcTargetValidator.Validate(
            TargetInput(controlOptions, request.TenantKey, dataStoreId, provider)
        );
        if (targetValidation.Target is not { } target)
        {
            return RetirementRefused(
                CdcDiagnosticCategory.TargetMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC retirement rejected the requested target.",
                "$.bindingIdentity",
                "an invalid target",
                now,
                targetValidation.Diagnostics
            );
        }

        CdcBindingLifecycleResult bindingRead = await bindingLifecycle
            .ReadBindingAsync(target.ToBindingIdentity(), cancellationToken)
            .ConfigureAwait(false);

        // Without the record there is nothing this retirement may name: the governed artifacts are the
        // record's, and automation never infers a binding from the artifacts that happen to exist.
        if (bindingRead.State?.Binding is not { } binding)
        {
            return RetirementRefused(
                CdcDiagnosticCategory.BindingMissing,
                CdcDiagnosticComponent.Binding,
                "CDC retirement requires the durable binding record of the generation it retires.",
                "$.bindingIdentity",
                bindingRead.State?.State.ToString() ?? bindingRead.Status.ToString(),
                now,
                bindingRead.Diagnostics
            );
        }

        // The binding names the provider its artifacts were created under, and this deployment's
        // adapters are for one provider only. Checked here rather than at the provider step, because
        // the provider step is the last one: a record naming another provider would otherwise have its
        // connector stopped and deleted and its topics and grants removed before the mismatch was
        // reached, and the database teardown that failed is the only step that would have reported it.
        // Adoption refuses the same mismatch for the same reason.
        if (binding.Provider != provider)
        {
            return RetirementRefused(
                CdcDiagnosticCategory.ProviderMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC retirement requires the binding record to name this deployment's provider.",
                "$.binding.provider",
                binding.Provider.ToString(),
                now,
                []
            );
        }

        CdcArtifactNameResult artifactNames = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        if (artifactNames.Inventory is not { } inventory)
        {
            return RetirementRefused(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC retirement could not recover the governed artifact names from the binding record.",
                "$.governedArtifacts",
                "unrecoverable",
                now,
                artifactNames.Diagnostics
            );
        }

        // The record names governed artifacts in one physical database, and the connection string is
        // not proof that this is that one. A retirement of a superseded generation pointed at the
        // target's current source finds none of that generation's provider artifacts, records every one
        // as absent, issues a cleanup proof for a database that never held them, and then deletes the
        // one record that named the real ones — while the old source keeps its publication and slot, or
        // its capture instances and gating role. So the source is proved here, before the fence, rather
        // than inferred at the end from a teardown that found nothing. A generation whose source is no
        // longer the target's current source is retirable only against that source's own connection,
        // and without one, refusing is the only honest answer.
        (DbConnection? sourceConnection, string? sourceConnectionRefusal) =
            await OpenProviderConnectionWithinBudgetAsync(
                    provider,
                    request.ConnectionString,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (sourceConnection is null)
        {
            return RetirementRefused(
                CdcDiagnosticCategory.ProviderSetupInvalid,
                CdcDiagnosticComponent.ProviderSetup,
                "CDC retirement could not reach the instance database it proves the binding's physical "
                    + "source against.",
                "$.binding",
                sourceConnectionRefusal ?? "unavailable",
                now,
                []
            );
        }

        string? observedSourceFingerprint;
        await using (sourceConnection)
        {
            // The same validate-only pass every other verb reads the live source identity from. Nothing
            // is provisioned or removed by it, and only the fingerprint is taken: whether the artifacts
            // themselves are still there is what the teardown below reports.
            CdcProviderSetupResult boundSource = await SetupProviderWithinBudgetAsync(
                    ProviderSetupRequest(
                        request.ProviderSetup,
                        provider,
                        binding.PhysicalSourceFingerprint,
                        inventory,
                        sourceConnection,
                        DdlCdc.CdcProviderSetupMode.ValidateOnly
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            observedSourceFingerprint = boundSource.ObservedSourceFingerprint?.Value;
        }

        if (
            !string.Equals(
                observedSourceFingerprint,
                binding.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            return RetirementRefused(
                CdcDiagnosticCategory.SourceMismatch,
                CdcDiagnosticComponent.ProviderSetup,
                "CDC retirement requires the connected database to be the binding's own physical source.",
                "$.binding.physicalSourceFingerprint",
                observedSourceFingerprint is null ? "unreadable" : "a different physical source",
                now,
                []
            );
        }

        List<CdcGovernedArtifact> governedArtifacts = [];

        // (1) Fence the connector so it commits no further offsets.
        CdcConnectResult fence = await connectClient
            .StopConnectorAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        if (!fence.Succeeded && fence.Outcome != CdcConnectOutcome.NotFound)
        {
            return RetirementFailed(
                CdcDiagnosticComponent.ConnectorRuntime,
                "CDC retirement could not stop the connector it retires.",
                "$.governedArtifacts",
                fence.Outcome.ToString(),
                timeProvider.GetUtcNow()
            );
        }

        // (2) Delete the committed offsets while the connector is stopped and still exists.
        //
        // A connector the worker does not have is the one case this cannot answer. Deleting a connector's
        // configuration leaves its committed offsets in the shared store, and the worker answers 404 for
        // an offsets delete only because the connector is absent — never because the store holds no
        // offsets under that name. Reading that 404 as an absence would issue a cleanup proof for offsets
        // that may still be there and then delete the one record that names them, so retirement refuses
        // unless the operator has taken that judgement on themselves.
        if (fence.Outcome == CdcConnectOutcome.NotFound)
        {
            if (!request.ConnectorAlreadyAbsent)
            {
                return RetirementRefused(
                    CdcDiagnosticCategory.ConnectOffsetStoreInvalid,
                    CdcDiagnosticComponent.ConnectOffsetStore,
                    "CDC retirement cannot observe the committed source offsets of a connector the worker "
                        + "does not have, and the shared offset store may still hold them under this name.",
                    "$.governedArtifacts",
                    inventory.ConnectorName,
                    timeProvider.GetUtcNow(),
                    []
                );
            }

            // The proof records whose assertion this is, because the worker made none. A reason that
            // spoke for the worker here would read as evidence in a proof that outlives the record.
            //
            // Neither case the switch is documented for can leave committed offsets behind. A
            // connector that was never registered committed none, and a retirement that removed a
            // connector deleted its offsets first - step (2) runs while the connector is stopped and
            // still exists, and step (3) only then removes the configuration - so an absence this
            // control plane produced is an absence of offsets too. What is left is a connector
            // deleted outside this control plane, which is exactly the judgement the switch names.
            governedArtifacts.Add(
                new CdcGovernedArtifact(
                    CdcGovernedArtifactKind.ConnectSourceOffsets,
                    inventory.ConnectorName,
                    CdcCleanupState.NotFound,
                    "the operator asserted the connector was already absent, so the Kafka Connect worker "
                        + "could not report on its committed source offsets"
                )
            );
        }
        else
        {
            // The fence found the connector and stopped it, so the worker owes a definite answer here: a
            // 404 now would contradict the connector it reported a moment ago.
            CdcConnectResult offsets = await connectClient
                .DeleteConnectorOffsetsAsync(inventory.ConnectorName, cancellationToken)
                .ConfigureAwait(false);
            if (!offsets.Succeeded)
            {
                return RetirementFailed(
                    CdcDiagnosticComponent.ConnectOffsetStore,
                    "CDC retirement could not delete the connector's committed source offsets.",
                    "$.governedArtifacts",
                    offsets.Outcome.ToString(),
                    timeProvider.GetUtcNow()
                );
            }

            governedArtifacts.Add(
                Artifact(
                    CdcGovernedArtifactKind.ConnectSourceOffsets,
                    inventory.ConnectorName,
                    CdcCleanupState.Deleted
                )
            );
        }

        // (3) Delete the connector configuration.
        CdcConnectResult connector = await connectClient
            .DeleteConnectorAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        if (ToCleanupState(connector) is not { } connectorRemoval)
        {
            return RetirementFailed(
                CdcDiagnosticComponent.ConnectorRuntime,
                "CDC retirement could not delete the connector configuration.",
                "$.governedArtifacts",
                connector.Outcome.ToString(),
                timeProvider.GetUtcNow()
            );
        }

        governedArtifacts.Add(
            Artifact(CdcGovernedArtifactKind.KafkaConnectConnector, inventory.ConnectorName, connectorRemoval)
        );

        // (4) The binding's own topics and ACLs. The shared Connect offset store is not among them.
        try
        {
            governedArtifacts.AddRange(
                await kafkaAdmin
                    .DeleteBindingArtifactsAsync(inventory, cancellationToken)
                    .ConfigureAwait(false)
            );
        }
        catch (KafkaException exception)
        {
            // The broker response body is never surfaced verbatim; the error code is bounded evidence.
            return RetirementFailed(
                CdcDiagnosticComponent.KafkaPolicy,
                "CDC retirement could not remove the binding's governed Kafka artifacts.",
                "$.governedArtifacts",
                exception.Error.Code.ToString(),
                timeProvider.GetUtcNow()
            );
        }

        // (5) The provider capture artifacts, under the same step budget every provider pass runs
        // under. A teardown that spends it ends the retirement with no proof and the binding record
        // intact, which is what any other failed step here does.
        using CancellationTokenSource providerBudget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        providerBudget.CancelAfter(controlOptions.Timeouts.ProviderSetup);

        try
        {
            await using DbConnection connection = connectionFactory.Create(
                provider,
                request.ConnectionString
            );
            await connection.OpenAsync(providerBudget.Token).ConfigureAwait(false);

            governedArtifacts.AddRange(
                await providerArtifactTeardown
                    .DeleteAsync(
                        new(
                            inventory,
                            request.ProviderSetup.ExpectedSourceInventory,
                            new DbConnectionCdcProviderDatabaseExecutor(connection)
                        ),
                        providerBudget.Token
                    )
                    .ConfigureAwait(false)
            );
        }
        catch (DbException exception)
        {
            // A provider message quotes connection settings, so only the rejection's type is reported.
            return RetirementFailed(
                CdcDiagnosticComponent.ProviderSetup,
                "CDC retirement could not remove the binding's provider capture artifacts.",
                "$.governedArtifacts",
                exception.GetType().Name,
                timeProvider.GetUtcNow()
            );
        }
        catch (InvalidOperationException exception)
        {
            return RetirementFailed(
                CdcDiagnosticComponent.ProviderSetup,
                "CDC retirement could not remove the binding's provider capture artifacts.",
                "$.governedArtifacts",
                exception.GetType().Name,
                timeProvider.GetUtcNow()
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget, not the caller: reported as the step failure it is rather than propagating a
            // cancellation the caller never asked for.
            return RetirementFailed(
                CdcDiagnosticComponent.ProviderSetup,
                "CDC retirement could not remove the binding's provider capture artifacts.",
                "$.governedArtifacts",
                "timedOut",
                timeProvider.GetUtcNow()
            );
        }

        CdcCleanupProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            request.OperationId,
            timeProvider.GetUtcNow(),
            binding.ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            governedArtifacts
        );

        // The proof must account for every artifact the binding governs before it can authorize the
        // record's removal: an incomplete teardown never becomes a deleted binding.
        CdcContractValidationResult proofValidation = CdcCleanupProofValidator.Validate(
            proof,
            binding,
            timeProvider.GetUtcNow()
        );
        if (!proofValidation.Succeeded)
        {
            return CdcContractReadResult<CdcCleanupProof>.Failure(proofValidation.Diagnostics);
        }

        // (6) and (7): the terminal incident state and the binding record, removed together by the
        // verified-cleanup operation that owns both and runs last.
        CdcBindingLifecycleResult deletion = await bindingLifecycle
            .DeleteStateAfterVerifiedCleanupAsync(proof, cancellationToken)
            .ConfigureAwait(false);
        if (deletion.Status != CdcControlPlaneOperationStatus.Succeeded)
        {
            // Every governed artifact is already gone by this point, so this is the most incomplete a
            // retirement gets rather than a refusal: the record that survives is what the retry reads
            // to finish, and it is the only thing left to remove.
            return RetirementIncomplete(
                CdcDiagnosticCategory.LocalStateUnavailable,
                CdcDiagnosticComponent.StateStore,
                "CDC retirement could not delete the binding record after verified cleanup.",
                "$.bindingIdentity",
                deletion.Status.ToString(),
                timeProvider.GetUtcNow(),
                deletion.Diagnostics
            );
        }

        logger.LogDebug(
            "CDC retirement removed generation {Generation} and its {ArtifactCount} governed artifacts.",
            binding.Generation,
            governedArtifacts.Count
        );

        return CdcContractReadResult<CdcCleanupProof>.Success(proof);
    }

    /// <summary>
    /// The cleanup state a connector-configuration removal reports, or null when the worker's answer is
    /// not evidence that the configuration is gone.
    /// </summary>
    /// <remarks>
    /// A 404 is an absence here because the configuration is the thing being asked about: a connector the
    /// worker cannot find is a connector it does not have. That reading does not carry to the committed
    /// offsets, which survive their connector's configuration and live in the cluster-scoped store.
    /// </remarks>
    private static CdcCleanupState? ToCleanupState(CdcConnectResult result) =>
        result switch
        {
            { Succeeded: true } => CdcCleanupState.Deleted,
            { Outcome: CdcConnectOutcome.NotFound } => CdcCleanupState.NotFound,
            _ => null,
        };

    private static CdcGovernedArtifact Artifact(
        CdcGovernedArtifactKind artifactKind,
        string artifactName,
        CdcCleanupState cleanupState
    ) =>
        new(
            artifactKind,
            artifactName,
            cleanupState,
            cleanupState == CdcCleanupState.Deleted
                ? "the Kafka Connect worker reported the governed artifact and it was removed"
                : "the Kafka Connect worker reported no such governed artifact"
        );

    /// <summary>
    /// Reports a step that ended a retirement after it had begun removing governed artifacts. No proof
    /// is issued, so the binding record stays and names what a retry must finish removing.
    /// </summary>
    private static CdcContractReadResult<CdcCleanupProof> RetirementFailed(
        CdcDiagnosticComponent component,
        string message,
        string path,
        string observed,
        DateTimeOffset observedAt
    ) =>
        RetirementIncomplete(
            CdcDiagnosticCategory.ArtifactNotRemoved,
            component,
            message,
            path,
            observed,
            observedAt,
            []
        );

    /// <summary>
    /// Reports a retirement that stopped partway. Reissuing it is the way it completes, because the
    /// binding record it did not reach still names every artifact that is left.
    /// </summary>
    private static CdcContractReadResult<CdcCleanupProof> RetirementIncomplete(
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string path,
        string observed,
        DateTimeOffset observedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) =>
        Retirement(
            CdcRetirementDiagnosticCodes.IncompleteRetryable,
            retryable: true,
            "every governed artifact removed before the binding record",
            category,
            component,
            message,
            path,
            observed,
            observedAt,
            diagnostics
        );

    /// <summary>
    /// Reports a retirement refused before it changed anything.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="RetirementIncomplete"/> because the two need opposite handling and
    /// are indistinguishable to a caller once they share a code: reissuing a partial teardown is how it
    /// finishes, while reissuing a refusal repeats a request that was wrong the first time. Every
    /// refusal reported through here is decided before the connector fence, or on a fence that found no
    /// connector to stop, so the deployment is exactly as the operator left it.
    /// </remarks>
    private static CdcContractReadResult<CdcCleanupProof> RetirementRefused(
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string path,
        string observed,
        DateTimeOffset observedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) =>
        Retirement(
            CdcRetirementDiagnosticCodes.RefusedNoMutation,
            retryable: false,
            "a retirement request this deployment can begin",
            category,
            component,
            message,
            path,
            observed,
            observedAt,
            diagnostics
        );

    private static CdcContractReadResult<CdcCleanupProof> Retirement(
        string code,
        bool retryable,
        string expected,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string path,
        string observed,
        DateTimeOffset observedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) =>
        CdcContractReadResult<CdcCleanupProof>.Failure([
            new CdcDiagnostic(
                code,
                category,
                CdcDiagnosticSeverity.Error,
                component,
                observedAt,
                message,
                retryable,
                artifactKind: "cdcRetirement",
                expected: expected,
                observed: observed
            ).WithPath(path),
            .. diagnostics,
        ]);

    /// <summary>
    /// Replaces the physical source behind an enabled target with a new binding generation.
    /// </summary>
    /// <remarks>
    /// Every refusal that can be decided without reading state the replacement itself creates is
    /// decided before anything is changed, because the first thing this does change is fence the
    /// outgoing connector, and a target that cannot be replaced must not be left with its publication
    /// stopped. That includes the two the enablement sequence would otherwise reach only afterwards:
    /// the target must be one the DMS projector is configured to project, and the projector's status
    /// endpoint must answer, because the replacing generation's readiness is collected from it. Both
    /// are read-only and neither depends on the cutover, so both are settled here; the enablement
    /// sequence still runs them for itself, since it is also entered directly. Once the fence is
    /// applied the replacement is under way, and a later step that cannot produce its evidence ends it
    /// with the outgoing generation fenced, which is the cutover's own semantics rather than a refusal.
    ///
    /// The outgoing connector is stopped rather than deleted: stopping fences it from the source it is
    /// being replaced from while leaving its configuration and committed offsets for the retirement
    /// that removes them in order.
    ///
    /// The rotated source identity reaches durable state through the new generation's binding record,
    /// which the enablement sequence creates from the fingerprint it reads out of the replacing
    /// database. Nothing rewrites the outgoing record, and no artifact of the outgoing generation is
    /// reused: every governed name carries the generation, and a collision refuses the replacement
    /// rather than being provisioned over. The outgoing generation is retained until an explicit
    /// retirement removes it.
    /// </remarks>
    public async Task<CdcAdmission> ReplaceSourceAsync(
        CdcReplaceSourceRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProvisioningEvidence);
        ArgumentNullException.ThrowIfNull(request.ProviderSetup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        CoreCdc.CdcProvider provider = eligibilityProbe.Provider;
        string dataStoreId = request.DataStoreId.ToString(CultureInfo.InvariantCulture);
        CdcTargetIdentity unvalidatedTarget = UnvalidatedTargetIdentity(
            controlOptions,
            request.TenantKey,
            dataStoreId,
            provider
        );

        CdcTargetValidationResult targetValidation = CdcTargetValidator.Validate(
            TargetInput(controlOptions, request.TenantKey, dataStoreId, provider)
        );
        if (targetValidation.Target is not { } target)
        {
            return Refused(
                request.OperationId,
                unvalidatedTarget,
                CdcDiagnosticCategory.TargetMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement rejected the requested target.",
                "an invalid target",
                targetValidation.Diagnostics
            );
        }

        CdcTargetIdentity targetIdentity = target.ToTargetIdentity();

        // The replacing generation must advance past the one it replaces: every governed artifact name
        // carries the generation, so a generation that does not advance names the outgoing artifacts.
        if (target.Generation <= request.PreviousGeneration)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.BindingMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement requires a binding generation later than the one it replaces.",
                target.Generation.ToString(CultureInfo.InvariantCulture),
                []
            );
        }

        CdcBindingLifecycleResult previousRead = await bindingLifecycle
            .ReadBindingAsync(
                target.ToBindingIdentity() with
                {
                    Generation = request.PreviousGeneration,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            previousRead.Status
            is CdcControlPlaneOperationStatus.StateStoreUnavailable
                or CdcControlPlaneOperationStatus.InvalidOperation
        )
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.LocalStateUnavailable,
                CdcDiagnosticComponent.StateStore,
                "CDC source replacement could not read the durable state of the generation it replaces.",
                previousRead.Status.ToString(),
                previousRead.Diagnostics
            );
        }

        // Source replacement is supported only for a source this deployment enabled through the
        // new-database path; without that generation's record there is nothing being replaced.
        if (previousRead.State?.Binding is not { } previousBinding)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.BindingMissing,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement requires the durable binding record of the generation it replaces.",
                previousRead.State?.State.ToString() ?? "absent",
                []
            );
        }

        // The record names the provider its artifacts were created under, and this deployment's
        // adapters are for one provider only. The binding identity carries no provider, so a record
        // written under the other engine is readable at these very coordinates. Checked here, before
        // the artifact-name recovery below and well before the cutover barrier: this operation's first
        // change to the deployment is fencing that generation's connector, and a replacement run by a
        // control plane that can neither validate nor retire the artifacts left behind must not stop
        // it. Retirement and adoption refuse the same mismatch for the same reason.
        if (previousBinding.Provider != provider)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.ProviderMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement requires the binding record of the generation it replaces to "
                    + "name this deployment's provider.",
                previousBinding.Provider.ToString(),
                []
            );
        }

        if (
            previousRead.State.State == CdcBindingState.IncidentLatched
            || previousRead.State.Incident is not null
        )
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.SourceHistoryLost,
                CdcDiagnosticComponent.SourceHistory,
                "CDC source replacement cannot recover a generation whose source-history loss is terminal.",
                previousRead.State.State.ToString(),
                []
            );
        }

        CdcArtifactNameResult previousNames = CdcArtifactNameGenerator.RecoverFromBinding(previousBinding);
        CdcArtifactNameResult replacementNames = CdcArtifactNameGenerator.Render(
            new(target.DeploymentKey, target.TopicPrefix, target.InstanceKey, target.Generation, provider)
        );
        if (
            previousNames.Inventory is not { } previousInventory
            || replacementNames.Inventory is not { } replacementInventory
        )
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.ArtifactNameMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement could not resolve the governed artifact names of both generations.",
                "unresolvable",
                [.. previousNames.Diagnostics, .. replacementNames.Diagnostics]
            );
        }

        if (SharedGovernedArtifactName(previousInventory, replacementInventory) is { } sharedName)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.UnexpectedArtifact,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement never reuses a governed artifact of the generation it replaces.",
                sharedName,
                []
            );
        }

        // The durable state of the generation this creates, read before the probe below so its own
        // stamp precedes the clock the classification runs against. A replacement that made its
        // binding durable and activated tracking before failing at a later step is a retry of that
        // generation rather than a first attempt at it, and only this record can tell the two apart.
        CdcBindingLifecycleResult replacementRead = await bindingLifecycle
            .ReadBindingAsync(target.ToBindingIdentity(), cancellationToken)
            .ConfigureAwait(false);
        if (
            replacementRead.Status
            is CdcControlPlaneOperationStatus.StateStoreUnavailable
                or CdcControlPlaneOperationStatus.InvalidOperation
        )
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.LocalStateUnavailable,
                CdcDiagnosticComponent.StateStore,
                "CDC source replacement could not read the durable state of the generation it creates.",
                replacementRead.Status.ToString(),
                replacementRead.Diagnostics
            );
        }

        CdcContractReadResult<InitialCdcProvisioningProof> issuedProof = CdcProvisioningProofFactory.Issue(
            new(request.OperationId, targetIdentity, null),
            request.ProvisioningEvidence,
            now
        );
        if (issuedProof.Contract is not { } provisioningProof)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.MalformedProof,
                CdcDiagnosticComponent.ProofValidation,
                "CDC source replacement requires the operator's explicit provisioning evidence.",
                "refused",
                issuedProof.Diagnostics
            );
        }

        // One read-only observation of the replacing source, and both refusals it can settle, before
        // the outgoing connector is fenced.
        InitialCdcEligibilityObservation eligibility = await eligibilityProbe
            .ProbeAsync(
                new(
                    new(request.OperationId, targetIdentity, null),
                    provisioningProof,
                    request.ConnectionString
                )
                {
                    CommandTimeout = controlOptions.Timeouts.EligibilityProbe,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        DateTimeOffset probedAt = timeProvider.GetUtcNow();

        // A published cache-ahead latch is a projection state a replacement cannot clear, and a latch
        // that cannot be read is not a clear one. Either way the source is not replaceable. This is
        // the stricter of the two checks on that dimension — the classifier below rejects only a
        // published latch — so it stays as its own refusal rather than being folded into it.
        if (eligibility.CacheAheadState != CdcCacheAheadState.Clear)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.ProjectionNonOperational,
                CdcDiagnosticComponent.Projection,
                "CDC source replacement cannot proceed while the cache-ahead recovery latch is published.",
                eligibility.CacheAheadState.ToString(),
                eligibility.Diagnostics
            );
        }

        // The replacing source must be a different physical source than the one it replaces. A restore,
        // rollback, or copied backup carries the replaced database's own dms.DataStoreIdentity row, so
        // its fingerprint is that of the source being replaced until the identity is rotated. Binding a
        // new generation to an unrotated identity would publish one physical source under two
        // generations, and the replacement would report the same source it was supposed to replace.
        if (
            eligibility.PhysicalSourceFingerprint is { } replacingFingerprint
            && string.Equals(
                replacingFingerprint,
                previousBinding.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.SourceMismatch,
                CdcDiagnosticComponent.ProviderSetup,
                "CDC source replacement requires the replacing source's identity to have been rotated "
                    + "away from the generation it replaces.",
                "retained",
                eligibility.Diagnostics
            );
        }

        // The rest of what makes a source bindable, asked of the classifier that owns the rule rather
        // than restated here: a lifecycle already tracking, resetting or rebuilding, and pre-capture
        // rows the replacing generation would capture over. The enablement sequence runs this same
        // classification against this same observation, so a source that fails it is going to be
        // refused either way — and refusing it here is what keeps a replacement that cannot proceed
        // from stopping the outgoing generation's publication first. The enablement still classifies
        // for itself, because it is also entered directly.
        //
        // Which of the two classifications is the one the enablement will run, decided from the
        // replacing generation's own record rather than assumed to be the unbound one. A replacement
        // that reached step 4 and then failed has activated tracking on the replacing source, and the
        // unbound classifier rejects exactly that as `RejectUnboundTracking` — so a preflight fixed on
        // it would refuse the reissue of a replacement that is merely unfinished, while a direct
        // `cdc enable` cannot rescue it either: the generation this fenced is still bound, which is
        // what `CdcSecondGenerationRule` refuses. The retry classification is the enablement's own,
        // over the same record it will read for itself once the fence is applied.
        bool replacementFirstAttempt =
            replacementRead.Status == CdcControlPlaneOperationStatus.BindingMissing;

        bool canBind;
        string classificationObserved;
        IReadOnlyList<CdcDiagnostic> classificationDiagnostics;
        if (replacementFirstAttempt)
        {
            CdcInitialEnablePreBindingEligibilityResult preBinding =
                CdcInitialEnableRetryClassifier.EvaluatePreBindingEligibility(
                    new(
                        request.OperationId,
                        probedAt,
                        probedAt,
                        targetIdentity,
                        eligibility.PhysicalSourceFingerprint,
                        provisioningProof,
                        eligibility
                    )
                );
            canBind = preBinding.CanCreateBinding;
            classificationObserved = ClassificationObserved(preBinding.Rejection);
            classificationDiagnostics = preBinding.Rejection?.Diagnostics ?? preBinding.Diagnostics;
        }
        else
        {
            CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
                new(
                    request.OperationId,
                    probedAt,
                    probedAt,
                    targetIdentity,
                    eligibility.PhysicalSourceFingerprint,
                    provisioningProof,
                    eligibility,
                    replacementRead.State
                )
            );
            canBind = retry.Action == CdcRetryAction.Proceed;
            classificationObserved = ClassificationObserved(retry);
            classificationDiagnostics = retry.Diagnostics;
        }

        if (!canBind)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.InvalidObservation,
                CdcDiagnosticComponent.Retry,
                "CDC source replacement requires a replacing source the enablement sequence can bind.",
                classificationObserved,
                classificationDiagnostics
            );
        }

        // The replacing generation's own enablement will refuse without both of these, and both are
        // reads: the projection target is a configuration fact, and the status endpoint is a GET. They
        // are answered here rather than after the fence below, because a replacement refused for either
        // one would otherwise leave the outgoing generation stopped and nothing replacing it.
        CdcExplicitProjectionTargetProofResult projectionTarget = projectionTargetProof.Prove(
            target,
            timeProvider.GetUtcNow()
        );
        if (!projectionTarget.Succeeded)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.TargetMismatch,
                CdcDiagnosticComponent.Projection,
                "CDC source replacement requires the target to be configured on the DMS projector itself.",
                projectionTarget.State.ToString(),
                projectionTarget.Diagnostics
            );
        }

        CdcProjectionCorrelationObservation preflight = await projectionCorrelation
            .CollectAsync(new(request.OperationId, targetIdentity, null), cancellationToken)
            .ConfigureAwait(false);
        if (preflight.CorrelationState == CdcProjectionCorrelationState.Unavailable)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.StatusObservationUnavailable,
                CdcDiagnosticComponent.Projection,
                "CDC source replacement could not read the running DMS projection status the replacing "
                    + "generation must observe caught-up evidence from.",
                preflight.CorrelationState.ToString(),
                preflight.Diagnostics
            );
        }

        // The cutover barrier: the outgoing connector is fenced so it publishes nothing further from the
        // source being replaced. A connector the worker does not hold is already fenced.
        CdcConnectResult fence = await connectClient
            .StopConnectorAsync(previousInventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        if (!fence.Succeeded && fence.Outcome != CdcConnectOutcome.NotFound)
        {
            // The message does not claim the outgoing generation is still publishing, because this
            // refusal cannot establish that. A stop the worker refused outright leaves it publishing,
            // but a stop the worker accepted and did not settle out of within the wait's budget
            // reports the same outcome, and that connector has very likely stopped - the worker
            // applies a stop asynchronously. Saying "nothing changed" would be wrong half the time,
            // and the half it is wrong about is the one where publication has already stopped.
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.ConnectorNotRunning,
                CdcDiagnosticComponent.ConnectorRuntime,
                "CDC source replacement did not prove the connector of the generation it replaces was "
                    + "fenced. That generation may or may not still be publishing: the worker applies a "
                    + "stop asynchronously, so reissue the replacement to observe and act on the state "
                    + "it actually reached.",
                fence.Outcome.ToString(),
                [],
                // The one replace-source refusal a reissue can resolve without the operator changing
                // anything: an unsettled stop settles, and the retry then observes STOPPED and
                // proceeds. Every other refusal here names a fact that has to be changed first.
                retryable: true
            );
        }

        logger.LogDebug(
            "CDC source replacement fenced generation {Generation} and is enabling generation {Replacement}.",
            request.PreviousGeneration,
            target.Generation
        );

        return await EnableCoreAsync(
                new CdcEnableRequest(
                    request.OperationId,
                    request.TenantKey,
                    request.DataStoreId,
                    request.ConnectionString,
                    request.ProvisioningEvidence,
                    request.ProviderSetup
                ),
                request.PreviousGeneration,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What a refused replacement classification was observed to be. Both classifiers can decline
    /// without naming a rejection, which is reported as the refusal it is rather than as an absence.
    /// </summary>
    private static string ClassificationObserved(CdcRetry? rejection) =>
        rejection is { } retry ? $"{retry.RetryClassification} / {retry.Action}" : "rejected";

    /// <summary>
    /// Why an unbound enablement may not create its binding, or null when the broker and the worker
    /// both answered and neither holds a governed external artifact of this inventory.
    /// </summary>
    /// <remarks>
    /// Evidence that could not be obtained refuses exactly as an artifact that exists does. A broker or
    /// worker that did not answer establishes neither presence nor absence, and silently adopting an
    /// artifact set is the outcome this guard exists to prevent — so the unreadable answer is the
    /// refusing one. Only the worker's own 404 proves the connector is absent: every other outcome
    /// either found it or failed to look.
    ///
    /// The provider capture artifacts are deliberately not asked about here. They are governed too, but
    /// the provider pass creates or exact-matches them against the binding this enablement is about to
    /// write, which is the design's own first-enablement rule for them, and they live in the database
    /// the eligibility observation has already classified.
    /// </remarks>
    private static CdcUnboundArtifactRefusal? UnboundGovernedArtifactRefusal(
        CdcArtifactInventory inventory,
        CdcKafkaGovernedTopicPresence topicPresence,
        CdcConnectResult<IReadOnlyDictionary<string, string>> connector
    )
    {
        if (!topicPresence.Readable)
        {
            return new(
                CdcDiagnosticCategory.KafkaPolicyInvalid,
                "the broker could not prove the governed topics are absent"
            );
        }

        if (topicPresence.ExistingTopicNames.Count != 0)
        {
            return new(CdcDiagnosticCategory.UnexpectedArtifact, topicPresence.ExistingTopicNames[0]);
        }

        if (connector.Outcome == CdcConnectOutcome.Succeeded)
        {
            return new(CdcDiagnosticCategory.UnexpectedArtifact, inventory.ConnectorName);
        }

        if (connector.Outcome != CdcConnectOutcome.NotFound)
        {
            return new(
                CdcDiagnosticCategory.ConnectorConfigInvalid,
                $"the worker could not prove the connector is absent: {connector.Outcome}"
            );
        }

        return null;
    }

    /// <summary>The category and observation of a refused unbound enablement.</summary>
    private readonly record struct CdcUnboundArtifactRefusal(CdcDiagnosticCategory Category, string Observed);

    /// <summary>
    /// The first governed artifact name the replacing generation would share with the generation it
    /// replaces, or null when the two name sets are disjoint.
    /// </summary>
    private static string? SharedGovernedArtifactName(
        CdcArtifactInventory previous,
        CdcArtifactInventory replacement
    )
    {
        HashSet<string> previousNames = [.. previous.GovernedArtifacts.Select(artifact => artifact.Name)];

        return replacement
            .GovernedArtifacts.Select(artifact => artifact.Name)
            .FirstOrDefault(previousNames.Contains);
    }

    /// <summary>
    /// The admission a refused operation reports: no step observed its evidence, and the refusal names
    /// what stopped it.
    /// </summary>
    /// <param name="retryable">
    /// Whether reissuing the same operation could reach a different answer. False for the refusals
    /// that name a fact the operator must change first - an invalid target, a generation that does not
    /// advance, an unrotated source identity, a latched incident. True only where the refusal names
    /// something the deployment may settle on its own.
    /// </param>
    private CdcAdmission Refused(
        string operationId,
        CdcTargetIdentity targetIdentity,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string observed,
        IReadOnlyList<CdcDiagnostic> diagnostics,
        bool retryable = false
    )
    {
        DateTimeOffset observedAt = timeProvider.GetUtcNow();

        return CdcInitialAdmissionEvaluator.Evaluate(
            new(operationId, observedAt, observedAt, targetIdentity, null, null, null, null)
            {
                StateStoreDiagnostics =
                [
                    new CdcDiagnostic(
                        "replaceSourceRefused",
                        category,
                        CdcDiagnosticSeverity.Error,
                        component,
                        observedAt,
                        message,
                        retryable,
                        artifactKind: "cdcSourceReplacement",
                        expected: "a replaceable source and a new binding generation",
                        observed: observed
                    ).WithPath("$.steps"),
                    .. diagnostics,
                ],
            }
        );
    }

    /// <summary>
    /// Adopts an already provisioned artifact set under an operator-supplied binding record.
    /// </summary>
    /// <remarks>
    /// Nothing here is inferred and nothing is provisioned. The operator's record names every artifact,
    /// each of those artifacts is read back live, and the record becomes durable only once all eight
    /// verifications are exact matches. Every read is a describe: a pass that created an absent topic or
    /// repaired a missing grant would make adoption a first-time enablement path, which it is not. A
    /// refused adoption therefore leaves the deployment exactly as it found it — no binding record, no
    /// artifact, and no latched incident, even when it proves the source history is already lost.
    /// </remarks>
    public async Task<CdcContractReadResult<CdcAdoptionProof>> AdoptAsync(
        CdcAdoptRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.ProviderSetup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcControlOptions controlOptions = options.Value;
        CdcBinding binding = request.Binding;
        CoreCdc.CdcProvider provider = binding.Provider;
        DateTimeOffset now = timeProvider.GetUtcNow();

        // The control plane's provider adapters are the deployment's own. A record naming another
        // provider would be verified against a source, a barrier, and a history this process cannot read.
        if (provider != eligibilityProbe.Provider)
        {
            return CdcContractReadResult<CdcAdoptionProof>.Failure([
                AdoptionRefused(
                    CdcDiagnosticCategory.ProviderMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC adoption requires the supplied binding record to name this deployment's provider.",
                    "$.binding.provider",
                    provider.ToString(),
                    now
                ),
            ]);
        }

        CdcArtifactNameResult artifactNames = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        if (artifactNames.Inventory is not { } inventory)
        {
            return CdcContractReadResult<CdcAdoptionProof>.Failure([
                AdoptionRefused(
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC adoption could not recover the governed artifact names from the supplied binding record.",
                    "$.binding",
                    "unrecoverable",
                    now
                ),
                .. artifactNames.Diagnostics,
            ]);
        }

        List<CdcAdoptionVerificationResult> verifications = [];
        List<CdcDiagnostic> refusals = [];

        void Verify(CdcAdoptionVerificationKind kind, bool exactMatch, string evidence, string observed)
        {
            if (exactMatch)
            {
                verifications.Add(new(kind, CdcAdoptionVerificationState.ExactMatch, evidence));
                return;
            }

            refusals.Add(VerificationRefused(kind, observed, timeProvider.GetUtcNow()));
        }

        (DbConnection? openedConnection, string? connectionRefusal) =
            await OpenProviderConnectionWithinBudgetAsync(
                    provider,
                    request.ConnectionString,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (openedConnection is null)
        {
            return CdcContractReadResult<CdcAdoptionProof>.Failure([
                AdoptionRefused(
                    CdcDiagnosticCategory.ProviderSetupInvalid,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC adoption could not reach the instance database it verifies the binding against.",
                    "$.binding",
                    connectionRefusal ?? "unavailable",
                    timeProvider.GetUtcNow()
                ),
            ]);
        }

        await using DbConnection connection = openedConnection;

        CdcProviderSetupResult validated = await SetupProviderWithinBudgetAsync(
                ProviderSetupRequest(
                    request.ProviderSetup,
                    provider,
                    binding.PhysicalSourceFingerprint,
                    inventory,
                    connection,
                    DdlCdc.CdcProviderSetupMode.ValidateOnly
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcProviderSetupObservationMapping providerSetupObservation =
            CdcProviderSetupResultMapper.MapValidateOnlyResult(
                request.OperationId,
                timeProvider.GetUtcNow(),
                binding,
                validated
            );

        Verify(
            CdcAdoptionVerificationKind.PhysicalSource,
            string.Equals(
                validated.ObservedSourceFingerprint?.Value,
                binding.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            ),
            "the live physical source fingerprint matches the supplied binding record",
            validated.ObservedSourceFingerprint is null ? "unreadable" : "a different physical source"
        );
        Verify(
            CdcAdoptionVerificationKind.ProviderArtifacts,
            IsProviderSetupExactMatch(providerSetupObservation.ProviderSetup),
            "the provider capture artifacts and grants match the binding inventory",
            providerSetupObservation.ProviderSetup.SetupOutcome.ToString()
        );

        CdcObservationContext context = new(
            request.OperationId,
            binding.ToTargetIdentity(),
            binding.PhysicalSourceFingerprint
        );

        CdcKafkaPolicyObservation kafkaPolicy = await kafkaAdmin
            .DescribeBindingKafkaPolicyAsync(context, inventory, cancellationToken)
            .ConfigureAwait(false);

        Verify(
            CdcAdoptionVerificationKind.KafkaTopics,
            AreGovernedTopicsExactMatch(kafkaPolicy, provider),
            "the governed topics match the binding's Kafka policy",
            kafkaPolicy.PolicyState.ToString()
        );
        Verify(
            CdcAdoptionVerificationKind.KafkaAcls,
            AreGovernedAclsExactMatch(kafkaPolicy, provider),
            "the governed topic grants match the binding's Kafka policy",
            kafkaPolicy.PolicyState.ToString()
        );

        CdcConnectResult<CdcConnectorStatus> connectorStatus = await connectClient
            .GetConnectorStatusAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        CdcConnectResult<CdcConnectorOffsets> committedOffsets = await connectClient
            .GetConnectorOffsetsAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack = await connectClient
            .GetConnectorConfigAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);

        CdcConnectorRuntimeObservation connectorRuntime = observationMapper.MapRuntime(
            context,
            binding,
            connectorStatus,
            committedOffsets
        );

        // Adoption repairs deployment state around an artifact set that is already publishing, so the
        // connector and its sole task must both be running. A task count alone does not say that: a
        // paused, stopped, or failed connector still declares its one task, and adopting it would mint
        // a binding record asserting a publication that is not happening.
        Verify(
            CdcAdoptionVerificationKind.Connector,
            connectorStatus.Succeeded
                && connectorRuntime.ConnectorState == CdcConnectorRuntimeState.Running
                && connectorRuntime.TaskCount == 1
                && connectorRuntime.RunningTaskCount == 1
                && connectorRuntime.SoleTaskState == CdcConnectorRuntimeState.Running,
            "the worker holds the binding's connector running a single running task",
            connectorStatus.Succeeded
                ? $"connector {connectorRuntime.ConnectorState}, "
                    + $"{connectorRuntime.TaskCount?.ToString(CultureInfo.InvariantCulture) ?? "an unreadable count of"} tasks, "
                    + $"{connectorRuntime.RunningTaskCount?.ToString(CultureInfo.InvariantCulture) ?? "an unreadable count of"} running, "
                    + $"sole task {connectorRuntime.SoleTaskState}"
                : connectorStatus.Outcome.ToString()
        );

        if (
            !TryComposeConnectorTemplate(
                binding,
                new CdcConnectorProviderSetupEvidence(binding.Generation, validated),
                controlOptions,
                provider,
                out CdcConnectorTemplateRequest? templateRequest,
                out string? templateRejection
            )
        )
        {
            return CdcContractReadResult<CdcAdoptionProof>.Failure([
                .. refusals,
                AdoptionRefused(
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC adoption could not compose the connector template inputs the live configuration is verified against.",
                    "$.connectorConfig",
                    templateRejection,
                    timeProvider.GetUtcNow()
                ),
            ]);
        }

        string? sqlServerCatalogName = SqlServerCatalogName(templateRequest, provider);
        CdcConnectorConfigurationObservation connectorConfig = observationMapper.MapConfiguration(
            context,
            templateRequest,
            new CdcConnectorProviderSetupEvidence(binding.Generation, validated),
            SourcePartitionEvidence(readBack.Value, provider),
            readBack
        );
        CdcConnectorOffsetObservation offsetObservation = observationMapper.MapOffset(
            context,
            binding,
            sqlServerCatalogName,
            committedOffsets
        );

        Verify(
            CdcAdoptionVerificationKind.ConnectorConfig,
            connectorConfig.ConfigurationState == CdcConnectorConfigurationState.Matched,
            "the live connector configuration matches the configuration the binding renders",
            connectorConfig.ConfigurationState.ToString()
        );
        Verify(
            CdcAdoptionVerificationKind.ConnectOffsets,
            HasCommittedStreamingOffset(offsetObservation),
            "the connector has committed a streaming position under the binding's own source partition",
            offsetObservation.SourcePartitionMatchResult.ToString()
        );

        // The classifier is asked for the phase this artifact set is actually in: an adopted binding was
        // admitted before this deployment state went missing, so a state that is not continuous is a
        // terminal loss rather than an enablement still in progress. It is reported and refuses the
        // adoption; it is never latched, because there is no binding record to latch it against and a
        // refused adoption changes nothing.
        CdcSqlServerSchemaHistoryEvidence? schemaHistory = await kafkaAdmin
            .ReadSqlServerSchemaHistoryAsync(
                inventory,
                CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                HasCommittedStreamingOffset(offsetObservation),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcSourceHistoryClassificationResult sourceHistory = await sourcePositions
            .ObserveSourceHistoryAsync(
                new(
                    request.OperationId,
                    binding,
                    providerSetupObservation.ProviderSetup,
                    offsetObservation,
                    providerSetupObservation.ProviderHistory
                )
                {
                    SqlServerSchemaHistory = schemaHistory,
                    ExpectedConnectSourcePartitionHash = CdcSourcePartitionHashCalculator
                        .Compute(provider, inventory.ConnectorName, sqlServerCatalogName)
                        .Hash,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        Verify(
            CdcAdoptionVerificationKind.SourceHistoryContinuity,
            sourceHistory.Observation.Continuity == CdcSourceHistoryContinuity.Healthy,
            "the exact resume position is proved for every required provider source artifact",
            sourceHistory.Observation.Continuity.ToString()
        );

        // Adoption imports a record the operator supplied, so it is the other way a second logical
        // target can come to bind a physical source this deployment already publishes.
        CdcBindingConflict conflict = await FindBindingConflictAsync(
                binding,
                CdcSecondGenerationRule.Allowed,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (conflict.Blocked)
        {
            refusals.Add(
                AdoptionRefused(
                    conflict.Category,
                    CdcDiagnosticComponent.Binding,
                    conflict.Message,
                    "$.binding",
                    conflict.Observed,
                    timeProvider.GetUtcNow()
                )
            );
            refusals.AddRange(conflict.Diagnostics);
        }

        if (refusals.Count != 0)
        {
            logger.LogDebug(
                "CDC adoption refused the supplied binding record: {RefusedCount} verifications did not match.",
                refusals.Count
            );

            return CdcContractReadResult<CdcAdoptionProof>.Failure(refusals);
        }

        CdcAdoptionProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            request.OperationId,
            timeProvider.GetUtcNow(),
            binding,
            verifications
        );

        CdcContractValidationResult proofValidation = CdcAdoptionProofValidator.Validate(
            proof,
            timeProvider.GetUtcNow()
        );
        if (!proofValidation.Succeeded)
        {
            return CdcContractReadResult<CdcAdoptionProof>.Failure(proofValidation.Diagnostics);
        }

        // The binding record is created by the guarded atomic state operation, from the verified proof:
        // an existing record must match it exactly, and one that does not leaves the deployment
        // untouched.
        CdcBindingLifecycleResult import = await bindingLifecycle
            .ImportVerifiedBindingAsync(proof, cancellationToken)
            .ConfigureAwait(false);
        if (import.Status != CdcControlPlaneOperationStatus.Succeeded)
        {
            return CdcContractReadResult<CdcAdoptionProof>.Failure([
                AdoptionRefused(
                    import.Status == CdcControlPlaneOperationStatus.BindingMismatch
                        ? CdcDiagnosticCategory.BindingMismatch
                        : CdcDiagnosticCategory.LocalStateUnavailable,
                    CdcDiagnosticComponent.StateStore,
                    "CDC adoption could not make the verified binding record durable.",
                    "$.binding",
                    import.Status.ToString(),
                    timeProvider.GetUtcNow()
                ),
                .. import.Diagnostics,
            ]);
        }

        logger.LogDebug(
            "CDC adoption imported the verified binding record for generation {Generation}.",
            binding.Generation
        );

        return CdcContractReadResult<CdcAdoptionProof>.Success(proof);
    }

    /// <summary>
    /// Whether the provider inspection found every capture artifact, grant, source table, and heartbeat
    /// the binding's inventory names. Evidence that could not be obtained is not a match.
    /// </summary>
    private static bool IsProviderSetupExactMatch(CdcProviderSetupObservation observation) =>
        observation.SetupOutcome == CoreCdc.CdcProviderSetupOutcome.Satisfied
        && IsSettled(observation.ArtifactInventoryState)
        && IsSettled(observation.GrantInventoryState)
        && IsSettled(observation.SourceInventoryState)
        && IsSettled(observation.HeartbeatState);

    private static bool IsSettled(CdcProviderSetupState state) =>
        state is CdcProviderSetupState.Matched or CdcProviderSetupState.NotApplicable;

    /// <summary>
    /// Whether every governed topic the binding names was found conforming. The schema-history topic is
    /// SQL Server-only evidence, so its absence is a match for PostgreSQL and a refusal for SQL Server.
    /// </summary>
    private static bool AreGovernedTopicsExactMatch(
        CdcKafkaPolicyObservation observation,
        CoreCdc.CdcProvider provider
    ) =>
        observation.PublicTopic?.State == CdcKafkaPolicyItemState.Satisfied
        && observation.ProgressTopic?.State == CdcKafkaPolicyItemState.Satisfied
        && (
            provider == CoreCdc.CdcProvider.SqlServer
                ? observation.SchemaHistoryTopic?.State == CdcKafkaPolicyItemState.Satisfied
                : observation.SchemaHistoryTopic is null
        );

    /// <summary>
    /// Whether every governed grant was found as the binding requires. A deployment with no authorizer
    /// reports the grants as not applicable, which is the whole of the ACL evidence it can produce.
    /// </summary>
    private static bool AreGovernedAclsExactMatch(
        CdcKafkaPolicyObservation observation,
        CoreCdc.CdcProvider provider
    ) =>
        IsGranted(observation.PublicTopicAcls?.State)
        && IsGranted(observation.ProgressTopicAcls?.State)
        && (
            provider == CoreCdc.CdcProvider.SqlServer
                ? IsGranted(observation.SchemaHistoryTopicAcls?.State)
                : observation.SchemaHistoryTopicAcls is null
        );

    private static bool IsGranted(CdcKafkaPolicyItemState? state) =>
        state is CdcKafkaPolicyItemState.Satisfied or CdcKafkaPolicyItemState.NotApplicable;

    /// <summary>
    /// Reports one live verification that did not exactly match the operator's record, against the
    /// component that produced the evidence.
    /// </summary>
    private static CdcDiagnostic VerificationRefused(
        CdcAdoptionVerificationKind kind,
        string observed,
        DateTimeOffset observedAt
    )
    {
        (CdcDiagnosticCategory category, CdcDiagnosticComponent component) = kind switch
        {
            CdcAdoptionVerificationKind.PhysicalSource => (
                CdcDiagnosticCategory.SourceMismatch,
                CdcDiagnosticComponent.ProviderSetup
            ),
            CdcAdoptionVerificationKind.ProviderArtifacts => (
                CdcDiagnosticCategory.ProviderSetupInvalid,
                CdcDiagnosticComponent.ProviderSetup
            ),
            CdcAdoptionVerificationKind.Connector => (
                CdcDiagnosticCategory.ConnectorNotRunning,
                CdcDiagnosticComponent.ConnectorRuntime
            ),
            CdcAdoptionVerificationKind.ConnectorConfig => (
                CdcDiagnosticCategory.ConnectorConfigInvalid,
                CdcDiagnosticComponent.ConnectorConfig
            ),
            CdcAdoptionVerificationKind.KafkaTopics or CdcAdoptionVerificationKind.KafkaAcls => (
                CdcDiagnosticCategory.KafkaPolicyInvalid,
                CdcDiagnosticComponent.KafkaPolicy
            ),
            CdcAdoptionVerificationKind.ConnectOffsets => (
                CdcDiagnosticCategory.ConnectOffsetStoreInvalid,
                CdcDiagnosticComponent.ConnectOffsetStore
            ),
            _ => (CdcDiagnosticCategory.SourceHistoryLost, CdcDiagnosticComponent.SourceHistory),
        };

        return new CdcDiagnostic(
            "adoptVerificationNotExactMatch",
            category,
            CdcDiagnosticSeverity.Error,
            component,
            observedAt,
            "CDC adoption requires every live verification to exactly match the supplied binding record.",
            retryable: false,
            artifactKind: kind.ToString(),
            expected: CdcAdoptionVerificationState.ExactMatch.ToString(),
            observed: observed
        ).WithPath("$.verificationResults");
    }

    private static CdcDiagnostic AdoptionRefused(
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string path,
        string observed,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            "adoptRefused",
            category,
            CdcDiagnosticSeverity.Error,
            component,
            observedAt,
            message,
            retryable: false,
            artifactKind: "cdcAdoption",
            expected: "a complete, live-verified binding record",
            observed: observed
        ).WithPath(path);

    /// <summary>
    /// Collects the target's observations from the durable binding record outwards: the provider's own
    /// artifacts, the running projector, the shared offset store, the binding's Kafka artifacts, the
    /// registered connector, the provider barrier, source-history continuity, and connector lag.
    /// </summary>
    /// <remarks>
    /// Collection stops as soon as an evidence source names something the rest cannot be observed
    /// against — a target that is not a CDC target, an unreadable binding record, a binding that is
    /// missing or is not this target's. What was collected is reported and the remaining observations
    /// are absent, which the evaluators report as unavailable rather than as satisfied.
    /// </remarks>
    private async Task<CdcCollectedTargetObservations> CollectTargetObservationsAsync(
        CdcTargetOperationRequest request,
        CancellationToken cancellationToken
    )
    {
        CdcControlOptions controlOptions = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        CoreCdc.CdcProvider provider = eligibilityProbe.Provider;
        string dataStoreId = request.DataStoreId.ToString(CultureInfo.InvariantCulture);

        CdcTargetStatusEvaluationInput evaluation = new(
            request.OperationId,
            now,
            UnvalidatedTargetIdentity(controlOptions, request.TenantKey, dataStoreId, provider),
            null
        );

        CdcCollectedTargetObservations Blocked(
            CdcDiagnostic stepDiagnostic,
            IReadOnlyList<CdcDiagnostic> diagnostics
        ) => new(evaluation with { StateStoreDiagnostics = [stepDiagnostic, .. diagnostics] });

        CdcTargetValidationResult targetValidation = CdcTargetValidator.Validate(
            TargetInput(controlOptions, request.TenantKey, dataStoreId, provider)
        );
        if (targetValidation.Target is not { } target)
        {
            return Blocked(
                StatusStep(
                    "statusTargetInvalid",
                    CdcDiagnosticCategory.TargetMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC status rejected the requested target.",
                    "an invalid target",
                    now
                ),
                targetValidation.Diagnostics
            );
        }

        evaluation = evaluation with { TargetIdentity = target.ToTargetIdentity() };

        CdcBindingLifecycleResult bindingRead = await bindingLifecycle
            .ReadBindingAsync(target.ToBindingIdentity(), cancellationToken)
            .ConfigureAwait(false);
        if (
            bindingRead.Status
            is CdcControlPlaneOperationStatus.StateStoreUnavailable
                or CdcControlPlaneOperationStatus.InvalidOperation
        )
        {
            return Blocked(
                StatusStep(
                    "statusBindingStateUnavailable",
                    CdcDiagnosticCategory.LocalStateUnavailable,
                    CdcDiagnosticComponent.StateStore,
                    "CDC status could not read the durable binding state.",
                    bindingRead.Status.ToString(),
                    now
                ),
                bindingRead.Diagnostics
            );
        }

        evaluation = evaluation with { BindingState = bindingRead.State };

        // A binding that is missing, or that is another binding for these coordinates, names no
        // governed artifact this status may be collected against.
        if (
            bindingRead.State?.Binding is not { } binding
            || bindingRead.State.State
                is not (CdcBindingState.BindingPresent or CdcBindingState.IncidentLatched)
        )
        {
            return new(evaluation);
        }

        // The same mismatch retirement, adoption, and source replacement refuse, refused here too and
        // for a sharper reason: the artifact names below are recovered under the record's provider,
        // while the provider-setup input built from them is selected by this deployment's. A record
        // naming the other engine leaves the names this provider needs absent, and composing a setup
        // request from them throws out of an operation whose whole contract is to observe and report
        // what it found. Answered before the instance connection is opened, so no database this
        // control plane could not inspect anyway is reached.
        if (binding.Provider != provider)
        {
            return Blocked(
                StatusStep(
                    "statusProviderMismatch",
                    CdcDiagnosticCategory.ProviderMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC status requires the binding record to name this deployment's provider.",
                    binding.Provider.ToString(),
                    now
                ),
                []
            );
        }

        CdcArtifactNameResult artifactNames = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        if (artifactNames.Inventory is not { } inventory)
        {
            return Blocked(
                StatusStep(
                    "statusArtifactNamesInvalid",
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    CdcDiagnosticComponent.Binding,
                    "CDC status could not recover the governed artifact names from the binding.",
                    "unrecoverable",
                    now
                ),
                artifactNames.Diagnostics
            );
        }

        // The provider artifacts are inspected without being changed, and the same pass reports the
        // fingerprint of the source that actually answered. The binding is compared against that
        // observed source rather than against itself, so a database swapped underneath the binding is
        // reported as a source mismatch.
        (DbConnection? openedConnection, string? connectionRefusal) =
            await OpenProviderConnectionWithinBudgetAsync(
                    provider,
                    request.ConnectionString,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (openedConnection is null)
        {
            return Blocked(
                StatusStep(
                    "statusProviderConnectionUnavailable",
                    CdcDiagnosticCategory.ProviderSetupInvalid,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC status could not reach the instance database its provider inspection runs against.",
                    connectionRefusal ?? "unavailable",
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        await using DbConnection connection = openedConnection;

        CdcProviderSetupResult validated = await SetupProviderWithinBudgetAsync(
                ProviderSetupRequest(
                    request.ProviderSetup,
                    provider,
                    binding.PhysicalSourceFingerprint,
                    inventory,
                    connection,
                    DdlCdc.CdcProviderSetupMode.ValidateOnly
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcProviderSetupObservationMapping providerSetupObservation =
            CdcProviderSetupResultMapper.MapValidateOnlyResult(
                request.OperationId,
                timeProvider.GetUtcNow(),
                binding,
                validated
            );

        evaluation = evaluation with
        {
            ProviderSetup = providerSetupObservation.ProviderSetup,
            PhysicalSourceFingerprint = validated.ObservedSourceFingerprint?.Value,
        };

        CdcObservationContext context = new(
            request.OperationId,
            target.ToTargetIdentity(),
            binding.PhysicalSourceFingerprint
        );

        CdcProjectionCorrelationObservation projection = await projectionCorrelation
            .CollectAsync(context, cancellationToken)
            .ConfigureAwait(false);

        // The describe variants, never the ensure ones: a status is what the target is now. A pass that
        // created an absent topic or re-granted a missing ACL would report artifacts it had just made
        // itself, and would silently undo what a failed retirement had already removed.
        evaluation = evaluation with
        {
            Projection = projection,
            ConnectOffsetStore = await kafkaAdmin
                .DescribeConnectOffsetStoreAsync(context, cancellationToken)
                .ConfigureAwait(false),
        };
        evaluation = evaluation with
        {
            KafkaPolicy = await kafkaAdmin
                .DescribeBindingKafkaPolicyAsync(context, inventory, cancellationToken)
                .ConfigureAwait(false),
        };

        if (
            !TryComposeConnectorTemplate(
                binding,
                new CdcConnectorProviderSetupEvidence(binding.Generation, validated),
                controlOptions,
                provider,
                out CdcConnectorTemplateRequest? templateRequest,
                out string? templateRejection
            )
        )
        {
            return Blocked(
                StatusStep(
                    "statusConnectorInputsInvalid",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC status could not compose the connector template inputs the read-back is compared against.",
                    templateRejection,
                    timeProvider.GetUtcNow()
                ),
                []
            );
        }

        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack = await connectClient
            .GetConnectorConfigAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);

        // One barrier capture and one observation of it: a status is what the target is now, so a
        // connector that has not yet committed past the position the source is at reports exactly that
        // rather than being waited on. The barrier is captured before the committed offset is read, so
        // an offset at or past it is evidence the connector passed a position the source had already
        // reached rather than one it reached afterwards.
        CdcProviderBarrierCaptureResult capturedBarrier = await sourcePositions
            .CaptureBarrierAsync(
                BarrierCapture(request.ConnectionString, binding, controlOptions),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcConnectResult<CdcConnectorOffsets> committedOffsets = await connectClient
            .GetConnectorOffsetsAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);

        string? sqlServerCatalogName = SqlServerCatalogName(templateRequest, provider);
        CdcConnectorOffsetObservation offsetObservation = observationMapper.MapOffset(
            context,
            binding,
            sqlServerCatalogName,
            committedOffsets
        );

        evaluation = evaluation with
        {
            ConnectorConfig = observationMapper.MapConfiguration(
                context,
                templateRequest,
                new CdcConnectorProviderSetupEvidence(binding.Generation, validated),
                SourcePartitionEvidence(readBack.Value, provider),
                readBack
            ),
            ConnectorRuntime = observationMapper.MapRuntime(
                context,
                binding,
                await connectClient
                    .GetConnectorStatusAsync(inventory.ConnectorName, cancellationToken)
                    .ConfigureAwait(false),
                committedOffsets
            ),
        };

        string? expectedSourcePartitionHash = CdcSourcePartitionHashCalculator
            .Compute(provider, inventory.ConnectorName, sqlServerCatalogName)
            .Hash;

        evaluation = evaluation with
        {
            ProviderBarrier = sourcePositions.ObserveProviderBarrier(
                new(
                    request.OperationId,
                    binding,
                    projection.ProjectionObservedAt,
                    capturedBarrier,
                    offsetObservation,
                    expectedSourcePartitionHash
                )
            ),
        };

        // After initial admission the schema-history states a first enablement leaves unknown are a
        // terminal loss: the run that writes that history has already happened.
        CdcSqlServerSchemaHistoryEvidence? schemaHistory = await kafkaAdmin
            .ReadSqlServerSchemaHistoryAsync(
                inventory,
                CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                HasCommittedStreamingOffset(offsetObservation),
                cancellationToken
            )
            .ConfigureAwait(false);

        CdcSourceHistoryClassificationResult sourceHistory = await sourcePositions
            .ObserveSourceHistoryAsync(
                new(
                    request.OperationId,
                    binding,
                    providerSetupObservation.ProviderSetup,
                    offsetObservation,
                    providerSetupObservation.ProviderHistory
                )
                {
                    SqlServerSchemaHistory = schemaHistory,
                    ExpectedConnectSourcePartitionHash = expectedSourcePartitionHash,
                    LatchedIncident = bindingRead.State.Incident,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        evaluation = evaluation with { SourceHistory = sourceHistory.Observation };

        // The latch is written once, from the classifier's own candidate, which it raises only for a
        // loss it proved and never for one it read back from the record.
        if (sourceHistory.IncidentCandidate is { } incidentCandidate)
        {
            evaluation = await LatchSourceHistoryLossAsync(evaluation, incidentCandidate, cancellationToken)
                .ConfigureAwait(false);
        }

        // The fence follows the continuity, not the candidate. Latching is idempotent by design - a
        // poll that reads an already-latched incident deliberately raises no second candidate - so
        // fencing from the candidate would give the connector exactly one chance to be stopped. A
        // worker that refused that stop, or a process that exited between the durable latch and the
        // stop, would leave it publishing indefinitely with nothing re-attempting it: no later status
        // raises a candidate, and restart declines a lost continuity rather than acting on it.
        if (sourceHistory.Observation.Continuity == CdcSourceHistoryContinuity.Lost)
        {
            evaluation = await FenceLostSourceHistoryAsync(evaluation, inventory, cancellationToken)
                .ConfigureAwait(false);
        }

        CdcConnectorLagReadResult lagReading = await lagReader
            .ReadAsync(provider, inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        evaluation = evaluation with
        {
            Lag = CdcConnectorLagObservationMapper.Map(
                context,
                lagReading,
                controlOptions.LagThreshold,
                timeProvider.GetUtcNow()
            ),
        };

        return new(evaluation)
        {
            Context = context,
            BindingRecord = binding,
            Inventory = inventory,
            Continuity = sourceHistory.Observation.Continuity,
        };
    }

    /// <summary>
    /// Fences the connector that carries a lost source history, so it commits no further offsets
    /// against a source it can no longer resume from exactly.
    /// </summary>
    /// <remarks>
    /// Driven by the classified continuity rather than by the incident candidate that latches it. The
    /// classifier raises a candidate only for a loss it proved and never for one it read back from the
    /// binding record, which is what keeps latching idempotent; fencing on that same signal would give
    /// the connector exactly one chance to be stopped. A worker that refused the stop, or a process
    /// that exited between the durable latch and the stop, would leave it publishing indefinitely with
    /// nothing re-attempting it: a later status raises no candidate, and restart declines a lost
    /// continuity rather than acting on it.
    ///
    /// A connector already observed <c>STOPPED</c> is left alone — that is the state this asks for, and
    /// a status should not issue a request whose answer it already holds. Every other state is asked
    /// again, including one this poll could not read, because an unreadable runtime is not evidence
    /// that the connector is fenced. A connector the worker does not hold answers <c>NotFound</c>,
    /// which is not a failure to fence.
    ///
    /// A fence the worker did not apply is reported on the connector runtime: the loss is latched
    /// either way, and a status that reported a contained incident while the connector still committed
    /// offsets would leave nothing behind saying so.
    /// </remarks>
    private async Task<CdcTargetStatusEvaluationInput> FenceLostSourceHistoryAsync(
        CdcTargetStatusEvaluationInput evaluation,
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    )
    {
        if (evaluation.ConnectorRuntime?.ConnectorState == CdcConnectorRuntimeState.Stopped)
        {
            return evaluation;
        }

        CdcConnectResult fence = await connectClient
            .StopConnectorAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);

        if (fence.Succeeded || fence.Outcome == CdcConnectOutcome.NotFound)
        {
            return evaluation;
        }

        logger.LogDebug(
            "CDC status could not fence the connector carrying a lost source history: {Outcome}.",
            fence.Outcome
        );

        return WithUnappliedConnectorAction(
            evaluation,
            inventory,
            fence,
            "statusIncidentFenceNotApplied",
            CdcDiagnosticCategory.SourceHistoryLost,
            "CDC status proved a source-history loss but could not fence the connector that carries it.",
            "the connector stopped so it commits no further offsets against the lost source"
        );
    }

    /// <summary>
    /// Latches a proved source-history loss durably in the binding record.
    /// </summary>
    /// <remarks>
    /// Written from the classifier's own incident candidate, which it raises only for a loss it proved
    /// and never for one it read back from the binding record — so repeated status polls latch once.
    /// Fencing the connector belongs to <see cref="FenceLostSourceHistoryAsync"/>, which runs on every
    /// poll that classifies the continuity as lost rather than only on the poll that latched it.
    ///
    /// A latch that could not be written is reported in the status's state-store diagnostics, which is
    /// what the binding state is evaluated as unavailable from: the loss is terminal and the record that
    /// must outlive this poll does not carry it, so no later step may read the binding as though it did.
    /// Restart is refused for the same poll without depending on those diagnostics — the classifier
    /// raises an incident candidate only from a proved loss, so the continuity this collection reports
    /// is <c>Lost</c> whenever a latch was attempted at all, and the restart gate admits only
    /// <c>Healthy</c>. The connector is fenced from that same continuity either way, and the operator is
    /// left an error naming the unwritten latch to retry against.
    /// </remarks>
    private async Task<CdcTargetStatusEvaluationInput> LatchSourceHistoryLossAsync(
        CdcTargetStatusEvaluationInput evaluation,
        CdcSourceHistoryIncidentCandidate incidentCandidate,
        CancellationToken cancellationToken
    )
    {
        CdcBindingLifecycleResult latch = await bindingLifecycle
            .LatchSourceHistoryLossAsync(incidentCandidate.ToIncident(), cancellationToken)
            .ConfigureAwait(false);
        logger.LogDebug("CDC status latched a source-history continuity loss: {Status}.", latch.Status);

        if (latch.Status == CdcControlPlaneOperationStatus.Succeeded)
        {
            return evaluation with { BindingState = latch.State ?? evaluation.BindingState };
        }

        return evaluation with
        {
            StateStoreDiagnostics =
            [
                .. evaluation.StateStoreDiagnostics,
                StatusStep(
                    "statusSourceHistoryLatchNotDurable",
                    CdcDiagnosticCategory.SourceHistoryLost,
                    CdcDiagnosticComponent.SourceHistory,
                    "CDC status proved a terminal source-history loss but could not latch it durably in "
                        + "the binding record.",
                    latch.Status.ToString(),
                    timeProvider.GetUtcNow()
                ),
                .. latch.Diagnostics,
            ],
        };
    }

    /// <summary>
    /// Reports that a connector lifecycle request the worker was asked for did not take, so the status
    /// never reads as though the request had been applied.
    /// </summary>
    /// <remarks>
    /// The diagnostic is carried on the connector runtime the request was aimed at rather than in the
    /// status's state-store diagnostics: those report a binding record that could not be read, and in
    /// both cases here the binding was read — the connector is what the request did not reach.
    ///
    /// Both callers need this for the same reason. A latched source-history loss whose fence was
    /// refused would otherwise report a contained incident while the connector kept committing
    /// offsets, and a refused restart would be indistinguishable from one the worker applied to a
    /// connector that then failed anyway — only the refusal is worth reissuing.
    /// </remarks>
    private CdcTargetStatusEvaluationInput WithUnappliedConnectorAction(
        CdcTargetStatusEvaluationInput evaluation,
        CdcArtifactInventory inventory,
        CdcConnectResult result,
        string code,
        CdcDiagnosticCategory category,
        string message,
        string expected
    )
    {
        if (evaluation.ConnectorRuntime is not { } connectorRuntime)
        {
            return evaluation;
        }

        CdcDiagnostic diagnostic = new CdcDiagnostic(
            code,
            category,
            CdcDiagnosticSeverity.Error,
            CdcDiagnosticComponent.ConnectorRuntime,
            timeProvider.GetUtcNow(),
            message,
            retryable: true,
            artifactKind: "connector",
            artifactName: inventory.ConnectorName,
            expected: expected,
            observed: result.Outcome.ToString()
        ).WithPath("$.connectorRuntime");

        return evaluation with
        {
            ConnectorRuntime = connectorRuntime with
            {
                Diagnostics = [.. connectorRuntime.Diagnostics, diagnostic],
            },
        };
    }

    /// <summary>
    /// Evaluates the collected observations into the shared status contract. The status is stamped when
    /// it is composed rather than when collection started, so no observation it reports is in its
    /// future.
    /// </summary>
    private CdcStatus Compose(CdcTargetStatusEvaluationInput evaluation)
    {
        DateTimeOffset observedAt = timeProvider.GetUtcNow();

        return CdcAggregateStatusEvaluator.Evaluate(
            new(observedAt, [CdcTargetStatusEvaluator.Evaluate(evaluation with { ObservedAt = observedAt })])
        );
    }

    /// <summary>
    /// Reads the running DMS projector until it reports the target caught up, or until the step's budget
    /// is spent. The observation is returned as it was last read: an exhausted budget is reported as the
    /// evidence that was actually observed, never as a caught-up projector.
    /// </summary>
    private Task<CdcProjectionCorrelationObservation> WaitForCaughtUpAsync(
        CdcObservationContext context,
        CdcControlOptions controlOptions,
        CancellationToken cancellationToken
    ) =>
        PollAsync(
            token => projectionCorrelation.CollectAsync(context, token),
            IsCaughtUp,
            controlOptions.Timeouts.ProjectionCaughtUp,
            controlOptions.Timeouts.PollInterval,
            cancellationToken
        );

    /// <summary>
    /// Polls one step until its evidence is satisfied or its budget is spent, returning what was last
    /// observed either way. Elapsed time is never evidence, so a spent budget ends the wait rather than
    /// standing in for the observation it was waiting on.
    /// </summary>
    /// <remarks>
    /// The budget bounds real elapsed time, not just the decision between observations: each wait is
    /// clamped to what the budget has left, and every observation after the first is issued under that
    /// remainder. Without both, an observation or a delay begun just inside the deadline would run its
    /// own full step timeout past it, and a slow-answering step would spend that timeout on every one of
    /// its polls - the budget would bound the number of reads rather than the time they take.
    ///
    /// The first observation is the exception, and is issued under the caller's token alone: the step
    /// has to report what it observed, and a first observation cancelled by the budget would leave
    /// nothing to report but the elapsed time this method refuses to treat as evidence. Every later
    /// observation has the one before it to fall back on, so cutting one off costs no evidence.
    /// </remarks>
    private async Task<TObservation> PollAsync<TObservation>(
        Func<CancellationToken, Task<TObservation>> observe,
        Func<TObservation, bool> satisfied,
        TimeSpan budget,
        TimeSpan pollInterval,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + budget;
        TObservation observation = await observe(cancellationToken).ConfigureAwait(false);

        while (!satisfied(observation))
        {
            TimeSpan remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return observation;
            }

            await Task.Delay(
                    remaining < pollInterval ? remaining : pollInterval,
                    timeProvider,
                    cancellationToken
                )
                .ConfigureAwait(false);

            remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return observation;
            }

            using CancellationTokenSource remainingBudget = new(remaining, timeProvider);
            using CancellationTokenSource observeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, remainingBudget.Token);

            try
            {
                observation = await observe(observeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Cut off by this step's own budget rather than by the caller, so the observation before
                // it is what the step actually observed.
                return observation;
            }
        }

        return observation;
    }

    /// <summary>
    /// The projector is caught up only when it reported the binding's own target, reported itself
    /// operational, and reported that target drained. Anything else is another target, an unusable
    /// report, or a backlog.
    /// </summary>
    private static bool IsCaughtUp(CdcProjectionCorrelationObservation observation) =>
        observation.CorrelationState == CdcProjectionCorrelationState.Matched
        && observation.OperationalHealthStatus == DocumentCacheOperationalHealthStatus.Operational
        && observation.CaughtUpStatus == DocumentCacheCaughtUpStatus.CaughtUp;

    /// <summary>
    /// Whether the connector has committed a streaming position under the binding's own Connect source
    /// partition. A snapshot offset, a null offset, and an offset committed under another source
    /// partition are each not one, so none of them makes an empty schema history a retained-offset loss.
    /// </summary>
    private static bool HasCommittedStreamingOffset(CdcConnectorOffsetObservation? offset) =>
        offset is { IsSnapshot: false, IsNull: false }
        && offset.SourcePartitionMatchResult == CdcConnectorOffsetMatchResult.Exact;

    /// <summary>
    /// The probe request one enablement attempt runs, under the configured eligibility-probe budget.
    /// </summary>
    /// <remarks>
    /// The budget is passed rather than left to the request's own default: it is a per-step timeout the
    /// deployment configures and options validation requires, and a request that omitted it would bound
    /// the probe by a constant no setting could move.
    /// </remarks>
    private static CdcEligibilityProbeRequest EligibilityProbeRequest(
        CdcEnableRequest request,
        CdcValidatedTarget target,
        InitialCdcProvisioningProof provisioningProof,
        CdcControlOptions controlOptions
    ) =>
        new(
            new(request.OperationId, target.ToTargetIdentity(), null),
            provisioningProof,
            request.ConnectionString
        )
        {
            CommandTimeout = controlOptions.Timeouts.EligibilityProbe,
        };

    /// <summary>
    /// Composes the connector template inputs for one generation, or names the rejection for the
    /// caller's own refusal.
    /// </summary>
    /// <remarks>
    /// Every verb that renders, registers, or compares a connector configuration composes the same
    /// inputs from the same sources, and the composition validates them as it goes. It is written once
    /// here so that rule has one definition, while each caller keeps its own refusal: a rejection is
    /// an enablement that cannot proceed, an adoption that cannot verify, or a status that cannot
    /// compare, and those are not the same answer. The rejected value stays inside the exception, so
    /// only the rejection's type is ever reported.
    /// </remarks>
    private static bool TryComposeConnectorTemplate(
        CdcBinding binding,
        CdcConnectorProviderSetupEvidence setupEvidence,
        CdcControlOptions controlOptions,
        CoreCdc.CdcProvider provider,
        [NotNullWhen(true)] out CdcConnectorTemplateRequest? templateRequest,
        [NotNullWhen(false)] out string? rejection
    )
    {
        try
        {
            templateRequest = new(
                binding,
                setupEvidence,
                controlOptions.ToDeploymentPolicy(),
                controlOptions.ToProviderConnectionProperties(ToDdlProvider(provider)),
                controlOptions.ToKafkaClientSecurityProperties()
            );
            rejection = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            templateRequest = null;
            rejection = exception.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// One provider-barrier capture, under the deployment's own configured budget rather than the
    /// contract's defaults.
    /// </summary>
    /// <remarks>
    /// The capture is the half of the barrier step that waits on the provider: SQL Server cannot name a
    /// position until the heartbeat after-image appears in the capture instance, so the wait belongs to
    /// the capture and not only to the committed-offset polling that follows it. Left on the record's
    /// 45-second default the capture would fail well inside a <c>Timeouts.ProviderBarrier</c> an
    /// operator raised for exactly that source, and the raised value would have no effect on the step
    /// it was raised for.
    /// </remarks>
    private static CdcProviderBarrierCaptureRequest BarrierCapture(
        string connectionString,
        CdcBinding binding,
        CdcControlOptions controlOptions
    ) =>
        new(connectionString, binding)
        {
            CaptureWaitTimeout = controlOptions.Timeouts.ProviderBarrier,
            PollInterval = controlOptions.Timeouts.PollInterval,
        };

    /// <summary>
    /// The source partition the registered connector will commit under, taken from the configuration the
    /// worker actually holds. It is read from the live read-back rather than from what was rendered, so
    /// a connector registered under another partition is reported as configuration drift; the partition
    /// it does commit under is separately checked against the binding when the offsets are observed.
    /// </summary>
    private static CdcConnectorTemplateSourcePartitionEvidence? SourcePartitionEvidence(
        IReadOnlyDictionary<string, string>? readBack,
        CoreCdc.CdcProvider provider
    )
    {
        if (readBack is null || !readBack.TryGetValue("topic.prefix", out string? topicPrefix))
        {
            return null;
        }

        Dictionary<string, string> properties = new(StringComparer.Ordinal) { ["server"] = topicPrefix };

        if (
            provider == CoreCdc.CdcProvider.SqlServer
            && readBack.TryGetValue(SqlServerCatalogPropertyName, out string? catalogName)
        )
        {
            properties["database"] = catalogName;
        }

        return new(properties);
    }

    private static string? SqlServerCatalogName(
        CdcConnectorTemplateRequest templateRequest,
        CoreCdc.CdcProvider provider
    ) =>
        provider == CoreCdc.CdcProvider.SqlServer
        && templateRequest.ProviderConnectionProperties.Properties.TryGetValue(
            SqlServerCatalogPropertyName,
            out string? catalogName
        )
            ? catalogName
            : null;

    /// <summary>
    /// Reports how the plugin answered without carrying its messages out of the worker: Connect echoes
    /// the submitted value back in every validation message.
    /// </summary>
    private static string PluginValidationSummary(
        CdcConnectResult<CdcConnectConfigValidation> pluginValidation
    ) =>
        pluginValidation.Value is { } validation
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} rejected connector properties",
                validation.ErrorCount
            )
            : pluginValidation.Outcome.ToString();

    /// <summary>
    /// Carries the template service's own verdict onto the admission. The template has already bounded
    /// and classified each diagnostic's text, so nothing is restated or re-derived here.
    /// </summary>
    private static IReadOnlyList<CdcDiagnostic> TemplateDiagnostics(
        CdcConnectorTemplateResult result,
        DateTimeOffset observedAt
    ) =>
        [
            .. result.Diagnostics.Select(diagnostic =>
                CdcConnectorObservationMapper.ToDiagnostic(
                    diagnostic,
                    observedAt,
                    "CDC connector configuration does not satisfy the rendered connector template."
                )
            ),
        ];

    /// <summary>
    /// The identity reported before the target has been validated. It is composed from the request as
    /// supplied so a rejected target is still reported against the target the operator named.
    /// </summary>
    private static CdcTargetIdentity UnvalidatedTargetIdentity(
        CdcControlOptions options,
        string tenantKey,
        string dataStoreId,
        CoreCdc.CdcProvider provider
    ) =>
        new(
            options.DeploymentKey,
            CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(tenantKey) ?? tenantKey,
            dataStoreId,
            options.InstanceKey,
            options.Generation,
            provider
        );

    private static CdcTargetInput TargetInput(
        CdcControlOptions options,
        string tenantKey,
        string dataStoreId,
        CoreCdc.CdcProvider provider
    ) =>
        new(
            options.DeploymentKey,
            tenantKey,
            dataStoreId,
            options.InstanceKey,
            provider,
            options.TopicPrefix,
            options.Generation,
            options.PartitionCount,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm
        );

    /// <summary>
    /// Whether the deployment already holds a binding that refuses a candidate one: some other
    /// logical target on the same physical source, or another live generation of this same target.
    /// </summary>
    /// <remarks>
    /// Each logical public topic maps to exactly one physical database. The captured rows carry no
    /// tenant or data-store discriminator, so once two targets publish one database there is nothing
    /// downstream that can tell their streams apart afterwards. The per-target record read earlier
    /// cannot see this: two CMS aliases of one database are two different targets with two different
    /// records, and only a deployment-wide look at what is already bound distinguishes a second alias
    /// from a first enablement.
    ///
    /// Another live generation of the same logical target is the other conflict, and a different one:
    /// every governed name carries the generation, so two generations never collide on an artifact —
    /// they publish one target's rows twice, from two sources, with nothing downstream to separate
    /// them. Moving a target to another physical database is a guarded source replacement, which
    /// fences the outgoing connector before the replacing generation is enabled, so the enablement
    /// admits exactly one other live generation: the one that replacement fenced and named. An enable
    /// retry names its own generation, which is the same binding rather than a second one.
    ///
    /// Adoption is held to the physical-source rule only. It reconstitutes the record of an artifact
    /// set that already exists and registers nothing, so refusing it would block the recovery of a
    /// generation this deployment had already published rather than stop a second publisher.
    ///
    /// A listing that cannot be read refuses rather than proceeds. What is asked here is what the
    /// deployment already binds, and a store that cannot be enumerated is not an answer to it —
    /// treating it as one would admit exactly the duplicates the check exists to stop.
    /// </remarks>
    private async Task<CdcBindingConflict> FindBindingConflictAsync(
        CdcBinding candidate,
        CdcSecondGenerationRule secondGeneration,
        CancellationToken cancellationToken
    )
    {
        CdcBindingLifecycleListResult listing = await bindingLifecycle
            .ListBindingsAsync(candidate.DeploymentKey, cancellationToken)
            .ConfigureAwait(false);

        if (listing.Status != CdcControlPlaneOperationStatus.Succeeded)
        {
            return new(
                true,
                "enableDeploymentBindingsUnreadable",
                CdcDiagnosticCategory.LocalStateUnavailable,
                "CDC enablement could not read the deployment's bindings to prove no other target "
                    + "already publishes this physical source.",
                listing.Status.ToString(),
                listing.Diagnostics
            );
        }

        foreach (CdcBindingStateContract state in listing.States)
        {
            if (state.Binding is not { } existing)
            {
                // A record this build cannot read as a binding may be the very one that binds this
                // source, so it is no more an answer than an unreadable listing is.
                return new(
                    true,
                    "enableDeploymentBindingsUnreadable",
                    CdcDiagnosticCategory.LocalStateUnavailable,
                    "CDC enablement found a record it could not read as a binding while proving no "
                        + "other target already publishes this physical source.",
                    state.State.ToString(),
                    []
                );
            }

            if (IsSameLogicalTarget(existing, candidate))
            {
                if (
                    !secondGeneration.RefusesAnother
                    || existing.Generation == candidate.Generation
                    || existing.Generation == secondGeneration.FencedGeneration
                )
                {
                    continue;
                }

                return new(
                    true,
                    "enableTargetGenerationAlreadyLive",
                    CdcDiagnosticCategory.BindingMismatch,
                    "CDC enablement never starts a second live generation of a target this deployment "
                        + "already binds. Replacing the physical source behind an enabled target is a "
                        + "guarded source replacement, which fences the generation it replaces first.",
                    $"generation {existing.Generation.ToString(CultureInfo.InvariantCulture)}",
                    []
                );
            }

            if (
                !string.Equals(
                    existing.PhysicalSourceFingerprint,
                    candidate.PhysicalSourceFingerprint,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            return new(
                true,
                "enablePhysicalSourceAlreadyBound",
                CdcDiagnosticCategory.SourceMismatch,
                "CDC enablement never binds a physical source another logical target of this "
                    + "deployment already publishes.",
                $"generation {existing.Generation.ToString(CultureInfo.InvariantCulture)} of "
                    + $"instance {existing.InstanceKey}",
                []
            );
        }

        return CdcBindingConflict.None;
    }

    private static bool IsSameLogicalTarget(CdcBinding existing, CdcBinding candidate) =>
        string.Equals(existing.TenantKey, candidate.TenantKey, StringComparison.Ordinal)
        && string.Equals(existing.DataStoreId, candidate.DataStoreId, StringComparison.Ordinal)
        && string.Equals(existing.InstanceKey, candidate.InstanceKey, StringComparison.Ordinal);

    /// <summary>
    /// Whether a live binding of the same logical target at another generation refuses the operation,
    /// and the one generation exempted from that because the caller fenced it first.
    /// </summary>
    private readonly record struct CdcSecondGenerationRule(bool RefusesAnother, long? FencedGeneration)
    {
        /// <summary>
        /// Every generation is admitted. Adoption reconstitutes the record of an artifact set that
        /// already exists, so a second generation there is state being recovered rather than started.
        /// </summary>
        public static CdcSecondGenerationRule Allowed { get; } = new(false, null);

        /// <summary>
        /// Another live generation refuses the operation, except the one a source replacement fenced
        /// before entering the enablement sequence. A plain enable fences nothing and passes null.
        /// </summary>
        public static CdcSecondGenerationRule RefusedUnlessFenced(long? fencedGeneration) =>
            new(true, fencedGeneration);
    }

    private readonly record struct CdcBindingConflict(
        bool Blocked,
        string Code,
        CdcDiagnosticCategory Category,
        string Message,
        string Observed,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    )
    {
        public static CdcBindingConflict None { get; } =
            new(false, string.Empty, CdcDiagnosticCategory.SourceMismatch, string.Empty, string.Empty, []);
    }

    private static CdcBinding Binding(
        CdcValidatedTarget target,
        CoreCdc.CdcProvider provider,
        string physicalSourceFingerprint,
        CdcArtifactInventory inventory
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            target.DeploymentKey,
            target.TenantKey,
            target.DataStoreId,
            target.InstanceKey,
            target.Generation,
            provider,
            physicalSourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            target.PartitionCount,
            target.PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );

    /// <summary>
    /// Opens the instance-database connection every provider pass runs over, under the same step budget
    /// the passes themselves run under.
    /// </summary>
    /// <remarks>
    /// Establishing the connection is provider work, so it is budgeted as provider work: the CLI adds no
    /// wall clock of its own, and an unreachable database would otherwise hold a verb open past every
    /// budget the deployment configured. A provider that refuses or never answers is reported as the
    /// failed step it is rather than thrown, so the verb still produces the CDC contract it owes — the
    /// same handling the retirement's own provider teardown already has.
    /// </remarks>
    /// <returns>
    /// The open connection, or the bounded token naming why it could not be opened. Provider messages
    /// quote connection settings, so only the rejection's type is reported.
    /// </returns>
    private async Task<(DbConnection? Connection, string? Refusal)> OpenProviderConnectionWithinBudgetAsync(
        CoreCdc.CdcProvider provider,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        TimeSpan budget = options.Value.Timeouts.ProviderSetup;
        using CancellationTokenSource budgetSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        budgetSource.CancelAfter(budget);

        DbConnection? connection = null;
        try
        {
            connection = connectionFactory.Create(provider, connectionString);
            await connection.OpenAsync(budgetSource.Token).ConfigureAwait(false);

            return (connection, null);
        }
        catch (Exception exception)
            when (exception is DbException or InvalidOperationException or ArgumentException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            )
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            string refusal = exception is OperationCanceledException ? "timedOut" : exception.GetType().Name;
            logger.LogDebug(
                exception,
                "CDC could not open the instance-database connection within its {Budget} budget: {Refusal}.",
                budget,
                refusal
            );

            return (null, refusal);
        }
    }

    /// <summary>
    /// Runs one provider-setup pass under <see cref="CdcControlTimeoutOptions.ProviderSetup"/>.
    /// </summary>
    /// <remarks>
    /// A pass that spends its budget returns a failed result rather than propagating a cancellation.
    /// That is the fail-closed result the step contract promises, and it needs no branch of its own at
    /// any call site: an exhausted budget is no evidence about the artifacts either way, which is
    /// exactly what a refused pass already means to each caller — the enablement refuses, the adoption
    /// refuses, and a status reports the provider evidence as unavailable.
    ///
    /// The budget is linked to the caller's token, so a cancellation the caller asked for still
    /// propagates as one instead of being reported as a provider failure.
    /// </remarks>
    private async Task<CdcProviderSetupResult> SetupProviderWithinBudgetAsync(
        CdcProviderSetupRequest setupRequest,
        CancellationToken cancellationToken
    )
    {
        TimeSpan budget = options.Value.Timeouts.ProviderSetup;
        using CancellationTokenSource budgetSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        budgetSource.CancelAfter(budget);

        try
        {
            return await providerSetup.SetupAsync(setupRequest, budgetSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                exception,
                "CDC provider setup did not complete within its {Budget} budget: {Mode}.",
                budget,
                setupRequest.Mode
            );

            return TimedOutProviderSetup(setupRequest, budget);
        }
    }

    /// <summary>
    /// The failed provider-setup result an exhausted step budget reports. It carries no artifact,
    /// grant, or history observation: the pass was interrupted, so nothing it had read is evidence.
    /// </summary>
    private static CdcProviderSetupResult TimedOutProviderSetup(
        CdcProviderSetupRequest setupRequest,
        TimeSpan budget
    ) =>
        new(
            setupRequest.Provider,
            setupRequest.Mode,
            DdlCdc.CdcProviderSetupOutcome.Failed,
            setupRequest.BoundPhysicalSourceFingerprint,
            ObservedSourceFingerprint: null,
            ArtifactInventory: [],
            GrantInventory: [],
            SourceTableInventory: [],
            ExpectedMessageKeyColumns: [],
            HeartbeatActionQuery: null,
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics:
            [
                new DdlCdc.CdcProviderDiagnostic(
                    "providerSetupTimedOut",
                    DdlCdc.CdcProviderDiagnosticCategory.ValidationMismatch,
                    DdlCdc.CdcProviderDiagnosticSeverity.Error,
                    DdlCdc.CdcPrincipalKind.SetupPrincipal,
                    // Deliberately not a source-history artifact kind: an interrupted pass observed no
                    // provider history, and a history-kinded diagnostic would be read as evidence about
                    // continuity rather than about the step.
                    DdlCdc.CdcProviderArtifactKind.None,
                    setupRequest.SetupPrincipal.SafePrincipalName,
                    ExpectedValue: budget.ToString("c", CultureInfo.InvariantCulture),
                    ObservedValue: "timedOut",
                    ProviderErrorClass: null,
                    DdlCdc.CdcProviderRetryContinuityClassification.Retryable
                ),
            ]
        );

    private static CdcProviderSetupRequest ProviderSetupRequest(
        CdcProviderSetupInputs inputs,
        CoreCdc.CdcProvider provider,
        string physicalSourceFingerprint,
        CdcArtifactInventory inventory,
        DbConnection connection,
        DdlCdc.CdcProviderSetupMode mode
    ) =>
        new(
            provider: ToDdlProvider(provider),
            mode: mode,
            boundPhysicalSourceFingerprint: new(
                CdcSourceFingerprintMetadata.Version,
                physicalSourceFingerprint
            ),
            setupPrincipal: new(new CdcSafeName(inputs.SetupPrincipal)),
            connectorPrincipal: new(new CdcSafeName(inputs.ConnectorPrincipal)),
            artifactNames: ProviderArtifactNames(provider, inventory),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false),
            expectedSourceInventory: inputs.ExpectedSourceInventory,
            dmsManagedTableInventory: inputs.DmsManagedTableInventory,
            databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(connection)
        );

    private static CdcProviderArtifactNames ProviderArtifactNames(
        CoreCdc.CdcProvider provider,
        CdcArtifactInventory inventory
    ) =>
        provider == CoreCdc.CdcProvider.Postgresql
            ? CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(inventory.PostgresqlPublicationName!),
                new CdcSafeName(inventory.PostgresqlLogicalSlotName!)
            )
            : CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName(inventory.SqlServerCdcGatingRoleName!),
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.Document] = new(inventory.SqlServerCaptureInstanceDocumentName!),
                    [CdcSourceTableKind.DocumentCache] = new(
                        inventory.SqlServerCaptureInstanceDocumentCacheName!
                    ),
                    [CdcSourceTableKind.CdcHeartbeat] = new(
                        inventory.SqlServerCaptureInstanceCdcHeartbeatName!
                    ),
                }
            );

    private static DdlCdc.CdcProvider ToDdlProvider(CoreCdc.CdcProvider provider) =>
        provider == CoreCdc.CdcProvider.Postgresql
            ? DdlCdc.CdcProvider.Postgresql
            : DdlCdc.CdcProvider.SqlServer;

    /// <summary>
    /// Reports the classifier's own decision as the step that stopped the sequence, including the
    /// action the operator must take before enablement can be attempted again.
    /// </summary>
    private static CdcDiagnostic RejectionStep(CdcRetry? rejection, DateTimeOffset observedAt)
    {
        if (rejection is null)
        {
            return Step(
                "enableEligibilityRejected",
                CdcDiagnosticCategory.InvalidObservation,
                CdcDiagnosticComponent.Retry,
                "CDC enablement rejected the target's pre-binding eligibility.",
                "rejected",
                observedAt
            );
        }

        string message =
            rejection.Action == CdcRetryAction.RetireUnusedBindingAndReprovision
                ? "CDC enablement is not an initial-enable workflow for this target; the unused binding "
                    + "generation must be retired and the target reprovisioned."
                : "CDC enablement rejected the target's durable state.";

        return Step(
            "enableEligibilityRejected",
            CdcDiagnosticCategory.InvalidObservation,
            CdcDiagnosticComponent.Retry,
            message,
            $"{rejection.RetryClassification} / {rejection.Action}",
            observedAt
        );
    }

    /// <summary>
    /// Reports the evidence source that ended a status collection early, against the binding state the
    /// rest of the status would have been collected from.
    /// </summary>
    private static CdcDiagnostic StatusStep(
        string code,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string observed,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            code,
            category,
            CdcDiagnosticSeverity.Error,
            component,
            observedAt,
            message,
            retryable: false,
            artifactKind: "cdcStatus",
            expected: "the target status observations to be collected",
            observed: observed
        ).WithPath("$.bindingState");

    private static CdcDiagnostic Step(
        string code,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string observed,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            code,
            category,
            CdcDiagnosticSeverity.Error,
            component,
            observedAt,
            message,
            retryable: false,
            artifactKind: "cdcEnablement",
            expected: "the initial readiness sequence to continue",
            observed: observed
        ).WithPath("$.steps");

    /// <summary>
    /// One status collection: the observations it gathered, and the binding facts an operation that
    /// acts on the target — rather than only reporting it — needs. They are absent when collection
    /// stopped before a binding named them.
    /// </summary>
    private sealed record CdcCollectedTargetObservations(CdcTargetStatusEvaluationInput Evaluation)
    {
        public CdcObservationContext? Context { get; init; }

        public CdcBinding? BindingRecord { get; init; }

        public CdcArtifactInventory? Inventory { get; init; }

        /// <summary>
        /// The continuity the check reported. It stays unknown when collection never reached the check,
        /// which is what an operation requiring affirmative evidence must refuse on.
        /// </summary>
        public CdcSourceHistoryContinuity Continuity { get; init; } = CdcSourceHistoryContinuity.Unknown;
    }
}
