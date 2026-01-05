// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Cache configuration settings for DMS HybridCache behavior.
/// All expiration values are in minutes. Bound from appsettings.json "CacheSettings" section.
/// </summary>
public class CacheSettings
{
    public int ClaimSetsCacheExpirationMinutes { get; set; } = 10;
    public int ApplicationContextCacheExpirationMinutes { get; set; } = 10;
    public int TokenCacheExpirationMinutes { get; set; } = 25;
}
