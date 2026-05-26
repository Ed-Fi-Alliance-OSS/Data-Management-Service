// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

#pragma warning disable S1128 // Query types in Model, response types in Model.ResourceClaims - both needed
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;
#pragma warning restore S1128

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class MssqlUnsupportedResourceClaimRepository : IResourceClaimRepository
{
    private const string Message = "ResourceClaim endpoints are not supported for MSSQL.";

    public Task<ResourceClaimListResult> GetResourceClaims(ResourceClaimQuery query) =>
        Task.FromResult<ResourceClaimListResult>(new ResourceClaimListResult.FailureUnknown(Message));

    public Task<ResourceClaimGetResult> GetResourceClaim(long id) =>
        Task.FromResult<ResourceClaimGetResult>(new ResourceClaimGetResult.FailureUnknown(Message));

    public Task<ResourceClaimActionListResult> GetResourceClaimActions(ResourceClaimActionQuery query) =>
        Task.FromResult<ResourceClaimActionListResult>(
            new ResourceClaimActionListResult.FailureUnknown(Message)
        );

    public Task<ResourceClaimActionAuthStrategyListResult> GetResourceClaimActionAuthStrategies(
        ResourceClaimActionAuthStrategyQuery query
    ) =>
        Task.FromResult<ResourceClaimActionAuthStrategyListResult>(
            new ResourceClaimActionAuthStrategyListResult.FailureUnknown(Message)
        );
}
