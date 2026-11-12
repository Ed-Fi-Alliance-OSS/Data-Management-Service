// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal enum EffectivePermissionsDifferenceKind
{
    MissingClaimSet,
    UnexpectedClaimSet,
    MissingClaim,
    UnexpectedClaim,
    MissingAction,
    UnexpectedAction,
    StrategySequence,
}

// Preserve original names and both sides; escaping belongs only at the reporting boundary.
internal sealed record EffectivePermissionsDifference(
    EffectivePermissionsDifferenceKind Kind,
    string ClaimSetName,
    string ResourceClaimName,
    string ActionName,
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Actual
);

internal sealed record EffectivePermissionsComparison(
    IReadOnlyList<EffectivePermissionsDifference> Differences
)
{
    internal bool IsMatch => Differences.Count == 0;
}

/// <summary>
/// Compares complete, validated, dereferenced metadata. Only catalog names ignore case.
/// Strategy positions and multiplicity matter; shared action record equality does not preserve order.
/// This cannot recover order already erased by shared resolver deduplication.
/// </summary>
internal static class EffectivePermissionsComparer
{
    internal static EffectivePermissionsComparison Compare(
        AuthorizationMetadataValidationResult.Success expected,
        AuthorizationMetadataValidationResult.Success actual
    )
    {
        List<EffectivePermissionsDifference> differences = [];
        var actualSets = actual.ClaimSets.ToDictionary(
            set => set.ClaimSetName,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var expectedSet in expected.ClaimSets)
        {
            if (!actualSets.Remove(expectedSet.ClaimSetName, out var actualSet))
            {
                // Report an absent container once, rather than cascading to every descendant.
                differences.Add(
                    new(
                        EffectivePermissionsDifferenceKind.MissingClaimSet,
                        expectedSet.ClaimSetName,
                        "",
                        "",
                        [expectedSet.ClaimSetName],
                        []
                    )
                );
                continue;
            }

            var actualClaims = actualSet.Claims.ToDictionary(claim => claim.Name, StringComparer.Ordinal);
            foreach (var expectedClaim in expectedSet.Claims)
            {
                if (!actualClaims.Remove(expectedClaim.Name, out var actualClaim))
                {
                    differences.Add(
                        new(
                            EffectivePermissionsDifferenceKind.MissingClaim,
                            expectedSet.ClaimSetName,
                            expectedClaim.Name,
                            "",
                            [expectedClaim.Name],
                            []
                        )
                    );
                    continue;
                }

                var actualActions = actualClaim.Actions.ToDictionary(
                    action => action.Name,
                    StringComparer.Ordinal
                );
                foreach (var expectedAction in expectedClaim.Actions)
                {
                    if (!actualActions.Remove(expectedAction.Name, out var actualAction))
                    {
                        differences.Add(
                            new(
                                EffectivePermissionsDifferenceKind.MissingAction,
                                expectedSet.ClaimSetName,
                                expectedClaim.Name,
                                expectedAction.Name,
                                [expectedAction.Name],
                                []
                            )
                        );
                        continue;
                    }

                    string[] expectedStrategies = expectedAction
                        .AuthorizationStrategies.Select(strategy => strategy.Name)
                        .ToArray();
                    string[] actualStrategies = actualAction
                        .AuthorizationStrategies.Select(strategy => strategy.Name)
                        .ToArray();
                    if (!expectedStrategies.SequenceEqual(actualStrategies, StringComparer.Ordinal))
                    {
                        differences.Add(
                            new(
                                EffectivePermissionsDifferenceKind.StrategySequence,
                                expectedSet.ClaimSetName,
                                expectedClaim.Name,
                                expectedAction.Name,
                                expectedStrategies,
                                actualStrategies
                            )
                        );
                    }
                }

                foreach (string actionName in actualActions.Keys)
                {
                    differences.Add(
                        new(
                            EffectivePermissionsDifferenceKind.UnexpectedAction,
                            expectedSet.ClaimSetName,
                            expectedClaim.Name,
                            actionName,
                            [],
                            [actionName]
                        )
                    );
                }
            }

            foreach (string claimName in actualClaims.Keys)
            {
                differences.Add(
                    new(
                        EffectivePermissionsDifferenceKind.UnexpectedClaim,
                        expectedSet.ClaimSetName,
                        claimName,
                        "",
                        [],
                        [claimName]
                    )
                );
            }
        }

        foreach (string setName in actualSets.Keys)
        {
            differences.Add(
                new(EffectivePermissionsDifferenceKind.UnexpectedClaimSet, setName, "", "", [], [setName])
            );
        }

        return new(differences);
    }
}
