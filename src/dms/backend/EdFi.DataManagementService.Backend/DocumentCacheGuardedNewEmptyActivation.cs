// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheGuardedNewEmptyActivationCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheGuardedNewEmptyActivationRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheGuardedNewEmptyActivationCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner
) : IDocumentCacheGuardedNewEmptyActivationCommand, IDocumentCacheAdministrativeCommandWorkflow
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheGuardedNewEmptyActivationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return commandRunner.ExecuteAsync(
            DocumentCacheAdministrativeCommandRunnerRequest.From(request),
            this,
            cancellationToken
        );
    }

    public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                Request(context),
                context.LiveTargetObservation,
                new DocumentCacheGuardedNewEmptyActivationPreflightFacts(
                    context.TargetContext.Generation,
                    DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                        context.TargetContext.TargetExecutionContext.SqlServerPrerequisites
                            ?? DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                    ),
                    new DocumentCacheGuardedNewEmptyActivationState(
                        canonicalDocumentsEmpty: true,
                        documentCacheEmpty: true,
                        documentProjectionWorkEmpty: true
                    )
                )
            )
        );
    }

    public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
            DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
        CancellationToken effectiveCancellationToken = cancellationScope.Token;

        (DocumentCacheAdministrativeCommandResult? Result, bool Commit) transaction =
            await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    async session =>
                    {
                        DocumentCacheAdministrativeCommandResult? guardedRejection = await VerifyGuardAsync(
                                context,
                                session,
                                effectiveCancellationToken
                            )
                            .ConfigureAwait(false);
                        if (guardedRejection is not null)
                        {
                            return (Result: guardedRejection, Commit: false);
                        }

                        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterTracking);

                        DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                            .Primitives.TryTransitionLifecycleAsync(
                                session,
                                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                                    DocumentCacheLifecycleState.Disabled,
                                    expectedCacheAheadRecoveryRequired: false,
                                    DocumentCacheLifecycleState.Tracking,
                                    nextCacheAheadRecoveryRequired: false
                                ),
                                effectiveCancellationToken
                            )
                            .ConfigureAwait(false);

                        if (!transition.Mutated)
                        {
                            return (Result: CreateTransitionGuardFailure(context, transition), Commit: false);
                        }

                        context.MarkMutated(transition.LifecycleReadResult.Lifecycle);
                        return (Result: (DocumentCacheAdministrativeCommandResult?)null, Commit: true);
                    },
                    static transaction => transaction.Commit,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);

        if (transaction.Result is not null)
        {
            return transaction.Result;
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.EnterTracking);
        return context.Completed();
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> VerifyGuardAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        IRelationalWriteSession session,
        CancellationToken cancellationToken
    )
    {
        await context
            .Primitives.LockCanonicalDocumentsForGuardedActivationAsync(session, cancellationToken)
            .ConfigureAwait(false);

        DocumentCacheLifecycleReadResult lifecycleReadResult = await context
            .Primitives.ReadLifecycleAsync(
                session,
                DocumentCacheAdministrativeStateLockMode.Exclusive,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!lifecycleReadResult.Succeeded)
        {
            return context.Failed(
                DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleObservationFailure,
                lifecycleReadResult.Message,
                retryable: false
            );
        }

        context.SetLiveTargetObservation(CreateTargetObservation(context, lifecycleReadResult.Lifecycle!));

        DocumentCacheGuardedNewEmptyActivationState guardedState = await context
            .Primitives.ReadGuardedNewEmptyActivationStateAsync(session, cancellationToken)
            .ConfigureAwait(false);
        DocumentCacheProviderPrerequisiteValidationResult activationPrerequisites = await context
            .Primitives.ValidateActivationPrerequisitesAsync(session, cancellationToken)
            .ConfigureAwait(false);

        DocumentCacheAdministrativeCommandResult preflightResult =
            DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                Request(context),
                context.LiveTargetObservation,
                new DocumentCacheGuardedNewEmptyActivationPreflightFacts(
                    context.TargetContext.Generation,
                    activationPrerequisites,
                    guardedState
                )
            );

        return preflightResult.Classification == DocumentCacheAdministrativeCommandClassification.Succeeded
            ? null
            : preflightResult;
    }

    private static DocumentCacheAdministrativeCommandResult CreateTransitionGuardFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeLifecycleTransitionResult transition
    )
    {
        if (transition.LifecycleReadResult.Succeeded)
        {
            context.SetLiveTargetObservation(
                CreateTargetObservation(context, transition.LifecycleReadResult.Lifecycle!)
            );
        }

        return context.Failed(
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
            transition.Message,
            retryable: false
        );
    }

    private static DocumentCacheTargetObservation CreateTargetObservation(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleObservation lifecycle
    )
    {
        DocumentCacheTargetExecutionContext executionContext = context.TargetContext.TargetExecutionContext;

        return DocumentCacheTargetObservation.ResolvedEligible(
            executionContext.TargetKey,
            executionContext.EffectiveSettings,
            executionContext.Generation,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint,
            lifecycle,
            executionContext.Inventory,
            executionContext.EnqueueTrigger,
            executionContext.SqlServerPrerequisites
        );
    }

    private static DocumentCacheGuardedNewEmptyActivationRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) => new(context.Request.TargetKey, context.Request.ExpectedPhysicalSourceFingerprint);
}
