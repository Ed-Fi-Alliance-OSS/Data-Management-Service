// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Resolves a write's first phase — target capture and lock, stored authorization, reference
/// resolution, and current-state hydration — inside the open write session.
/// </summary>
internal interface IRelationalWriteFirstPhase
{
    Task<RelationalWriteFirstPhaseResolution> ResolveAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The first phase's result: either a decoded outcome the executor continues from, or an immediate
/// caller-visible result — a missing PUT target, a stored authorization denial — after which the
/// executor rolls the session back and returns.
/// </summary>
internal sealed record RelationalWriteFirstPhaseResolution(
    RelationalWriteFirstPhaseOutcome? Outcome,
    RelationalWriteExecutorResult? ImmediateResult
)
{
    public static RelationalWriteFirstPhaseResolution Immediate(
        RelationalWriteExecutorResult immediateResult
    ) => new(null, immediateResult);
}

/// <summary>
/// Everything the first phase decided and observed, in one transaction, before any proposed
/// authorization or data modification runs.
/// </summary>
/// <param name="ExecutionRequest">
/// The fully resolved request: the initial in-transaction target observation applied, and for POST the
/// target-dependent relationship authorization plan already selected.
/// </param>
/// <param name="LockedTarget">
/// Proof that the existing target row is locked by this session from the capture statement through
/// commit; <see langword="null"/> exactly when the write creates a new document.
/// </param>
/// <param name="ResolvedReferences">The classified reference resolution outcome.</param>
/// <param name="CurrentState">
/// The hydrated current state for an existing target when a read plan was available, otherwise null.
/// </param>
internal sealed record RelationalWriteFirstPhaseOutcome(
    RelationalWriteExecutorRequest ExecutionRequest,
    RelationalWriteLockedTarget? LockedTarget,
    ResolvedReferenceSet ResolvedReferences,
    RelationalWriteCurrentState? CurrentState
);

/// <summary>
/// Proof that the current session holds the target document's row lock, constructible only from the
/// decoded outcome of the capture statement the session just executed. The guarded no-op path accepts
/// only this type in place of a freshness re-read: the row has been locked continuously from the
/// observing statement through commit, so no other transaction can have changed it in between.
/// </summary>
internal sealed class RelationalWriteLockedTarget
{
    private readonly IRelationalWriteSession _writeSession;

    private RelationalWriteLockedTarget(
        IRelationalWriteSession writeSession,
        long documentId,
        long observedContentVersion
    )
    {
        _writeSession = writeSession;
        DocumentId = documentId;
        ObservedContentVersion = observedContentVersion;
    }

    public long DocumentId { get; }

    public long ObservedContentVersion { get; }

    /// <summary>
    /// Mints the proof from a capture statement's outcome. Any other decoding path is rejected, so the
    /// type cannot be produced for a row this session did not lock.
    /// </summary>
    public static RelationalWriteLockedTarget FromCaptureOutcome(
        RelationalCompositeStatementOutcome captureOutcome,
        IRelationalWriteSession writeSession
    )
    {
        ArgumentNullException.ThrowIfNull(captureOutcome);
        ArgumentNullException.ThrowIfNull(writeSession);

        if (
            captureOutcome
            is not { Label: "capture-target", Value: RelationalCompositeCapturedTarget captured }
        )
        {
            throw new ArgumentException(
                "A locked target is constructible only from a capture-target statement outcome that "
                    + "observed a document.",
                nameof(captureOutcome)
            );
        }

        return new RelationalWriteLockedTarget(writeSession, captured.DocumentId, captured.ContentVersion);
    }

