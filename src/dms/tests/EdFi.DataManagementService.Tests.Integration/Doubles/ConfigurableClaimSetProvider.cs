// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

internal sealed class ConfigurableClaimSetProvider(
    FixtureContext fixture,
    Func<QualifiedResourceName, string, IReadOnlyList<string>> resolveStrategyNames
) : IClaimSetProvider
{
    private static readonly string[] _actions = ["Create", "Read", "Update", "Delete"];

    public Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
    {
        var resourceClaims = fixture
            .Resources.SelectMany(resource =>
                _actions.Select(action => new ResourceClaim(
                    Name: $"{Conventions.EdFiOdsResourceClaimBaseUri}/{resource.ProjectName.ToLowerInvariant()}/{resource.ResourceName.ToLowerInvariant()}",
                    Action: action,
                    AuthorizationStrategies:
                    [
                        .. resolveStrategyNames(resource, action)
                            .Select(static strategyName => new AuthorizationStrategy(strategyName)),
                    ]
                ))
            )
            .ToList();

        return Task.FromResult<IList<ClaimSet>>([
            new ClaimSet(Name: ExternalDoublesConstants.SmokeClaimSetName, ResourceClaims: resourceClaims),
        ]);
    }
}
