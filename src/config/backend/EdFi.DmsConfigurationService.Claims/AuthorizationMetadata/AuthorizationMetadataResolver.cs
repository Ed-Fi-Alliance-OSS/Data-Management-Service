// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

namespace EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;

/// <summary>
/// Resolves catalog entries against the hierarchy using CMS inheritance, defaults,
/// and overrides. Catalog order and hierarchy traversal determine authorization IDs;
/// each call starts a new response. Hierarchy parent links must already be populated
/// (Claim.Claims sets them during deserialization).
/// </summary>
public static class AuthorizationMetadataResolver
{
    public static AuthorizationMetadataResponse Resolve(
        IEnumerable<string> claimSetNames,
        IReadOnlyList<Claim> hierarchy
    ) => Resolve(claimSetNames, hierarchy, EqualityComparer<ClaimSetMetadata.Authorization>.Default);

    internal static AuthorizationMetadataResponse Resolve(
        IEnumerable<string> claimSetNames,
        IReadOnlyList<Claim> hierarchy,
        IEqualityComparer<ClaimSetMetadata.Authorization> authorizationComparer
    )
    {
        const int UninitializedId = 0;
        int nextAuthorizationId = 1;

        return new AuthorizationMetadataResponse(claimSetNames.Select(GetClaimSetMetadata).ToList());

        ClaimSetMetadata GetClaimSetMetadata(string claimSetName)
        {
            var responseClaims = new List<ClaimSetMetadata.Claim>();
            var responseAuthorizations = new List<ClaimSetMetadata.Authorization>();
            var authorizationIds = new Dictionary<ClaimSetMetadata.Authorization, int>(authorizationComparer);

            // Process each root claim in the hierarchy (there are actually multiple hierarchies present, with a true single root)
            foreach (var rootClaim in hierarchy)
            {
                AddLeafClaims(rootClaim);
            }

            // Prepare response
            ClaimSetMetadata claimSetMetadata = new(
                ClaimSetName: claimSetName,
                Claims: responseClaims.OrderBy(c => c.Name).ToList(),
                Authorizations: responseAuthorizations.ToList()
            );

            return claimSetMetadata;

            void AddLeafClaims(Claim claim)
            {
                if (claim.Claims.Count > 0)
                {
                    // Perform depth-first processing of the hierarchy
                    foreach (var childClaim in claim.Claims)
                    {
                        AddLeafClaims(childClaim);
                    }
                }
                else
                {
                    // Process the leaf-node claim
                    var currentClaim = claim;
                    bool includeLeafNodeClaim = false;

                    var grantedActionByName = new Dictionary<string, ClaimSetMetadata.Action>();
                    var actionDefaultsByName = new Dictionary<string, DefaultAction>();

                    // Climb the lineage of the hierarchy to the root node
                    while (currentClaim is not null)
                    {
                        // Capture the defaults for any actions that have not yet been encountered while processing the lineage
                        currentClaim.DefaultAuthorization?.Actions.ForEach(a =>
                            actionDefaultsByName.TryAdd(a.Name, a)
                        );

                        // Look for claim set specific metadata defined on the current claim
                        var matchingClaimSet = currentClaim.ClaimSets.Find(cs =>
                            cs.Name.Equals(claimSetName, StringComparison.OrdinalIgnoreCase)
                        );

                        if (matchingClaimSet is not null)
                        {
                            // With the presence metadata defined for our target claim set, we will add the leaf node resource claim to the response
                            includeLeafNodeClaim = true;

                            // Look for metadata defined for actions that has not yet been encountered
                            foreach (var action in matchingClaimSet.Actions)
                            {
                                if (!grantedActionByName.ContainsKey(action.Name))
                                {
                                    // Capture the action with authorization strategy overrides, if present
                                    grantedActionByName[action.Name] = new ClaimSetMetadata.Action(
                                        action.Name,
                                        action
                                            .AuthorizationStrategyOverrides.Select(
                                                aso => new ClaimSetMetadata.AuthorizationStrategy(aso.Name)
                                            )
                                            .ToArray()
                                    );
                                }
                            }
                        }

                        // Process the parent claim next
                        currentClaim = currentClaim.Parent;
                    }

                    if (includeLeafNodeClaim)
                    {
                        // Finalize the claim's authorization and apply it to the response
                        int authorizationId = ApplyAuthorizationToResponse(GetProposedAuthorization());

                        // Add the claim to the response, with the associated authorizationId
                        responseClaims.Add(new ClaimSetMetadata.Claim(claim.Name, authorizationId));

                        int ApplyAuthorizationToResponse(ClaimSetMetadata.Authorization proposedAuthorization)
                        {
                            // Look for an existing equivalent authorization
                            if (
                                authorizationIds.TryGetValue(
                                    proposedAuthorization,
                                    out int existingAuthorizationId
                                )
                            )
                            {
                                return existingAuthorizationId;
                            }

                            // Assign the next id
                            int newAuthorizationId = nextAuthorizationId++;
                            var newAuthorization = proposedAuthorization with { Id = newAuthorizationId };

                            // Capture this unique authorization's Id (for reuse by other claims)
                            authorizationIds.Add(newAuthorization, newAuthorizationId);

                            // Add the authorization to the response
                            responseAuthorizations.Add(newAuthorization);

                            return newAuthorizationId;
                        }

                        ClaimSetMetadata.Authorization GetProposedAuthorization()
                        {
                            ApplyDefaultsToGrantedActions();

                            return new ClaimSetMetadata.Authorization(
                                UninitializedId,
                                grantedActionByName
                                    .Select(kvp => new ClaimSetMetadata.Action(
                                        kvp.Key,
                                        kvp.Value.AuthorizationStrategies.Select(
                                                s => new ClaimSetMetadata.AuthorizationStrategy(s.Name)
                                            )
                                            .ToArray()
                                    ))
                                    .ToArray()
                            );
                        }

                        void ApplyDefaultsToGrantedActions()
                        {
                            // Apply defaults to claims, if claim set specific overrides were not defined
                            foreach (var kvp in grantedActionByName)
                            {
                                string actionName = kvp.Key;
                                var grantedAction = kvp.Value;

                                // If no authorization strategies have been identified...
                                if (
                                    grantedAction.AuthorizationStrategies.Length == 0
                                    // ... and we have defaults that have been identified
                                    && actionDefaultsByName.TryGetValue(actionName, out var defaultAction)
                                    && defaultAction.AuthorizationStrategies is { Count: > 0 }
                                )
                                {
                                    grantedActionByName[actionName] = new ClaimSetMetadata.Action(
                                        actionName,
                                        defaultAction
                                            .AuthorizationStrategies.Select(
                                                s => new ClaimSetMetadata.AuthorizationStrategy(s.Name)
                                            )
                                            .ToArray()
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
