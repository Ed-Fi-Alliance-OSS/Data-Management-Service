// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed class DefaultRelationalWriteExecutor(
    IRelationalWriteSessionFactory writeSessionFactory,
    IReferenceResolverAdapterFactory referenceResolverAdapterFactory,
    IRelationalWriteFlattener writeFlattener,
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalCurrentEtagPreconditionChecker currentEtagPreconditionChecker,
    IRelationalCommittedRepresentationReader committedRepresentationReader,
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteFreshnessChecker writeFreshnessChecker,
    IRelationalWriteNoProfileMergeSynthesizer noProfileMergeSynthesizer,
    IRelationalWriteProfileMergeSynthesizer profileMergeSynthesizer,
    IRelationalWritePersister persister,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalWriteConstraintResolver writeConstraintResolver,
    IRelationalReadMaterializer readMaterializer,
    IEtagComposer etagComposer,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger<DefaultRelationalWriteExecutor>? logger = null
) : IRelationalWriteExecutor
{
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));

    private readonly IReferenceResolverAdapterFactory _referenceResolverAdapterFactory =
        referenceResolverAdapterFactory
        ?? throw new ArgumentNullException(nameof(referenceResolverAdapterFactory));

    private readonly IRelationalCommittedRepresentationReader _committedRepresentationReader =
        committedRepresentationReader
        ?? throw new ArgumentNullException(nameof(committedRepresentationReader));

    private readonly IRelationalWriteFreshnessChecker _writeFreshnessChecker =
        writeFreshnessChecker ?? throw new ArgumentNullException(nameof(writeFreshnessChecker));

    private readonly IRelationalWritePersister _persister =
        persister ?? throw new ArgumentNullException(nameof(persister));

    private readonly RelationalWriteExecutionStateResolver _executionStateResolver = new(
        targetLookupResolver,
        currentStateLoader,
        currentEtagPreconditionChecker,
        etagComposer
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
        RelationalWriteExecutorRequest request,
        CancellationToken cancellationToken = default
    ) => ExecuteAsyncInternal(request, cancellationToken);

    private async Task<RelationalWriteExecutorResult> ExecuteAsyncInternal(
        RelationalWriteExecutorRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        RelationalWriteExecutorResult? writeFailureResult = null;
        var executionRequest = request;
        RelationalWriteCurrentState? currentState = null;

        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var storedAuthorizationBoundary = await _storedRelationshipAuthorizationOrchestrator
                .ResolveAsync(executionRequest, writeSession, cancellationToken)
                .ConfigureAwait(false);

            if (storedAuthorizationBoundary.ImmediateResult is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return storedAuthorizationBoundary.ImmediateResult;
            }

            executionRequest = storedAuthorizationBoundary.ExecutionRequest;
            var ifMatchPreconditionEvaluation =
                RelationalWriteExecutionStateResolver.GetIfMatchPreconditionEvaluation(executionRequest);
            // A stored-auth POST lookup is the authorization boundary for this attempt. If it saw
            // CreateNew, keep that decision stable so a later race cannot become an update without
            // stored-value authorization.
            var postTargetReevaluation = storedAuthorizationBoundary.PostTargetReevaluation;

            if (
                request.WritePrecondition is WritePrecondition.IfMatch
                && ifMatchPreconditionEvaluation is IfMatchPreconditionEvaluation.BeforeProposedAuthorization
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

                if (executionRequest.TargetContext is RelationalWriteTargetContext.CreateNew)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                        executionRequest.OperationKind
                    );
                }
            }

            var referenceResolver = new ReferenceResolver(
                _referenceResolverAdapterFactory.CreateSessionAdapter(
                    writeSession.Connection,
                    writeSession.Transaction
                )
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
                request.WritePrecondition is not WritePrecondition.IfMatch
                || ifMatchPreconditionEvaluation
                    is IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            )
            {
                var executionStateResolutionOptions =
                    ifMatchPreconditionEvaluation
                    is IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
                        ? ExecutionStateResolutionOptions.DeferredIfMatch(
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
                ifMatchPreconditionEvaluation
                is IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
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

                var guardedNoOpCommittedResponse = await _committedRepresentationReader
                    .ReadAsync(
                        executionRequest,
                        new RelationalWritePersistResult(
                            guardedTarget.DocumentId,
                            guardedTarget.DocumentUuid,
                            guardedTarget.ObservedContentVersion
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RelationalWriteExecutorResults.BuildGuardedNoOpSuccessResult(
                    request.OperationKind,
                    guardedTarget.DocumentUuid,
                    guardedNoOpCommittedResponse
                );
            }

            var persistedTarget = await _persister
                .PersistAsync(executionRequest, mergeResult, writeSession, cancellationToken)
                .ConfigureAwait(false);

            RelationalWritePersistedTargetValidator.Validate(executionRequest.TargetContext, persistedTarget);

            var committedResponse = await _committedRepresentationReader
                .ReadAsync(executionRequest, persistedTarget, cancellationToken)
                .ConfigureAwait(false);

            await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildAppliedWriteSuccessResult(
                request.OperationKind,
                executionRequest.TargetContext,
                persistedTarget,
                committedResponse
            );
        }
        catch (RelationalWriteRequestValidationException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildValidationFailureResult(
                request.OperationKind,
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
            return RelationalWriteExecutorResults.BuildPlannerContractMismatchResult(
                request.OperationKind,
                ex
            );
        }
        catch (RelationalWriteRelationshipAuthorizationNotAuthorizedException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildRelationshipAuthorizationFailureResult(
                request.OperationKind,
                ex.RelationshipFailure
            );
        }
        catch (RelationalWriteInvalidRelationshipAuthorizationFailureException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                request.OperationKind,
                [ex.FailureMessage],
                ex.Diagnostics
            );
        }
        catch (DbException ex)
        {
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
}
