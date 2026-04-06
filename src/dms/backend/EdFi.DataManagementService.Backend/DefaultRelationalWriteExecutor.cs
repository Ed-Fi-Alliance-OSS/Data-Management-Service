// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal sealed class DefaultRelationalWriteExecutor(
    IRelationalWriteSessionFactory writeSessionFactory,
    IReferenceResolverAdapterFactory referenceResolverAdapterFactory,
    IRelationalWriteFlattener writeFlattener,
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteFreshnessChecker writeFreshnessChecker,
    IRelationalWriteNoProfileMergeSynthesizer noProfileMergeSynthesizer,
    IRelationalWriteNoProfilePersister noProfilePersister
) : IRelationalWriteExecutor
{
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));

    private readonly IReferenceResolverAdapterFactory _referenceResolverAdapterFactory =
        referenceResolverAdapterFactory
        ?? throw new ArgumentNullException(nameof(referenceResolverAdapterFactory));

    private readonly IRelationalWriteFlattener _writeFlattener =
        writeFlattener ?? throw new ArgumentNullException(nameof(writeFlattener));

    private readonly IRelationalWriteCurrentStateLoader _currentStateLoader =
        currentStateLoader ?? throw new ArgumentNullException(nameof(currentStateLoader));

    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));

    private readonly IRelationalWriteFreshnessChecker _writeFreshnessChecker =
        writeFreshnessChecker ?? throw new ArgumentNullException(nameof(writeFreshnessChecker));

    private readonly IRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer =
        noProfileMergeSynthesizer ?? throw new ArgumentNullException(nameof(noProfileMergeSynthesizer));

    private readonly IRelationalWriteNoProfilePersister _noProfilePersister =
        noProfilePersister ?? throw new ArgumentNullException(nameof(noProfilePersister));

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

        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var targetContext = request.TargetContext;
            var executionRequest = request;
            var referenceResolver = new ReferenceResolver(
                _referenceResolverAdapterFactory.CreateSessionAdapter(
                    writeSession.Connection,
                    writeSession.Transaction
                )
            );
            var resolvedReferences = await referenceResolver
                .ResolveAsync(request.ReferenceResolutionRequest, cancellationToken)
                .ConfigureAwait(false);

            if (resolvedReferences.HasFailures)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return BuildReferenceFailureResult(request.OperationKind, resolvedReferences);
            }

            RelationalWriteCurrentState? currentState = null;
            InSessionTargetResolution? inSessionTargetResolution = null;

            if (
                request.TargetRequest
                    is RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid)
                && targetContext is RelationalWriteTargetContext.CreateNew
            )
            {
                inSessionTargetResolution = await ResolveCreateVsExistingPostTargetAsync(
                        request,
                        referentialId,
                        candidateDocumentUuid,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            else if (targetContext is RelationalWriteTargetContext.ExistingDocument existingDocument)
            {
                inSessionTargetResolution = await LoadCurrentStateForExistingTargetAsync(
                        request,
                        existingDocument,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (inSessionTargetResolution is not null)
            {
                if (inSessionTargetResolution.ImmediateResult is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return inSessionTargetResolution.ImmediateResult;
                }

                targetContext = inSessionTargetResolution.TargetContext!;
                executionRequest = request with { TargetContext = targetContext };
                currentState = inSessionTargetResolution.CurrentState;
            }

            var flattenedWriteSet = _writeFlattener.Flatten(
                new FlatteningInput(
                    executionRequest.OperationKind,
                    executionRequest.TargetContext,
                    executionRequest.WritePlan,
                    executionRequest.SelectedBody,
                    resolvedReferences
                )
            );

            var noProfileMergeResult = _noProfileMergeSynthesizer.Synthesize(
                new RelationalWriteNoProfileMergeRequest(request.WritePlan, flattenedWriteSet, currentState)
            );

            var identityStabilityFailure = RelationalWriteIdentityStability.TryBuildFailureResult(
                executionRequest,
                noProfileMergeResult
            );

            if (identityStabilityFailure is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return identityStabilityFailure;
            }

            if (
                targetContext is RelationalWriteTargetContext.ExistingDocument guardedTarget
                && RelationalWriteGuardedNoOp.IsNoOpCandidate(noProfileMergeResult)
            )
            {
                var isCurrent = await _writeFreshnessChecker
                    .IsCurrentAsync(executionRequest, guardedTarget, writeSession, cancellationToken)
                    .ConfigureAwait(false);

                if (!isCurrent)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildStaleNoOpCompareResult(request.OperationKind);
                }

                await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BuildGuardedNoOpSuccessResult(request.OperationKind, guardedTarget.DocumentUuid);
            }

            await _noProfilePersister
                .PersistAsync(executionRequest, noProfileMergeResult, writeSession, cancellationToken)
                .ConfigureAwait(false);

            await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BuildAppliedWriteSuccessResult(request.OperationKind, executionRequest.TargetContext);
        }
        catch (RelationalWriteRequestValidationException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return BuildValidationFailureResult(request.OperationKind, ex.ValidationFailures);
        }
        catch
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static RelationalWriteExecutorResult BuildGuardedNoOpSuccessResult(
        RelationalWriteOperationKind operationKind,
        DocumentUuid documentUuid
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpdateSuccess(documentUuid),
                RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid),
                RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static RelationalWriteExecutorResult BuildAppliedWriteSuccessResult(
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetContext targetContext
    )
    {
        return (operationKind, targetContext) switch
        {
            (RelationalWriteOperationKind.Post, RelationalWriteTargetContext.CreateNew(var documentUuid)) =>
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(documentUuid),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            (
                RelationalWriteOperationKind.Post,
                RelationalWriteTargetContext.ExistingDocument
                (_, var documentUuid, _)
            ) => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpdateSuccess(documentUuid),
                RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
            ),
            (
                RelationalWriteOperationKind.Put,
                RelationalWriteTargetContext.ExistingDocument
                (_, var documentUuid, _)
            ) => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid),
                RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };
    }

    private static RelationalWriteExecutorResult BuildStaleNoOpCompareResult(
        RelationalWriteOperationKind operationKind
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureWriteConflict(),
                RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureWriteConflict(),
                RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static RelationalWriteExecutorResult BuildReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ResolvedReferenceSet resolvedReferences
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureReference(
                    [.. resolvedReferences.InvalidDocumentReferences],
                    [.. resolvedReferences.InvalidDescriptorReferences]
                )
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureReference(
                    [.. resolvedReferences.InvalidDocumentReferences],
                    [.. resolvedReferences.InvalidDescriptorReferences]
                )
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static RelationalWriteExecutorResult BuildValidationFailureResult(
        RelationalWriteOperationKind operationKind,
        WriteValidationFailure[] validationFailures
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureValidation(validationFailures)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureValidation(validationFailures)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private async Task<InSessionTargetResolution> ResolveCreateVsExistingPostTargetAsync(
        RelationalWriteExecutorRequest request,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var targetLookupResult = await _targetLookupResolver
            .ResolveForPostAsync(
                request.MappingSet,
                request.WritePlan.Model.Resource,
                referentialId,
                candidateDocumentUuid,
                writeSession.Connection,
                writeSession.Transaction,
                cancellationToken
            )
            .ConfigureAwait(false);

        var targetContext = RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult);

        if (targetContext is RelationalWriteTargetContext.CreateNew createdTargetContext)
        {
            return new InSessionTargetResolution(createdTargetContext, null, null);
        }

        if (targetContext is not RelationalWriteTargetContext.ExistingDocument existingTargetContext)
        {
            throw new InvalidOperationException(
                $"Relational POST target re-evaluation returned unsupported result type '{targetLookupResult.GetType().Name}'."
            );
        }

        return await LoadCurrentStateForExistingTargetAsync(
                request with
                {
                    TargetContext = existingTargetContext,
                },
                existingTargetContext,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<InSessionTargetResolution> LoadCurrentStateForExistingTargetAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var missingReadPlanResult = TryBuildMissingExistingDocumentReadPlanResult(request);

        if (missingReadPlanResult is not null)
        {
            return new InSessionTargetResolution(null, null, missingReadPlanResult);
        }

        var currentState = await _currentStateLoader
            .LoadAsync(
                new RelationalWriteCurrentStateLoadRequest(request.ExistingDocumentReadPlan!, targetContext),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (currentState is not null)
        {
            return new InSessionTargetResolution(
                RefreshTargetContextFromCurrentState(targetContext, currentState),
                currentState,
                null
            );
        }

        return await HandleMissingExistingTargetAsync(request, writeSession, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InSessionTargetResolution> HandleMissingExistingTargetAsync(
        RelationalWriteExecutorRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        return request.TargetRequest switch
        {
            RelationalWriteTargetRequest.Put => new InSessionTargetResolution(
                null,
                null,
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            ),
            RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid) =>
                await ReevaluatePostTargetAsync(
                        request,
                        referentialId,
                        candidateDocumentUuid,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Relational existing-target recovery does not support target request type '{request.TargetRequest.GetType().Name}'."
            ),
        };
    }

    private async Task<InSessionTargetResolution> ReevaluatePostTargetAsync(
        RelationalWriteExecutorRequest request,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var targetLookupResult = await _targetLookupResolver
            .ResolveForPostAsync(
                request.MappingSet,
                request.WritePlan.Model.Resource,
                referentialId,
                candidateDocumentUuid,
                writeSession.Connection,
                writeSession.Transaction,
                cancellationToken
            )
            .ConfigureAwait(false);

        var targetContext = RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult);

        if (targetContext is RelationalWriteTargetContext.CreateNew createdTargetContext)
        {
            return new InSessionTargetResolution(createdTargetContext, null, null);
        }

        if (targetContext is not RelationalWriteTargetContext.ExistingDocument existingTargetContext)
        {
            throw new InvalidOperationException(
                $"Relational POST target re-evaluation returned unsupported result type '{targetLookupResult.GetType().Name}'."
            );
        }

        var missingReadPlanResult = TryBuildMissingExistingDocumentReadPlanResult(request);

        if (missingReadPlanResult is not null)
        {
            return new InSessionTargetResolution(null, null, missingReadPlanResult);
        }

        var currentState = await _currentStateLoader
            .LoadAsync(
                new RelationalWriteCurrentStateLoadRequest(
                    request.ExistingDocumentReadPlan!,
                    existingTargetContext
                ),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return currentState is not null
            ? new InSessionTargetResolution(
                RefreshTargetContextFromCurrentState(existingTargetContext, currentState),
                currentState,
                null
            )
            : new InSessionTargetResolution(
                null,
                null,
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
    }

    private static RelationalWriteExecutorResult? TryBuildMissingExistingDocumentReadPlanResult(
        RelationalWriteExecutorRequest request
    )
    {
        if (request.ExistingDocumentReadPlan is not null)
        {
            return null;
        }

        var failureMessage = RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(
            request.WritePlan.Model.Resource
        );

        return request.OperationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(failureMessage)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(failureMessage)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
        };
    }

    private static RelationalWriteTargetContext.ExistingDocument RefreshTargetContextFromCurrentState(
        RelationalWriteTargetContext.ExistingDocument targetContext,
        RelationalWriteCurrentState currentState
    ) => targetContext with { ObservedContentVersion = currentState.DocumentMetadata.ContentVersion };

    private sealed record InSessionTargetResolution(
        RelationalWriteTargetContext? TargetContext,
        RelationalWriteCurrentState? CurrentState,
        RelationalWriteExecutorResult? ImmediateResult
    );
}
