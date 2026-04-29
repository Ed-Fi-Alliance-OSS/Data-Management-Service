// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Profile;

internal readonly record struct SeparateScopeBuffer(
    TableWritePlan TableWritePlan,
    ImmutableArray<FlattenedWriteValue> Values,
    ImmutableArray<CollectionWriteCandidate> CollectionCandidates
)
{
    public static SeparateScopeBuffer From(RootExtensionWriteRowBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return new SeparateScopeBuffer(buffer.TableWritePlan, buffer.Values, buffer.CollectionCandidates);
    }

    public static SeparateScopeBuffer From(CandidateAttachedAlignedScopeData scopeData)
    {
        ArgumentNullException.ThrowIfNull(scopeData);
        return new SeparateScopeBuffer(
            scopeData.TableWritePlan,
            scopeData.Values,
            scopeData.CollectionCandidates
        );
    }
}

internal readonly record struct SeparateScopeSynthesisResult(
    ProfileSeparateTableMergeOutcome? Outcome,
    RelationalWriteMergedTableState? TableState,
    ProfileCreatabilityRejection? Rejection
)
{
    public bool IsSkipped => Outcome is null && TableState is null && Rejection is null;

    public static SeparateScopeSynthesisResult Skipped => new(null, null, null);

    public static SeparateScopeSynthesisResult Table(
        ProfileSeparateTableMergeOutcome outcome,
        RelationalWriteMergedTableState state
    ) => new(outcome, state, null);

    public static SeparateScopeSynthesisResult Reject(ProfileCreatabilityRejection rejection) =>
        new(ProfileSeparateTableMergeOutcome.RejectCreateDenied, null, rejection);
}
