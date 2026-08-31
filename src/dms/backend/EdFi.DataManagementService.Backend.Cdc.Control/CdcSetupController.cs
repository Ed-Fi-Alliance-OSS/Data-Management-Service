// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
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
);

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
    public async Task<CdcAdmission> EnableAsync(
        CdcEnableRequest request,
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
                    now
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
            .ProbeAsync(EligibilityProbeRequest(request, target, provisioningProof), cancellationToken)
            .ConfigureAwait(false);

        evaluation = evaluation with
        {
            EligibilityObservation = eligibility,
            PhysicalSourceFingerprint = eligibility.PhysicalSourceFingerprint,
        };

        CdcRetryClassification? retryClassification = null;
        if (firstAttempt)
        {
            CdcInitialEnablePreBindingEligibilityResult preBinding =
                CdcInitialEnableRetryClassifier.EvaluatePreBindingEligibility(
                    new(
                        request.OperationId,
                        now,
                        now,
                        target.ToTargetIdentity(),
                        eligibility.PhysicalSourceFingerprint,
                        provisioningProof,
                        eligibility
                    )
                );
            if (!preBinding.CanCreateBinding)
            {
                return Blocked(
                    RejectionStep(preBinding.Rejection, now),
                    preBinding.Rejection?.Diagnostics ?? preBinding.Diagnostics
                );
            }
        }
        else
        {
            CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
                new(
                    request.OperationId,
                    now,
                    now,
                    target.ToTargetIdentity(),
                    eligibility.PhysicalSourceFingerprint,
                    provisioningProof,
                    eligibility,
                    bindingRead.State
                )
            );
            if (retry.Action != CdcRetryAction.Proceed)
            {
                return Blocked(RejectionStep(retry, now), retry.Diagnostics);
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
                    now
                ),
                eligibility.Diagnostics
            );
        }

        // Step 3: the binding record is durable before any external artifact is created, so nothing is
        // ever provisioned that the control plane cannot name afterwards.
        CdcBinding binding = Binding(target, provider, physicalSourceFingerprint, inventory);
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
                        now
                    ),
                    []
                );
            }

            // The activation is carried into the admission as a fresh read-only observation of the
            // instance database rather than as the command's own report of itself: every later step is
            // classified against the observed lifecycle, and a command that answered without leaving the
            // database tracking is not an activation the sequence may build on.
            eligibility = await eligibilityProbe
                .ProbeAsync(EligibilityProbeRequest(request, target, provisioningProof), cancellationToken)
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
        await using DbConnection connection = connectionFactory.Create(provider, request.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        CdcProviderSetupResult created = await providerSetup
            .SetupAsync(
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
            return Blocked(
                Step(
                    "enableProviderSetupFailed",
                    CdcDiagnosticCategory.ProviderSetupInvalid,
                    CdcDiagnosticComponent.ProviderSetup,
                    "CDC enablement could not create or exact-match the provider capture artifacts.",
                    created.Outcome.ToString(),
                    now
                ),
                []
            );
        }

        // The shared observation is composed from validate-only evidence, so the artifacts just created
        // are read back through the same inspection every later status check uses.
        CdcProviderSetupResult validated = await providerSetup
            .SetupAsync(
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

        CdcObservationContext context = new(
            request.OperationId,
            target.ToTargetIdentity(),
            physicalSourceFingerprint
        );

        evaluation = evaluation with
        {
            ConnectOffsetStore = await kafkaAdmin
                .EnsureConnectOffsetStoreAsync(context, cancellationToken)
                .ConfigureAwait(false),
        };
        evaluation = evaluation with
        {
            KafkaPolicy = await kafkaAdmin
                .EnsureBindingKafkaPolicyAsync(context, inventory, cancellationToken)
                .ConfigureAwait(false),
        };

        logger.LogDebug("CDC enablement provisioned the provider and Kafka artifacts for the binding.");

        // Step 6: render the connector, validate it before it is registered, register it, and validate
        // what the worker actually holds against the same template rules.
        CdcConnectorTemplateRequest templateRequest;
        try
        {
            templateRequest = new(
                binding,
                new CdcConnectorProviderSetupEvidence(target.Generation, created),
                controlOptions.ToDeploymentPolicy(),
                controlOptions.ToProviderConnectionProperties(ToDdlProvider(provider)),
                controlOptions.ToKafkaClientSecurityProperties()
            );
        }
        catch (ArgumentException exception)
        {
            // The rejected value is carried in the exception, so only the rejection's type is reported.
            return Blocked(
                Step(
                    "enableConnectorInputsInvalid",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC enablement could not compose the connector template inputs.",
                    exception.GetType().Name,
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
            .CaptureBarrierAsync(new(request.ConnectionString, binding), cancellationToken)
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

            return Compose(collected.Evaluation);
        }

        CdcConnectResult restart = await connectClient
            .RestartConnectorAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        logger.LogDebug("CDC restart asked the worker to restart the connector: {Outcome}.", restart.Outcome);

        // The runtime evidence is re-read so the reported status describes the connector the restart
        // left behind rather than the one that was observed before it.
        return Compose(
            collected.Evaluation with
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
            }
        );
    }

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
        CdcConnectResult offsets = await connectClient
            .DeleteConnectorOffsetsAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        if (ToCleanupState(offsets) is not { } committedOffsets)
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
            Artifact(CdcGovernedArtifactKind.ConnectSourceOffsets, inventory.ConnectorName, committedOffsets)
        );

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

        // (5) The provider capture artifacts.
        try
        {
            await using DbConnection connection = connectionFactory.Create(
                provider,
                request.ConnectionString
            );
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            governedArtifacts.AddRange(
                await providerArtifactTeardown
                    .DeleteAsync(
                        new(
                            inventory,
                            request.ProviderSetup.ExpectedSourceInventory,
                            new DbConnectionCdcProviderDatabaseExecutor(connection)
                        ),
                        cancellationToken
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
            return RetirementRefused(
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
    /// The cleanup state one Connect removal reports, or null when the worker's answer is not evidence
    /// that the artifact is gone.
    /// </summary>
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

    private static CdcContractReadResult<CdcCleanupProof> RetirementFailed(
        CdcDiagnosticComponent component,
        string message,
        string path,
        string observed,
        DateTimeOffset observedAt
    ) =>
        RetirementRefused(
            CdcDiagnosticCategory.ArtifactNotRemoved,
            component,
            message,
            path,
            observed,
            observedAt,
            []
        );

    /// <summary>
    /// Reports the step that ended a retirement. No proof is issued, so the binding record stays and
    /// names what a retry must finish removing.
    /// </summary>
    private static CdcContractReadResult<CdcCleanupProof> RetirementRefused(
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
                "retireIncomplete",
                category,
                CdcDiagnosticSeverity.Error,
                component,
                observedAt,
                message,
                retryable: true,
                artifactKind: "cdcRetirement",
                expected: "every governed artifact removed before the binding record",
                observed: observed
            ).WithPath(path),
            .. diagnostics,
        ]);

    /// <summary>
    /// Replaces the physical source behind an enabled target with a new binding generation.
    /// </summary>
    /// <remarks>
    /// Every refusal is decided before anything is changed, because the first thing this does change is
    /// fence the outgoing connector, and a target that cannot be replaced must not be left with its
    /// publication stopped. The outgoing connector is stopped rather than deleted: stopping fences it
    /// from the source it is being replaced from while leaving its configuration and committed offsets
    /// for the retirement that removes them in order.
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

        // A published cache-ahead latch is a projection state a replacement cannot clear, and a latch
        // that cannot be read is not a clear one. Either way the source is not replaceable, and this is
        // decided before the outgoing connector is fenced.
        InitialCdcEligibilityObservation eligibility = await eligibilityProbe
            .ProbeAsync(
                new(
                    new(request.OperationId, targetIdentity, null),
                    provisioningProof,
                    request.ConnectionString
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
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

        // The cutover barrier: the outgoing connector is fenced so it publishes nothing further from the
        // source being replaced. A connector the worker does not hold is already fenced.
        CdcConnectResult fence = await connectClient
            .StopConnectorAsync(previousInventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);
        if (!fence.Succeeded && fence.Outcome != CdcConnectOutcome.NotFound)
        {
            return Refused(
                request.OperationId,
                targetIdentity,
                CdcDiagnosticCategory.ConnectorNotRunning,
                CdcDiagnosticComponent.ConnectorRuntime,
                "CDC source replacement could not fence the connector of the generation it replaces.",
                fence.Outcome.ToString(),
                []
            );
        }

        logger.LogDebug(
            "CDC source replacement fenced generation {Generation} and is enabling generation {Replacement}.",
            request.PreviousGeneration,
            target.Generation
        );

        return await EnableAsync(
                new CdcEnableRequest(
                    request.OperationId,
                    request.TenantKey,
                    request.DataStoreId,
                    request.ConnectionString,
                    request.ProvisioningEvidence,
                    request.ProviderSetup
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

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
    private CdcAdmission Refused(
        string operationId,
        CdcTargetIdentity targetIdentity,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string observed,
        IReadOnlyList<CdcDiagnostic> diagnostics
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
                        retryable: false,
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

        await using DbConnection connection = connectionFactory.Create(provider, request.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        CdcProviderSetupResult validated = await providerSetup
            .SetupAsync(
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

        Verify(
            CdcAdoptionVerificationKind.Connector,
            connectorStatus.Succeeded && connectorRuntime.TaskCount == 1,
            "the worker holds the binding's connector running a single task",
            connectorStatus.Succeeded
                ? $"{connectorRuntime.TaskCount?.ToString(CultureInfo.InvariantCulture) ?? "an unreadable count of"} tasks"
                : connectorStatus.Outcome.ToString()
        );

        CdcConnectorTemplateRequest templateRequest;
        try
        {
            templateRequest = new(
                binding,
                new CdcConnectorProviderSetupEvidence(binding.Generation, validated),
                controlOptions.ToDeploymentPolicy(),
                controlOptions.ToProviderConnectionProperties(ToDdlProvider(provider)),
                controlOptions.ToKafkaClientSecurityProperties()
            );
        }
        catch (ArgumentException exception)
        {
            // The rejected value is carried in the exception, so only the rejection's type is reported.
            return CdcContractReadResult<CdcAdoptionProof>.Failure([
                .. refusals,
                AdoptionRefused(
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC adoption could not compose the connector template inputs the live configuration is verified against.",
                    "$.connectorConfig",
                    exception.GetType().Name,
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
        await using DbConnection connection = connectionFactory.Create(provider, request.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        CdcProviderSetupResult validated = await providerSetup
            .SetupAsync(
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

        evaluation = evaluation with
        {
            Projection = projection,
            ConnectOffsetStore = await kafkaAdmin
                .EnsureConnectOffsetStoreAsync(context, cancellationToken)
                .ConfigureAwait(false),
        };
        evaluation = evaluation with
        {
            KafkaPolicy = await kafkaAdmin
                .EnsureBindingKafkaPolicyAsync(context, inventory, cancellationToken)
                .ConfigureAwait(false),
        };

        CdcConnectorTemplateRequest templateRequest;
        try
        {
            templateRequest = new(
                binding,
                new CdcConnectorProviderSetupEvidence(binding.Generation, validated),
                controlOptions.ToDeploymentPolicy(),
                controlOptions.ToProviderConnectionProperties(ToDdlProvider(provider)),
                controlOptions.ToKafkaClientSecurityProperties()
            );
        }
        catch (ArgumentException exception)
        {
            // The rejected value is carried in the exception, so only the rejection's type is reported.
            return Blocked(
                StatusStep(
                    "statusConnectorInputsInvalid",
                    CdcDiagnosticCategory.ConnectorConfigInvalid,
                    CdcDiagnosticComponent.ConnectorConfig,
                    "CDC status could not compose the connector template inputs the read-back is compared against.",
                    exception.GetType().Name,
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
            .CaptureBarrierAsync(new(request.ConnectionString, binding), cancellationToken)
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

        if (sourceHistory.IncidentCandidate is { } incidentCandidate)
        {
            evaluation = await LatchSourceHistoryLossAsync(
                    evaluation,
                    incidentCandidate,
                    inventory,
                    cancellationToken
                )
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
    /// Latches a proved source-history loss durably and fences the connector that carries it, so it
    /// commits no further offsets against a source it can no longer resume from exactly.
    /// </summary>
    /// <remarks>
    /// The latch is written from the classifier's own incident candidate, which it raises only for a
    /// loss it proved and never for one it read back from the binding record — so repeated status polls
    /// latch once. A latch that could not be written leaves the binding state as it was read, and the
    /// loss is reported from the observation rather than from a state that was not durable.
    /// </remarks>
    private async Task<CdcTargetStatusEvaluationInput> LatchSourceHistoryLossAsync(
        CdcTargetStatusEvaluationInput evaluation,
        CdcSourceHistoryIncidentCandidate incidentCandidate,
        CdcArtifactInventory inventory,
        CancellationToken cancellationToken
    )
    {
        CdcBindingLifecycleResult latch = await bindingLifecycle
            .LatchSourceHistoryLossAsync(incidentCandidate.ToIncident(), cancellationToken)
            .ConfigureAwait(false);
        logger.LogDebug("CDC status latched a source-history continuity loss: {Status}.", latch.Status);

        await connectClient
            .StopConnectorAsync(inventory.ConnectorName, cancellationToken)
            .ConfigureAwait(false);

        return latch.Status == CdcControlPlaneOperationStatus.Succeeded
            ? evaluation with
            {
                BindingState = latch.State ?? evaluation.BindingState,
            }
            : evaluation;
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
    private async Task<TObservation> PollAsync<TObservation>(
        Func<CancellationToken, Task<TObservation>> observe,
        Func<TObservation, bool> satisfied,
        TimeSpan budget,
        TimeSpan pollInterval,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + budget;

        while (true)
        {
            TObservation observation = await observe(cancellationToken).ConfigureAwait(false);
            if (satisfied(observation) || timeProvider.GetUtcNow() >= deadline)
            {
                return observation;
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
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

    private static CdcEligibilityProbeRequest EligibilityProbeRequest(
        CdcEnableRequest request,
        CdcValidatedTarget target,
        InitialCdcProvisioningProof provisioningProof
    ) =>
        new(
            new(request.OperationId, target.ToTargetIdentity(), null),
            provisioningProof,
            request.ConnectionString
        );

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
