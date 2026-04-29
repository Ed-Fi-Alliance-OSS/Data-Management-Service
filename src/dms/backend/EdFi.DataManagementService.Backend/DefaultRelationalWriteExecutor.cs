// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
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

        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var targetContext = request.TargetContext;
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

            RelationalWriteMergeResult mergeResult;

            // Profile decision sequence — runs before flattening for profile-aware dispatch
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
                // (e.g. $._ext.sample on School). Slice 5 CP3 also allows
                // collection-aligned separate-table scopes; both shapes classify as
                // SeparateTableNonCollection and now flow through the same executor path.
                // Slice 4 widens the fence to TopLevelCollection requests; Slice 5 CP4
                // retires the prior TopLevelCollectionFenceGate (the walker's topology-
                // driven child enumeration now handles inlined top-level collections,
                // collection-descendant inlined non-collection scopes, and the other
                // shapes the predicate fenced). Slice 5 CP4 also opens the slice gate on
                // NestedAndExtensionCollections: nested base collections, root-extension
                // child collections, collection-aligned 1:1 extension scopes, and nested
                // collections under aligned-extension scopes all flow through to the
                // synthesizer.
                bool fencePassed = requiredFamily switch
                {
                    RequiredSliceFamily.RootTableOnly => true,
                    RequiredSliceFamily.SeparateTableNonCollection => true,
                    RequiredSliceFamily.TopLevelCollection => true,
                    RequiredSliceFamily.NestedAndExtensionCollections => true,
                    _ => throw new InvalidOperationException(
                        $"Unhandled RequiredSliceFamily '{requiredFamily}' at the profile fence gate. "
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

                var guardedNoOpCommittedResponse = await _committedRepresentationReader
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

    private static string GetCommittedResponseEtag(JsonNode committedResponse)
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
                "Committed relational write readback did not produce an external response _etag."
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
