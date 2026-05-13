// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Returns a single claim set granting Create/Read/Update/Delete on every resource
/// listed in the supplied <see cref="FixtureContext"/>, all under the
/// <c>NoFurtherAuthorizationRequired</c> strategy. Resource claim URIs are built
/// from the lowercased project and resource names so they line up with how the
/// resource-action authorization middleware composes claims at request time.
/// </summary>
internal sealed class AllowAllClaimSetProvider(FixtureContext fixture) : IClaimSetProvider
{
    private static readonly string[] _actions = ["Create", "Read", "Update", "Delete"];

    public Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
    {
        var resourceClaims = fixture
            .Resources.SelectMany(r =>
                _actions.Select(a => new ResourceClaim(
                    Name: $"{Conventions.EdFiOdsResourceClaimBaseUri}/{r.ProjectName.ToLowerInvariant()}/{r.ResourceName.ToLowerInvariant()}",
                    Action: a,
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy(
                            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                        ),
                    ]
                ))
            )
            .ToList();

        return Task.FromResult<IList<ClaimSet>>([
            new ClaimSet(Name: ExternalDoublesConstants.SmokeClaimSetName, ResourceClaims: resourceClaims),
        ]);
    }
}
