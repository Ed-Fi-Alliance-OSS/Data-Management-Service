// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Utilities;

internal static class ClaimSetExtensions
{
    /// <summary>
    /// Finds a claim set by name using case-insensitive comparison.
    /// </summary>
    public static ClaimSet? FindClaimSetByName(this IEnumerable<ClaimSet> claimSets, string claimSetName)
    {
        return claimSets.SingleOrDefault(c =>
            string.Equals(c.Name, claimSetName, StringComparison.InvariantCultureIgnoreCase)
        );
    }

    /// <summary>
    /// Finds resource claims matching the requested resource URI.
    /// </summary>
    public static ResourceClaim[] FindMatchingResourceClaims(this ClaimSet claimSet, string resourceClaimUri)
    {
        return claimSet
            .ResourceClaims.Where(r =>
                string.Equals(r.Name, resourceClaimUri, StringComparison.InvariantCultureIgnoreCase)
            )
            .ToArray();
    }
}
