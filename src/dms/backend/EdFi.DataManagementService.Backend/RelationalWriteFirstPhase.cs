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

        return new RelationalWriteLockedTarget(
            writeSession,
            captured.DocumentId,
            captured.ContentVersion
        );
    }

    public bool IsHeldBy(IRelationalWriteSession writeSession) => ReferenceEquals(_writeSession, writeSession);
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
/// </remarks>
internal sealed class CompositeRelationalWriteFirstPhase(
    IReferenceResolverAdapterFactory referenceResolverAdapterFactory,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger? logger = null,
    RelationalCommandBudget? commandBudget = null
) : IRelationalWriteFirstPhase
{
    private const string StoredNamespaceAuthorizationLabel = "stored-namespace-authorization";
    private const string StoredRelationshipAuthorizationLabel = "stored-relationship-authorization";
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

    private sealed record CompositeFirstPhasePlan(
        RelationalCompositeCommand Command,
        NamespaceStatementPlan? NamespacePlan,
        RelationshipStatementPlan RelationshipPlan,
        ReferenceStatementPlan? ReferencePlan,
        HydrationStatementPlan? HydrationPlan
    );

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
                is not (
                    RelationshipStatementDisposition.None
                    or RelationshipStatementDisposition.Emitted
                )
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

        if (!TryAppendStoredNamespaceAuthorization(builder, carrier, input, out var namespacePlan))
        {
            return null;
        }

        if (
            !TryAppendStoredRelationshipAuthorization(
                builder,
                carrier,
                input,
                relationshipDisposition,
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
        );
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

        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes;

        try
        {
            outcomes = await new RelationalCompositeCommandExecution()
                .ExecuteAsync(writeSession, plan.Command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            if (TryMapAuthorizationFailure(input, namespacePlan, relationshipPlan, exception) is { } mapped)
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
        RelationshipStatementPlan relationshipPlan,
        long targetDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        return relationshipPlan.Disposition switch
        {
            RelationshipStatementDisposition.None => null,
            RelationshipStatementDisposition.DeferredNoClaims =>
                RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                    executionRequest.OperationKind,
                    relationshipPlan.NoClaims!
                ),
            RelationshipStatementDisposition.Unbuildable =>
                RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    executionRequest.OperationKind,
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                ),
            RelationshipStatementDisposition.Emitted or RelationshipStatementDisposition.Standalone =>
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
                RelationalWriteTargetLookupSupport.BuildPutCaptureTargetPredicate(
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

    private sealed record NamespaceStatementPlan(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> Checks,
        NamespacePrefixParameterization PrefixParameterization
    );

    private static bool TryAppendStoredNamespaceAuthorization(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        RelationalWriteExecutorInput input,
        out NamespaceStatementPlan? statementPlan
    )
    {
        if (input.StoredNamespaceAuthorization is not { } namespaceAuthorization)
        {
            statementPlan = null;
            return true;
        }

        var sqlPlan = new NamespaceAuthorizationSqlCompiler(input.MappingSet.Key.Dialect).Compile(
            new NamespaceAuthorizationSqlSpec(
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                NamespaceAuthorizationSqlSpecDefaults.ProposedNamespaceParameterName,
                RowGuardPredicateSql: carrier.CapturedTargetPresentPredicate
            )
        );
        var command = NamespaceAuthorizationExecutor.BuildCommand(
            sqlPlan,
            new NamespaceAuthorizationExecutionRequest(
                input.MappingSet,
                DocumentId: 0L,
                ProposedNamespace: null,
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization
            )
        );

        if (
            !builder.Fits(
                GetParameterCountAfterSubstitution(
                    command,
                    NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName
                )
            )
        )
        {
            statementPlan = null;
            return false;
        }

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            BuildCarrierSubstitutions(
                carrier,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                carrier.CapturedTargetIdExpression
            )
        );
        var resultSetCount = namespaceAuthorization.Checks.Count;

        builder.Append(
            StoredNamespaceAuthorizationLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, readCancellation) => ConsumeResultSetSpanAsync(reader, resultSetCount, readCancellation),
            resultSetCount
        );

        statementPlan = new NamespaceStatementPlan(
            namespaceAuthorization.Checks,
            namespaceAuthorization.NamespacePrefixParameterization
        );
        return true;
    }

    /// <summary>
    /// How the stored relationship authorization participates in this attempt.
    /// </summary>
    internal sealed record RelationshipStatementPlan(
        RelationshipStatementDisposition Disposition,
        int Ordinal = -1,
        RelationshipAuthorizationResult.Authorized? Authorized = null,
        RelationshipAuthorizationResult.NoClaims? NoClaims = null
    );

    internal enum RelationshipStatementDisposition
    {
        /// <summary>No executable stored relationship authorization applies.</summary>
        None,

        /// <summary>The check was co-batched into the composite command.</summary>
        Emitted,

        /// <summary>
        /// The claim list binds as a table-valued parameter, so the check runs standalone on the same
        /// session after the composite command, for an observed existing target only.
        /// </summary>
        Standalone,

        /// <summary>A deferred denial: the caller holds no claims that could authorize.</summary>
        DeferredNoClaims,

        /// <summary>
        /// Executable checks arrived without claim parameterization; an observed existing target maps
        /// to the existing unknown-failure result.
        /// </summary>
        Unbuildable,
    }

    /// <summary>
    /// Classifies how the applicable stored relationship authorization — the POST plan's
    /// existing-resource stored values when plans are present, otherwise the request's stored result —
    /// participates in the attempt. Shared with test seams so classification cannot drift.
    /// </summary>
    internal static RelationshipStatementPlan ClassifyStoredRelationshipDisposition(
        RelationalWriteExecutorInput input
    )
    {
        var storedAuthorization = input.PostRelationshipAuthorizationPlans is { } plans
            ? plans.ExistingResourcePlan.StoredValues
            : input.StoredRelationshipAuthorization;

        switch (storedAuthorization)
        {
            case null
            or RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return new RelationshipStatementPlan(RelationshipStatementDisposition.None);

            case RelationshipAuthorizationResult.NoClaims noClaims:
                return new RelationshipStatementPlan(
                    RelationshipStatementDisposition.DeferredNoClaims,
                    NoClaims: noClaims
                );

            case RelationshipAuthorizationResult.KnownButNotEnabled:
                throw new InvalidOperationException(
                    "Known-but-not-enabled stored relationship authorization results must be handled by repository preflight before executor entry."
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError:
                throw new InvalidOperationException(
                    "Security-configuration stored relationship authorization results must be handled by repository preflight before executor entry."
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                if (authorized.ClaimEducationOrganizationIdParameterization is not { } parameterization)
                {
                    return new RelationshipStatementPlan(
                        RelationshipStatementDisposition.Unbuildable,
                        Authorized: authorized
                    );
                }

                return parameterization.Kind
                    is AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured
                    ? new RelationshipStatementPlan(
                        RelationshipStatementDisposition.Standalone,
                        Authorized: authorized
                    )
                    : new RelationshipStatementPlan(
                        RelationshipStatementDisposition.Emitted,
                        Authorized: authorized
                    );

            default:
                throw new InvalidOperationException(
                    $"Unsupported stored relationship authorization result '{storedAuthorization.GetType().Name}'."
                );
        }
    }

    private bool TryAppendStoredRelationshipAuthorization(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        RelationalWriteExecutorInput input,
        RelationshipStatementPlan classifiedPlan,
        out RelationshipStatementPlan statementPlan
    )
    {
        if (classifiedPlan.Disposition is not RelationshipStatementDisposition.Emitted)
        {
            statementPlan = classifiedPlan;
            return true;
        }

        var authorized = classifiedPlan.Authorized!;
        var command = BuildStoredRelationshipCommand(
            input,
            authorized,
            authorized.ClaimEducationOrganizationIdParameterization!
        );

        if (
            !builder.Fits(
                GetParameterCountAfterSubstitution(
                    command,
                    SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName
                )
            )
        )
        {
            statementPlan = classifiedPlan;
            return false;
        }

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            BuildCarrierSubstitutions(
                carrier,
                SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                carrier.CapturedTargetIdExpression
            )
        );
        var ordinal = builder.Append(
            StoredRelationshipAuthorizationLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            ReadStoredRelationshipRowAsync
        );

        statementPlan = classifiedPlan with { Ordinal = ordinal };
        return true;
    }

    private RelationalCommand BuildStoredRelationshipCommand(
        RelationalWriteExecutorInput input,
        RelationshipAuthorizationResult.Authorized authorized,
        AuthorizationClaimEducationOrganizationIdParameterization parameterization
    )
    {
        var emittedAuth1Index = RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
            input.OperationKind
        );
        var sqlPlan = authorized.ExecutableShape is { } executableShape
            ? SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                input.MappingSet,
                executableShape,
                parameterization,
                emittedAuth1Index
            )
            : SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                input.MappingSet,
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    authorized.CheckSpecs,
                    parameterization,
                    emittedAuth1Index
                )
            );

        if (sqlPlan.ProposedValueParametersInOrder.Count > 0)
        {
            throw new InvalidOperationException(
                "Single-record relationship authorization executor cannot execute proposed-value checks without extracted runtime values."
            );
        }

        return SingleRecordRelationshipAuthorizationExecutor.BuildCommand(
            sqlPlan,
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                input.MappingSet,
                DocumentId: 0L,
                authorized.CheckSpecs,
                parameterization,
                emittedAuth1Index,
                authorized.ExecutableShape
            ),
            _relationalParameterConfigurator
        );
    }

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

    private static int GetParameterCountAfterSubstitution(
        RelationalCommand command,
        params string[] substitutedParameterNames
    )
    {
        HashSet<string> substitutedBareNames = new(
            substitutedParameterNames.Select(static name => name.TrimStart('@')),
            StringComparer.OrdinalIgnoreCase
        );

        return command.Parameters.Count(parameter =>
            !substitutedBareNames.Contains(parameter.Name.TrimStart('@'))
        );
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
            BuildCarrierSubstitutions(
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

    private static IReadOnlyDictionary<string, string> BuildCarrierSubstitutions(
        IRelationalCompositeTargetCarrier carrier,
        string parameterName,
        string expression
    )
    {
        Dictionary<string, string> substitutions = new(StringComparer.OrdinalIgnoreCase);

        foreach (var reservedName in carrier.ReservedNames)
        {
            var bareName = reservedName.TrimStart('@');
            substitutions[bareName] = $"@{bareName}";
        }

        substitutions[parameterName.TrimStart('@')] = expression;
        return substitutions;
    }

    private static HydrationExecutionOptions BuildHydrationOptions(
        RelationalWriteExecutorInput input
    ) =>
        BuildHydrationOptions(
            input.ProfileWriteContext,
            RelationalWriteExecutionStateResolver.GetEtagPreconditionEvaluation(input)
        );

    private static HydrationExecutionOptions BuildHydrationOptions(
        RelationalWriteExecutorRequest request
    ) =>
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
    /// Maps a provider failure raised by the composite command through the same authorization failure
    /// mappers the standalone executors use, so a denial carried by the AUTH1 device produces exactly
    /// the result it always has. Anything unmapped propagates to the executor's existing database
    /// failure handling.
    /// </summary>
    private RelationalWriteExecutorResult? TryMapAuthorizationFailure(
        RelationalWriteExecutorInput input,
        NamespaceStatementPlan? namespacePlan,
        RelationshipStatementPlan relationshipPlan,
        DbException exception
    )
    {
        var dialect = input.MappingSet.Key.Dialect;

        if (namespacePlan is not null)
        {
            var plannedCheckValueSources = namespacePlan
                .Checks.Select(static check => check.ValueSource)
                .ToArray();

            if (
                NamespaceAuthorizationProviderFailureMapper.IsStaleStoredTargetFailure(
                    dialect,
                    exception,
                    _providerFailureExtractor,
                    plannedCheckValueSources
                )
            )
            {
                // Unreachable while the capture lock holds; kept as the same defensive mapping the
                // standalone execution had.
                return RelationalWriteExecutorResults.BuildStaleTargetResult(input.OperationKind);
            }

            if (
                NamespaceAuthorizationProviderFailureMapper.TryMapNamespaceAuthorizationFailure(
                    dialect,
                    exception,
                    _providerFailureExtractor,
                    plannedCheckValueSources,
                    namespacePlan.PrefixParameterization.ConfiguredPrefixesInOrder,
                    out var namespaceFailure
                )
            )
            {
                return RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                    input.OperationKind,
                    namespaceFailure!
                );
            }

            if (
                NamespaceAuthorizationProviderFailureMapper.TryBuildInvalidAuthorizationFailureDiagnostics(
                    dialect,
                    exception,
                    _providerFailureExtractor,
                    plannedCheckValueSources,
                    namespacePlan.Checks,
                    out var namespaceDiagnostics
                )
            )
            {
                return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    input.OperationKind,
                    [NamespaceAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata],
                    namespaceDiagnostics
                );
            }
        }

        if (
            relationshipPlan is
            { Disposition: RelationshipStatementDisposition.Emitted, Authorized: { } authorized }
        )
        {
            var emittedAuth1Index = RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                input.OperationKind
            );

            if (
                RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
                    dialect,
                    exception,
                    _providerFailureExtractor,
                    emittedAuth1Index,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization!.ClaimEducationOrganizationIds,
                    out var relationshipFailure,
                    out var invalidFailureDiagnostic
                )
            )
            {
                return RelationalWriteExecutorResults.BuildRelationshipAuthorizationFailureResult(
                    input.OperationKind,
                    relationshipFailure!
                );
            }

            if (invalidFailureDiagnostic is not null)
            {
                RelationshipAuthorizationProviderFailureMapper.LogInvalidFailurePayload(
                    _logger,
                    invalidFailureDiagnostic
                );

                return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    input.OperationKind,
                    [
                        RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError,
                    ],
                    AuthorizationSecurityConfigurationDiagnostics.ForRelationshipAuthorizationAuth1(
                        invalidFailureDiagnostic,
                        authorized.CheckSpecs
                    )
                );
            }
        }

        return null;
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
        RelationshipStatementPlan relationshipPlan,
        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes,
        RelationalCompositeCapturedTarget capturedTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        switch (relationshipPlan.Disposition)
        {
            case RelationshipStatementDisposition.None:
                return null;

            case RelationshipStatementDisposition.DeferredNoClaims:
                return RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                    executionRequest.OperationKind,
                    relationshipPlan.NoClaims!
                );

            case RelationshipStatementDisposition.Unbuildable:
                return RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    executionRequest.OperationKind,
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                );

            case RelationshipStatementDisposition.Standalone:
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

            case RelationshipStatementDisposition.Emitted:
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
            ReferenceResolver.TryBuildLookupRequest(input.ReferenceResolutionRequest)
            is not { } lookupRequest
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

    private sealed record StoredRelationshipAuthorizationRow(int AuthorizationResult, long ContentVersion);

    private static async Task<object?> ReadStoredRelationshipRowAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = new StoredRelationshipAuthorizationRow(
            reader.GetInt32(reader.GetOrdinal("AuthorizationResult")),
            reader.GetInt64(reader.GetOrdinal("ContentVersion"))
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Stored relationship authorization returned more than one row for a single locked target."
            );
        }

        return row;
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

    /// <summary>
    /// Consumes a statement's declared result-set span without materializing rows: the namespace
    /// checks either authorized (a constant row), were vacuous behind the row guard (no row), or
    /// aborted the command before reaching here.
    /// </summary>
    private static async Task<object?> ConsumeResultSetSpanAsync(
        DbDataReader reader,
        int resultSetCount,
        CancellationToken cancellationToken
    )
    {
        for (var resultSetIndex = 0; resultSetIndex < resultSetCount; resultSetIndex++)
        {
            if (resultSetIndex > 0 && !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Expected {resultSetCount} authorization result sets but the provider produced "
                        + $"{resultSetIndex}."
                );
            }

            // Rows carry no information — a denial aborts the command instead — but each result set
            // is still read through so a provider that raises during row streaming raises here.
            bool hasRow;

            do
            {
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            } while (hasRow);
        }

        return null;
    }

    private sealed class ReplayReferenceResolverAdapter(IReadOnlyList<ReferenceLookupResult> decodedResults)
        : IReferenceResolverAdapter
    {
        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(decodedResults);
    }
}
