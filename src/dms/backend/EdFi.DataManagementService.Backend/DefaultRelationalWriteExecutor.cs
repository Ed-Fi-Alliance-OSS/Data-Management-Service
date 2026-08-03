// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

internal sealed class DefaultRelationalWriteExecutor(
    IRelationalWriteSessionFactory writeSessionFactory,
    IReferenceResolverAdapterFactory referenceResolverAdapterFactory,
    IRelationalWriteFlattener writeFlattener,
    IRelationalWriteNoProfileMergeSynthesizer noProfileMergeSynthesizer,
    IRelationalWriteProfileMergeSynthesizer profileMergeSynthesizer,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalWriteConstraintResolver writeConstraintResolver,
    IRelationalReadMaterializer readMaterializer,
    IServedEtagComposer servedEtagComposer,
    IOptions<ResourceLinksOptions> linksOptions,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger<DefaultRelationalWriteExecutor>? logger = null,
    ILoggerFactory? loggerFactory = null,
    IDocumentCacheWriterTelemetry? documentCacheWriterTelemetry = null,
    IDataStoreSelection? dataStoreSelection = null,
    IRelationalWriteFirstPhase? writeFirstPhase = null,
    IRelationalWriteSecondCommandPhase? secondCommandPhase = null
) : IRelationalWriteExecutor
{
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));

    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));

    private readonly ResourceLinksOptions _linksOptions =
        linksOptions?.Value ?? throw new ArgumentNullException(nameof(linksOptions));

    private readonly IDocumentCacheWriterTelemetry _documentCacheWriterTelemetry =
        documentCacheWriterTelemetry ?? NoOpDocumentCacheWriterTelemetry.Instance;

    private readonly IDataStoreSelection? _dataStoreSelection = dataStoreSelection;

    /// <summary>
    /// The composite first phase: target capture and lock, stored authorization, reference
    /// resolution, and current-state hydration in one command. Test seams may substitute a fake.
    /// </summary>
    private readonly IRelationalWriteFirstPhase _writeFirstPhase =
        writeFirstPhase
        ?? new CompositeRelationalWriteFirstPhase(
            referenceResolverAdapterFactory
                ?? throw new ArgumentNullException(nameof(referenceResolverAdapterFactory)),
            relationalParameterConfigurator,
            relationshipAuthorizationProviderFailureExtractor,
            logger
        );

    private readonly RelationalWriteExecutionStateResolver _executionStateResolver = new(
        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RelationalWriteExecutionStateResolver>()
    );

    private readonly RelationalWriteMergeOrchestrator _mergeOrchestrator = new(
        writeFlattener,
        readMaterializer,
        noProfileMergeSynthesizer,
        profileMergeSynthesizer
    );

    /// <summary>
    /// The second command: the proposed namespace and proposed relationship <c>AUTH1</c> statements, plus
    /// the data-modifying statements and the committed <c>ContentVersion</c> read in DML mode.
    /// </summary>
    private readonly IRelationalWriteSecondCommandPhase _secondCommandPhase =
        secondCommandPhase
        ?? new CompositeRelationalWriteSecondCommand(
            relationalParameterConfigurator,
            relationshipAuthorizationProviderFailureExtractor,
            logger
        );

    private readonly RelationalWriteDatabaseFailureResultMapper _databaseFailureResultMapper = new(
        writeExceptionClassifier,
        writeConstraintResolver
    );

    public Task<RelationalWriteExecutorResult> ExecuteAsync(
        RelationalWriteExecutorInput input,
        CancellationToken cancellationToken = default
    ) => ExecuteAsyncInternal(input, cancellationToken);

    private async Task<RelationalWriteExecutorResult> ExecuteAsyncInternal(
        RelationalWriteExecutorInput input,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        RelationalWriteExecutorResult? writeFailureResult = null;
        RelationalWriteExecutorRequest? executionRequest = null;

        // The success result the attempt has earned, composed before the commit and returned only once
        // the commit succeeds. Carrying it out of the try is what lets the commit itself sit outside
        // every handler that rolls back.
        RelationalWriteExecutorResult? committedResult = null;

        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // One composite command observes and locks the target, runs stored authorization,
            // resolves references, and hydrates current state inside this session's transaction
            // before anything else runs. That first observation is the decision for the attempt: no
            // normal path re-observes it, so a create that lands afterwards can no longer turn this
            // write into an update, and the lock held from the capture through commit is what stands
            // in for the guarded no-op freshness re-read.
            var firstPhase = await _writeFirstPhase
                .ResolveAsync(input, writeSession, cancellationToken)
                .ConfigureAwait(false);

            if (firstPhase.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return firstPhase.ImmediateResult;
            }

            var outcome = firstPhase.Outcome!;
            executionRequest = outcome.ExecutionRequest;
            var request = executionRequest;
            var currentState = outcome.CurrentState;
            var lockedTarget = outcome.LockedTarget;
            var resolvedReferences = outcome.ResolvedReferences;

            var etagPreconditionEvaluation =
                RelationalWriteExecutionStateResolver.GetEtagPreconditionEvaluation(executionRequest);

            // If-None-Match is a sibling of If-Match, so the before-auth gate must admit both to
            // agree with GetEtagPreconditionEvaluation's broadened defer decision; otherwise an
            // If-None-Match write would silently skip the precondition resolution.
            if (
                RelationalWriteExecutionStateResolver.HasEtagPrecondition(request.WritePrecondition)
                && etagPreconditionEvaluation is EtagPreconditionEvaluation.BeforeProposedAuthorization
            )
            {
                // If-Match on an insert (CreateNew) fails (412); If-None-Match on an insert is the
                // create-only success case and proceeds.
                if (executionRequest.TargetContext is RelationalWriteTargetContext.CreateNew)
                {
                    if (executionRequest.WritePrecondition is WritePrecondition.IfMatch)
                    {
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                            executionRequest.OperationKind,
                            ETagPreconditionFailureReason.TargetDoesNotExist
                        );
                    }
                }
                else
                {
                    // The first phase guarantees current state for an existing target on this path:
                    // a missing read plan already returned, and the capture lock makes an empty
                    // hydration impossible.
                    var isSatisfied = EtagPreconditionEvaluator.IsSatisfiedByCurrentState(
                        executionRequest.WritePrecondition,
                        currentState!.DocumentMetadata.ContentVersion,
                        executionRequest.MappingSet.Key.EffectiveSchemaHash
                    );

                    if (!isSatisfied)
                    {
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                            executionRequest.OperationKind,
                            EtagPreconditionEvaluator.GetFailureReason(executionRequest.WritePrecondition)
                        );
                    }
                }
            }

            var hasMissingDocumentReferenceFailures = HasMissingDocumentReferenceFailures(resolvedReferences);

            if (
                HasDescriptorReferenceFailures(resolvedReferences)
                || HasNonMissingDocumentReferenceFailures(resolvedReferences)
                || (executionRequest.ProfileWriteContext is not null && hasMissingDocumentReferenceFailures)
            )
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return RelationalWriteExecutorResults.BuildReferenceFailureResult(
                    executionRequest.OperationKind,
                    resolvedReferences
                );
            }

            var deferMissingDocumentReferenceFailures =
                executionRequest.ProfileWriteContext is null && hasMissingDocumentReferenceFailures;

            var targetContext = executionRequest.TargetContext;
            var mergeBoundary = _mergeOrchestrator.Resolve(
                executionRequest,
                targetContext,
                currentState,
                resolvedReferences,
                allowMissingDocumentReferencesForPrecedence: deferMissingDocumentReferenceFailures
            );

            if (mergeBoundary.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return mergeBoundary.ImmediateResult;
            }

            var mergeResult = mergeBoundary.MergeResult!;

            // Identity comparison needs the finalized merged root row, but immutable identity
            // failures must win before any proposed authorization evaluates rejected values.
            var identityStabilityFailure = RelationalWriteIdentityStability.TryBuildFailureResult(
                executionRequest,
                mergeResult
            );

            if (identityStabilityFailure is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return identityStabilityFailure;
            }

            // Every situation that needs no data-modifying statement is decided here, in process, from
            // the merged result and the hydrated current state. Deciding it before authorization is what
            // lets the second command exist exactly once: it is emitted if and only if proposed
            // authorization is configured or DML is required, and it is in authorization-only mode
            // exactly when DML is not. A request that is several no-DML situations at once therefore
            // still costs one command rather than one per condition.
            var deferredPreconditionResult =
                etagPreconditionEvaluation
                is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
                    ? _executionStateResolver.TryBuildDeferredPreconditionFailureResult(
                        executionRequest,
                        currentState
                    )
                    : null;

            var guardedNoOpTarget =
                targetContext is RelationalWriteTargetContext.ExistingDocument existingTarget
                && mergeResult.SupportsGuardedNoOp
                && RelationalWriteGuardedNoOp.IsNoOpCandidate(mergeResult)
                    ? existingTarget
                    : null;

            var requiresDataModifyingStatements =
                deferredPreconditionResult is null
                && !deferMissingDocumentReferenceFailures
                && guardedNoOpTarget is null;

            // NamespaceBased AND-composes with the relationship OR-group and runs before it, so a
            // namespace denial surfaces over a concurrent relationship denial. Mirrors the stored-side
            // ordering used for locked-target authorization. In DML mode the same command carries the
            // document row, the resource tables, and the committed ContentVersion read behind those
            // checks.
            // The canonical persist now happens inside the second command's DML mode, so the
            // canonical-writer wait is measured over that command rather than over a separate
            // persister call. Authorization-only mode runs no data-modifying statement and is
            // therefore not a canonical persist to report.
            long canonicalPersistStartTimestamp = Stopwatch.GetTimestamp();
            RelationalWriteSecondCommandResolution secondCommand;
            try
            {
                secondCommand = await _secondCommandPhase
                    .ResolveAsync(
                        executionRequest,
                        mergeResult,
                        requiresDataModifyingStatements
                            ? RelationalWriteSecondCommandMode.Dml
                            : RelationalWriteSecondCommandMode.AuthorizationOnly,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                if (requiresDataModifyingStatements)
                {
                    RecordCanonicalWriterWait(
                        executionRequest,
                        DocumentCacheWriterTelemetryLabel.Failed,
                        canonicalPersistStartTimestamp
                    );
                }

                throw;
            }

            if (secondCommand.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return secondCommand.ImmediateResult;
            }

            if (deferredPreconditionResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return deferredPreconditionResult;
            }

            if (deferMissingDocumentReferenceFailures)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return RelationalWriteExecutorResults.BuildReferenceFailureResult(
                    executionRequest.OperationKind,
                    resolvedReferences
                );
            }

            if (guardedNoOpTarget is { } guardedTarget)
            {
                // The lock proof replaces the freshness re-read: the capture statement locked this
                // row and the lock holds through commit, so no other transaction can have bumped
                // ContentVersion since it was observed. A no-op without the proof is a decode bug,
                // not a fall-back-to-query situation.
                ValidateGuardedNoOpLockProof(lockedTarget, guardedTarget, writeSession);

                var guardedNoOpEtag = ComposeCommittedEtag(
                    executionRequest,
                    guardedTarget.ObservedContentVersion
                );

                committedResult = RelationalWriteExecutorResults.BuildGuardedNoOpSuccessResult(
                    request.OperationKind,
                    guardedTarget.DocumentUuid,
                    guardedNoOpEtag
                );
            }
            else
            {
                var persistedTarget =
                    secondCommand.PersistResult
                    ?? throw new InvalidOperationException(
                        "The second command ran in DML mode but returned no persisted target."
                    );

                RecordCanonicalWriterWait(
                    executionRequest,
                    DocumentCacheWriterTelemetryLabel.AppliedWrite,
                    canonicalPersistStartTimestamp
                );

                RelationalWritePersistedTargetValidator.Validate(
                    executionRequest.TargetContext,
                    persistedTarget
                );

                var committedEtag = ComposeCommittedEtag(executionRequest, persistedTarget.ContentVersion);

                committedResult = RelationalWriteExecutorResults.BuildAppliedWriteSuccessResult(
                    request.OperationKind,
                    executionRequest.TargetContext,
                    persistedTarget,
                    committedEtag
                );
            }
        }
        catch (RelationalWriteRequestValidationException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildValidationFailureResult(
                input.OperationKind,
                ex.ValidationFailures
            );
        }
        catch (ProfilePlannerContractMismatchException ex)
        {
            // Planner-driven invariant failure: Core handed the backend planner a profile/scope
            // combination the compiled scope catalog cannot satisfy. Shape this as a profile
            // contract-mismatch result, mirroring the upfront ProfileWriteContractValidator
            // failure path. We do NOT broaden this catch to InvalidOperationException — generic
            // invariant violations remain fail-fast for true backend bugs.
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildPlannerContractMismatchResult(input.OperationKind, ex);
        }
        catch (RelationalWriteRelationshipAuthorizationNotAuthorizedException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildRelationshipAuthorizationFailureResult(
                input.OperationKind,
                ex.RelationshipFailure
            );
        }
        catch (RelationalWriteInvalidRelationshipAuthorizationFailureException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                input.OperationKind,
                [ex.FailureMessage],
                ex.Diagnostics
            );
        }
        catch (DbException ex)
        {
            // A failure inside the first phase has no resolved request to attribute a write failure
            // to, and the phase's read-only statements cannot violate a write constraint — its
            // authorization denials were already mapped there — so it stays an unmapped fault exactly
            // as the pre-adoption target lookup was.
            if (executionRequest is null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            bool isMappedWriteFailure;

            try
            {
                isMappedWriteFailure = _databaseFailureResultMapper.TryBuild(
                    executionRequest,
                    ex,
                    out writeFailureResult
                );
            }
            catch
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);

            if (isMappedWriteFailure)
            {
                return writeFailureResult!;
            }

            throw;
        }
        catch
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        // The commit sits outside every handler above by construction, not by a flag any of them has to
        // remember to check. Once the commit has begun the server may already have applied it and only
        // failed to acknowledge it, so a client-side rollback could only fail on its own and replace the
        // real failure with an unrelated one. Session disposal releases the transaction, which settles
        // any state the server left pending.
        try
        {
            await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // Only a resolved request reaches the commit, so a commit-phase provider failure is
            // attributable and maps to the same write-failure results an in-attempt failure would. An
            // unmapped one surfaces unchanged, exactly as it does inside the attempt.
            if (_databaseFailureResultMapper.TryBuild(executionRequest!, ex, out var commitFailureResult))
            {
                return commitFailureResult!;
            }

            throw;
        }

        return committedResult!;
    }

    internal static void ValidateGuardedNoOpLockProof(
        RelationalWriteLockedTarget? lockedTarget,
        RelationalWriteTargetContext.ExistingDocument guardedTarget,
        IRelationalWriteSession writeSession
    )
    {
        ArgumentNullException.ThrowIfNull(guardedTarget);
        ArgumentNullException.ThrowIfNull(writeSession);

        if (
            lockedTarget is null
            || !lockedTarget.IsHeldBy(writeSession)
            || lockedTarget.DocumentId != guardedTarget.DocumentId
            || lockedTarget.ObservedContentVersion != guardedTarget.ObservedContentVersion
        )
        {
            throw new InvalidOperationException(
                "Guarded no-op reached without a matching capture lock proof from the current write session."
            );
        }
    }

    private static bool HasDescriptorReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
        resolvedReferences.InvalidDescriptorReferences.Count > 0;

    private static bool HasNonMissingDocumentReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
        resolvedReferences.InvalidDocumentReferences.Any(static failure =>
            failure.Reason is not DocumentReferenceFailureReason.Missing
        );

    private static bool HasMissingDocumentReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
        resolvedReferences.InvalidDocumentReferences.Any(static failure =>
            failure.Reason is DocumentReferenceFailureReason.Missing
        );

    /// <summary>
    /// Composes the served <c>_etag</c> for a just-committed write. The write response carries only
    /// the etag; the final committed <c>ContentVersion</c> is persistence metadata (from the persister,
    /// or the capture-locked stamp on the guarded no-op path). No <c>dms.Document</c> query, hydrate,
    /// or hashing occurs here — this is a pure string composition over the stored counter and the
    /// request's representation selectors (profile, format, link mode).
    /// </summary>
    private string ComposeCommittedEtag(RelationalWriteExecutorRequest request, long contentVersion) =>
        _servedEtagComposer.Compose(
            new ServedEtagContext(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                request.ProfileWriteContext?.ProfileName,
                _linksOptions.Enabled,
                contentVersion
            )
        );

    private void RecordCanonicalWriterWait(
        RelationalWriteExecutorRequest request,
        string outcome,
        long startTimestamp
    )
    {
        _documentCacheWriterTelemetry.RecordSameDocumentWait(
            DocumentCacheWriterMetricContext.ForCanonicalWriter(
                request.MappingSet.Key.Dialect,
                _dataStoreSelection,
                DocumentCacheWriterTelemetryLabel.CanonicalWrite,
                outcome
            ),
            DocumentCacheWriterContentionParticipant.CanonicalWriter,
            DocumentCacheWriterContentionPhase.CanonicalPersist,
            DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp)
        );
    }
}
