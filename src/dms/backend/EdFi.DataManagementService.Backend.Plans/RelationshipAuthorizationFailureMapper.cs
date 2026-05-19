// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Maps runtime AUTH1 ordinal payloads back to stable cross-boundary relationship failure metadata.
/// </summary>
public static class RelationshipAuthorizationFailureMapper
{
    public static bool TryMapAuth1Failure(
        RelationshipAuthorizationAuth1FailurePayload payload,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<long> claimEducationOrganizationIds,
        out RelationshipAuthorizationFailure? relationshipFailure
    )
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(checkSpecs);
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIds);

        relationshipFailure = null;

        if (checkSpecs.Count == 0 || HasDuplicateSubjectFailureOrdinals(payload.SubjectFailures))
        {
            return false;
        }

        var valueSource = checkSpecs[0].ValueSource;

        if (checkSpecs.Any(checkSpec => checkSpec.ValueSource != valueSource))
        {
            return false;
        }

        List<RelationshipAuthorizationFailedStrategy> failedStrategies = [];

        for (var strategyOrdinal = 0; strategyOrdinal < checkSpecs.Count; strategyOrdinal++)
        {
            var subjectFailures = payload
                .SubjectFailures.Where(failure => failure.StrategyOrdinal == strategyOrdinal)
                .OrderBy(failure => failure.SubjectOrdinal)
                .ToArray();

            if (subjectFailures.Length == 0)
            {
                continue;
            }

            var checkSpec = checkSpecs[strategyOrdinal];

            if (!TryMapStrategy(checkSpec, subjectFailures, out var failedStrategy))
            {
                return false;
            }

            failedStrategies.Add(failedStrategy);
        }

        if (
            failedStrategies.Count == 0
            || payload.SubjectFailures.Any(failure => failure.StrategyOrdinal >= checkSpecs.Count)
        )
        {
            return false;
        }

        relationshipFailure = new RelationshipAuthorizationFailure(
            MapValueSource(valueSource),
            payload.EmittedAuth1Index,
            [.. failedStrategies],
            [
                .. claimEducationOrganizationIds
                    .Distinct()
                    .Order()
                    .Select(static id => new EducationOrganizationId(id)),
            ]
        );
        return true;
    }

    private static bool TryMapStrategy(
        RelationshipAuthorizationCheckSpec checkSpec,
        IReadOnlyList<RelationshipAuthorizationAuth1SubjectFailure> subjectFailures,
        out RelationshipAuthorizationFailedStrategy failedStrategy
    )
    {
        failedStrategy = new RelationshipAuthorizationFailedStrategy(
            0,
            0,
            string.Empty,
            string.Empty,
            null,
            []
        );

        List<RelationshipAuthorizationFailedSubject> failedSubjects = [];

        foreach (var subjectFailure in subjectFailures)
        {
            if (
                subjectFailure.SubjectOrdinal >= checkSpec.Subjects.Count
                || !IsFailureKindCompatibleWithValueSource(subjectFailure.FailureKind, checkSpec.ValueSource)
            )
            {
                return false;
            }

            failedSubjects.Add(
                MapSubject(
                    subjectFailure.SubjectOrdinal,
                    subjectFailure.FailureKind,
                    checkSpec.Subjects[subjectFailure.SubjectOrdinal]
                )
            );
        }

        failedStrategy = new RelationshipAuthorizationFailedStrategy(
            checkSpec.ConfiguredStrategy.RawConfiguredIndex,
            checkSpec.RelationshipLocalOrder,
            checkSpec.ConfiguredStrategy.StrategyName,
            MapStrategyKind(checkSpec.Direction),
            MapAuthObject(checkSpec.AuthObject),
            [.. failedSubjects]
        );
        return true;
    }

    private static RelationshipAuthorizationFailedSubject MapSubject(
        int subjectIndex,
        RelationshipAuthorizationAuth1SubjectFailureKind failureKind,
        RelationshipAuthorizationSubject subject
    ) =>
        new(
            subjectIndex,
            MapSubjectFailureKind(failureKind),
            new RelationshipAuthorizationRootBinding(
                $"{subject.Resource.ProjectName}.{subject.Resource.ResourceName}",
                subject.Table.ToString(),
                subject.Column.Value
            ),
            [
                .. subject.Contributors.Select(
                    static contributor => new RelationshipAuthorizationSecurableElement(
                        contributor.Kind.ToString(),
                        contributor.JsonPath,
                        contributor.ReadableName
                    )
                ),
            ],
            BuildSubjectHint(failureKind)
        );

    private static bool HasDuplicateSubjectFailureOrdinals(
        IReadOnlyList<RelationshipAuthorizationAuth1SubjectFailure> subjectFailures
    )
    {
        HashSet<(int StrategyOrdinal, int SubjectOrdinal)> seenOrdinals = [];

        return subjectFailures.Any(failure =>
            !seenOrdinals.Add((failure.StrategyOrdinal, failure.SubjectOrdinal))
        );
    }

    private static bool IsFailureKindCompatibleWithValueSource(
        RelationshipAuthorizationAuth1SubjectFailureKind failureKind,
        RelationshipAuthorizationValueSource valueSource
    ) =>
        (failureKind, valueSource) switch
        {
            (RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship, _) => true,
            (
                RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull,
                RelationshipAuthorizationValueSource.Stored
            ) => true,
            (
                RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing,
                RelationshipAuthorizationValueSource.Proposed
            ) => true,
            _ => false,
        };

    private static RelationshipAuthorizationFailureValueSource MapValueSource(
        RelationshipAuthorizationValueSource valueSource
    ) =>
        valueSource switch
        {
            RelationshipAuthorizationValueSource.Stored => RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationValueSource.Proposed =>
                RelationshipAuthorizationFailureValueSource.Proposed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(valueSource),
                valueSource,
                "Unsupported relationship authorization value source."
            ),
        };

    private static RelationshipAuthorizationSubjectFailureKind MapSubjectFailureKind(
        RelationshipAuthorizationAuth1SubjectFailureKind failureKind
    ) =>
        failureKind switch
        {
            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship =>
                RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull =>
                RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
            RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing =>
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 relationship failure kind."
            ),
        };

    private static string MapStrategyKind(RelationshipAuthorizationHierarchyDirection direction) =>
        direction switch
        {
            RelationshipAuthorizationHierarchyDirection.Normal =>
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            RelationshipAuthorizationHierarchyDirection.Inverted =>
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unsupported relationship authorization direction."
            ),
        };

    private static RelationshipAuthorizationAuthObjectInfo MapAuthObject(
        RelationshipAuthorizationAuthObject authObject
    ) =>
        new(
            authObject.Name.ToString(),
            authObject.SubjectValueColumn.Value,
            authObject.ClaimEducationOrganizationIdColumn.Value
        );

    private static string BuildSubjectHint(RelationshipAuthorizationAuth1SubjectFailureKind failureKind) =>
        failureKind switch
        {
            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship =>
                "No matching relationship authorization row was found for the subject value and claim EducationOrganizationIds.",
            RelationshipAuthorizationAuth1SubjectFailureKind.StoredValueNull =>
                "Stored relationship authorization subject value is null.",
            RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing =>
                "Proposed relationship authorization subject value is missing.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 relationship failure kind."
            ),
        };
}
