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
    IRelationalWriteFreshnessChecker writeFreshnessChecker,
    IRelationalWriteNoProfileMergeSynthesizer noProfileMergeSynthesizer,
    IRelationalWriteNonCollectionPersister nonCollectionPersister
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

    private readonly IRelationalWriteFreshnessChecker _writeFreshnessChecker =
        writeFreshnessChecker ?? throw new ArgumentNullException(nameof(writeFreshnessChecker));

    private readonly IRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer =
        noProfileMergeSynthesizer ?? throw new ArgumentNullException(nameof(noProfileMergeSynthesizer));

    private readonly IRelationalWriteNonCollectionPersister _nonCollectionPersister =
        nonCollectionPersister ?? throw new ArgumentNullException(nameof(nonCollectionPersister));

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

            var flattenedWriteSet = _writeFlattener.Flatten(
                new FlatteningInput(
                    request.OperationKind,
                    request.TargetContext,
                    request.WritePlan,
                    request.SelectedBody,
                    resolvedReferences
                )
            );

            var resource = request.WritePlan.Model.Resource;

            RelationalWriteCurrentState? currentState = null;

            if (request.TargetContext is RelationalWriteTargetContext.ExistingDocument existingDocument)
            {
                if (request.ReadPlan is null)
                {
                    throw new InvalidOperationException(
                        RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(resource)
                    );
                }

                currentState = await _currentStateLoader
                    .LoadAsync(
                        new RelationalWriteCurrentStateLoadRequest(request.ReadPlan, existingDocument),
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var noProfileMergeResult = _noProfileMergeSynthesizer.Synthesize(
                new RelationalWriteNoProfileMergeRequest(request.WritePlan, flattenedWriteSet, currentState)
            );

            var identityStabilityFailure = RelationalWriteIdentityStability.TryBuildFailureResult(
                request,
                noProfileMergeResult
            );

            if (identityStabilityFailure is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return identityStabilityFailure;
            }

            if (
                request.TargetContext is RelationalWriteTargetContext.ExistingDocument guardedTarget
                && RelationalWriteGuardedNoOp.IsNoOpCandidate(noProfileMergeResult)
            )
            {
                var isCurrent = await _writeFreshnessChecker
                    .IsCurrentAsync(request, guardedTarget, writeSession, cancellationToken)
                    .ConfigureAwait(false);

                if (!isCurrent)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildStaleNoOpCompareResult(request.OperationKind);
                }

                await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BuildGuardedNoOpSuccessResult(request.OperationKind, guardedTarget.DocumentUuid);
            }

            var persisted = await _nonCollectionPersister
                .TryPersistAsync(request, noProfileMergeResult, writeSession, cancellationToken)
                .ConfigureAwait(false);

            if (persisted)
            {
                await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BuildAppliedWriteSuccessResult(request.OperationKind, request.TargetContext);
            }

            var failureMessage = RelationalWriteSupport.BuildWriteExecutionNotImplementedMessage(
                request.OperationKind,
                resource,
                currentStateLoaded: currentState is not null
            );

            var result = request.OperationKind switch
            {
                RelationalWriteOperationKind.Post => (RelationalWriteExecutorResult)
                    new RelationalWriteExecutorResult.Upsert(new UpsertResult.UnknownFailure(failureMessage)),
                RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UnknownFailure(failureMessage)
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
            };

            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return result;
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
}
