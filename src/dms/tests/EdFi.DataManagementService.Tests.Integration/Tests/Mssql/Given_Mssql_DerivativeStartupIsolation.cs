// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// The host boots with an unusable snapshot and an unusable read replica already configured, so
/// startup and readiness see them rather than being handed them afterwards.
/// </summary>
public sealed class Given_Mssql_DerivativeStartupIsolation : MssqlApiIntegrationTestBase
{
    private readonly DerivativeRealizationRecorder _recorder = new();

    private string _snapshotConnectionString = null!;
    private string _replicaConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override void ConfigureAdditionalServices(IServiceCollection services) =>
        services.RecordMssqlRealization(_recorder);

    protected override IDataStoreProvider? CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    )
    {
        // One derivative that is offline in the sense of naming a database that does not exist, and a
        // second the same way, both published before the host starts.
        _snapshotConnectionString = Reachability.AbsentDatabaseConnectionString(primaryConnectionString);
        _replicaConnectionString = Reachability.AbsentDatabaseConnectionString(primaryConnectionString);

        return FakeDataStoreProvider.Mutable([
            DerivativeRoutingSupport.ParentWith(
                ExternalDoublesConstants.StableDataStoreId,
                primaryConnectionString,
                RelationalProviderToken.SqlServer,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = _snapshotConnectionString,
                    [DataStoreDerivativeType.ReadReplica] = _replicaConnectionString,
                }
            ),
        ]);
    }

    [Test]
    public Task It_starts_and_reports_healthy() =>
        DerivativeStartupIsolationScenario.It_starts_and_reports_healthy(Harness);

    [Test]
    public Task It_realizes_no_derivative() =>
        DerivativeStartupIsolationScenario.It_realizes_no_derivative(
            Harness,
            _recorder,
            PrimaryConnectionString,
            _snapshotConnectionString,
            _replicaConnectionString
        );

    [Test]
    public Task It_still_offers_the_configured_snapshot() =>
        DerivativeStartupIsolationScenario.It_still_offers_the_configured_snapshot(Harness);
}
