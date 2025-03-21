// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using CmsHierarchy.Model;

namespace CmsHierarchy;

public class ClaimSetToAuthHierarchy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static Claim[] GetBaseClaimHierarchy()
    {
        var path = Path.Combine("ClaimSetFiles", "AuthorizationHierarchy.json");
        var jsonData = File.ReadAllText(path);
        var claims = JsonSerializer.Deserialize<List<Claim>>(jsonData);
        return [.. claims!];
    }

    /// <summary>
    /// Transforms the existing claims by applying the modifications specified in the given claim set file.
    /// </summary>
    public static Claim[] TransformClaims(string claimSetFileToTransform, Claim[] existingClaims)
    {
        var path = Path.Combine("ClaimSetFiles", claimSetFileToTransform);
        var jsonData = File.ReadAllText(path);
        var claimSetData = JsonSerializer.Deserialize<ClaimSetResourceClaims>(jsonData, JsonOptions);
        var claimSetName = claimSetData!.Name;
        foreach (ResourceClaim resourceClaim in claimSetData.ResourceClaims)
        {
            if (resourceClaim != null)
            {
                var singularName = PluralToSingular(resourceClaim.Name!);

                var existingClaim = existingClaims
                    .SelectMany(x => SearchRecursive(x, singularName))
                    .SingleOrDefault();
                if (existingClaim != null)
                {
                    var actionsAndAuthStrategies = resourceClaim.AuthorizationStrategyOverridesForCRUD;
                    var actions = new List<ClaimSetAction>();
                    if (actionsAndAuthStrategies != null)
                    {
                        foreach (var actionAuthStrategy in actionsAndAuthStrategies)
                        {
                            if (actionAuthStrategy != null)
                            {
                                actions.Add(
                                    new ClaimSetAction
                                    {
                                        Name = actionAuthStrategy.ActionName!,
                                        AuthorizationStrategyOverrides =
                                        [
                                            .. actionAuthStrategy.AuthorizationStrategies!,
                                        ],
                                    }
                                );
                            }
                        }
                    }
                    existingClaim.ClaimSets ??= [];
                    if (
                        !existingClaim.ClaimSets.Exists(x =>
                            string.Equals(x.Name, claimSetName, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        existingClaim.ClaimSets.Add(new ClaimSet { Name = claimSetName, Actions = actions });
                    }
                }
            }
        }
        return existingClaims;
    }

    /// <summary>
    /// Recursively searches for a claim with a specified name within a claim hierarchy.
    /// </summary>
    private static IEnumerable<Claim> SearchRecursive(Claim claim, string searchTerm)
    {
        if (string.Equals(ResourceName(claim.Name), searchTerm, StringComparison.InvariantCultureIgnoreCase))
        {
            yield return claim;
        }
        if (claim.Claims != null)
        {
            foreach (var child in claim.Claims)
            {
                foreach (var match in SearchRecursive(child, searchTerm))
                {
                    yield return match;
                }
            }
        }

        static string ResourceName(string? resourceUri)
        {
            if (string.IsNullOrEmpty(resourceUri))
            {
                return string.Empty;
            }

            Uri uri = new(resourceUri);
            string[] pathSegments = uri.AbsolutePath.Split('/');
            return pathSegments[^1];
        }
    }

    static string PluralToSingular(string resourceClaimName)
    {
        if (
            resourceClaimName.EndsWith("ies", StringComparison.OrdinalIgnoreCase)
            && resourceClaimName.Length > 3
        )
        {
            return resourceClaimName[..^3] + "y";
        }

        if (
            resourceClaimName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && resourceClaimName.Length > 1
        )
        {
            return resourceClaimName[..^1];
        }
        return resourceClaimName;
    }
}
