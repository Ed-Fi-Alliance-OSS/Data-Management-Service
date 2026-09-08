// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// An unreachable selected snapshot answers Snapshot Not Found from every connection-acquisition point
/// on a PostgreSQL read.
/// </summary>
public sealed class Given_Postgresql_SnapshotUnavailable : PostgresqlApiIntegrationTestBase
{
    private readonly HydrationGate _hydrationGate = new();
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.ReadReplica, DataStoreDerivativeType.Snapshot];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture, grantReadChanges: true);

    protected override void ConfigureAdditionalServices(IServiceCollection services) =>
        services.AddHydrationGate(_hydrationGate);

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
    public Task SeedDistinguishableDatabases() =>
        DerivativeRoutingSupport.SeedDistinguishableStudentsAsync(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_returns_snapshot_not_found_when_the_fingerprint_read_cannot_connect() =>
        SnapshotUnavailableScenario.It_returns_snapshot_not_found_when_the_fingerprint_read_cannot_connect(
            Harness,
            Reachability,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_returns_snapshot_not_found_when_the_resource_key_read_cannot_connect() =>
        SnapshotUnavailableScenario.It_returns_snapshot_not_found_when_the_resource_key_read_cannot_connect(
            Harness,
            Reachability,
            OpenAssertionConnectionAsync,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_returns_snapshot_not_found_when_the_repository_query_cannot_connect() =>
        SnapshotUnavailableScenario.It_returns_snapshot_not_found_when_the_repository_query_cannot_connect(
            Harness,
            Reachability,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_returns_snapshot_not_found_when_hydration_cannot_connect() =>
        SnapshotUnavailableScenario.It_returns_snapshot_not_found_when_hydration_cannot_connect(
            Harness,
            _hydrationGate,
            Reachability,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_returns_snapshot_not_found_for_a_provider_invalid_snapshot_string() =>
        SnapshotUnavailableScenario.It_returns_snapshot_not_found_for_a_provider_invalid_snapshot_string(
            Harness,
            _provider,
            Reachability,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_keeps_the_availability_response_for_a_provider_invalid_replica_string() =>
        SnapshotUnavailableScenario.It_keeps_the_availability_response_for_a_provider_invalid_replica_string(
            Harness,
            _provider,
            Reachability,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString
        );

    [Test]
    public Task It_keeps_the_service_configuration_error_for_a_provider_invalid_primary_string() =>
        SnapshotUnavailableScenario.It_keeps_the_service_configuration_error_for_a_provider_invalid_primary_string(
            Harness,
            _provider,
            Reachability,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString
        );

    [Test]
    public Task It_keeps_the_not_provisioned_response_for_a_reachable_snapshot() =>
        SnapshotUnavailableScenario.It_keeps_the_not_provisioned_response_for_a_reachable_snapshot(
            Harness,
            OpenAssertionConnectionAsync,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_keeps_the_schema_mismatch_response_for_a_reachable_snapshot() =>
        SnapshotUnavailableScenario.It_keeps_the_schema_mismatch_response_for_a_reachable_snapshot(
            Harness,
            OpenAssertionConnectionAsync,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_does_not_translate_a_failure_after_a_successful_acquisition() =>
        SnapshotUnavailableScenario.It_does_not_translate_a_failure_after_a_successful_acquisition(
            Harness,
            OpenAssertionConnectionAsync,
            "ALTER TABLE edfi.\"Student\" RENAME TO \"Student_hidden\"",
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_does_not_translate_an_ordinary_miss_on_a_reachable_snapshot() =>
        SnapshotUnavailableScenario.It_does_not_translate_an_ordinary_miss_on_a_reachable_snapshot(Harness);

    [Test]
    public Task It_does_not_translate_an_invalid_parameter_on_a_reachable_snapshot() =>
        SnapshotUnavailableScenario.It_does_not_translate_an_invalid_parameter_on_a_reachable_snapshot(
            Harness
        );

    [Test]
    public Task It_keeps_the_availability_response_when_the_replica_is_unreachable() =>
        SnapshotUnavailableScenario.It_keeps_the_availability_response_when_the_replica_is_unreachable(
            Harness,
            Reachability,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica)
        );
}

/// <summary>
/// The snapshot answer must not displace an authorization denial, so this fixture is the same
/// arrangement with authorization actually enforced and a claim set that permits creating a Student but
/// not reading one.
/// </summary>
public sealed class Given_Postgresql_SnapshotUnavailable_With_Read_Authorization_Denied
    : PostgresqlApiIntegrationTestBase
{
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.ReadReplica, DataStoreDerivativeType.Snapshot];

    protected override bool BypassAuthorization => false;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        DocumentCacheReadAccelerationScenario.CreateStudentCreateOnlyClaimSetProvider();

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
    public void PublishTheDerivatives() =>
        DerivativeRoutingSupport.PublishFullArrangement(
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_does_not_translate_an_authorization_denial_on_a_reachable_snapshot() =>
        SnapshotUnavailableScenario.It_does_not_translate_an_authorization_denial_on_a_reachable_snapshot(
            Harness
        );
}

/// <summary>
/// The same arrangement with cache read acceleration enabled and the cache adapter's acquisition forced
/// to fail, which is what makes both halves of the acceleration contract observable at once.
/// </summary>
[Category("DocumentCacheReadAcceleration")]
public sealed class Given_Postgresql_SnapshotUnavailable_With_Cache_Acceleration
    : PostgresqlApiIntegrationTestBase
{
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.ReadReplica, DataStoreDerivativeType.Snapshot];

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool ForceDocumentCacheReadLookupAdapterAcquisitionFailure => true;

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
                RelationalProviderToken.Postgresql
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
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.ReadReplica),
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_bypasses_the_cache_and_still_answers_snapshot_not_found() =>
        SnapshotUnavailableScenario.It_bypasses_the_cache_and_still_answers_snapshot_not_found(
            Harness,
            Reachability,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_falls_back_relationally_when_a_primary_cache_acquisition_fails() =>
        SnapshotUnavailableScenario.It_falls_back_relationally_when_a_primary_cache_acquisition_fails(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.Postgresql,
            PrimaryConnectionString
        );
}
