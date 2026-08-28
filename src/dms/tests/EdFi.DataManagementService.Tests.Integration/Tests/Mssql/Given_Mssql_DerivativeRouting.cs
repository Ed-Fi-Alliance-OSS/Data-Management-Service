// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Snapshot and read-replica routing over the real HTTP pipeline against three separately provisioned
/// SQL Server databases.
/// </summary>
public sealed class Given_Mssql_DerivativeRouting : MssqlApiIntegrationTestBase
{
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.ReadReplica, DataStoreDerivativeType.Snapshot];

    // The change-query surfaces are gated on ReadChanges, which the CRUD actions do not imply.
    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture, grantReadChanges: true);

    protected override IDataStoreProvider? CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    )
    {
        _provider = FakeDataStoreProvider.Mutable([
            DerivativeRoutingSupport.ParentOnly(
                ExternalDoublesConstants.StableDataStoreId,
                primaryConnectionString,
                RelationalProviderToken.SqlServer
            ),
        ]);

        return _provider;
    }

    [SetUp]
    public Task SeedDistinguishableDatabases() =>
        DerivativeRoutingSupport.SeedDistinguishableStudentsAsync(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.SqlServer,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_serves_an_eligible_read_from_the_replica() =>
        DerivativeRoutingScenario.It_serves_an_eligible_read_from_the_replica(Harness);

    [Test]
    public Task It_prefers_the_snapshot_over_the_replica() =>
        DerivativeRoutingScenario.It_prefers_the_snapshot_over_the_replica(Harness);

    [Test]
    public Task It_serves_a_get_by_id_from_the_same_target() =>
        DerivativeRoutingScenario.It_serves_a_get_by_id_from_the_same_target(Harness);

    [Test]
    public Task It_routes_every_eligible_read_surface() =>
        DerivativeRoutingScenario.It_routes_every_eligible_read_surface(Harness);

    [Test]
    public Task It_answers_the_tracked_change_surfaces_the_same_way_either_side() =>
        DerivativeRoutingScenario.It_answers_the_tracked_change_surfaces_the_same_way_either_side(Harness);

    [Test]
    public Task It_returns_not_found_when_no_snapshot_is_configured() =>
        DerivativeRoutingScenario.It_returns_not_found_when_no_snapshot_is_configured(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.SqlServer,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica)
        );

    [Test]
    public Task It_rejects_a_mutation_that_asks_for_a_snapshot() =>
        DerivativeRoutingScenario.It_rejects_a_mutation_that_asks_for_a_snapshot(Harness);

    [Test]
    public Task It_leaves_a_mutation_alone_without_a_snapshot_request() =>
        DerivativeRoutingScenario.It_leaves_a_mutation_alone_without_a_snapshot_request(Harness);

    [Test]
    public Task It_writes_to_the_parent_while_reads_go_to_the_replica() =>
        DerivativeRoutingScenario.It_writes_to_the_parent_while_reads_go_to_the_replica(Harness);

    [Test]
    public Task It_serves_the_replacement_after_a_derivative_is_replaced() =>
        DerivativeRoutingScenario.It_serves_the_replacement_after_a_derivative_is_replaced(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.SqlServer,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_returns_to_the_parent_after_the_derivatives_are_removed() =>
        DerivativeRoutingScenario.It_returns_to_the_parent_after_the_derivatives_are_removed(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.SqlServer,
            PrimaryConnectionString
        );

    [Test]
    public Task It_does_not_interrupt_in_flight_requests_when_configuration_changes() =>
        DerivativeRoutingScenario.It_does_not_interrupt_in_flight_requests_when_configuration_changes(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.SqlServer,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_authorizes_from_the_parent_while_serving_a_derivative() =>
        DerivativeRoutingScenario.It_authorizes_from_the_parent_while_serving_a_derivative(Harness);

    [Test]
    public Task It_stops_at_selection_before_content_and_body_validation() =>
        DerivativeRoutingScenario.It_stops_at_selection_before_content_and_body_validation(Harness);

    [Test]
    public Task It_returns_not_found_for_an_unknown_resource() =>
        DerivativeRoutingScenario.It_returns_not_found_for_an_unknown_resource(Harness);
}
