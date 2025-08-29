// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Decorator that adds caching functionality to claim set retrieval.
/// This service wraps another IClaimSetProvider implementation and caches its results
/// to improve performance by avoiding repeated calls to the underlying provider.
/// </summary>
public class CachedClaimSetProvider(IClaimSetProvider claimSetProvider, ClaimSetsCache claimSetsCache)
    : IClaimSetProvider
{
    /// <summary>
    /// Retrieves claim sets from cache if available, otherwise fetches from the provider
    /// and caches the result for future requests.
    /// </summary>
    public async Task<IList<ClaimSet>> GetAllClaimSets()
    {
        // Try to get claim sets from cache first
        var cachedClaimSets = claimSetsCache.GetCachedClaimSets();
        if (cachedClaimSets != null)
        {
            return cachedClaimSets;
        }

        // Cache miss - fetch from the provider
        var claimSets = await claimSetProvider.GetAllClaimSets();
        if (claimSets.Count > 0)
        {
            claimSetsCache.CacheClaimSets(claimSets);
        }

        return claimSets;
    }
}
