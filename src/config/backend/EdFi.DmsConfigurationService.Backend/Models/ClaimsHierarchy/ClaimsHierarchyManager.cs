// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public interface IClaimsHierarchyManager
{
    void RemoveClaimSetFromHierarchy(string claimSetName, List<Claim> claims);

    void CloneClaimSetInHierarchy(string sourceClaimSetName, string targetClaimSetName, List<Claim> claims);

    IReadOnlyList<string> ApplyImportedClaimSetToHierarchy(ClaimSetImportCommand command, List<Claim> claims);
}

public class ClaimsHierarchyManager : IClaimsHierarchyManager
{
    public void RemoveClaimSetFromHierarchy(string claimSetName, List<Claim> claims)
    {
        foreach (var claim in claims)
        {
            claim.ClaimSets.RemoveAll(cs => cs.Name.Equals(claimSetName, StringComparison.OrdinalIgnoreCase));

            if (claim.Claims.Any())
            {
                RemoveClaimSetFromHierarchy(claimSetName, claim.Claims);
            }
        }
    }

    public void CloneClaimSetInHierarchy(
        string sourceClaimSetName,
        string targetClaimSetName,
        List<Claim> claims
    )
    {
        foreach (var claim in claims)
        {
            var source = claim.ClaimSets.Find(cs =>
                cs.Name.Equals(sourceClaimSetName, StringComparison.OrdinalIgnoreCase)
            );

            if (
                source is not null
                && !claim.ClaimSets.Exists(cs =>
                    cs.Name.Equals(targetClaimSetName, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                claim.ClaimSets.Add(
                    new ClaimSet
                    {
                        Name = targetClaimSetName,
                        Actions =
                        [
                            .. source.Actions.Select(action => new ClaimSetAction
                            {
                                Name = action.Name,
                                AuthorizationStrategyOverrides =
                                [
                                    .. action.AuthorizationStrategyOverrides.Select(
                                        strategy => new AuthorizationStrategy { Name = strategy.Name }
                                    ),
                                ],
                            }),
                        ],
                    }
                );
            }

            if (claim.Claims.Any())
            {
                CloneClaimSetInHierarchy(sourceClaimSetName, targetClaimSetName, claim.Claims);
            }
        }
    }

    public IReadOnlyList<string> ApplyImportedClaimSetToHierarchy(
        ClaimSetImportCommand command,
        List<Claim> claims
    )
    {
        if (string.IsNullOrEmpty(command.Name) || command.ResourceClaims is null)
        {
            throw new NullReferenceException(
                "Claim set name or resource claims are null on the import command."
            );
        }

        var skippedClaimNames = new List<string>();
        var claimsByName = BuildClaimLookup(claims);

        foreach (var resourceClaim in Flatten(command.ResourceClaims))
        {
            var claimName = resourceClaim.ClaimName ?? resourceClaim.Name;

            if (string.IsNullOrWhiteSpace(claimName))
            {
                continue;
            }

            if (!claimsByName.TryGetValue(claimName, out var claim))
            {
                skippedClaimNames.Add(claimName);
                continue;
            }

            var claimSet = claim.ClaimSets.Find(cs =>
                cs.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)
            );

            if (claimSet is not null)
            {
                // Record as skipped and continue rather than throwing to allow graceful handling by callers.
                skippedClaimNames.Add(claimName);
                continue;
            }

            claimSet = new ClaimSet { Name = command.Name, Actions = [] };
            claim.ClaimSets.Add(claimSet);

            if (resourceClaim.Actions is not { Count: > 0 })
            {
                continue;
            }

            foreach (
                var actionName in resourceClaim
                    .Actions.Where(action => action.Enabled)
                    .Select(action => action.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
            )
            {
                var newAction = new ClaimSetAction
                {
                    Name = actionName!,
                    AuthorizationStrategyOverrides = [],
                };

                var overrides = GetAuthorizationStrategyOverrides(resourceClaim, actionName!);

                if (overrides.Count > 0)
                {
                    newAction.AuthorizationStrategyOverrides =
                    [
                        .. overrides
                            .SelectMany(overrideAction => overrideAction.AuthorizationStrategies ?? [])
                            .Select(strategy => new AuthorizationStrategy
                            {
                                Name = strategy.AuthorizationStrategyName,
                            }),
                    ];
                }

                claimSet.Actions.Add(newAction);
            }
        }

        return skippedClaimNames;
    }

    private static Dictionary<string, Claim> BuildClaimLookup(IEnumerable<Claim> claims)
    {
        var claimLookup = new Dictionary<string, Claim>(StringComparer.OrdinalIgnoreCase);

        void AddClaims(IEnumerable<Claim> currentClaims)
        {
            foreach (var claim in currentClaims)
            {
                claimLookup[claim.Name] = claim;
                AddClaims(claim.Claims);
            }
        }

        AddClaims(claims);
        return claimLookup;
    }

    private static IEnumerable<ResourceClaim> Flatten(IEnumerable<ResourceClaim> resourceClaims)
    {
        foreach (var resourceClaim in resourceClaims)
        {
            yield return resourceClaim;

            if (resourceClaim.Children.Count > 0)
            {
                foreach (var childResourceClaim in Flatten(resourceClaim.Children))
                {
                    yield return childResourceClaim;
                }
            }
        }
    }

    private static List<ClaimSetResourceClaimActionAuthStrategies> GetAuthorizationStrategyOverrides(
        ResourceClaim resourceClaim,
        string actionName
    )
    {
        return resourceClaim
            .AuthorizationStrategyOverrides.Where(overrideAction =>
                overrideAction.ActionName is not null
                && overrideAction.ActionName.Equals(actionName, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
    }
}
