// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <param name="resolveStrategyNames">
/// The strategies the claim set configures for a resource and action, or <see langword="null"/> when the claim
/// set does not grant that action on the resource at all.
/// </param>
internal sealed class ConfigurableClaimSetProvider(
    FixtureContext fixture,
    Func<QualifiedResourceName, string, IReadOnlyList<string>?> resolveStrategyNames,
    bool grantReadChanges = false
) : IClaimSetProvider
{
    private static readonly string[] _crudActions = ["Create", "Read", "Update", "Delete"];

    /// <summary>
    /// The change-query surfaces are gated on their own action, which the CRUD actions do not imply.
    /// It is opt-in rather than always granted so that no existing fixture's authorization changes.
    /// </summary>
    private static readonly string[] _crudAndReadChangesActions =
    [
        "Create",
        "Read",
        "Update",
        "Delete",
        "ReadChanges",
    ];

    private string[] Actions => grantReadChanges ? _crudAndReadChangesActions : _crudActions;

    public Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
    {
        var resourceClaims = fixture
            .Resources.SelectMany(resource =>
                Actions
                    .Select(action => (Action: action, StrategyNames: resolveStrategyNames(resource, action)))
                    .Where(static grant => grant.StrategyNames is not null)
                    .Select(grant => new ResourceClaim(
                        Name: $"{Conventions.EdFiOdsResourceClaimBaseUri}/{resource.ProjectName.ToLowerInvariant()}/{resource.ResourceName.ToLowerInvariant()}",
                        Action: grant.Action,
                        AuthorizationStrategies:
                        [
                            .. grant.StrategyNames!.Select(static strategyName => new AuthorizationStrategy(
                                strategyName
                            )),
                        ]
                    ))
            )
            .ToList();

        return Task.FromResult<IList<ClaimSet>>([
            new ClaimSet(Name: ExternalDoublesConstants.SmokeClaimSetName, ResourceClaims: resourceClaims),
        ]);
    }
}
