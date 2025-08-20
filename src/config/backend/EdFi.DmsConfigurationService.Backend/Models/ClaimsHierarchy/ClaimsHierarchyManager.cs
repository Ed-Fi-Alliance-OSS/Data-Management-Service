// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public interface IClaimsHierarchyManager
{
    void RemoveClaimSetFromHierarchy(string claimSetName, List<Claim> claims);
    void ApplyImportedClaimSetToHierarchy(ClaimSetImportCommand command, List<Claim> claims);
}

public class ClaimsHierarchyManager : IClaimsHierarchyManager
{
    public void RemoveClaimSetFromHierarchy(string claimSetName, List<Claim> claims)
    {
        foreach (var claim in claims)
        {
            claim.ClaimSets.RemoveAll(cs => cs.Name.Equals(claimSetName, StringComparison.OrdinalIgnoreCase));

            // Process down the hierarchy, recursively
            if (claim.Claims.Any())
            {
                RemoveClaimSetFromHierarchy(claimSetName, claim.Claims);
            }
        }
    }

    public void ApplyImportedClaimSetToHierarchy(ClaimSetImportCommand command, List<Claim> claims)
    {
        // Perform basic validation check (command should already be validated)
        if (string.IsNullOrEmpty(command.Name) || command.ResourceClaims == null)
        {
            throw new NullReferenceException(
                "Claim set name or resource claims are null on the import command."
            );
        }

        ApplyImportedClaimSetToHierarchy(command.Name, command.ResourceClaims, claims);
    }

    private void ApplyImportedClaimSetToHierarchy(
        string claimSetName,
        List<ResourceClaim> importResourceClaims,
        List<Claim> claims
    )
    {
        // Logic to apply imported claim set metadata to the hierarchy
        // This should ignore authorizationStrategyOverridesForCRUD with isInheritedFromParent == true
        // Implement the logic based on the structure of ClaimSetImportCommand
        foreach (var resourceClaim in importResourceClaims)
        {
            // Find or create the claim in the hierarchy
            var claim = claims.FirstOrDefault(c => c.Name == resourceClaim.Name);

            // If the claim is not found, throw an exception
            // NOTE: The command for the import should already have been validated at this point, so this is purely a defensive check
            if (claim == null)
            {
                throw new Exception(
                    $"Claim '{resourceClaim.Name}' not found in hierarchy and cannot be imported."
                );
            }

            // Find or create the ClaimSet for the metadata in the claims hierarchy
            // NOTE: It is expected that all claim set information will have been removed by a call to RemoveClaimSetFromHierarchy
            var claimSet = claim.ClaimSets.FirstOrDefault(cs => cs.Name == claimSetName);

            if (claimSet != null)
            {
                throw new InvalidOperationException(
                    "The claim set already exists in the hierarchy, but it should not have been removed first with a call to RemoveClaimSetFromHierarchy."
                );
            }

            // Create and initialize the claim set
            claimSet = new ClaimSet { Name = claimSetName, Actions = [] };

            claim.ClaimSets.Add(claimSet);

            if (resourceClaim.Actions != null)
            {
                // Add actions with overrides (if present) from the ResourceClaim
                foreach (var action in resourceClaim.Actions.Where(a => a.Enabled))
                {
                    // Create the action for the claim set
                    var newAction = new ClaimSetAction
                    {
                        Name = action.Name ?? "",
                        AuthorizationStrategyOverrides = [],
                    };

                    // Look for overrides on import command
                    var overrideForCrud = resourceClaim.AuthorizationStrategyOverridesForCRUD.SingleOrDefault(
                        x => x?.ActionName == action.Name
                    );

                    // If overrides for the action are present, apply them
                    if (overrideForCrud is { AuthorizationStrategies: not null })
                    {
                        // Apply authorization strategy overrides
                        newAction.AuthorizationStrategyOverrides =
                        [
                            .. overrideForCrud.AuthorizationStrategies.Select(
                                strategy => new AuthorizationStrategy
                                {
                                    Name = strategy.AuthorizationStrategyName,
                                }
                            ),
                        ];
                    }

                    claimSet.Actions.Add(newAction);
                }
            }

            // Recursively apply to child claims, but only if there are still resource claims to be imported
            if (resourceClaim.Children.Count != 0 && claim.Claims.Count != 0)
            {
                ApplyImportedClaimSetToHierarchy(claimSetName, resourceClaim.Children, claim.Claims);
            }
        }
    }
}
