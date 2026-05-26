// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class MssqlUnsupportedResourceClaimRepository(ILogger<MssqlUnsupportedResourceClaimRepository> logger)
    : IResourceClaimRepository
{
    private const string Message = "ResourceClaim endpoints are not supported for MSSQL.";

    public Task<ResourceClaimListResult> GetResourceClaims(ResourceClaimQuery query)
    {
        logger.LogError(Message);
        return Task.FromResult<ResourceClaimListResult>(new ResourceClaimListResult.FailureUnknown(Message));
    }

    public Task<ResourceClaimGetResult> GetResourceClaim(long id)
    {
        logger.LogError(Message);
        return Task.FromResult<ResourceClaimGetResult>(new ResourceClaimGetResult.FailureUnknown(Message));
    }

    public Task<ResourceClaimActionListResult> GetResourceClaimActions(ResourceClaimActionQuery query)
    {
        logger.LogError(Message);
        return Task.FromResult<ResourceClaimActionListResult>(
            new ResourceClaimActionListResult.FailureUnknown(Message)
        );
    }

    public Task<ResourceClaimActionAuthStrategyListResult> GetResourceClaimActionAuthStrategies(
        ResourceClaimActionAuthStrategyQuery query
    )
    {
        logger.LogError(Message);
        return Task.FromResult<ResourceClaimActionAuthStrategyListResult>(
            new ResourceClaimActionAuthStrategyListResult.FailureUnknown(Message)
        );
    }
}
