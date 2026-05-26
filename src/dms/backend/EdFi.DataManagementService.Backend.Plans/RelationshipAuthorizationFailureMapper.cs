// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Maps runtime AUTH1 ordinal payloads back to stable cross-boundary relationship failure metadata.
/// </summary>
public static class RelationshipAuthorizationFailureMapper
{
    public static bool TryMapNoClaimsFailure(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> noClaimsFailures,
        IReadOnlyList<long> claimEducationOrganizationIds,
        int emittedAuth1Index,
        out RelationshipAuthorizationFailure? relationshipFailure
    )
    {
        ArgumentNullException.ThrowIfNull(checkSpecs);
        ArgumentNullException.ThrowIfNull(noClaimsFailures);
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIds);

        relationshipFailure = null;

        if (
            checkSpecs.Count == 0
            || noClaimsFailures.Count == 0
            || noClaimsFailures.Any(static failure =>
                failure.FailureKind != RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds
                || failure.ConfiguredStrategy is null
                || failure.RelationshipLocalOrder is null
                || failure.ValueSource is null
            )
        )
        {
            return false;
        }

        var valueSource = checkSpecs[0].ValueSource;

        if (
            checkSpecs.Any(checkSpec => checkSpec.ValueSource != valueSource)
            || noClaimsFailures.Any(failure => failure.ValueSource != valueSource)
        )
        {
            return false;
        }

        var checkSpecsByStrategyIdentity = checkSpecs.ToDictionary(static checkSpec =>
            (checkSpec.ConfiguredStrategy.RawConfiguredIndex, checkSpec.RelationshipLocalOrder)
        );
        var noClaimsFailuresByStrategyIdentity = noClaimsFailures
            .GroupBy(static failure =>
                (failure.ConfiguredStrategy!.RawConfiguredIndex, failure.RelationshipLocalOrder!.Value)
            )
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        List<RelationshipAuthorizationFailedStrategy> failedStrategies = [];

        foreach (
            var failureGroup in noClaimsFailuresByStrategyIdentity
                .OrderBy(static group => group.Key.RawConfiguredIndex)
                .ThenBy(static group => group.Key.Value)
        )
        {
            if (
                !checkSpecsByStrategyIdentity.TryGetValue(failureGroup.Key, out var checkSpec)
                || checkSpec.Subjects.Count == 0
            )
            {
                return false;
            }

            failedStrategies.Add(
                new RelationshipAuthorizationFailedStrategy(
                    checkSpec.ConfiguredStrategy.RawConfiguredIndex,
                    checkSpec.RelationshipLocalOrder,
                    checkSpec.ConfiguredStrategy.StrategyName,
                    MapStrategyKind(checkSpec),
                    MapAuthObject(SelectStrategyAuthObject(checkSpec, failureGroup.Value)),
                    [
                        .. checkSpec.Subjects.Select(
                            (subject, subjectIndex) =>
                            {
                                var subjectFailureMetadata = SelectNoClaimsSubjectFailureMetadata(
                                    subject,
                                    checkSpec.AuthObject,
                                    failureGroup.Value
                                );

                                return MapSubject(
                                    subjectIndex,
                                    RelationshipAuthorizationSubjectFailureKind.NoClaimEducationOrganizationIds,
                                    subject,
                                    subjectFailureMetadata?.Hint
                                        ?? "Relationship authorization requires at least one claim EducationOrganizationId."
                                );
                            }
                        ),
                    ],
                    SelectStrategyHint(failureGroup.Value)
                )
            );
        }

        if (
            failedStrategies.Count == 0
            || noClaimsFailuresByStrategyIdentity.Keys.Any(strategyIdentity =>
                !checkSpecsByStrategyIdentity.ContainsKey(strategyIdentity)
            )
        )
        {
            return false;
        }

        relationshipFailure = new RelationshipAuthorizationFailure(
            MapValueSource(valueSource),
            emittedAuth1Index,
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
        [MaybeNullWhen(false)] out RelationshipAuthorizationFailedStrategy failedStrategy
    )
    {
        failedStrategy = null!;

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
                    MapSubjectFailureKind(subjectFailure.FailureKind),
                    checkSpec.Subjects[subjectFailure.SubjectOrdinal],
                    BuildSubjectHint(subjectFailure.FailureKind)
                )
            );
        }

        failedStrategy = new RelationshipAuthorizationFailedStrategy(
            checkSpec.ConfiguredStrategy.RawConfiguredIndex,
            checkSpec.RelationshipLocalOrder,
            checkSpec.ConfiguredStrategy.StrategyName,
            MapStrategyKind(checkSpec),
            MapAuthObject(checkSpec.AuthObject),
            [.. failedSubjects]
        );
        return true;
    }

    private static RelationshipAuthorizationFailedSubject MapSubject(
        int subjectIndex,
        RelationshipAuthorizationSubjectFailureKind failureKind,
        RelationshipAuthorizationSubject subject,
        string hint
    ) =>
        new(
            subjectIndex,
            failureKind,
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
            hint
        )
        {
            PersonSubject = MapPersonSubject(subject),
        };

    private static RelationshipAuthorizationFailureMetadata? SelectNoClaimsSubjectFailureMetadata(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationAuthObject strategyAuthObject,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failureGroup
    )
    {
        var subjectAuthObject = SelectSubjectAuthObject(subject, strategyAuthObject);

        if (subject.PersonMetadata is { } personMetadata)
        {
            return failureGroup.FirstOrDefault(failure =>
                    failure.PersonMetadata?.PersonKind == personMetadata.PersonKind
                    && failure.PersonMetadata.AuthObject == personMetadata.AuthObject
                ) ?? failureGroup.FirstOrDefault(failure => failure.AuthObject == subjectAuthObject);
        }

        return failureGroup.FirstOrDefault(failure =>
                failure.PersonMetadata is null && failure.AuthObject == subjectAuthObject
            ) ?? failureGroup.FirstOrDefault(failure => failure.PersonMetadata is null);
    }

    private static RelationshipAuthorizationAuthObject SelectStrategyAuthObject(
        RelationshipAuthorizationCheckSpec checkSpec,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failureGroup
    ) => failureGroup.FirstOrDefault()?.AuthObject ?? checkSpec.AuthObject;

    private static RelationshipAuthorizationAuthObject SelectSubjectAuthObject(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationAuthObject strategyAuthObject
    ) => subject.PersonMetadata?.AuthObject ?? strategyAuthObject;

    private static string? SelectStrategyHint(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failureGroup
    ) => failureGroup.Select(static failure => failure.Hint).FirstOrDefault(static hint => hint is not null);

    private static RelationshipAuthorizationPersonSubjectInfo? MapPersonSubject(
        RelationshipAuthorizationSubject subject
    )
    {
        if (subject.PersonMetadata is not { } personMetadata)
        {
            return null;
        }

        return new RelationshipAuthorizationPersonSubjectInfo(
            personMetadata.PersonKind.ToString(),
            MapAuthObject(personMetadata.AuthObject),
            personMetadata.AuthObject.FailureHint
        );
    }

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

    private static string MapStrategyKind(RelationshipAuthorizationCheckSpec checkSpec) =>
        checkSpec.ConfiguredStrategy.StrategyName switch
        {
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
            or AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted => MapEdOrgStrategyKind(
                checkSpec.Direction
            ),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople =>
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted =>
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly =>
                AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly =>
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility =>
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
            _ => MapEdOrgStrategyKind(checkSpec.Direction),
        };

    private static string MapEdOrgStrategyKind(RelationshipAuthorizationHierarchyDirection direction) =>
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
