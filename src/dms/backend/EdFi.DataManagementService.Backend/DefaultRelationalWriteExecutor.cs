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
    IRelationalWritePersister persister,
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
    IRelationalWriteFirstPhase? writeFirstPhase = null
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

    private readonly IRelationalWritePersister _persister =
        persister ?? throw new ArgumentNullException(nameof(persister));

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

    private readonly ProposedRelationshipAuthorizationOrchestrator _proposedRelationshipAuthorizationOrchestrator =
        new(persister);

    private readonly ProposedNamespaceAuthorizationOrchestrator _proposedNamespaceAuthorizationOrchestrator =
        new(
            relationshipAuthorizationProviderFailureExtractor
                ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance
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

            // NamespaceBased AND-composes with the relationship OR-group and runs before it, so a
            // namespace denial surfaces over a concurrent relationship denial. Mirrors the
            // stored-side ordering used for locked-target authorization.
            var namespaceAuthorizationBoundary = await _proposedNamespaceAuthorizationOrchestrator
                .ResolveAsync(executionRequest, mergeResult, writeSession, cancellationToken)
                .ConfigureAwait(false);

            if (namespaceAuthorizationBoundary.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return namespaceAuthorizationBoundary.ImmediateResult;
            }

            var proposedAuthorizationBoundary = await _proposedRelationshipAuthorizationOrchestrator
                .ResolveAsync(
                    executionRequest,
                    mergeResult,
                    writeSession,
                    cancellationToken,
                    forceStandaloneAuthorization: deferMissingDocumentReferenceFailures
                )
                .ConfigureAwait(false);

            if (proposedAuthorizationBoundary.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return proposedAuthorizationBoundary.ImmediateResult;
            }

            mergeResult = proposedAuthorizationBoundary.MergeResult;

            if (
                etagPreconditionEvaluation
                is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            )
            {
                var deferredPreconditionResult =
                    _executionStateResolver.TryBuildDeferredPreconditionFailureResult(
                        executionRequest,
                        currentState
                    );

                if (deferredPreconditionResult is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return deferredPreconditionResult;
                }
            }

            if (deferMissingDocumentReferenceFailures)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return RelationalWriteExecutorResults.BuildReferenceFailureResult(
                    executionRequest.OperationKind,
                    resolvedReferences
                );
            }

            if (
                targetContext is RelationalWriteTargetContext.ExistingDocument guardedTarget
                && mergeResult.SupportsGuardedNoOp
                && RelationalWriteGuardedNoOp.IsNoOpCandidate(mergeResult)
            )
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

                await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RelationalWriteExecutorResults.BuildGuardedNoOpSuccessResult(
                    request.OperationKind,
                    guardedTarget.DocumentUuid,
                    guardedNoOpEtag
                );
            }

            long canonicalPersistStartTimestamp = Stopwatch.GetTimestamp();
            RelationalWritePersistResult persistedTarget;
            try
            {
                persistedTarget = await _persister
                    .PersistAsync(executionRequest, mergeResult, writeSession, cancellationToken)
                    .ConfigureAwait(false);
                RecordCanonicalWriterWait(
                    executionRequest,
                    DocumentCacheWriterTelemetryLabel.AppliedWrite,
                    canonicalPersistStartTimestamp
                );
            }
            catch
            {
                RecordCanonicalWriterWait(
                    executionRequest,
                    DocumentCacheWriterTelemetryLabel.Failed,
                    canonicalPersistStartTimestamp
                );
                throw;
            }

            RelationalWritePersistedTargetValidator.Validate(executionRequest.TargetContext, persistedTarget);

            var committedEtag = ComposeCommittedEtag(executionRequest, persistedTarget.ContentVersion);

            await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildAppliedWriteSuccessResult(
                request.OperationKind,
                executionRequest.TargetContext,
                persistedTarget,
                committedEtag
            );
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
