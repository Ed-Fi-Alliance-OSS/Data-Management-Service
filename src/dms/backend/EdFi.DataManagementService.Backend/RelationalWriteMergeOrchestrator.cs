// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

internal sealed class RelationalWriteMergeOrchestrator(
    IRelationalWriteFlattener writeFlattener,
    IRelationalReadMaterializer readMaterializer,
    IRelationalWriteNoProfileMergeSynthesizer noProfileMergeSynthesizer,
    IRelationalWriteProfileMergeSynthesizer profileMergeSynthesizer
)
{
    private readonly IRelationalWriteFlattener _writeFlattener =
        writeFlattener ?? throw new ArgumentNullException(nameof(writeFlattener));

    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));

    private readonly IRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer =
        noProfileMergeSynthesizer ?? throw new ArgumentNullException(nameof(noProfileMergeSynthesizer));

    private readonly IRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer =
        profileMergeSynthesizer ?? throw new ArgumentNullException(nameof(profileMergeSynthesizer));

    public RelationalWriteMergeBoundary Resolve(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext targetContext,
        RelationalWriteCurrentState? currentState,
        ResolvedReferenceSet resolvedReferences
    )
    {
        if (request.ProfileWriteContext is null)
        {
            return new RelationalWriteMergeBoundary(
                ResolveNoProfileMerge(request, currentState, resolvedReferences),
                null
            );
        }

        return ResolveProfileMerge(request, targetContext, currentState, resolvedReferences);
    }

    private RelationalWriteMergeResult ResolveNoProfileMerge(
        RelationalWriteExecutorRequest request,
        RelationalWriteCurrentState? currentState,
        ResolvedReferenceSet resolvedReferences
    )
    {
        var flattenedWriteSet = _writeFlattener.Flatten(
            new FlatteningInput(
                request.OperationKind,
                request.TargetContext,
                request.WritePlan,
                request.SelectedBody,
                resolvedReferences,
                // The no-profile merge matches collection rows by raw object?[] semantic
                // identity values via ObjectValueArrayComparer, which maps a missing
                // identity property and an explicit JSON null to the same key. The
                // relational storage model collapses them the same way (both persist as
                // SQL NULL), so two request siblings that differ only by
                // absent-vs-explicit-null on an identity slot cannot be persisted
                // distinctly. Enforce that invariant at flatten time so persistence never
                // sees an ambiguous pair. The profile path enforces the equivalent
                // invariant on Core-emitted address streams in ProfileWriteContractValidator.
                validateStorageCollapsedCollectionIdentityUniqueness: true
            )
        );

        return _noProfileMergeSynthesizer.Synthesize(
            new RelationalWriteNoProfileMergeRequest(request.WritePlan, flattenedWriteSet, currentState)
        );
    }

    private RelationalWriteMergeBoundary ResolveProfileMerge(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext targetContext,
        RelationalWriteCurrentState? currentState,
        ResolvedReferenceSet resolvedReferences
    )
    {
        var profileWriteContext = request.ProfileWriteContext!;

        if (
            targetContext is RelationalWriteTargetContext.CreateNew
            && !profileWriteContext.Request.RootResourceCreatable
        )
        {
            return new RelationalWriteMergeBoundary(
                null,
                RelationalWriteExecutorResults.BuildProfileCreatabilityRejectionResult(
                    request.OperationKind,
                    profileWriteContext.ProfileName
                )
            );
        }

        var profileAppliedWriteContext = BuildProfileAppliedWriteContext(
            request,
            targetContext,
            currentState,
            profileWriteContext
        );

        var effectiveProfileRequest = profileAppliedWriteContext?.Request ?? profileWriteContext.Request;
        var profileWritableBody = effectiveProfileRequest.WritableRequestBody;
        var contractValidationFailures = ValidateProfileContract(
            request,
            profileWriteContext,
            profileAppliedWriteContext
        );

        if (contractValidationFailures.Length > 0)
        {
            return new RelationalWriteMergeBoundary(
                null,
                RelationalWriteExecutorResults.BuildProfileContractMismatchResult(
                    request.OperationKind,
                    contractValidationFailures
                )
            );
        }

        var profileFlattenedWriteSet = _writeFlattener.Flatten(
            new FlatteningInput(
                request.OperationKind,
                request.TargetContext,
                request.WritePlan,
                profileWritableBody,
                resolvedReferences,
                // Profile separate-table outcome comes from scope metadata
                // (RequestScopeState/StoredScopeState), not inferred from buffer
                // presence. The synthesizer must still see an empty visible-present
                // extension buffer so Insert/Update overlay can run.
                emitEmptyExtensionBuffers: true
            )
        );

        var profileMergeOutcome = _profileMergeSynthesizer.Synthesize(
            new RelationalWriteProfileMergeRequest(
                request.WritePlan,
                profileFlattenedWriteSet,
                profileWritableBody,
                currentState,
                effectiveProfileRequest,
                profileAppliedWriteContext,
                resolvedReferences
            )
        );

        return profileMergeOutcome.IsRejection
            ? new RelationalWriteMergeBoundary(
                null,
                RelationalWriteExecutorResults.BuildProfileCreatabilityRejectionResult(
                    request.OperationKind,
                    profileWriteContext.ProfileName
                )
            )
            : new RelationalWriteMergeBoundary(profileMergeOutcome.MergeResult!, null);
    }

    private ProfileAppliedWriteContext? BuildProfileAppliedWriteContext(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext targetContext,
        RelationalWriteCurrentState? currentState,
        BackendProfileWriteContext profileWriteContext
    )
    {
        if (targetContext is not RelationalWriteTargetContext.ExistingDocument || currentState is null)
        {
            return null;
        }

        var reconstitutedDocument = _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                request.ExistingDocumentReadPlan!,
                currentState.DocumentMetadata,
                currentState.TableRowsInDependencyOrder,
                currentState.DescriptorRowsInPlanOrder,
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        return profileWriteContext.StoredStateProjectionInvoker.ProjectStoredState(
            reconstitutedDocument,
            profileWriteContext.Request,
            profileWriteContext.CompiledScopeCatalog
        );
    }

    private static ProfileFailure[] ValidateProfileContract(
        RelationalWriteExecutorRequest request,
        BackendProfileWriteContext profileWriteContext,
        ProfileAppliedWriteContext? profileAppliedWriteContext
    )
    {
        var httpMethod = request.OperationKind == RelationalWriteOperationKind.Post ? "POST" : "PUT";

        return profileAppliedWriteContext is not null
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
    }
}

internal sealed record RelationalWriteMergeBoundary(
    RelationalWriteMergeResult? MergeResult,
    RelationalWriteExecutorResult? ImmediateResult
);
