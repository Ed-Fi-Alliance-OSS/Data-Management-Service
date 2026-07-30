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
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalCurrentEtagPreconditionChecker currentEtagPreconditionChecker,
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteFreshnessChecker writeFreshnessChecker,
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
    IDataStoreSelection? dataStoreSelection = null
) : IRelationalWriteExecutor
{
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));

    private readonly IReferenceResolverAdapterFactory _referenceResolverAdapterFactory =
        referenceResolverAdapterFactory
        ?? throw new ArgumentNullException(nameof(referenceResolverAdapterFactory));

    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));

    private readonly ResourceLinksOptions _linksOptions =
        linksOptions?.Value ?? throw new ArgumentNullException(nameof(linksOptions));

    private readonly IDocumentCacheWriterTelemetry _documentCacheWriterTelemetry =
        documentCacheWriterTelemetry ?? NoOpDocumentCacheWriterTelemetry.Instance;

    private readonly IDataStoreSelection? _dataStoreSelection = dataStoreSelection;

    private readonly IRelationalWriteFreshnessChecker _writeFreshnessChecker =
        writeFreshnessChecker ?? throw new ArgumentNullException(nameof(writeFreshnessChecker));

    private readonly IRelationalWritePersister _persister =
        persister ?? throw new ArgumentNullException(nameof(persister));

    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));

    private readonly RelationalWriteExecutionStateResolver _executionStateResolver = new(
        targetLookupResolver,
        currentStateLoader,
        currentEtagPreconditionChecker,
        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RelationalWriteExecutionStateResolver>()
    );

    private readonly RelationalWriteMergeOrchestrator _mergeOrchestrator = new(
        writeFlattener,
        readMaterializer,
        noProfileMergeSynthesizer,
        profileMergeSynthesizer
    );

    private readonly StoredRelationshipAuthorizationOrchestrator _storedRelationshipAuthorizationOrchestrator =
        new(
            targetLookupResolver,
            relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance,
            relationshipAuthorizationProviderFailureExtractor
                ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance,
            logger
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
        RelationalWriteCurrentState? currentState = null;

        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // The target document is observed inside this session's transaction before any
            // authorization, precondition, reference resolution, hydration, or DML runs. That first
            // observation is the decision for the attempt: no normal path re-observes it, so a create
            // that lands afterwards can no longer turn this write into an update.
            var initialTarget = await ResolveInitialTargetAsync(input, writeSession, cancellationToken)
                .ConfigureAwait(false);

            if (initialTarget.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return initialTarget.ImmediateResult;
            }

            var request = input.Resolve(initialTarget.TargetContext!);
            executionRequest = request;

            var storedAuthorizationBoundary = await _storedRelationshipAuthorizationOrchestrator
                .ResolveAsync(executionRequest, writeSession, cancellationToken)
                .ConfigureAwait(false);

            if (storedAuthorizationBoundary.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return storedAuthorizationBoundary.ImmediateResult;
            }

            executionRequest = storedAuthorizationBoundary.ExecutionRequest;
            var etagPreconditionEvaluation =
                RelationalWriteExecutionStateResolver.GetEtagPreconditionEvaluation(executionRequest);
            // A stored-auth POST lookup is the authorization boundary for this attempt. If it saw
            // CreateNew, keep that decision stable so a later race cannot become an update without
            // stored-value authorization.
            var postTargetReevaluation = storedAuthorizationBoundary.PostTargetReevaluation;

            // If-None-Match is a sibling of If-Match, so the before-auth dispatch gate must admit both to
            // agree with GetEtagPreconditionEvaluation's broadened defer decision; otherwise an
            // If-None-Match write would silently skip the precondition resolution. Reuse the resolver's
            // single predicate so the two cannot drift and re-open the fail-open path.
            if (
                RelationalWriteExecutionStateResolver.HasEtagPrecondition(request.WritePrecondition)
                && etagPreconditionEvaluation is EtagPreconditionEvaluation.BeforeProposedAuthorization
            )
            {
                var resolvedExecutionState = await _executionStateResolver
                    .ResolveAsync(
                        executionRequest,
                        writeSession,
                        ExecutionStateResolutionOptions.Standard(postTargetReevaluation),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (resolvedExecutionState.ImmediateResult is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return resolvedExecutionState.ImmediateResult;
                }

                executionRequest = resolvedExecutionState.ExecutionRequest;
                currentState = resolvedExecutionState.CurrentState;

                // If-Match on an insert (CreateNew) fails (412); If-None-Match on an insert is the
                // create-only success case and proceeds.
                if (
                    executionRequest.TargetContext is RelationalWriteTargetContext.CreateNew
                    && executionRequest.WritePrecondition is WritePrecondition.IfMatch
                )
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                        executionRequest.OperationKind,
                        ETagPreconditionFailureReason.TargetDoesNotExist
                    );
                }
            }

            var referenceResolver = new ReferenceResolver(
                _referenceResolverAdapterFactory.CreateSessionAdapter(writeSession.CreateCommandExecutor())
            );
            var resolvedReferences = await referenceResolver
                .ResolveAsync(executionRequest.ReferenceResolutionRequest, cancellationToken)
                .ConfigureAwait(false);

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

            if (
                !RelationalWriteExecutionStateResolver.HasEtagPrecondition(request.WritePrecondition)
                || etagPreconditionEvaluation
                    is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            )
            {
                var executionStateResolutionOptions =
                    etagPreconditionEvaluation
                    is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
                        ? ExecutionStateResolutionOptions.DeferredEtagPrecondition(
                            storedAuthorizationBoundary,
                            postTargetReevaluation
                        )
                        : ExecutionStateResolutionOptions.Standard(postTargetReevaluation);

                var resolvedExecutionState = await _executionStateResolver
                    .ResolveAsync(
                        executionRequest,
                        writeSession,
                        executionStateResolutionOptions,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (resolvedExecutionState.ImmediateResult is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return resolvedExecutionState.ImmediateResult;
                }

                executionRequest = resolvedExecutionState.ExecutionRequest;
                currentState = resolvedExecutionState.CurrentState;
            }

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
                var isCurrent = await _writeFreshnessChecker
                    .IsCurrentAsync(executionRequest, guardedTarget, writeSession, cancellationToken)
                    .ConfigureAwait(false);

                if (!isCurrent)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return RelationalWriteExecutorResults.BuildStaleNoOpCompareResult(
                        request.OperationKind,
                        request.WritePrecondition
                    );
                }

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
            // A failure during the initial target observation has no resolved request to attribute a
            // write failure to, and the observation itself cannot violate a write constraint, so it
            // stays an unmapped fault exactly as the pre-session lookup was.
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

    /// <summary>
    /// Resolves the write's target document on the write session's command seam. POST resolves by
    /// referential id and always yields a target context; PUT resolves by document uuid and shapes a
    /// missing target into the caller-visible not-exists or precondition-failed result, which the
    /// caller returns after rolling the session back.
    /// </summary>
    private async Task<InitialTargetResolution> ResolveInitialTargetAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var commandExecutor = writeSession.CreateCommandExecutor();

        if (
            input.TargetRequest is RelationalWriteTargetRequest.Post(
                var referentialId,
                var candidateDocumentUuid
            )
        )
        {
            var postLookupResult = await _targetLookupResolver
                .ResolveForPostAsync(
                    input.MappingSet,
                    input.WritePlan.Model.Resource,
                    referentialId,
                    candidateDocumentUuid,
                    commandExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return new InitialTargetResolution(
                RelationalWriteSupport.TryTranslateTargetContext(postLookupResult)
                    ?? throw BuildUnsupportedLookupResultException(input, postLookupResult),
                null
            );
        }

        if (input.TargetRequest is RelationalWriteTargetRequest.Put(var documentUuid))
        {
            var putLookupResult = await _targetLookupResolver
                .ResolveForPutAsync(
                    input.MappingSet,
                    input.WritePlan.Model.Resource,
                    documentUuid,
                    commandExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (RelationalWriteSupport.TryTranslateTargetContext(putLookupResult) is { } targetContext)
            {
                return new InitialTargetResolution(targetContext, null);
            }

            if (putLookupResult is not RelationalWriteTargetLookupResult.NotFound)
            {
                throw BuildUnsupportedLookupResultException(input, putLookupResult);
            }

            // RFC 9110 §13.1.1 If-Match: * requires the target to exist; a wildcard against a missing
            // PUT target yields the precondition-failed (412) result rather than not-exists (404).
            return new InitialTargetResolution(
                null,
                input.WritePrecondition is WritePrecondition.IfMatch { IsWildcard: true }
                    ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                        input.OperationKind,
                        ETagPreconditionFailureReason.TargetDoesNotExist
                    )
                    : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            );
        }

        throw new InvalidOperationException(
            $"Relational write target resolution does not support target request type '{input.TargetRequest.GetType().Name}'."
        );
    }

    private static InvalidOperationException BuildUnsupportedLookupResultException(
        RelationalWriteExecutorInput input,
        RelationalWriteTargetLookupResult lookupResult
    ) =>
        new(
            $"Relational {input.OperationKind} target lookup returned unsupported result type '{lookupResult.GetType().Name}'."
        );

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
    /// or the freshness-checked stamp on the guarded no-op path). No <c>dms.Document</c> query, hydrate,
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

    private sealed record InitialTargetResolution(
        RelationalWriteTargetContext? TargetContext,
        RelationalWriteExecutorResult? ImmediateResult
    );
}
