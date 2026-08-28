// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Real authorization - not bypassed - while a derivative serves the rows.
/// </summary>
public sealed class Given_Postgresql_DerivativeParentAuthorization : PostgresqlApiIntegrationTestBase
{
    private static readonly Dictionary<RouteQualifierName, RouteQualifierValue> _routeContext = new()
    {
        [new RouteQualifierName("district")] = new RouteQualifierValue(
            DerivativeParentAuthorizationScenario.DistrictQualifierSegment
        ),
    };

    private readonly MutableNamespacePrefixJwtValidationService _clientIdentity = new(
        ExternalDoublesConstants.SmokeToken,
        ExternalDoublesConstants.SmokeClientId,
        [],
        [CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix]
    );

    protected override string RouteQualifierSegments => "district";

    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    // Supplied through the mutable identity below rather than this fixed list, because the seed has to
    // widen the caller and the assertions have to narrow it again.
    protected override MutableNamespacePrefixJwtValidationService? CreateJwtValidationService() =>
        _clientIdentity;

    /// <summary>
    /// Namespace-based authorization on every resource this fixture exposes, so the descriptor endpoint
    /// under test is genuinely authorized rather than falling through to an unrestricted claim.
    /// </summary>
    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (_, _) => [AuthorizationStrategyNameConstants.NamespaceBased]
        );

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.Snapshot];

    protected override IDataStoreProvider? CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    )
    {
        _provider = FakeDataStoreProvider.Mutable([
            DerivativeRoutingSupport.ParentOnly(
                ExternalDoublesConstants.StableDataStoreId,
                primaryConnectionString,
                RelationalProviderToken.Postgresql
            ),
        ]);

        return _provider;
    }

    [SetUp]
    public Task SeedAuthorizedAndUnauthorizedRows() =>
        DerivativeParentAuthorizationScenario.SeedAsync(
            Harness,
            _provider,
            _clientIdentity,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            _routeContext,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_resolves_route_context_from_the_parent() =>
        DerivativeParentAuthorizationScenario.AssertRouteContextResolvedFromTheParent(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            _routeContext
        );

    [Test]
    public Task It_applies_parent_authorization_to_derivative_rows() =>
        DerivativeParentAuthorizationScenario.It_applies_parent_authorization_to_derivative_rows(
            Harness,
            Reachability,
            PrimaryConnectionString
        );

    [Test]
    public Task It_yields_nothing_for_an_unauthorized_namespace_on_the_routed_path() =>
        DerivativeParentAuthorizationScenario.It_yields_nothing_for_an_unauthorized_namespace_on_the_routed_path(
            Harness,
            Reachability,
            PrimaryConnectionString
        );

    [Test]
    public Task It_refuses_an_unauthorized_write_while_a_derivative_is_configured() =>
        DerivativeParentAuthorizationScenario.It_refuses_an_unauthorized_write_while_a_derivative_is_configured(
            Harness
        );
}
