// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Security;

public class ApiClientDetailsCache(IMemoryCache memoryCache)
{
    private readonly IMemoryCache _cache = memoryCache;

    public void CacheApiDetails(string tokenId, ApiClientDetails apiClientDetails, TimeSpan expiration)
    {
        _cache.Set(tokenId, apiClientDetails, expiration);
    }

    public ApiClientDetails? GetCachedApiDetails(string tokenId)
    {
        _cache.TryGetValue(tokenId, out ApiClientDetails? apiClientDetails);
        return apiClientDetails;
    }
}
