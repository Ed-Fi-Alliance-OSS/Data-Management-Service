// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Corrects the classification of a validated proposed authorization failure using request-local runtime
/// provenance. The AUTH1 SQL reports every null proposed subject as proposed-missing; a subject that is null
/// only because its submitted reference could not be resolved was supplied, so it is reported as
/// no-relationship — the same outcome as an existing but unrelated reference.
/// </summary>
/// <remarks>
/// Only the failure kind (and its diagnostic hint) of marked proposed-missing subjects changes. Strategy and
/// subject identities, ordering, auth-object and person metadata, and every other failure stay as mapped.
/// Neither input is mutated.
/// </remarks>
internal static class ProposedAuthorizationFailureClassification
{
    public static RelationshipAuthorizationFailure CorrectRelationshipFailure(
        RelationshipAuthorizationFailure failure,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck
    )
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(runtimeCheck);

        if (
            failure.ValueSource is not RelationshipAuthorizationFailureValueSource.Proposed
            || failure.EmittedAuth1Index != runtimeCheck.EmittedAuth1Index
        )
        {
            return failure;
        }

        Dictionary<
            (int ConfiguredStrategyIndex, int RelationshipLocalOrder),
            ProposedRelationshipAuthorizationRuntimeStrategy
        > runtimeStrategies = new(runtimeCheck.Strategies.Count);

        foreach (var runtimeStrategy in runtimeCheck.Strategies)
        {
            runtimeStrategies.TryAdd(
                (
                    runtimeStrategy.CheckSpec.ConfiguredStrategy.RawConfiguredIndex,
                    runtimeStrategy.CheckSpec.RelationshipLocalOrder
                ),
                runtimeStrategy
            );
        }

        RelationshipAuthorizationFailedStrategy[]? correctedStrategies = null;

        for (var strategyIndex = 0; strategyIndex < failure.FailedStrategies.Length; strategyIndex++)
        {
            var failedStrategy = failure.FailedStrategies[strategyIndex];

            if (
                !runtimeStrategies.TryGetValue(
                    (failedStrategy.ConfiguredStrategyIndex, failedStrategy.RelationshipLocalOrder),
                    out var runtimeStrategy
                )
            )
            {
                continue;
            }

            var correctedStrategy = CorrectStrategy(failedStrategy, runtimeStrategy);

            if (ReferenceEquals(correctedStrategy, failedStrategy))
            {
                continue;
            }

            correctedStrategies ??= [.. failure.FailedStrategies];
            correctedStrategies[strategyIndex] = correctedStrategy;
        }

        return correctedStrategies is null
            ? failure
            : failure with
            {
                FailedStrategies = correctedStrategies,
            };
    }

    /// <summary>
    /// Reports a proposed-missing custom-view denial as no-matching-row when the check the payload names was
    /// actually emitted with a document basis that is null only because its submitted reference could not be
    /// resolved — the same outcome as a resolved basis outside the view. Planned identity, readable names,
    /// and the configured hint are unchanged; the input is not mutated.
    /// </summary>
    public static CustomViewAuthorizationFailure CorrectCustomViewFailure(
        CustomViewAuthorizationFailure failure,
        IReadOnlyList<ProposedCustomViewRuntimeValue> emittedValues
    )
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(emittedValues);

        if (
            failure.ValueSource is not CustomViewAuthorizationFailureValueSource.Proposed
            || failure.FailureKind is not CustomViewAuthorizationFailureKind.ProposedValueMissing
        )
        {
            return failure;
        }

        foreach (var emittedValue in emittedValues)
        {
            if (emittedValue.Check.Index == failure.EmittedAuth1Index)
            {
                return emittedValue.IsNullBecauseDeferredMissingReference
                    ? failure with
                    {
                        FailureKind = CustomViewAuthorizationFailureKind.NoMatchingRow,
                    }
                    : failure;
            }
        }

        return failure;
    }

    private static RelationshipAuthorizationFailedStrategy CorrectStrategy(
        RelationshipAuthorizationFailedStrategy failedStrategy,
        ProposedRelationshipAuthorizationRuntimeStrategy runtimeStrategy
    )
    {
        RelationshipAuthorizationFailedSubject[]? correctedSubjects = null;

        for (var subjectIndex = 0; subjectIndex < failedStrategy.FailedSubjects.Length; subjectIndex++)
        {
            var failedSubject = failedStrategy.FailedSubjects[subjectIndex];

            if (
                failedSubject.FailureKind
                    is not RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
                || !IsMarked(runtimeStrategy, failedSubject.SubjectIndex)
            )
            {
                continue;
            }

            correctedSubjects ??= [.. failedStrategy.FailedSubjects];
            correctedSubjects[subjectIndex] = failedSubject with
            {
                FailureKind = RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                Hint = RelationshipAuthorizationFailureMapper.BuildSubjectHint(
                    RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                ),
            };
        }

        return correctedSubjects is null
            ? failedStrategy
            : failedStrategy with
            {
                FailedSubjects = correctedSubjects,
            };
    }

    private static bool IsMarked(
        ProposedRelationshipAuthorizationRuntimeStrategy runtimeStrategy,
        int subjectOrdinal
    )
    {
        foreach (var runtimeSubject in runtimeStrategy.Subjects)
        {
            if (runtimeSubject.SubjectOrdinal == subjectOrdinal)
            {
                return runtimeSubject.IsNullBecauseDeferredMissingReference;
            }
        }

        return false;
    }
}
