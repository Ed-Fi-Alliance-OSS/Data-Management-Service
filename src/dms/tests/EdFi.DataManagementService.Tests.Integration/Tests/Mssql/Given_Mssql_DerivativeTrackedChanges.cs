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
/// The tracked-change surfaces, on a fixture whose ApiSchema maps tracked-change identities, so
/// <c>/deletes</c> and <c>/keyChanges</c> answer with real bodies that can name the serving database.
/// </summary>
public sealed class Given_Mssql_DerivativeTrackedChanges : MssqlApiIntegrationTestBase
{
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.Snapshot];

    // A tracked key change only exists if an identity was updated, which DMS refuses unless the
    // resource is named here.
    protected override string AllowIdentityUpdateOverrides => "Student";

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
    public Task SeedDistinguishableTrackedChanges() =>
        DerivativeTrackedChangeScenario.SeedAsync(
            Harness,
            _provider,
            ExternalDoublesConstants.StableDataStoreId,
            RelationalProviderToken.SqlServer,
            PrimaryConnectionString,
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        );

    [Test]
    public Task It_serves_deletes_from_the_selected_target() =>
        DerivativeTrackedChangeScenario.It_serves_deletes_from_the_selected_target(
            Harness,
            Reachability,
            PrimaryConnectionString
        );

    [Test]
    public Task It_serves_key_changes_from_the_selected_target() =>
        DerivativeTrackedChangeScenario.It_serves_key_changes_from_the_selected_target(
            Harness,
            Reachability,
            PrimaryConnectionString
        );

    [Test]
    public Task It_serves_available_change_versions_from_the_selected_target() =>
        DerivativeTrackedChangeScenario.It_serves_available_change_versions_from_the_selected_target(
            Harness,
            Reachability,
            PrimaryConnectionString
        );
}
