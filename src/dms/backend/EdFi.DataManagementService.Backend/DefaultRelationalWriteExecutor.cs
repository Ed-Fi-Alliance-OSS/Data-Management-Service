// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace EdFi.DataManagementService.Backend;

internal sealed class DefaultRelationalWriteExecutor(
    IRelationalWriteSessionFactory writeSessionFactory,
    IReferenceResolverAdapterFactory referenceResolverAdapterFactory,
    IRelationalWriteFlattener writeFlattener,
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalCommittedRepresentationReader committedRepresentationReader,
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalWriteFreshnessChecker writeFreshnessChecker,
    IRelationalWriteNoProfileMergeSynthesizer noProfileMergeSynthesizer,
    IRelationalWriteProfileMergeSynthesizer profileMergeSynthesizer,
    IRelationalWritePersister persister,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalWriteConstraintResolver writeConstraintResolver,
    IRelationalReadMaterializer readMaterializer
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

    private readonly IRelationalCommittedRepresentationReader _committedRepresentationReader =
        committedRepresentationReader
        ?? throw new ArgumentNullException(nameof(committedRepresentationReader));

    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));

    private readonly IRelationalWriteFreshnessChecker _writeFreshnessChecker =
        writeFreshnessChecker ?? throw new ArgumentNullException(nameof(writeFreshnessChecker));

    private readonly IRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer =
        noProfileMergeSynthesizer ?? throw new ArgumentNullException(nameof(noProfileMergeSynthesizer));

    private readonly IRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer =
        profileMergeSynthesizer ?? throw new ArgumentNullException(nameof(profileMergeSynthesizer));

    private readonly IRelationalWritePersister _persister =
        persister ?? throw new ArgumentNullException(nameof(persister));

    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));

    private readonly IRelationalWriteConstraintResolver _writeConstraintResolver =
        writeConstraintResolver ?? throw new ArgumentNullException(nameof(writeConstraintResolver));

    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));

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
        JsonNode? cachedCommittedDoc = null;

        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var targetContext = request.TargetContext;

            // If-Match pre-check uses the incoming (pre-session) target context. For PUT and
            // POST-as-update the target is already ExistingDocument before the executor is
            // called, so the DocumentId is available here. Performing the check before
            // referenceResolver.ResolveAsync eliminates unnecessary reference-resolution
            // queries on every rejected stale-ETag request.
            if (
                request.IfMatchEtag is not null
                && targetContext is RelationalWriteTargetContext.ExistingDocument ifMatchTarget
            )
            {
                var (etagMismatch, ifMatchCommittedDoc) = await CheckIfMatchEtagAsync(
                        request,
                        ifMatchTarget,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (etagMismatch is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return etagMismatch;
                }

                // Cache the committed representation already read during the pre-check so the
                // guarded no-op path can reuse it without issuing a second database round-trip.
                cachedCommittedDoc = ifMatchCommittedDoc;
            }

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

            // When a POST's in-session resolution discovers the document already exists
            // (target flipped CreateNew → ExistingDocument), enforce If-Match before any
            // write work proceeds. The early pre-check was skipped because the incoming
            // target context was CreateNew and no DocumentId was yet available.
            if (
                request.IfMatchEtag is not null
                && request.TargetContext is RelationalWriteTargetContext.CreateNew
                && targetContext is RelationalWriteTargetContext.ExistingDocument postFlipTarget
            )
            {
                var (postFlipMismatch, postFlipCommittedDoc) = await CheckIfMatchEtagAsync(
                        request,
                        postFlipTarget,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (postFlipMismatch is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return postFlipMismatch;
                }

                // Cache the committed representation already read during the post-flip check
                // so the guarded no-op path can reuse it without a second database round-trip.
                cachedCommittedDoc = postFlipCommittedDoc;
            }

            RelationalWriteMergeResult mergeResult;

            // Profile decision sequence - runs before flattening for profile-aware dispatch
            if (request.ProfileWriteContext is not null)
            {
                var profileWriteContext = request.ProfileWriteContext;

                // Step 1: Root creatability rejection (create-new only)
                if (
                    targetContext is RelationalWriteTargetContext.CreateNew
                    && !profileWriteContext.Request.RootResourceCreatable
                )
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildProfileCreatabilityRejectionResult(
                        request.OperationKind,
                        profileWriteContext.ProfileName
                    );
                }

                // Step 2: Stored-state projection (existing-document only)
                ProfileAppliedWriteContext? profileAppliedWriteContext = null;
                if (
                    targetContext is RelationalWriteTargetContext.ExistingDocument
                    && currentState is not null
                )
                {
                    var reconstitutedDocument = _readMaterializer.Materialize(
                        new RelationalReadMaterializationRequest(
                            request.ExistingDocumentReadPlan!,
                            currentState.DocumentMetadata,
                            currentState.TableRowsInDependencyOrder,
                            currentState.DescriptorRowsInPlanOrder,
                            RelationalGetRequestReadMode.StoredDocument
                        )
                    );

                    profileAppliedWriteContext =
                        profileWriteContext.StoredStateProjectionInvoker.ProjectStoredState(
                            reconstitutedDocument,
                            profileWriteContext.Request,
                            profileWriteContext.CompiledScopeCatalog
                        );
                }

                // After projection, the projected request/context pair is authoritative for the
                // remainder of the profile-aware executor path.
                var effectiveProfileRequest =
                    profileAppliedWriteContext?.Request ?? profileWriteContext.Request;
                var profileWritableBody = effectiveProfileRequest.WritableRequestBody;

                // Step 3: Contract validation
                var httpMethod = request.OperationKind == RelationalWriteOperationKind.Post ? "POST" : "PUT";
                var contractValidationFailures = profileAppliedWriteContext is not null
                    ? ProfileWriteContractValidator.ValidateWriteContext(
                        profileAppliedWriteContext,
                        profileWriteContext.CompiledScopeCatalog,
                        profileWriteContext.ProfileName,
                        request.WritePlan.Model.Resource.ResourceName,
                        httpMethod,
                        "write"
                    )
                    : ProfileWriteContractValidator.ValidateRequestContract(
                        profileWriteContext.Request,
                        profileWriteContext.CompiledScopeCatalog,
                        profileWriteContext.ProfileName,
                        request.WritePlan.Model.Resource.ResourceName,
                        httpMethod,
                        "write"
                    );

                if (contractValidationFailures.Length > 0)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildProfileContractMismatchResult(
                        request.OperationKind,
                        contractValidationFailures
                    );
                }

                // Step 4: Slice-fence classification
                // CompiledScopeCatalog includes any inlined (non-table-backed) scopes the profile
                // middleware discovered via ContentTypeScopeDiscovery. Extract them by difference
                // so the topology index inherits the correct ancestor topology for each one.
                var tableBackedScopes = new HashSet<string>(
                    request.WritePlan.TablePlansInDependencyOrder.Select(tp =>
                        tp.TableModel.JsonScope.Canonical
                    ),
                    StringComparer.Ordinal
                );
                var inlinedScopes = profileWriteContext
                    .CompiledScopeCatalog.Where(d => !tableBackedScopes.Contains(d.JsonScope))
                    .Select(d => (d.JsonScope, d.ScopeKind))
                    .ToArray();
                var topologyIndex = ScopeTopologyIndex.BuildFromWritePlan(request.WritePlan, inlinedScopes);
                var requiredFamily = profileAppliedWriteContext is not null
                    ? ProfileSliceFenceClassifier.ClassifyForExistingDocument(
                        profileAppliedWriteContext,
                        topologyIndex
                    )
                    : ProfileSliceFenceClassifier.ClassifyForCreateNew(
                        effectiveProfileRequest,
                        topologyIndex
                    );

                // Slice 3 widens the fence for root-attached separate-table scopes
                // (e.g. $._ext.sample on School). Collection-aligned separate-table scopes
                // (DbTableKind.CollectionExtensionScope) remain fenced for slice 5 because
                // ScopeTopologyIndex classifies both root-attached and collection-aligned
                // separate-table scopes under the same SeparateTableNonCollection family.
                // The fence inspects the scopes actually exercised by the current profiled
                // request — not the whole write plan — so mixed plans that carry an unused
                // collection-aligned table alongside a supported root-attached scope are
                // allowed through as long as the request itself touches only supported
                // scopes. This preserves the Task 5 "mixed plan, unused collection scope"
                // contract without splitting the topology / family enums.
                // Slice 4 widens the fence further: TopLevelCollection requests now pass the
                // fence via TopLevelCollectionFenceGate, unless the request also exercises a
                // collection-aligned separate-table scope (Slice 3's guard is preserved for
                // that edge case).
                bool fencePassed = requiredFamily switch
                {
                    RequiredSliceFamily.RootTableOnly => true,
                    RequiredSliceFamily.SeparateTableNonCollection =>
                        !RequestExercisesCollectionAlignedSeparateTableScope(
                            request.WritePlan,
                            effectiveProfileRequest,
                            profileAppliedWriteContext
                        ),
                    RequiredSliceFamily.TopLevelCollection => TopLevelCollectionFenceGate(
                        request.WritePlan,
                        topologyIndex,
                        effectiveProfileRequest,
                        profileAppliedWriteContext
                    ),
                    RequiredSliceFamily.NestedAndExtensionCollections => false,
                    _ => throw new InvalidOperationException(
                        $"Unhandled RequiredSliceFamily '{requiredFamily}' at the slice-3 fence gate. "
                            + "A new family was added without updating the executor's fence switch."
                    ),
                };

                if (!fencePassed)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildSliceFenceResult(request.OperationKind, requiredFamily);
                }

                // Slice-2 classification passed: flatten + profile synthesize. The profile merge
                // synthesizer itself is root-only, but the compiled write plan may still carry
                // multiple tables — those remain valid Slice 2 inputs as long as the flattened
                // handoff is root-only. Non-root flattened buffers on the root row (root-extension
                // rows, collection candidates) must fail closed; the synthesizer's input contract
                // enforces that invariant so upstream fencing bugs surface here rather than
                // silently producing a partial merge result.
                var profileFlattenedWriteSet = _writeFlattener.Flatten(
                    new FlatteningInput(
                        executionRequest.OperationKind,
                        executionRequest.TargetContext,
                        executionRequest.WritePlan,
                        profileWritableBody,
                        resolvedReferences,
                        // Profile Slice 3 decision matrix: separate-table outcome comes from
                        // scope metadata (RequestScopeState/StoredScopeState), not inferred
                        // buffer presence. The shaper may emit a visible-present scope with
                        // no bound scalar data (e.g. _ext: { sample: {} } when all members
                        // are absent under the profile); the synthesizer must still see a
                        // buffer so Insert/Update overlay can run. See
                        // reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor/03-separate-table-profile-merge.md:60.
                        emitEmptyRootExtensionBuffers: true
                    )
                );

                var profileMergeOutcome = _profileMergeSynthesizer.Synthesize(
                    new RelationalWriteProfileMergeRequest(
                        executionRequest.WritePlan,
                        profileFlattenedWriteSet,
                        profileWritableBody,
                        currentState,
                        effectiveProfileRequest,
                        profileAppliedWriteContext,
                        resolvedReferences
                    )
                );

                if (profileMergeOutcome.IsRejection)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildProfileCreatabilityRejectionResult(
                        request.OperationKind,
                        profileWriteContext.ProfileName
                    );
                }

                mergeResult = profileMergeOutcome.MergeResult!;
            }
            else
            {
                var flattenedWriteSet = _writeFlattener.Flatten(
                    new FlatteningInput(
                        executionRequest.OperationKind,
                        executionRequest.TargetContext,
                        executionRequest.WritePlan,
                        executionRequest.SelectedBody,
                        resolvedReferences
                    )
                );

                mergeResult = _noProfileMergeSynthesizer.Synthesize(
                    new RelationalWriteNoProfileMergeRequest(
                        request.WritePlan,
                        flattenedWriteSet,
                        currentState
                    )
                );
            }

            var identityStabilityFailure = RelationalWriteIdentityStability.TryBuildFailureResult(
                executionRequest,
                mergeResult
            );

            if (identityStabilityFailure is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return identityStabilityFailure;
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
                    return BuildStaleNoOpCompareResult(request.OperationKind);
                }

                // Reuse the representation already fetched by the If-Match pre-check when
                // available; fall back to a fresh read when no pre-check was performed.
                var guardedNoOpCommittedResponse =
                    cachedCommittedDoc
                    ?? await _committedRepresentationReader
                        .ReadAsync(
                            executionRequest,
                            new RelationalWritePersistResult(
                                guardedTarget.DocumentId,
                                guardedTarget.DocumentUuid
                            ),
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BuildGuardedNoOpSuccessResult(
                    request.OperationKind,
                    guardedTarget.DocumentUuid,
                    guardedNoOpCommittedResponse
                );
            }

            var persistedTarget = await _persister
                .PersistAsync(executionRequest, mergeResult, writeSession, cancellationToken)
                .ConfigureAwait(false);

            ValidatePersistedTargetIdentity(executionRequest.TargetContext, persistedTarget);

            var committedResponse = await _committedRepresentationReader
                .ReadAsync(executionRequest, persistedTarget, writeSession, cancellationToken)
                .ConfigureAwait(false);

            await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BuildAppliedWriteSuccessResult(
                request.OperationKind,
                executionRequest.TargetContext,
                persistedTarget,
                committedResponse
            );
        }
        catch (RelationalWriteRequestValidationException ex)
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return BuildValidationFailureResult(request.OperationKind, ex.ValidationFailures);
        }
        catch (DbException ex)
        {
            bool isMappedWriteFailure;

            try
            {
                isMappedWriteFailure = TryBuildWriteFailureResult(
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

    private static RelationalWriteExecutorResult BuildETagMismatchResult(
        RelationalWriteOperationKind operationKind
    ) =>
        operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureETagMisMatch()
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureETagMisMatch()
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

    private async Task<(
        RelationalWriteExecutorResult? Mismatch,
        JsonNode? CommittedDoc
    )> CheckIfMatchEtagAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument existingTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        // RFC 7232 §3.1 wildcard: If-Match: * matches any existing representation.
        // Bypass ETag comparison and row-lock acquisition; proceed as if no If-Match
        // header was supplied. CommittedDoc is returned null so the caller's guarded
        // no-op path continues to read the committed representation normally.
        if (string.Equals(request.IfMatchEtag, "*", StringComparison.Ordinal))
        {
            return (null, null);
        }

        // Acquire a row-level lock on dms.Document before reading the committed
        // representation. The lock is held for the lifetime of this transaction,
        // preventing a concurrent writer from modifying the row between the ETag
        // comparison and the subsequent write DML, eliminating the TOCTOU race on
        // the If-Match changed-write path.
        // PostgreSQL uses FOR UPDATE; MSSQL uses UPDLOCK/HOLDLOCK/ROWLOCK.
        var rowFound = await AcquireDocumentRowLockAsync(
                request.MappingSet.Key.Dialect,
                existingTarget.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        // The document was resolved as existing before the executor session opened, but the
        // row is gone now (concurrent DELETE or rollback). Short-circuit without attempting
        // committed-representation rehydration — that read would find no row and would throw
        // InvalidOperationException from GetCommittedResponseEtag. Returning the ETag mismatch
        // result is the correct semantic: the client's precondition no longer holds.
        if (!rowFound)
        {
            return (BuildETagMismatchResult(request.OperationKind), null);
        }

        var committedRepresentation = await _committedRepresentationReader
            .ReadAsync(
                request,
                new RelationalWritePersistResult(existingTarget.DocumentId, existingTarget.DocumentUuid),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        var currentEtag = GetCommittedResponseEtag(committedRepresentation);

        return string.Equals(currentEtag, request.IfMatchEtag, StringComparison.Ordinal)
            ? (null, committedRepresentation)
            : (BuildETagMismatchResult(request.OperationKind), null);
    }

    private static async Task<bool> AcquireDocumentRowLockAsync(
        SqlDialect dialect,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        await using var command = writeSession.CreateCommand(
            DocumentRowLock.BuildCommand(dialect, documentId)
        );
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // null / DBNull means the WHERE clause matched no row — the document was deleted
        // (or never committed) between target resolution and lock acquisition.
        return scalar is not null and not DBNull;
    }

    private static RelationalWriteExecutorResult BuildGuardedNoOpSuccessResult(
        RelationalWriteOperationKind operationKind,
        DocumentUuid documentUuid,
        JsonNode committedResponse
    )
    {
        var etag = GetCommittedResponseEtag(committedResponse);

        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpdateSuccess(documentUuid, etag),
                RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid, etag),
                RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static RelationalWriteExecutorResult BuildAppliedWriteSuccessResult(
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetContext targetContext,
        RelationalWritePersistResult persistedTarget,
        JsonNode committedResponse
    )
    {
        var etag = GetCommittedResponseEtag(committedResponse);
        var documentUuid = persistedTarget.DocumentUuid;

        return (operationKind, targetContext) switch
        {
            (RelationalWriteOperationKind.Post, RelationalWriteTargetContext.CreateNew) =>
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(documentUuid, etag),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            (RelationalWriteOperationKind.Post, RelationalWriteTargetContext.ExistingDocument) =>
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpdateSuccess(documentUuid, etag),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            (RelationalWriteOperationKind.Put, RelationalWriteTargetContext.ExistingDocument) =>
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(documentUuid, etag),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };
    }

    private static void ValidatePersistedTargetIdentity(
        RelationalWriteTargetContext targetContext,
        RelationalWritePersistResult persistedTarget
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(persistedTarget);

        if (persistedTarget.DocumentId <= 0)
        {
            throw new InvalidOperationException(
                "Relational write persistence completed without returning a valid committed DocumentId."
            );
        }

        switch (targetContext)
        {
            case RelationalWriteTargetContext.CreateNew(var documentUuid)
                when persistedTarget.DocumentUuid != documentUuid:
                throw new InvalidOperationException(
                    $"Relational write create completed for document uuid '{documentUuid.Value}', "
                        + $"but persistence returned committed uuid '{persistedTarget.DocumentUuid.Value}'."
                );

            case RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _)
                when persistedTarget.DocumentId != documentId || persistedTarget.DocumentUuid != documentUuid:
                throw new InvalidOperationException(
                    $"Relational write targeted existing document id {documentId} / uuid '{documentUuid.Value}', "
                        + "but persistence returned a different committed target identity."
                );
        }
    }

    private static string GetCommittedResponseEtag(
        JsonNode committedResponse,
        [CallerMemberName] string callerName = ""
    )
    {
        ArgumentNullException.ThrowIfNull(committedResponse);

        if (
            committedResponse is not JsonObject documentObject
            || documentObject["_etag"] is not JsonValue etagValue
            || !etagValue.TryGetValue(out string? etag)
            || string.IsNullOrWhiteSpace(etag)
        )
        {
            throw new InvalidOperationException(
                $"Committed relational write readback did not produce an external response _etag. Caller: {callerName}"
            );
        }

        return etag;
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

    private static RelationalWriteExecutorResult BuildProfileCreatabilityRejectionResult(
        RelationalWriteOperationKind operationKind,
        string profileName
    ) =>
        operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureProfileDataPolicy(profileName)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureProfileDataPolicy(profileName)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

    private static RelationalWriteExecutorResult BuildProfileContractMismatchResult(
        RelationalWriteOperationKind operationKind,
        ProfileFailure[] failures
    )
    {
        var message = $"Profile write contract mismatch: {failures[0].Message}";
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(message)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(message)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static RelationalWriteExecutorResult BuildSliceFenceResult(
        RelationalWriteOperationKind operationKind,
        RequiredSliceFamily requiredFamily
    )
    {
        var message = $"Profile-aware persist for {requiredFamily} shapes is not yet supported (DMS-1124).";
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(message)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(message)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    /// <summary>
    /// Gate for the Slice 4 TopLevelCollection family. Returns <c>true</c> (pass fence) when the
    /// request exercises only topology already supported by slices 1-4. Root/root-extension
    /// inlined scopes are allowed through by their inherited topology; collection-descendant
    /// inlined scopes and collection-aligned separate-table scopes remain fenced until a later
    /// slice defines their merge contract.
    /// </summary>
    private static bool TopLevelCollectionFenceGate(
        ResourceWritePlan writePlan,
        ScopeTopologyIndex topologyIndex,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    ) =>
        !RequestExercisesUnsupportedTopLevelCollectionScope(
            writePlan,
            topologyIndex,
            profileRequest,
            profileAppliedContext
        )
        && !RequestExercisesCollectionAlignedSeparateTableScope(
            writePlan,
            profileRequest,
            profileAppliedContext
        );

    /// <summary>
    /// Returns <c>true</c> when the current profiled request exercises a
    /// <see cref="ScopeTopologyKind.TopLevelBaseCollection"/> scope that is not the backing
    /// table's collection row scope itself. This preserves earlier-slice root/root-extension
    /// inlined support while keeping inlined top-level collections and collection-descendant
    /// inlined non-collection scopes fenced.
    /// </summary>
    private static bool RequestExercisesUnsupportedTopLevelCollectionScope(
        ResourceWritePlan writePlan,
        ScopeTopologyIndex topologyIndex,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        foreach (var scopeState in profileRequest.RequestScopeStates)
        {
            if (scopeState.Visibility == ProfileVisibilityKind.Hidden)
            {
                continue;
            }

            if (IsUnsupportedTopLevelCollectionScope(scopeState.Address.JsonScope, writePlan, topologyIndex))
            {
                return true;
            }
        }

        if (
            profileRequest.VisibleRequestCollectionItems.Any(item =>
                IsUnsupportedTopLevelCollectionScope(item.Address.JsonScope, writePlan, topologyIndex)
            )
        )
        {
            return true;
        }

        if (profileAppliedContext is null)
        {
            return false;
        }

        if (
            profileAppliedContext.StoredScopeStates.Any(state =>
                IsUnsupportedTopLevelCollectionScope(state.Address.JsonScope, writePlan, topologyIndex)
            )
        )
        {
            return true;
        }

        return profileAppliedContext.VisibleStoredCollectionRows.Any(row =>
            IsUnsupportedTopLevelCollectionScope(row.Address.JsonScope, writePlan, topologyIndex)
        );
    }

    private static bool IsUnsupportedTopLevelCollectionScope(
        string scopeAddress,
        ResourceWritePlan writePlan,
        ScopeTopologyIndex topologyIndex
    )
    {
        if (topologyIndex.GetTopology(scopeAddress) != ScopeTopologyKind.TopLevelBaseCollection)
        {
            return false;
        }

        var owner = ProfileBindingClassificationCore.ResolveOwnerTablePlan(scopeAddress, writePlan);
        return owner is null
            || owner.TableModel.IdentityMetadata.TableKind is not DbTableKind.Collection
            || !string.Equals(owner.TableModel.JsonScope.Canonical, scopeAddress, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> when the current profiled request exercises at least one scope
    /// whose owner table is a <see cref="DbTableKind.CollectionExtensionScope"/> — a
    /// separate-table non-collection scope aligned to a collection row. Slice 3 keeps these
    /// fenced; slice 5 will lift the fence. Inspecting the exercised scopes (request/stored
    /// scope states + visible collection items/rows) rather than the whole write plan keeps
    /// mixed plans that carry an unused collection-aligned table alongside a supported
    /// root-attached scope passable — the Task 5 synthesizer contract already permits those
    /// mixed plans so long as the current request does not touch the collection-aligned
    /// scope. Ownership is resolved via
    /// <see cref="ProfileBindingClassificationCore.ResolveOwnerTablePlan"/> so the fence
    /// uses identical prefix semantics to per-table binding classification.
    /// </summary>
    private static bool RequestExercisesCollectionAlignedSeparateTableScope(
        ResourceWritePlan writePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        foreach (var scopeState in profileRequest.RequestScopeStates)
        {
            if (scopeState.Visibility == ProfileVisibilityKind.Hidden)
            {
                // Mirrors ProfileSliceFenceClassifier.ClassifyForCreateNew: hidden
                // request-side scopes are preserve-only and do not escalate slice family,
                // so they must not count as "exercised" for the collection-aligned fence.
                // Stored-side hidden scopes still participate below (the classifier's
                // existing rule: hidden stored scopes still require the owning slice to
                // preserve them correctly).
                continue;
            }
            if (IsCollectionAlignedOwner(scopeState.Address.JsonScope, writePlan))
            {
                return true;
            }
        }
        if (
            profileRequest.VisibleRequestCollectionItems.Any(item =>
                IsCollectionAlignedOwner(item.Address.JsonScope, writePlan)
            )
        )
        {
            return true;
        }
        if (profileAppliedContext is null)
        {
            return false;
        }
        if (
            profileAppliedContext.StoredScopeStates.Any(state =>
                IsCollectionAlignedOwner(state.Address.JsonScope, writePlan)
            )
        )
        {
            return true;
        }
        return profileAppliedContext.VisibleStoredCollectionRows.Any(row =>
            IsCollectionAlignedOwner(row.Address.JsonScope, writePlan)
        );
    }

    private static bool IsCollectionAlignedOwner(string scopeAddress, ResourceWritePlan writePlan)
    {
        var owner = ProfileBindingClassificationCore.ResolveOwnerTablePlan(scopeAddress, writePlan);
        return owner?.TableModel.IdentityMetadata.TableKind is DbTableKind.CollectionExtensionScope;
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
                new RelationalWriteCurrentStateLoadRequest(
                    request.ExistingDocumentReadPlan!,
                    targetContext,
                    includeDescriptorProjection: request.ProfileWriteContext is not null
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
                    existingTargetContext,
                    includeDescriptorProjection: request.ProfileWriteContext is not null
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

    private bool TryBuildWriteFailureResult(
        RelationalWriteExecutorRequest request,
        DbException exception,
        out RelationalWriteExecutorResult? result
    )
    {
        result = null;

        if (!_writeExceptionClassifier.TryClassify(exception, out var classification))
        {
            return false;
        }

        result = classification switch
        {
            RelationalWriteExceptionClassification.ConstraintViolation violation =>
                BuildConstraintViolationFailureResult(request, violation),
            RelationalWriteExceptionClassification.UnrecognizedWriteFailure => BuildUnknownFailureResult(
                request.OperationKind,
                BuildUnrecognizedDatabaseWriteFailureMessage(request.WritePlan.Model.Resource)
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational write exception classification '{classification.GetType().Name}'."
            ),
        };

        return true;
    }

    private RelationalWriteExecutorResult BuildConstraintViolationFailureResult(
        RelationalWriteExecutorRequest request,
        RelationalWriteExceptionClassification.ConstraintViolation violation
    )
    {
        var resolution = _writeConstraintResolver.Resolve(
            new RelationalWriteConstraintResolutionRequest(
                request.WritePlan,
                request.ReferenceResolutionRequest,
                violation
            )
        );

        return resolution switch
        {
            RelationalWriteConstraintResolution.RootNaturalKeyUnique => BuildIdentityConflictFailureResult(
                request
            ),
            RelationalWriteConstraintResolution.RequestReference requestReference
                when TryBuildRequestReferenceFailureResult(
                    request.OperationKind,
                    request.ReferenceResolutionRequest,
                    requestReference,
                    out var referenceFailureResult
                ) => referenceFailureResult!,
            RelationalWriteConstraintResolution.RequestReference
            or RelationalWriteConstraintResolution.Unresolved => BuildUnknownFailureResult(
                request.OperationKind,
                BuildUnexpectedConstraintFailureMessage(request.WritePlan.Model.Resource)
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational write constraint resolution '{resolution.GetType().Name}'."
            ),
        };
    }

    private static RelationalWriteExecutorResult BuildIdentityConflictFailureResult(
        RelationalWriteExecutorRequest request
    )
    {
        var duplicateIdentityValues = BuildDuplicateIdentityValues(request);
        var resourceName = new ResourceName(request.WritePlan.Model.Resource.ResourceName);

        return request.OperationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureIdentityConflict(resourceName, duplicateIdentityValues)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureIdentityConflict(resourceName, duplicateIdentityValues)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
        };
    }

    private static bool TryBuildRequestReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ReferenceResolverRequest request,
        RelationalWriteConstraintResolution.RequestReference resolution,
        out RelationalWriteExecutorResult? result
    )
    {
        result = resolution.ReferenceKind switch
        {
            RelationalWriteReferenceKind.Document => TryBuildDocumentReferenceFailureResult(
                operationKind,
                request,
                resolution,
                out var documentReferenceResult
            )
                ? documentReferenceResult
                : null,
            RelationalWriteReferenceKind.Descriptor => TryBuildDescriptorReferenceFailureResult(
                operationKind,
                request,
                resolution,
                out var descriptorReferenceResult
            )
                ? descriptorReferenceResult
                : null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resolution),
                resolution.ReferenceKind,
                "Unsupported relational write reference kind."
            ),
        };

        return result is not null;
    }

    private static bool TryBuildDocumentReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ReferenceResolverRequest request,
        RelationalWriteConstraintResolution.RequestReference resolution,
        out RelationalWriteExecutorResult? result
    )
    {
        var invalidDocumentReferences = request
            .DocumentReferences.Where(reference =>
                MatchesRequestReference(reference.Path, resolution.ReferencePath)
                && RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo)
                    == resolution.TargetResource
            )
            .Select(static reference =>
                DocumentReferenceFailure.From(reference, DocumentReferenceFailureReason.Missing)
            )
            .ToArray();

        result =
            invalidDocumentReferences.Length == 0
                ? null
                : operationKind switch
                {
                    RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureReference(invalidDocumentReferences, [])
                    ),
                    RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureReference(invalidDocumentReferences, [])
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
                };

        return result is not null;
    }

    private static bool TryBuildDescriptorReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ReferenceResolverRequest request,
        RelationalWriteConstraintResolution.RequestReference resolution,
        out RelationalWriteExecutorResult? result
    )
    {
        var invalidDescriptorReferences = request
            .DescriptorReferences.Where(reference =>
                MatchesRequestReference(reference.Path, resolution.ReferencePath)
                && RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo)
                    == resolution.TargetResource
            )
            .Select(DescriptorReferenceFailureClassifier.Missing)
            .ToArray();

        result =
            invalidDescriptorReferences.Length == 0
                ? null
                : operationKind switch
                {
                    RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureReference([], invalidDescriptorReferences)
                    ),
                    RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureReference([], invalidDescriptorReferences)
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
                };

        return result is not null;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildDuplicateIdentityValues(
        RelationalWriteExecutorRequest request
    )
    {
        var referentialIdentityParameters = GetReferentialIdentityParametersOrThrow(request);
        return referentialIdentityParameters
            .IdentityElements.Select(identityElement =>
                TryResolveIdentityValue(
                    request.SelectedBody,
                    identityElement.IdentityJsonPath,
                    out var identityValue
                )
                    ? new KeyValuePair<string, string>?(
                        new KeyValuePair<string, string>(
                            GetIdentityElementName(identityElement.IdentityJsonPath),
                            identityValue
                        )
                    )
                    : null
            )
            .OfType<KeyValuePair<string, string>>()
            .ToArray();
    }

    private static TriggerKindParameters.ReferentialIdentityMaintenance GetReferentialIdentityParametersOrThrow(
        RelationalWriteExecutorRequest request
    ) =>
        RelationalWriteSupport.GetReferentialIdentityParametersOrThrow(
            request.MappingSet,
            request.WritePlan.Model.Resource,
            request.WritePlan.Model.Root.Table
        );

    private static bool TryResolveIdentityValue(
        JsonNode selectedBody,
        string identityJsonPath,
        out string identityValue
    )
    {
        var segments = RelationalJsonPathSupport.GetRestrictedSegments(
            new JsonPathExpression(identityJsonPath, [])
        );
        JsonNode? currentNode = selectedBody;

        foreach (var segment in segments)
        {
            if (segment is not JsonPathSegment.Property property)
            {
                identityValue = string.Empty;
                return false;
            }

            if (currentNode is not JsonObject jsonObject)
            {
                identityValue = string.Empty;
                return false;
            }

            if (!jsonObject.TryGetPropertyValue(property.Name, out currentNode) || currentNode is null)
            {
                identityValue = string.Empty;
                return false;
            }
        }

        if (currentNode is not JsonValue jsonValue)
        {
            identityValue = string.Empty;
            return false;
        }

        identityValue = jsonValue.TryGetValue<string>(out var stringValue)
            ? stringValue
            : currentNode.ToJsonString();

        return true;
    }

    private static string GetIdentityElementName(string identityJsonPath)
    {
        var segments = RelationalJsonPathSupport.GetRestrictedSegments(
            new JsonPathExpression(identityJsonPath, [])
        );

        return segments.LastOrDefault() is JsonPathSegment.Property property
            ? property.Name
            : identityJsonPath;
    }

    private static bool MatchesRequestReference(JsonPath concretePath, JsonPathExpression referencePath)
    {
        return string.Equals(
            RelationalJsonPathSupport.ParseConcretePath(concretePath).WildcardPath,
            referencePath.Canonical,
            StringComparison.Ordinal
        );
    }

    private static RelationalWriteExecutorResult BuildUnknownFailureResult(
        RelationalWriteOperationKind operationKind,
        string failureMessage
    )
    {
        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(failureMessage)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(failureMessage)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static string BuildUnexpectedConstraintFailureMessage(QualifiedResourceName resource) =>
        $"Relational write failed for resource '{RelationalWriteSupport.FormatResource(resource)}' because the database reported a non-user-facing constraint violation.";

    private static string BuildUnrecognizedDatabaseWriteFailureMessage(QualifiedResourceName resource) =>
        $"Relational write failed for resource '{RelationalWriteSupport.FormatResource(resource)}' because the database reported an unrecognized final write failure.";

    private sealed record InSessionTargetResolution(
        RelationalWriteTargetContext? TargetContext,
        RelationalWriteCurrentState? CurrentState,
        RelationalWriteExecutorResult? ImmediateResult
    );
}
