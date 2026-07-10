// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed class RelationalWriteExecutionStateResolver(
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalCurrentEtagPreconditionChecker currentEtagPreconditionChecker,
    IServedEtagComposer servedEtagComposer,
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

    private readonly ILogger<RelationalWriteExecutionStateResolver> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// True when the request carries an HTTP conditional write precondition (If-Match or
    /// If-None-Match) whose current existence/etag the write flow must resolve. If-None-Match is a
    /// sibling of If-Match, so every structural "is a precondition present?" gate must admit both;
    /// only the proceed-vs-412 outcome differs, centralized in <see cref="EtagPreconditionEvaluator"/>.
    /// </summary>
    internal static bool HasEtagPrecondition(WritePrecondition precondition) =>
        precondition switch
        {
            WritePrecondition.None => false,
            WritePrecondition.IfMatch => true,
            WritePrecondition.IfNoneMatch => true,
            _ => throw new ArgumentOutOfRangeException(
                nameof(precondition),
                precondition,
                "Unsupported write precondition type."
            ),
        };

    public static EtagPreconditionEvaluation GetEtagPreconditionEvaluation(
        RelationalWriteExecutorRequest request
    ) =>
        HasEtagPrecondition(request.WritePrecondition)
        && (
            request.ProposedRelationshipAuthorization is not null
            || request.StoredNamespaceAuthorization is not null
            || request.ProposedNamespaceAuthorization is not null
        )
            ? EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
            : EtagPreconditionEvaluation.BeforeProposedAuthorization;

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
                || HasEtagPrecondition(request.WritePrecondition)
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
        // FAIL-OPEN HAZARD: this early return must admit BOTH If-Match and If-None-Match. If it kept
        // keying on If-Match only, an If-None-Match write against an existing, authorization-bounded
        // target would take the deferred path, return null here, and proceed WITHOUT the required 412.
        if (!HasEtagPrecondition(request.WritePrecondition))
        {
            return null;
        }

        if (request.TargetContext is RelationalWriteTargetContext.CreateNew)
        {
            // If-Match on an insert fails (no current representation to match). If-None-Match on an
            // insert is the create-only success case, so it proceeds.
            return request.WritePrecondition is WritePrecondition.IfMatch
                ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                    request.OperationKind,
                    ETagPreconditionFailureReason.TargetDoesNotExist
                )
                : null;
        }

        if (request.TargetContext is not RelationalWriteTargetContext.ExistingDocument)
        {
            throw new InvalidOperationException(
                $"Deferred etag precondition does not support target context '{request.TargetContext.GetType().Name}'."
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
                    "Deferred etag precondition for document {DocumentId}: no current representation "
                        + "(operation={OperationKind}); resolving missing-target outcome",
                    missingTarget.DocumentId,
                    request.OperationKind
                );
            }
            return request.OperationKind switch
            {
                RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureWriteConflict()
                ),
                // RFC 9110 §13.1.1 If-Match: * requires the target to exist; a wildcard against a missing PUT
                // target yields the precondition-failed (412) result rather than not-exists (404). An
                // If-None-Match against a now-missing target is the success case, so it falls through to
                // the normal not-exists (404) result.
                RelationalWriteOperationKind.Put => request.WritePrecondition
                    is WritePrecondition.IfMatch { IsWildcard: true }
                    ? RelationalWriteExecutorResults.BuildPreconditionFailureResult(request.OperationKind)
                    : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists()),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
            };
        }

        // If-Match / If-None-Match compare the state-significant projection of the composed etag
        // (ContentVersion, schemaEpoch). format, linkFlag, and profileCode are projected out (profileCode
        // as of the 2026-07-04 ADR amendment), so the values passed for them here are not significant.
        // EtagPreconditionEvaluator short-circuits a wildcard precondition to the existence answer because
        // currentState being non-null means the target row exists.
        var currentEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                request.ProfileWriteContext?.ProfileName,
                LinksEnabled: true,
                currentState.DocumentMetadata.ContentVersion
            )
        );

        var isSatisfied = EtagPreconditionEvaluator.IsSatisfied(
            request.WritePrecondition,
            targetExists: true,
            currentEtag
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var existing = (RelationalWriteTargetContext.ExistingDocument)request.TargetContext;
            _logger.LogDebug(
                "Deferred etag precondition for document {DocumentId}: "
                    + "currentTag={CurrentTag}, satisfied={IsSatisfied}",
                existing.DocumentId,
                currentEtag,
                isSatisfied
            );
        }

        return isSatisfied
            ? null
            : RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                request.OperationKind,
                EtagPreconditionEvaluator.GetFailureReason(request.WritePrecondition)
            );
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
            options.EtagPreconditionEvaluation
                is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
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
            options.EtagPreconditionEvaluation is EtagPreconditionEvaluation.BeforeProposedAuthorization
            && HasEtagPrecondition(request.WritePrecondition)
        )
        {
            var preconditionCheckResult = await _currentEtagPreconditionChecker
                .CheckAsync(
                    new RelationalCurrentEtagPreconditionCheckRequest(
                        request.MappingSet,
                        request.ExistingDocumentReadPlan!,
                        targetContext,
                        request.WritePrecondition,
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
                    preconditionCheckResult.IsSatisfied
                        ? null
                        : RelationalWriteExecutorResults.BuildPreconditionFailureResult(
                            request.OperationKind,
                            EtagPreconditionEvaluator.GetFailureReason(request.WritePrecondition)
                        )
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
                // RFC 9110 §13.1.1 If-Match: * requires the target to exist; a wildcard against a missing PUT
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

internal enum EtagPreconditionEvaluation
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
    EtagPreconditionEvaluation EtagPreconditionEvaluation,
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
            EtagPreconditionEvaluation.BeforeProposedAuthorization,
            ExistingTargetLockMode.NotRequired,
            CurrentStateProjectionMode.Standard,
            postTargetReevaluation
        );

    public static ExecutionStateResolutionOptions DeferredEtagPrecondition(
        StoredRelationshipAuthorizationBoundary storedAuthorizationBoundary,
        PostTargetReevaluationMode postTargetReevaluation
    )
    {
        var existingTargetLock = storedAuthorizationBoundary.ExistingTargetLocked
            ? ExistingTargetLockMode.AlreadyLocked
            : ExistingTargetLockMode.LockBeforeCurrentStateLoad;

        return new ExecutionStateResolutionOptions(
            EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization,
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
