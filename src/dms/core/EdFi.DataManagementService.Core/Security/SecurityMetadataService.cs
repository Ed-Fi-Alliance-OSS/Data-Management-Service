// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

public interface ISecurityMetadataService
{
    Task<IList<ClaimSet>> GetClaimSets();
}

public class SecurityMetadataService(
    ISecurityMetadataProvider securityMetadataProvider,
    ClaimSetsCache claimSetsCache
) : ISecurityMetadataService
{
    private readonly string CacheId = "ClaimSetsCache";

    public async Task<IList<ClaimSet>> GetClaimSets()
    {
        var claimSets = claimSetsCache.GetCachedClaimSets(CacheId);
        if (claimSets != null)
        {
            return claimSets;
        }
        var result = await securityMetadataProvider.GetAllClaimSets();

        if (result.Count > 0)
        {
            claimSetsCache.CacheClaimSets(CacheId, result);
        }
        return result;
    }
}
