// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed class RelationalWriteExecutionStateResolver(
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalCurrentEtagPreconditionChecker currentEtagPreconditionChecker,
    IServedEtagComposer servedEtagComposer,
    IIfMatchEvaluator ifMatchEvaluator,
    ILogger<RelationalWriteExecutionStateResolver> logger
)
{
    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));

    private readonly IRelationalWriteCurrentStateLoader _currentStateLoader =
        currentStateLoader ?? throw new ArgumentNullException(nameof(currentStateLoader));

    private readonly IRelationalCurrentEtagPreconditionChecker _currentEtagPreconditionChecker =
        currentEtagPreconditionChecker
        ?? throw new ArgumentNullException(nameof(currentEtagPreconditionChecker));

    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));

    private readonly IIfMatchEvaluator _ifMatchEvaluator =
        ifMatchEvaluator ?? throw new ArgumentNullException(nameof(ifMatchEvaluator));

    private readonly ILogger<RelationalWriteExecutionStateResolver> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public static IfMatchPreconditionEvaluation GetIfMatchPreconditionEvaluation(
        RelationalWriteExecutorRequest request
    ) =>
        request.WritePrecondition is WritePrecondition.IfMatch
        && (
            request.ProposedRelationshipAuthorization is not null
            || request.StoredNamespaceAuthorization is not null
            || request.ProposedNamespaceAuthorization is not null
        )
            ? IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            : IfMatchPreconditionEvaluation.BeforeProposedAuthorization;

    public async Task<ResolvedExecutionState> ResolveAsync(
        RelationalWriteExecutorRequest request,
        IRelationalWriteSession writeSession,
        ExecutionStateResolutionOptions options,
        CancellationToken cancellationToken
    )
    {
        var targetContext = request.TargetContext;
        RelationalWriteCurrentState? currentState = null;
        InSessionTargetResolution? inSessionTargetResolution = null;

        if (
            request.TargetRequest
                is RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid)
            && options.AllowPostTargetReevaluation
            && (
                targetContext is RelationalWriteTargetContext.CreateNew
                || request.WritePrecondition is WritePrecondition.IfMatch
            )
        )
        {
            inSessionTargetResolution = await ResolvePostTargetAsync(
                    request,
                    referentialId,
                    candidateDocumentUuid,
                    writeSession,
                    options,
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
                    options,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (inSessionTargetResolution?.ImmediateResult is not null)
        {
            return new ResolvedExecutionState(request, null, inSessionTargetResolution.ImmediateResult);
        }

        if (inSessionTargetResolution?.TargetContext is not null)
        {
            targetContext = inSessionTargetResolution.TargetContext;
            currentState = inSessionTargetResolution.CurrentState;
        }

        return new ResolvedExecutionState(request with { TargetContext = targetContext }, currentState, null);
    }

    public RelationalWriteExecutorResult? TryBuildDeferredPreconditionFailureResult(
        RelationalWriteExecutorRequest request,
        RelationalWriteCurrentState? currentState
    )
    {
        if (request.WritePrecondition is not WritePrecondition.IfMatch ifMatch)
        {
            return null;
        }

        if (request.TargetContext is RelationalWriteTargetContext.CreateNew)
        {
            return RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                request.OperationKind,
                ETagPreconditionFailureReason.TargetDoesNotExist
            );
        }

        if (request.TargetContext is not RelationalWriteTargetContext.ExistingDocument)
        {
            throw new InvalidOperationException(
                $"Deferred If-Match precondition does not support target context '{request.TargetContext.GetType().Name}'."
            );
        }

        if (request.ExistingDocumentReadPlan is null)
        {
            return RelationalWriteExecutorResults.BuildMissingExistingDocumentReadPlanResult(request);
        }

        if (currentState is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var missingTarget = (RelationalWriteTargetContext.ExistingDocument)request.TargetContext;
                _logger.LogDebug(
                    "Deferred If-Match precondition for document {DocumentId}: no current representation "
                        + "(operation={OperationKind}, wildcard={IsWildcard}, clientTag={ClientTag}); precondition fails",
                    missingTarget.DocumentId,
                    request.OperationKind,
                    ifMatch.IsWildcard,
                    LoggingSanitizer.SanitizeForLogging(ifMatch.Value)
                );
            }
            return request.OperationKind switch
            {
                RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureWriteConflict()
                ),
                // RFC 7232 If-Match: * requires the target to exist; a wildcard against a missing PUT
                // target yields the precondition-failed (412) result rather than not-exists (404).
                RelationalWriteOperationKind.Put => ifMatch.IsWildcard
                    ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                        request.OperationKind,
                        ETagPreconditionFailureReason.TargetDoesNotExist
                    )
                    : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists()),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
            };
        }

        // If-Match compares the state-significant projection of the composed etag (ContentVersion,
        // schemaEpoch). format, linkFlag, and profileCode are projected out (profileCode as of the
        // 2026-07-04 ADR amendment), so the values passed for them here are not significant. The
        // evaluator handles the wildcard precondition (If-Match: *) internally, short-circuiting to a
        // match because currentState being non-null means the target row exists.
        var currentEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                request.ProfileWriteContext?.ProfileName,
                LinksEnabled: true,
                currentState.DocumentMetadata.ContentVersion
            )
        );

        var evaluation = _ifMatchEvaluator.Evaluate(ifMatch, currentEtag);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var existing = (RelationalWriteTargetContext.ExistingDocument)request.TargetContext;
            _logger.LogDebug(
                "Deferred If-Match precondition for document {DocumentId}: wildcard={IsWildcard}, "
                    + "clientTag={ClientTag}, currentTag={CurrentTag}, matched={IsMatch}",
                existing.DocumentId,
                ifMatch.IsWildcard,
                LoggingSanitizer.SanitizeForLogging(ifMatch.Value),
                currentEtag,
                evaluation.IsMatch
            );
        }

        return evaluation.IsMatch
            ? null
            : RelationalWriteExecutorResults.BuildPreconditionFailureResult(request.OperationKind);
    }

    private async Task<InSessionTargetResolution> ResolvePostTargetAsync(
        RelationalWriteExecutorRequest request,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        IRelationalWriteSession writeSession,
        ExecutionStateResolutionOptions options,
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
                options.SuppressPostTargetReevaluation(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<InSessionTargetResolution> LoadCurrentStateForExistingTargetAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        ExecutionStateResolutionOptions options,
        CancellationToken cancellationToken
    )
    {
        var missingReadPlanResult = RelationalWriteExecutorResults.BuildMissingExistingDocumentReadPlanResult(
            request
        );

        if (missingReadPlanResult is not null)
        {
            return new InSessionTargetResolution(null, null, missingReadPlanResult);
        }

        if (
            options.IfMatchPreconditionEvaluation
                is IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            && options.ExistingTargetLock is ExistingTargetLockMode.LockBeforeCurrentStateLoad
        )
        {
            var lockedContentVersion = await RelationalWriteTargetLocking
                .TryLockExistingTargetAsync(
                    request.MappingSet.Key.Dialect,
                    targetContext.DocumentId,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (lockedContentVersion is null)
            {
                return await HandleMissingExistingTargetAsync(
                        request,
                        writeSession,
                        options,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            targetContext = targetContext with { ObservedContentVersion = lockedContentVersion.Value };
        }

        if (
            options.IfMatchPreconditionEvaluation is IfMatchPreconditionEvaluation.BeforeProposedAuthorization
            && request.WritePrecondition is WritePrecondition.IfMatch ifMatch
        )
        {
            var preconditionCheckResult = await _currentEtagPreconditionChecker
                .CheckAsync(
                    new RelationalCurrentEtagPreconditionCheckRequest(
                        request.MappingSet,
                        request.ExistingDocumentReadPlan!,
                        targetContext,
                        ifMatch,
                        request.ProfileWriteContext?.ProfileName
                    ),
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (preconditionCheckResult is not null)
            {
                return new InSessionTargetResolution(
                    preconditionCheckResult.TargetContext,
                    preconditionCheckResult.CurrentState,
                    preconditionCheckResult.IsMatch
                        ? null
                        : RelationalWriteExecutorResults.BuildPreconditionFailureResult(request.OperationKind)
                );
            }

            return await HandleMissingExistingTargetAsync(request, writeSession, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var currentState = await _currentStateLoader
            .LoadAsync(
                new RelationalWriteCurrentStateLoadRequest(
                    request.ExistingDocumentReadPlan!,
                    targetContext,
                    includeDescriptorProjection: request.ProfileWriteContext is not null
                        || options.CurrentStateProjection
                            is CurrentStateProjectionMode.IncludeDescriptorsForDeferredPrecondition
                ),
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

        return await HandleMissingExistingTargetAsync(request, writeSession, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InSessionTargetResolution> HandleMissingExistingTargetAsync(
        RelationalWriteExecutorRequest request,
        IRelationalWriteSession writeSession,
        ExecutionStateResolutionOptions options,
        CancellationToken cancellationToken
    )
    {
        return request.TargetRequest switch
        {
            RelationalWriteTargetRequest.Put => new InSessionTargetResolution(
                null,
                null,
                // RFC 7232 If-Match: * requires the target to exist; a wildcard against a missing PUT
                // target yields the precondition-failed (412) result rather than not-exists (404).
                request.WritePrecondition
                    is WritePrecondition.IfMatch { IsWildcard: true }
                    ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                        request.OperationKind,
                        ETagPreconditionFailureReason.TargetDoesNotExist
                    )
                    : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            ),
            RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid) =>
                options.AllowPostTargetReevaluation
                    ? await ResolvePostTargetAsync(
                            request,
                            referentialId,
                            candidateDocumentUuid,
                            writeSession,
                            options,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    : new InSessionTargetResolution(
                        null,
                        null,
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict()
                        )
                    ),
            _ => throw new InvalidOperationException(
                $"Relational existing-target recovery does not support target request type '{request.TargetRequest.GetType().Name}'."
            ),
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

internal sealed record ResolvedExecutionState(
    RelationalWriteExecutorRequest ExecutionRequest,
    RelationalWriteCurrentState? CurrentState,
    RelationalWriteExecutorResult? ImmediateResult
);

internal enum IfMatchPreconditionEvaluation
{
    BeforeProposedAuthorization,
    DeferredUntilAfterProposedAuthorization,
}

internal enum ExistingTargetLockMode
{
    NotRequired,
    AlreadyLocked,
    LockBeforeCurrentStateLoad,
}

internal enum CurrentStateProjectionMode
{
    Standard,
    IncludeDescriptorsForDeferredPrecondition,
}

internal sealed record ExecutionStateResolutionOptions(
    IfMatchPreconditionEvaluation IfMatchPreconditionEvaluation,
    ExistingTargetLockMode ExistingTargetLock,
    CurrentStateProjectionMode CurrentStateProjection,
    PostTargetReevaluationMode PostTargetReevaluation
)
{
    public bool AllowPostTargetReevaluation => PostTargetReevaluation is PostTargetReevaluationMode.Allowed;

    public static ExecutionStateResolutionOptions Standard(
        PostTargetReevaluationMode postTargetReevaluation
    ) =>
        new(
            IfMatchPreconditionEvaluation.BeforeProposedAuthorization,
            ExistingTargetLockMode.NotRequired,
            CurrentStateProjectionMode.Standard,
            postTargetReevaluation
        );

    public static ExecutionStateResolutionOptions DeferredIfMatch(
        StoredRelationshipAuthorizationBoundary storedAuthorizationBoundary,
        PostTargetReevaluationMode postTargetReevaluation
    )
    {
        var existingTargetLock = storedAuthorizationBoundary.ExistingTargetLocked
            ? ExistingTargetLockMode.AlreadyLocked
            : ExistingTargetLockMode.LockBeforeCurrentStateLoad;

        return new ExecutionStateResolutionOptions(
            IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization,
            existingTargetLock,
            CurrentStateProjectionMode.IncludeDescriptorsForDeferredPrecondition,
            postTargetReevaluation
        );
    }

    public ExecutionStateResolutionOptions SuppressPostTargetReevaluation() =>
        this with
        {
            PostTargetReevaluation = PostTargetReevaluationMode.Suppressed,
        };
}
