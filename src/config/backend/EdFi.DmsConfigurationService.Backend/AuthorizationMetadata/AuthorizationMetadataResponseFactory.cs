// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;
using EdFi.DmsConfigurationService.DataModel.Model;

namespace EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;

public interface IAuthorizationMetadataResponseFactory
{
    Task<AuthorizationMetadataResponse> Create(string? claimSetName, List<Claim> hierarchy);
}

public class AuthorizationMetadataResponseFactory(IClaimSetRepository _claimSetRepository)
    : IAuthorizationMetadataResponseFactory
{
    public async Task<AuthorizationMetadataResponse> Create(string? claimSetName, List<Claim> hierarchy)
    {
        var claimSetResult = await _claimSetRepository.QueryClaimSet(
            new ClaimSetQuery() { Limit = int.MaxValue }
        );

        var claimSetNames = claimSetResult switch
        {
            ClaimSetQueryResult.FailureUnknown failureUnknown => throw new Exception(
                failureUnknown.FailureMessage
            ),
            ClaimSetQueryResult.Success success => success
                .ClaimSetResponses.Select(x => x.Name)
                .Where(csn =>
                    claimSetName is null || csn.Equals(claimSetName, StringComparison.OrdinalIgnoreCase)
                )
                .ToList(),
            _ => throw new Exception(claimSetResult.GetType().Name),
        };

        return AuthorizationMetadataResolver.Resolve(claimSetNames, hierarchy);
    }
}
