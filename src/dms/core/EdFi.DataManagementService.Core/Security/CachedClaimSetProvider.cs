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
/// Supports tenant-keyed caching for multi-tenant scenarios.
/// </summary>
public class CachedClaimSetProvider(IClaimSetProvider claimSetProvider, ClaimSetsCache claimSetsCache)
    : IClaimSetProvider
{
    /// <summary>
    /// Retrieves claim sets from cache if available, otherwise fetches from the provider
    /// and caches the result for future requests. Uses tenant-specific cache keys in multi-tenant mode.
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant scenarios.</param>
    public async Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
    {
        // Try to get claim sets from cache first (using tenant-specific key)
        var cachedClaimSets = claimSetsCache.GetCachedClaimSets(tenant);
        if (cachedClaimSets != null)
        {
            return cachedClaimSets;
        }

        // Cache miss - fetch from the provider
        var claimSets = await claimSetProvider.GetAllClaimSets(tenant);
        if (claimSets.Count > 0)
        {
            claimSetsCache.CacheClaimSets(claimSets, tenant);
        }

        return claimSets;
    }
}
