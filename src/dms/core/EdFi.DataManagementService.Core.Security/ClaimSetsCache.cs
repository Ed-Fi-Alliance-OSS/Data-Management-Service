// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Security;

public class ClaimSetsCache(IMemoryCache memoryCache, TimeSpan expiration)
{
    public void CacheClaimSets(string cacheId, IList<ClaimSet> claimSets)
    {
        memoryCache.Set(cacheId, claimSets, expiration);
    }

    public IList<ClaimSet>? GetCachedClaimSets(string cacheId)
    {
        memoryCache.TryGetValue(cacheId, out IList<ClaimSet>? claimSets);
        return claimSets;
    }
}