    public bool IsHeldBy(IRelationalWriteSession writeSession) =>
        ReferenceEquals(_writeSession, writeSession);
}

/// <summary>
/// The production first phase: one composite command when every statement can safely share it,
/// otherwise ordered same-session segments whose command order is the behavioral precedence order.
/// </summary>
/// <remarks>
/// <para>
/// Statement 0 captures and locks the target — the referential-id lookup for POST, the document-uuid
/// lookup for PUT — and every later statement consumes the captured decision through the provider
/// carrier rather than re-observing, so a concurrent create landing after the capture cannot change
/// what this attempt decided. Stored namespace checks precede the stored relationship check, and both
/// abort the command through the AUTH1 device on denial, so statement order is the denial order the
/// caller observes. Reference resolution and current-state hydration follow; both are ordinary
/// observations that decode client-side.
/// </para>
/// <para>
/// On the single-composite path, statements behind the capture are emitted before the target is known,
/// so each is written to be
/// vacuous when the capture observed nothing: the namespace checks carry a row guard, the relationship
/// check's own target CTE yields no row, and the hydration batch hydrates no document. A POST that
/// resolves to create-new therefore pays the statements' empty result sets rather than a second
/// command shape — one command must serve both branches, because choosing per branch would require
/// knowing the target before the command runs.
/// </para>
/// <para>
/// A target-dependent POST immediate result, a provider shape that cannot be embedded, or a combined
/// parameter count above the command budget selects the ordered-segment path before a composite builder
/// is mutated. Capture runs alone first. Each later command binds the decoded captured DocumentId and
/// remains on the same transaction, so SQL Server never depends on a batch-local carrier crossing a
/// command boundary and the original row lock remains held through the phase.
/// </para>
/// <para>
/// The SQL Server hint set locks a target the capture actually observes; it does not lock a range when
/// there is none. A create — the bulk-import case, where the referential-id subquery yields no row —
/// takes no key lock at all, only the statement's intent lock on the table, so nothing is held across
/// reference resolution and the DML. An update-shaped capture holds exactly one row lock, which is the
/// reason for taking it: the observation deciding create-versus-update must be the row the write then
/// mutates. Measured directly against the generated schema rather than inferred from the hint names.
/// </para>
/// </remarks>
internal sealed class CompositeRelationalWriteFirstPhase(
    IReferenceResolverAdapterFactory referenceResolverAdapterFactory,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger? logger = null,
    RelationalCommandBudget? commandBudget = null,
    IRelationalCommandExecutor? customViewValidationCommandExecutor = null,
    IRelationalWriteExceptionClassifier? writeExceptionClassifier = null
) : IRelationalWriteFirstPhase
{
    private const string ReferenceResolutionLabel = "reference-resolution";
    private const string CurrentStateHydrationLabel = "current-state-hydration";

    private readonly IReferenceResolverAdapterFactory _referenceResolverAdapterFactory =
        referenceResolverAdapterFactory
        ?? throw new ArgumentNullException(nameof(referenceResolverAdapterFactory));

    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private readonly RelationalCommandBudget? _commandBudget = commandBudget;

    /// <summary>
    /// The fresh-connection executor the custom-view catalog validation runs on, or <see langword="null"/> to
    /// skip validation. Never the write session's: the probe reads the catalog, and issuing it on the session
    /// would run inside the transaction holding the target lock and consume that command's results.
    /// </summary>
    private readonly IRelationalCommandExecutor? _customViewValidationCommandExecutor =
        customViewValidationCommandExecutor;

    /// <summary>
    /// Keeps transient provider failures (deadlock victim, lock timeout) out of the custom-view attribution:
    /// they say nothing about the view's contract and must keep their retryable write-conflict classification.
    /// </summary>
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? new NoOpRelationalWriteExceptionClassifier();

    private sealed record CompositeFirstPhasePlan(
        RelationalCompositeCommand Command,
        StoredNamespaceStatementPlan? NamespacePlan,
        StoredRelationshipStatementPlan RelationshipPlan,
        ReferenceStatementPlan? ReferencePlan,
        HydrationStatementPlan? HydrationPlan
    )
    {
        /// <summary>
        /// The custom-view checks the command carried, or <see langword="null"/> when it carried none — so a
        /// provider failure is mapped only against checks that command actually sent.
        /// </summary>
        public StoredCustomViewStatementPlan? CustomViewPlan { get; init; }

        /// <summary>
        /// Exactly the custom-view checks the command carries statements for, so their views are validated
        /// before it runs and no other view's contract can preempt what it decides.
        /// </summary>
        public IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CustomViewCommandChecks { get; init; } =
        [];
    }

    public async Task<RelationalWriteFirstPhaseResolution> ResolveAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(writeSession);

        if (TryBuildSingleCompositePlan(input) is not { } plan)
        {
            return await ResolveInOrderedSegmentsAsync(input, writeSession, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ExecuteSingleCompositePlanAsync(input, writeSession, plan, cancellationToken)
            .ConfigureAwait(false);
    }

    private CompositeFirstPhasePlan? TryBuildSingleCompositePlan(RelationalWriteExecutorInput input)
    {
        var relationshipDisposition = ClassifyStoredRelationshipDisposition(input);

        if (
            input.PostRelationshipAuthorizationPlans?.CreateNewImmediateResult is not null
            || relationshipDisposition.Disposition
                is not (StoredRelationshipDisposition.None or StoredRelationshipDisposition.Emitted)
        )
        {
            return null;
        }

        var lookupRequest = ReferenceResolver.TryBuildLookupRequest(input.ReferenceResolutionRequest);
        RelationalCommand? lookupCommand = null;

        if (lookupRequest is not null)
        {
            lookupCommand = _referenceResolverAdapterFactory.TryBuildSessionLookupCommand(lookupRequest);

            if (lookupCommand is null)
            {
                return null;
            }
        }

        var dialect = input.MappingSet.Key.Dialect;
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(dialect),
            _commandBudget
        );
        var carrier = builder.Carrier;

        AppendCaptureTarget(builder, input);

        // Custom views and NamespaceBased are AND filters executing in CMS-configured order, and the command
        // aborts at its first failure, so only the views configured before the namespace statement can ride it.
        // A run configured after that check owes its own ordered segment, because its views may be validated
        // only once the check has passed — so the whole request takes the ordered-segments path, where every run
        // is already executed and validated in configured order.
        var (customViewsBeforeNamespace, customViewsAfterNamespace) = PartitionCustomViewRuns(input);

        if (customViewsAfterNamespace.Count > 0)
        {
            return null;
        }

        RelationalCompositeStoredAuthorization.AppendCustomViewRun(
            builder,
            carrier,
            input.MappingSet,
            customViewsBeforeNamespace
        );

        if (
            !RelationalCompositeStoredAuthorization.TryAppendNamespace(
                builder,
                carrier,
                input.MappingSet,
                input.StoredNamespaceAuthorization,
                out var namespacePlan
            )
        )
        {
            return null;
        }

        // Mapping uses the request's full planned list, never the emitted slice, so a payload resolves to the
        // check the planner assigned that index to.
        var customViewPlan =
            input.CustomViewAuthorization is { } customViewAuthorization
            && customViewsBeforeNamespace.Count > 0
                ? new StoredCustomViewStatementPlan(customViewAuthorization.Checks)
                : null;

        if (
            !RelationalCompositeStoredAuthorization.TryAppendRelationship(
                builder,
                carrier,
                input.MappingSet,
                relationshipDisposition,
                RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(input.OperationKind),
                _relationalParameterConfigurator,
                out var relationshipPlan
            )
        )
        {
            return null;
        }

        if (!TryAppendReferenceResolution(builder, lookupRequest, lookupCommand, out var referencePlan))
        {
            return null;
        }

        var hydrationPlan = AppendCurrentStateHydration(builder, carrier, input);

        return new CompositeFirstPhasePlan(
            builder.Seal(),
            namespacePlan,
            relationshipPlan,
            referencePlan,
            hydrationPlan
        )
        {
            CustomViewPlan = customViewPlan,
            CustomViewCommandChecks = customViewsBeforeNamespace,
        };
    }

    private async Task<RelationalWriteFirstPhaseResolution> ExecuteSingleCompositePlanAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CompositeFirstPhasePlan plan,
        CancellationToken cancellationToken
    )
    {
        var namespacePlan = plan.NamespacePlan;
        var relationshipPlan = plan.RelationshipPlan;
        var referencePlan = plan.ReferencePlan;
        var hydrationPlan = plan.HydrationPlan;

        // Ahead of the command, because the views it carries run ahead of the namespace check inside it: a table
        // masquerading as auth.{StrategyName} answers the membership SQL without raising anything, so nothing
        // later in the command would reveal it.
        if (_customViewValidationCommandExecutor is not null)
        {
            await CustomViewAuthorizationValidator
                .ValidateSingleRecordAsync(
                    _customViewValidationCommandExecutor,
                    input.MappingSet.Key.Dialect,
                    plan.CustomViewCommandChecks,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes;
        var execution = new RelationalCompositeCommandExecution();

        try
        {
            outcomes = await execution
                .ExecuteAsync(writeSession, plan.Command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            if (
                TryMapAuthorizationFailure(
                    input,
                    namespacePlan,
                    plan.CustomViewPlan,
                    relationshipPlan,
                    exception,
                    execution.Failure
                ) is
                { } mapped
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(mapped);
            }

            throw;
        }

        var capturedTarget = (RelationalCompositeCapturedTarget?)outcomes[0].Value;

        if (capturedTarget is null && input.TargetRequest is RelationalWriteTargetRequest.Put)
        {
            return RelationalWriteFirstPhaseResolution.Immediate(BuildMissingPutTargetResult(input));
        }

        RelationalWriteTargetContext targetContext = capturedTarget is null
            ? new RelationalWriteTargetContext.CreateNew(
                ((RelationalWriteTargetRequest.Post)input.TargetRequest).CandidateDocumentUuid
            )
            : new RelationalWriteTargetContext.ExistingDocument(
                capturedTarget.DocumentId,
                new DocumentUuid(capturedTarget.DocumentUuid),
                capturedTarget.ContentVersion
            );

        var (executionRequest, planSelectionImmediateResult) = ApplyTargetAndPlanSelection(
            input,
            targetContext
        );

        if (planSelectionImmediateResult is not null)
        {
            return RelationalWriteFirstPhaseResolution.Immediate(planSelectionImmediateResult);
        }

        if (capturedTarget is not null)
        {
            var storedRelationshipResult = await ResolveStoredRelationshipDispositionAsync(
                    executionRequest,
                    relationshipPlan,
                    outcomes,
                    capturedTarget,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (storedRelationshipResult is not null)
            {
                return RelationalWriteFirstPhaseResolution.Immediate(storedRelationshipResult);
            }

            if (
                RelationalWriteExecutorResults.BuildMissingExistingDocumentReadPlanResult(executionRequest) is
                { } missingReadPlanResult
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(missingReadPlanResult);
            }
        }

        var currentState = DecodeCurrentState(hydrationPlan, outcomes, capturedTarget);

        var resolvedReferences = await ResolveReferencesAsync(
                input,
                referencePlan,
                outcomes,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        var lockedTarget = capturedTarget is null
            ? null
            : RelationalWriteLockedTarget.FromCaptureOutcome(outcomes[0], writeSession);

        return new RelationalWriteFirstPhaseResolution(
            new RelationalWriteFirstPhaseOutcome(
                executionRequest,
                lockedTarget,
                resolvedReferences,
                currentState
            ),
            null
        );
    }

    private async Task<RelationalWriteFirstPhaseResolution> ResolveInOrderedSegmentsAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var captureBuilder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(input.MappingSet.Key.Dialect),
            _commandBudget
        );
        AppendCaptureTarget(captureBuilder, input);

        var captureOutcomes = await new RelationalCompositeCommandExecution()
            .ExecuteAsync(writeSession, captureBuilder.Seal(), cancellationToken)
            .ConfigureAwait(false);
        var captureOutcome = captureOutcomes[0];
        var capturedTarget = (RelationalCompositeCapturedTarget?)captureOutcome.Value;

        if (capturedTarget is null && input.TargetRequest is RelationalWriteTargetRequest.Put)
        {
            return RelationalWriteFirstPhaseResolution.Immediate(BuildMissingPutTargetResult(input));
        }

        RelationalWriteTargetContext targetContext = capturedTarget is null
            ? new RelationalWriteTargetContext.CreateNew(
                ((RelationalWriteTargetRequest.Post)input.TargetRequest).CandidateDocumentUuid
            )
            : new RelationalWriteTargetContext.ExistingDocument(
                capturedTarget.DocumentId,
                new DocumentUuid(capturedTarget.DocumentUuid),
                capturedTarget.ContentVersion
            );

        var (executionRequest, planSelectionImmediateResult) = ApplyTargetAndPlanSelection(
            input,
            targetContext
        );

        if (planSelectionImmediateResult is not null)
        {
            return RelationalWriteFirstPhaseResolution.Immediate(planSelectionImmediateResult);
        }

        if (capturedTarget is not null)
        {
            // Same configured order the co-batched path emits, now as separate segments: the views
            // configured before NamespaceBased, then the namespace check, then the views configured after it.
            var (segmentedViewsBefore, segmentedViewsAfter) = PartitionCustomViewRuns(input);

            if (
                await ExecuteStandaloneStoredCustomViewAsync(
                        executionRequest,
                        segmentedViewsBefore,
                        capturedTarget.DocumentId,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false) is
                { } customViewBeforeResult
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(customViewBeforeResult);
            }

            var storedNamespaceResult = await ExecuteStandaloneStoredNamespaceAsync(
                    executionRequest,
                    capturedTarget.DocumentId,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (storedNamespaceResult is not null)
            {
                return RelationalWriteFirstPhaseResolution.Immediate(storedNamespaceResult);
            }

            if (
                await ExecuteStandaloneStoredCustomViewAsync(
                        executionRequest,
                        segmentedViewsAfter,
                        capturedTarget.DocumentId,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false) is
                { } customViewAfterResult
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(customViewAfterResult);
            }

            var storedRelationshipResult = await ResolveStandaloneStoredRelationshipDispositionAsync(
                    executionRequest,
                    ClassifyStoredRelationshipDisposition(input),
                    capturedTarget.DocumentId,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (storedRelationshipResult is not null)
            {
                return RelationalWriteFirstPhaseResolution.Immediate(storedRelationshipResult);
            }

            if (
                RelationalWriteExecutorResults.BuildMissingExistingDocumentReadPlanResult(executionRequest) is
                { } missingReadPlanResult
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(missingReadPlanResult);
            }
        }

        var decodedReferenceResults = await ExecuteStandaloneReferenceLookupAsync(
                input,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        RelationalWriteCurrentState? currentState = null;

        if (capturedTarget is not null && executionRequest.ExistingDocumentReadPlan is { } readPlan)
        {
            var options = BuildHydrationOptions(executionRequest);
            var hydratedPage = await HydrationExecutor
                .ExecuteAsync(
                    batchSql => writeSession.CreateCommand(new RelationalCommand(batchSql)),
                    readPlan,
                    new PageKeysetSpec.Single(capturedTarget.DocumentId),
                    executionRequest.MappingSet.Key.Dialect,
                    options,
                    cancellationToken
                )
                .ConfigureAwait(false);
            currentState = DecodeCurrentState(hydratedPage, capturedTarget);
        }

        var resolvedReferences = await ResolveReferencesFromDecodedResultsAsync(
                input,
                decodedReferenceResults,
                cancellationToken
            )
            .ConfigureAwait(false);
        var lockedTarget = capturedTarget is null
            ? null
            : RelationalWriteLockedTarget.FromCaptureOutcome(captureOutcome, writeSession);

        return new RelationalWriteFirstPhaseResolution(
            new RelationalWriteFirstPhaseOutcome(
                executionRequest,
                lockedTarget,
                resolvedReferences,
                currentState
            ),
            null
        );
    }

    /// <summary>
    /// Runs one custom-view run as its own ordered segment on the write session, for the ordered-segments
    /// path. Mapping uses the request's full planned list so a run carrying request-wide indexes still
    /// resolves its payload to the right check.
    /// </summary>
    private async Task<RelationalWriteExecutorResult?> ExecuteStandaloneStoredCustomViewAsync(
        RelationalWriteExecutorRequest executionRequest,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> segmentChecks,
        long targetDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (segmentChecks.Count == 0 || executionRequest.CustomViewAuthorization is null)
        {
            return null;
        }

        var executionResult = await new CustomViewAuthorizationExecutor(
            writeSession.CreateCommandExecutor(),
            _providerFailureExtractor,
            _customViewValidationCommandExecutor,
            _writeExceptionClassifier
        )
            .ExecuteAsync(
                new CustomViewAuthorizationExecutionRequest(
                    executionRequest.MappingSet,
                    targetDocumentId,
                    segmentChecks,
                    executionRequest.CustomViewAuthorization.Checks
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            CustomViewAuthorizationExecutionResult.Authorized => null,
            CustomViewAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                RelationalWriteExecutorResults.BuildCustomViewAuthorizationFailureResult(
                    executionRequest.OperationKind,
                    notAuthorized.Failure
                ),
            CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    executionRequest.OperationKind,
                    [invalidFailure.FailureMessage],
                    invalidFailure.Diagnostics
                ),
            // Unreachable while the capture lock holds; the same defensive mapping the namespace segment uses.
            CustomViewAuthorizationExecutionResult.StaleTarget =>
                RelationalWriteExecutorResults.BuildStaleTargetResult(executionRequest.OperationKind),
            _ => throw new InvalidOperationException(
                $"Unsupported custom view authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }

    private async Task<RelationalWriteExecutorResult?> ExecuteStandaloneStoredNamespaceAsync(
        RelationalWriteExecutorRequest executionRequest,
        long targetDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (executionRequest.StoredNamespaceAuthorization is not { } namespaceAuthorization)
        {
            return null;
        }

        return await StoredNamespaceAuthorizationExecution
            .ExecuteAsync<RelationalWriteExecutorResult>(
                writeSession.CreateCommandExecutor(),
                _providerFailureExtractor,
                executionRequest.MappingSet,
                targetDocumentId,
                namespaceAuthorization,
                failure =>
                    RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                        executionRequest.OperationKind,
                        failure
                    ),
                (message, diagnostics) =>
                    RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                        executionRequest.OperationKind,
                        [message],
                        diagnostics
                    ),
                () => RelationalWriteExecutorResults.BuildStaleTargetResult(executionRequest.OperationKind),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<RelationalWriteExecutorResult?> ResolveStandaloneStoredRelationshipDispositionAsync(
        RelationalWriteExecutorRequest executionRequest,
        StoredRelationshipStatementPlan relationshipPlan,
        long targetDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        return relationshipPlan.Disposition switch
        {
            StoredRelationshipDisposition.None => null,
            StoredRelationshipDisposition.DeferredNoClaims =>
                RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                    executionRequest.OperationKind,
                    relationshipPlan.NoClaims!
                ),
            StoredRelationshipDisposition.Unbuildable =>
                RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    executionRequest.OperationKind,
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                ),
            StoredRelationshipDisposition.Emitted or StoredRelationshipDisposition.Standalone =>
                await ExecuteStandaloneStoredRelationshipAsync(
                        executionRequest,
                        relationshipPlan.Authorized!,
                        targetDocumentId,
                        writeSession,
                        _relationalParameterConfigurator,
                        _providerFailureExtractor,
                        _logger,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(relationshipPlan),
                relationshipPlan.Disposition,
                null
            ),
        };
    }

    private static void AppendCaptureTarget(
        RelationalCompositeCommandBuilder builder,
        RelationalWriteExecutorInput input
    )
    {
        var capturePredicate = input.TargetRequest switch
        {
            RelationalWriteTargetRequest.Post(var referentialId, _) =>
                RelationalWriteTargetLookupSupport.BuildPostCaptureTargetPredicate(
                    input.MappingSet,
                    input.WritePlan.Model.Resource,
                    referentialId
                ),
            RelationalWriteTargetRequest.Put(var documentUuid) =>
                RelationalWriteTargetLookupSupport.BuildDocumentUuidCaptureTargetPredicate(
                    input.MappingSet,
                    input.WritePlan.Model.Resource,
                    documentUuid
                ),
            _ => throw new InvalidOperationException(
                $"Relational write target resolution does not support target request type '{input.TargetRequest.GetType().Name}'."
            ),
        };

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            capturePredicate,
            builder.Allocator,
            builder.NextOrdinal
        );

        builder.AppendCaptureTarget(rewritten.Sql, rewritten.Parameters);
    }

    /// <summary>
    /// Classifies how the applicable stored relationship authorization — the POST plan's existing-resource
    /// stored values when plans are present, otherwise the request's stored result — participates in the
    /// attempt. Which result applies is the write path's concern; how a result participates is shared with
    /// every other verb that authorizes stored values.
    /// </summary>
    internal static StoredRelationshipStatementPlan ClassifyStoredRelationshipDisposition(
        RelationalWriteExecutorInput input
    ) =>
        RelationalCompositeStoredAuthorization.Classify(
            input.PostRelationshipAuthorizationPlans is { } plans
                ? plans.ExistingResourcePlan.StoredValues
                : input.StoredRelationshipAuthorization
        );

    private sealed record ReferenceStatementPlan(ReferenceLookupRequest LookupRequest, int? Ordinal);

    private static bool TryAppendReferenceResolution(
        RelationalCompositeCommandBuilder builder,
        ReferenceLookupRequest? lookupRequest,
        RelationalCommand? lookupCommand,
        out ReferenceStatementPlan? statementPlan
    )
    {
        if (lookupRequest is null)
        {
            statementPlan = null;
            return true;
        }

        if (lookupCommand is null)
        {
            throw new InvalidOperationException(
                "A composite reference statement requires a prebuilt lookup command."
            );
        }

        if (!builder.Fits(lookupCommand.Parameters.Count))
        {
            statementPlan = null;
            return false;
        }

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            lookupCommand,
            builder.Allocator,
            builder.NextOrdinal
        );
        var ordinal = builder.Append(
            ReferenceResolutionLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            static (reader, readCancellation) => ReadReferenceLookupResultsAsync(reader, readCancellation)
        );

        statementPlan = new ReferenceStatementPlan(lookupRequest, ordinal);
        return true;
    }

    private sealed record HydrationStatementPlan(
        int Ordinal,
        ResourceReadPlan ReadPlan,
        HydrationExecutionOptions Options
    );

    private static HydrationStatementPlan? AppendCurrentStateHydration(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        RelationalWriteExecutorInput input
    )
    {
        if (input.ExistingDocumentReadPlan is not { } readPlan)
        {
            return null;
        }

        // Mirrors the projection modes the sequential path used: descriptors join the load for profile
        // writes and for the deferred-precondition flow, which is knowable from the input alone.
        var options = BuildHydrationOptions(input);
        var batchSql = HydrationBatchBuilder.BuildGuardedSingleDocumentBatch(
            readPlan,
            input.MappingSet.Key.Dialect,
            options,
            carrier.CapturedTargetPresentPredicate
        );
        var keyset = new PageKeysetSpec.Single(0);
        var resultSetCount = HydrationExecutor.GetResultSetCount(readPlan, keyset, options);
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            new RelationalCommand(batchSql),
            builder.Allocator,
            builder.NextOrdinal,
            RelationalCompositeStoredAuthorization.BuildCarrierSubstitutions(
                carrier,
                HydrationSqlConventions.SingleDocumentIdParameterName,
                carrier.CapturedTargetIdExpression
            )
        );
        var ordinal = builder.Append(
            CurrentStateHydrationLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, readCancellation) =>
                ReadHydratedPageAsync(reader, readPlan, keyset, options, readCancellation),
            resultSetCount
        );

        return new HydrationStatementPlan(ordinal, readPlan, options);
    }

    private static HydrationExecutionOptions BuildHydrationOptions(RelationalWriteExecutorInput input) =>
        BuildHydrationOptions(
            input.ProfileWriteContext,
            RelationalWriteExecutionStateResolver.GetEtagPreconditionEvaluation(input)
        );

    private static HydrationExecutionOptions BuildHydrationOptions(RelationalWriteExecutorRequest request) =>
        BuildHydrationOptions(
            request.ProfileWriteContext,
            RelationalWriteExecutionStateResolver.GetEtagPreconditionEvaluation(request)
        );

    private static HydrationExecutionOptions BuildHydrationOptions(
        BackendProfileWriteContext? profileWriteContext,
        EtagPreconditionEvaluation etagPreconditionEvaluation
    ) =>
        new(
            IncludeDescriptorProjection: profileWriteContext is not null
                || etagPreconditionEvaluation
                    is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization,
            IncludeDocumentReferenceLookup: false,
            UseSingleDocumentFastPath: true
        );

    /// <summary>
    /// Maps a provider failure raised by the composite command through the shared stored-authorization
    /// classification, so a denial carried by the AUTH1 device produces exactly the result it always has.
    /// Anything unclassified propagates to the executor's existing database failure handling.
    /// </summary>
    private RelationalWriteExecutorResult? TryMapAuthorizationFailure(
        RelationalWriteExecutorInput input,
        StoredNamespaceStatementPlan? namespacePlan,
        StoredCustomViewStatementPlan? customViewPlan,
        StoredRelationshipStatementPlan relationshipPlan,
        DbException exception,
        RelationalCompositeFailureContext? failureContext
    ) =>
        RelationalCompositeStoredAuthorization.TryClassifyDenial(
            input.MappingSet.Key.Dialect,
            exception,
            namespacePlan,
            relationshipPlan,
            RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(input.OperationKind),
            _providerFailureExtractor,
            _logger,
            customViewPlan,
            // The write path plans no ownership check yet; Phase 6 wires it. Passed explicitly rather than
            // defaulted so a valid own1 denial cannot fail closed as a 500 through an omitted argument.
            ownershipPlan: null
        ) switch
        {
            // Stale is unreachable while the capture lock holds; kept as the same defensive mapping the
            // standalone execution had.
            StoredAuthorizationDenial.StaleTarget => RelationalWriteExecutorResults.BuildStaleTargetResult(
                input.OperationKind
            ),
            StoredAuthorizationDenial.NamespaceNotAuthorized(var failure) =>
                RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                    input.OperationKind,
                    failure
                ),
            StoredAuthorizationDenial.CustomViewNotAuthorized(var failure) =>
                RelationalWriteExecutorResults.BuildCustomViewAuthorizationFailureResult(
                    input.OperationKind,
                    failure
                ),
            StoredAuthorizationDenial.RelationshipNotAuthorized(var failure) =>
                RelationalWriteExecutorResults.BuildRelationshipAuthorizationFailureResult(
                    input.OperationKind,
                    failure
                ),
            StoredAuthorizationDenial.SecurityConfiguration(var messages, var diagnostics) =>
                RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    input.OperationKind,
                    messages,
                    diagnostics
                ),
            // A failure with no authorization payload in a command carrying custom-view statements is
            // attributed to the configured view, so a dropped or revoked auth.{StrategyName} keeps the
            // documented urn:ed-fi:api:system 500 rather than escaping as an unhandled provider error. The
            // statement label alone cannot decide it: PostgreSQL prepares the whole batch before executing
            // any of it, so a missing view surfaces at reader-open nominally against statement 0. A transient
            // provider failure (deadlock victim, lock timeout) proves nothing about the view's contract, so it
            // maps to the same retryable write-conflict result the executor's failure mapper would produce.
            _ when IsAttributableToCustomView(customViewPlan, failureContext) =>
                _writeExceptionClassifier.IsTransientFailure(exception)
                    ? BuildTransientWriteConflictResult(input.OperationKind)
                    : throw new CustomViewAuthorizationValidationException(exception),
            _ => null,
        };

    /// <summary>
    /// The transient-failure result for the operation, mirroring the write executor's database failure
    /// mapping so a deadlock in the opening command answers the same retryable 409 as one in the DML.
    /// </summary>
    private static RelationalWriteExecutorResult BuildTransientWriteConflictResult(
        RelationalWriteOperationKind operationKind
    ) =>
        operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureWriteConflict()
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureWriteConflict()
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

    private static bool IsAttributableToCustomView(
        StoredCustomViewStatementPlan? customViewPlan,
        RelationalCompositeFailureContext? failureContext
    )
    {
        if (customViewPlan is null)
        {
            return false;
        }

        if (
            string.Equals(
                failureContext?.Label,
                RelationalCompositeStoredAuthorization.CustomViewLabel,
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        return failureContext?.Stage
            is RelationalCompositeFailureStage.OpeningReader
                or RelationalCompositeFailureStage.Unattributable;
    }

    /// <summary>
    /// Splits the planned custom-view checks around the configured position of <c>NamespaceBased</c>. With no
    /// namespace check every view runs ahead of the relationship group, so the whole list is the first run.
    /// </summary>
    private static (
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Before,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> After
    ) PartitionCustomViewRuns(RelationalWriteExecutorInput input)
    {
        if (input.CustomViewAuthorization is not { } customViewAuthorization)
        {
            return ([], []);
        }

        // Only the stored source belongs in this phase; the proposed source runs after merge finalizes the
        // root row.
        return input.StoredNamespaceAuthorization is { } storedNamespaceAuthorization
            ? CustomViewAuthorizationCheckSplitter.PartitionByConfiguredIndex(
                customViewAuthorization.StoredChecks,
                storedNamespaceAuthorization.Checks[0].RawConfiguredIndex
            )
            : (customViewAuthorization.StoredChecks, []);
    }

    /// <summary>
    /// Shapes a PUT whose in-transaction target observation found nothing. RFC 9110 §13.1.1 If-Match:
    /// * requires the target to exist, so a wildcard against a missing PUT target yields the
    /// precondition-failed (412) result rather than not-exists (404).
    /// </summary>
    internal static RelationalWriteExecutorResult BuildMissingPutTargetResult(
        RelationalWriteExecutorInput input
    ) =>
        input.WritePrecondition is WritePrecondition.IfMatch { IsWildcard: true }
            ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                input.OperationKind,
                ETagPreconditionFailureReason.TargetDoesNotExist
            )
            : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists());

    /// <summary>
    /// Applies the initial in-transaction target observation and, for POST, selects the
    /// target-dependent relationship authorization plan the observation decides.
    /// </summary>
    internal static (
        RelationalWriteExecutorRequest ExecutionRequest,
        RelationalWriteExecutorResult? ImmediateResult
    ) ApplyTargetAndPlanSelection(
        RelationalWriteExecutorInput input,
        RelationalWriteTargetContext targetContext
    )
    {
        var executionRequest = input.Resolve(targetContext);

        if (input.PostRelationshipAuthorizationPlans is not { } plans)
        {
            return (executionRequest, null);
        }

        executionRequest = executionRequest with { PostRelationshipAuthorizationPlans = null };

        if (targetContext is RelationalWriteTargetContext.CreateNew)
        {
            if (plans.CreateNewImmediateResult is not null)
            {
                return (executionRequest, plans.CreateNewImmediateResult);
            }

            return (
                executionRequest with
                {
                    StoredRelationshipAuthorization = null,
                    ProposedRelationshipAuthorization = plans.CreateNewProposedRelationshipAuthorization,
                },
                null
            );
        }

        return (
            executionRequest with
            {
                StoredRelationshipAuthorization = plans.ExistingResourcePlan.StoredValues,
                ProposedRelationshipAuthorization = GetExistingResourceProposedAuthorization(
                    plans.ExistingResourcePlan
                ),
            },
            null
        );
    }

    private static RelationshipAuthorizationResult.Authorized? GetExistingResourceProposedAuthorization(
        RelationshipAuthorizationUpdatePlan existingResourcePlan
    ) =>
        existingResourcePlan.ProposedValues switch
        {
            RelationshipAuthorizationResult.Authorized authorized => authorized,
            RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired => null,
            RelationshipAuthorizationResult.NoClaims => null,
            RelationshipAuthorizationResult.KnownButNotEnabled => throw new InvalidOperationException(
                "Known-but-not-enabled POST relationship authorization results must be handled by repository preflight before executor entry."
            ),
            RelationshipAuthorizationResult.SecurityConfigurationError => throw new InvalidOperationException(
                "Security-configuration POST relationship authorization results must be handled by repository preflight before executor entry."
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported existing-resource POST proposed relationship authorization result '{existingResourcePlan.ProposedValues.GetType().Name}'."
            ),
        };

    /// <summary>
    /// Resolves what the stored relationship authorization decided for an observed existing target.
    /// A denial or invalid payload already surfaced as a mapped provider failure; what remains is the
    /// decoded success row, the deferred dispositions, and the table-valued fallback.
    /// </summary>
    private async Task<RelationalWriteExecutorResult?> ResolveStoredRelationshipDispositionAsync(
        RelationalWriteExecutorRequest executionRequest,
        StoredRelationshipStatementPlan relationshipPlan,
        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes,
        RelationalCompositeCapturedTarget capturedTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        switch (relationshipPlan.Disposition)
        {
            case StoredRelationshipDisposition.None:
                return null;

            case StoredRelationshipDisposition.DeferredNoClaims:
                return RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                    executionRequest.OperationKind,
                    relationshipPlan.NoClaims!
                );

            case StoredRelationshipDisposition.Unbuildable:
                return RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    executionRequest.OperationKind,
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                );

            case StoredRelationshipDisposition.Standalone:
                return await ExecuteStandaloneStoredRelationshipAsync(
                        executionRequest,
                        relationshipPlan.Authorized!,
                        capturedTarget.DocumentId,
                        writeSession,
                        _relationalParameterConfigurator,
                        _providerFailureExtractor,
                        _logger,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            case StoredRelationshipDisposition.Emitted:
                var row = (StoredRelationshipAuthorizationRow?)outcomes[relationshipPlan.Ordinal].Value;

                if (row is null)
                {
                    // The check's target CTE saw no row, which the capture lock makes unreachable.
                    // Kept as the same defensive mapping the standalone execution had.
                    return executionRequest.OperationKind switch
                    {
                        RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict()
                        ),
                        RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureNotExists()
                        ),
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(executionRequest),
                            executionRequest.OperationKind,
                            null
                        ),
                    };
                }

                if (row.AuthorizationResult != 1)
                {
                    return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                        executionRequest.OperationKind,
                        [
                            $"Relationship authorization returned unexpected result '{row.AuthorizationResult}'.",
                        ],
                        AuthorizationSecurityConfigurationDiagnostics.ForRelationshipInvalidAuthorizationResult(
                            relationshipPlan.Authorized!.CheckSpecs
                        )
                    );
                }

                return null;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(relationshipPlan),
                    relationshipPlan.Disposition,
                    null
                );
        }
    }

    /// <summary>
    /// Executes the stored relationship check standalone on the session — the pre-adoption command
    /// shape — and maps the execution result exactly as the composite decode does. Production reaches
    /// this only for the table-valued claim binding; test seams reuse it so the mapping cannot drift.
    /// </summary>
    internal static async Task<RelationalWriteExecutorResult?> ExecuteStandaloneStoredRelationshipAsync(
        RelationalWriteExecutorRequest executionRequest,
        RelationshipAuthorizationResult.Authorized authorized,
        long targetDocumentId,
        IRelationalWriteSession writeSession,
        IRelationalParameterConfigurator relationalParameterConfigurator,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var authorizationExecutor = new SingleRecordRelationshipAuthorizationExecutor(
            writeSession.CreateCommandExecutor(),
            relationalParameterConfigurator,
            providerFailureExtractor,
            logger
        );
        var executionResult = await authorizationExecutor
            .ExecuteAsync(
                new SingleRecordRelationshipAuthorizationExecutionRequest(
                    executionRequest.MappingSet,
                    targetDocumentId,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization!,
                    RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                        executionRequest.OperationKind
                    ),
                    authorized.ExecutableShape
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            SingleRecordRelationshipAuthorizationExecutionResult.Authorized => null,
            SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                RelationalWriteExecutorResults.BuildRelationshipAuthorizationFailureResult(
                    executionRequest.OperationKind,
                    notAuthorized.RelationshipFailure
                ),
            SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget =>
                executionRequest.OperationKind switch
                {
                    RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureWriteConflict()
                    ),
                    RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureNotExists()
                    ),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(executionRequest),
                        executionRequest.OperationKind,
                        null
                    ),
                },
            SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    executionRequest.OperationKind,
                    [invalidFailure.FailureMessage],
                    invalidFailure.Diagnostics
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }

    private static RelationalWriteCurrentState? DecodeCurrentState(
        HydrationStatementPlan? hydrationPlan,
        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes,
        RelationalCompositeCapturedTarget? capturedTarget
    )
    {
        if (hydrationPlan is null || capturedTarget is null)
        {
            return null;
        }

        var hydratedPage = (HydratedPage)outcomes[hydrationPlan.Ordinal].Value!;
        return DecodeCurrentState(hydratedPage, capturedTarget);
    }

    private static RelationalWriteCurrentState DecodeCurrentState(
        HydratedPage hydratedPage,
        RelationalCompositeCapturedTarget capturedTarget
    )
    {
        var currentState =
            RelationalWriteCurrentStateLoader.TranslateHydratedPage(hydratedPage, capturedTarget.DocumentId)
            ?? throw new InvalidOperationException(
                $"Current-state hydration returned no metadata for locked document id {capturedTarget.DocumentId}."
            );

        if (currentState.DocumentMetadata.ContentVersion != capturedTarget.ContentVersion)
        {
            throw new InvalidOperationException(
                $"Current-state hydration observed content version {currentState.DocumentMetadata.ContentVersion} "
                    + $"for document id {capturedTarget.DocumentId}, but the capture lock observed "
                    + $"{capturedTarget.ContentVersion}. The decoded result stream is misaligned."
            );
        }

        return currentState;
    }

    private async Task<IReadOnlyList<ReferenceLookupResult>?> ExecuteStandaloneReferenceLookupAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (
            ReferenceResolver.TryBuildLookupRequest(input.ReferenceResolutionRequest) is not { } lookupRequest
        )
        {
            return null;
        }

        return await _referenceResolverAdapterFactory
            .CreateSessionAdapter(writeSession.CreateCommandExecutor())
            .ResolveAsync(lookupRequest, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ResolvedReferenceSet> ResolveReferencesFromDecodedResultsAsync(
        RelationalWriteExecutorInput input,
        IReadOnlyList<ReferenceLookupResult>? decodedResults,
        CancellationToken cancellationToken
    )
    {
        IReferenceResolverAdapter adapter = new ReplayReferenceResolverAdapter(decodedResults ?? []);

        return await new ReferenceResolver(adapter)
            .ResolveAsync(input.ReferenceResolutionRequest, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ResolvedReferenceSet> ResolveReferencesAsync(
        RelationalWriteExecutorInput input,
        ReferenceStatementPlan? referencePlan,
        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        IReferenceResolverAdapter adapter;

        if (referencePlan is { Ordinal: { } ordinal })
        {
            var decodedResults = (IReadOnlyList<ReferenceLookupResult>)outcomes[ordinal].Value!;
            adapter = new ReplayReferenceResolverAdapter(decodedResults);
        }
        else if (referencePlan is not null)
        {
            // The provider's lookup shape could not join the composite command and runs standalone
            // on this same session.
            adapter = _referenceResolverAdapterFactory.CreateSessionAdapter(
                writeSession.CreateCommandExecutor()
            );
        }
        else
        {
            // No lookup was needed; replaying an empty set avoids constructing a provider adapter or
            // asking the write session for a command executor that cannot be invoked.
            adapter = new ReplayReferenceResolverAdapter([]);
        }

        return await new ReferenceResolver(adapter)
            .ResolveAsync(input.ReferenceResolutionRequest, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<object?> ReadReferenceLookupResultsAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        // The wrapper is deliberately not disposed: the composite execution owns the reader, and this
        // statement owns exactly one result set of it.
        var commandReader = new DbRelationalCommandReader(reader);

        return await ReferenceLookupResultReader
            .ReadAsync(commandReader, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<object?> ReadHydratedPageAsync(
        DbDataReader reader,
        ResourceReadPlan readPlan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions options,
        CancellationToken cancellationToken
    ) =>
        await HydrationExecutor
            .ReadPageAsync(reader, readPlan, keyset, options, cancellationToken)
            .ConfigureAwait(false);

    private sealed class ReplayReferenceResolverAdapter(IReadOnlyList<ReferenceLookupResult> decodedResults)
        : IReferenceResolverAdapter
    {
        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(decodedResults);
    }
}
