// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Memory cache wrapper for storing and retrieving claim sets with configurable expiration.
/// </summary>
public record ClaimSetsCache(IMemoryCache memoryCache, TimeSpan expiration)
{
    // Constant cache identifier used to store and retrieve claim sets from the cache
    private const string CacheId = "ClaimSetsCache";

    public void CacheClaimSets(IList<ClaimSet> claimSets)
    {
        memoryCache.Set(CacheId, claimSets, expiration);
    }

    public IList<ClaimSet>? GetCachedClaimSets()
    {
        memoryCache.TryGetValue(CacheId, out IList<ClaimSet>? claimSets);
        return claimSets;
    }

    public void ClearCache()
    {
        memoryCache.Remove(CacheId);
    }
}
