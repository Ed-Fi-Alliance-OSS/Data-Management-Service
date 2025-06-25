// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using CmsHierarchy.Model;

namespace CmsHierarchy;

public class ExtensionResourceClaimsToAuthHierarchy
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
    public static List<Claim> TransformClaims(string claimsFileToTransform, List<Claim> existingClaims)
    {
      var path = Path.Combine("ClaimSetFiles", claimsFileToTransform);
        var jsonData = File.ReadAllText(path);
        var extensionClaims = JsonSerializer.Deserialize<List<ResourceClaim>>(jsonData, JsonOptions);

        foreach (ResourceClaim resourceClaim in extensionClaims ?? Enumerable.Empty<ResourceClaim>())
        {
            var existingClaim = existingClaims
                .SelectMany(x => SearchRecursive(x, resourceClaim.Name!))
                .SingleOrDefault();

            if (existingClaim! != null)
            {
                // Add additional claims
                var childClaims = resourceClaim.Children.Select(x => new Claim { Name = x.Name });
                existingClaim.Claims!.AddRange(childClaims);
            }
            else
            {
                // Claim is added at root level
                existingClaims.Add(
                    new Claim
                    {
                        Name = resourceClaim.Name,
                        Claims = resourceClaim.Children.Select(x => new Claim { Name = x.Name }).ToList(),
                        ClaimSets = resourceClaim.ClaimSets,
                        DefaultAuthorization = new DefaultAuthorization
                        {
                            Actions = resourceClaim
                                .DefaultAuthorizationStrategiesForCRUD.Select(x => new Model.Action
                                {
                                    Name = x.ActionName,
                                    AuthorizationStrategies = x.AuthorizationStrategies?.ToList() ?? new List<AuthorizationStrategy>()
                                })
                                .ToList(),
                        },
                    }
                );
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
            try
            {
                if (string.IsNullOrEmpty(resourceUri))
                {
                    return string.Empty;
                }

                Uri uri = new(resourceUri);
                string[] pathSegments = uri.AbsolutePath.Split('/');
                return string.Join("/", pathSegments[^2..]);
            }
            catch(Exception ex) {
                // If the resourceUri is not a valid URI, return it as is
                Console.WriteLine($"Error processing resource URI '{resourceUri}': {ex.Message}");
                return resourceUri ?? string.Empty;
            }
        }
    }
}
