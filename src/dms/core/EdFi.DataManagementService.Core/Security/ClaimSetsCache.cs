// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Memory cache wrapper for storing and retrieving claim sets with configurable expiration.
/// Supports tenant-keyed caching for multi-tenant scenarios.
/// </summary>
public record ClaimSetsCache(IMemoryCache memoryCache, TimeSpan expiration)
{
    // Base cache identifier prefix for storing and retrieving claim sets from the cache
    private const string CacheIdPrefix = "ClaimSetsCache";

    /// <summary>
    /// Generates a cache key based on the tenant identifier.
    /// When tenant is null, uses the base cache ID for single-tenant mode.
    /// </summary>
    internal static string GetCacheKey(string? tenant) =>
        string.IsNullOrEmpty(tenant) ? CacheIdPrefix : $"{CacheIdPrefix}:{tenant}";

    public void CacheClaimSets(IList<ClaimSet> claimSets, string? tenant = null)
    {
        memoryCache.Set(GetCacheKey(tenant), claimSets, expiration);
    }

    public IList<ClaimSet>? GetCachedClaimSets(string? tenant = null)
    {
        memoryCache.TryGetValue(GetCacheKey(tenant), out IList<ClaimSet>? claimSets);
        return claimSets;
    }

    public void ClearCache(string? tenant = null)
    {
        memoryCache.Remove(GetCacheKey(tenant));
    }
}
