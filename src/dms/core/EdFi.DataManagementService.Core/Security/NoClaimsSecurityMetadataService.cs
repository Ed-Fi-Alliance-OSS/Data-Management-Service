// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

public class NoClaimsClaimSetCacheService(ILogger _logger) : IClaimSetCacheService
{
    public async Task<IList<ClaimSet>> GetClaimSets()
    {
        _logger.LogWarning(
            "GetClaimSets: Backend ClaimSetCacheService has been configured to always report success."
        );
        return await Task.FromResult(new List<ClaimSet>());
    }
}
