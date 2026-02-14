// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Cache configuration settings for DMS HybridCache behavior.
/// All expiration values are in seconds. Bound from appsettings.json "CacheSettings" section.
/// </summary>
public class CacheSettings
{
    public int ClaimSetsCacheExpirationSeconds { get; set; } = 600; // 10 minutes
    public int ApplicationContextCacheExpirationSeconds { get; set; } = 600; // 10 minutes
    public int TokenCacheExpirationSeconds { get; set; } = 1500; // 25 minutes
    public int ProfileCacheExpirationSeconds { get; set; } = 1800; // 30 minutes

    /// <summary>
    /// Enables the TTL-based refresh of cached DMS instance configuration from CMS.
    /// </summary>
    public bool DmsInstanceCacheRefreshEnabled { get; set; } = true;

    /// <summary>
    /// The number of seconds between automatic refreshes of the DMS instance cache.
    /// Set to 0 or a negative value to keep the cached configuration until the next explicit reload.
    /// </summary>
    public int DmsInstanceCacheExpirationSeconds { get; set; } = 600; // 10 minutes
}
