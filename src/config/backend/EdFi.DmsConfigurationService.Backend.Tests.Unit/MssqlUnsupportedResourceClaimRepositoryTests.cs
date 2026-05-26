// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class MssqlUnsupportedResourceClaimRepositoryTests
{
    private readonly IResourceClaimRepository _repository = new MssqlUnsupportedResourceClaimRepository();

    [Test]
    public async Task It_returns_failure_unknown_for_get_resource_claims()
    {
        var result = await _repository.GetResourceClaims(new ResourceClaimQuery());

        result.Should().BeOfType<ResourceClaimListResult.FailureUnknown>();
    }

    [Test]
    public async Task It_returns_failure_unknown_for_get_resource_claim_by_id()
    {
        var result = await _repository.GetResourceClaim(1L);

        result.Should().BeOfType<ResourceClaimGetResult.FailureUnknown>();
    }

    [Test]
    public async Task It_returns_failure_unknown_for_get_resource_claim_actions()
    {
        var result = await _repository.GetResourceClaimActions(new ResourceClaimActionQuery());

        result.Should().BeOfType<ResourceClaimActionListResult.FailureUnknown>();
    }

    [Test]
    public async Task It_returns_failure_unknown_for_get_resource_claim_action_auth_strategies()
    {
        var result = await _repository.GetResourceClaimActionAuthStrategies(
            new ResourceClaimActionAuthStrategyQuery()
        );

        result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureUnknown>();
    }
}
