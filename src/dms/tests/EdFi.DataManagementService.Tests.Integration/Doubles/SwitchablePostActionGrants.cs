// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// A claim set whose Create and Update grants on the written resources can be switched between requests.
/// Read and Delete are always granted, as is every action on resources the scenario does not write; Create
/// and Update on the written resources are whatever the test last set, where <see langword="null"/> leaves
/// the action ungranted. The middleware reads the claim set on every request, so a test can seed with both
/// actions and then narrow them.
/// </summary>
internal sealed class SwitchablePostActionGrants : IClaimSetProvider
{
    private static readonly string[] _noFurther =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    private readonly ConfigurableClaimSetProvider _inner;
    private readonly Func<QualifiedResourceName, bool> _isWrittenResource;
    private IReadOnlyList<string>? _create = _noFurther;
    private IReadOnlyList<string>? _update = _noFurther;

    public SwitchablePostActionGrants(
        FixtureContext fixture,
        Func<QualifiedResourceName, bool> isWrittenResource
    )
    {
        _isWrittenResource = isWrittenResource;
        _inner = new ConfigurableClaimSetProvider(fixture, Resolve);
    }

    public void Set(IReadOnlyList<string>? create, IReadOnlyList<string>? update)
    {
        _create = create;
        _update = update;
    }

    public Task<IList<ClaimSet>> GetAllClaimSets(
        string? tenant = null,
        CancellationToken cancellationToken = default
    ) => _inner.GetAllClaimSets(tenant, cancellationToken);

    private IReadOnlyList<string>? Resolve(QualifiedResourceName resource, string action) =>
        _isWrittenResource(resource)
            ? action switch
            {
                "Create" => _create,
                "Update" => _update,
                _ => _noFurther,
            }
            : _noFurther;
}
