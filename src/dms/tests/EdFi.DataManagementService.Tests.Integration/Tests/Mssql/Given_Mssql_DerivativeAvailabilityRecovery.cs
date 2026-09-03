// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SqlClient blocks further connection attempts for a period after a failed open. Every derivative is
/// realized with <c>PoolBlockingPeriod.NeverBlock</c>, so a derivative that becomes reachable again is
/// reached on the next request rather than after that period elapses - at the same effective
/// connection string, which is what makes this about the pool rather than about configuration.
/// </summary>
public sealed class Given_Mssql_DerivativeAvailabilityRecovery : MssqlApiIntegrationTestBase
{
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.Snapshot];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture, grantReadChanges: true);

    protected override IDataStoreProvider? CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    )
    {
        _provider = FakeDataStoreProvider.Mutable([
            DerivativeRoutingSupport.ParentWith(
                ExternalDoublesConstants.StableDataStoreId,
                primaryConnectionString,
                RelationalProviderToken.SqlServer,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = DerivativeConnectionString(
                        DataStoreDerivativeType.Snapshot
                    ),
                }
            ),
        ]);

        return _provider;
    }

    [Test]
    public async Task It_reaches_the_derivative_again_once_it_is_available()
    {
        string snapshotDatabaseName = new SqlConnectionStringBuilder(
            DerivativeConnectionString(DataStoreDerivativeType.Snapshot)
        ).InitialCatalog;

        await SetDatabaseOfflineAsync(snapshotDatabaseName, offline: true);

        try
        {
            using HttpResponseMessage unavailable = await DerivativeRoutingSupport.SendAsync(
                Harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: "true"
            );

            unavailable
                .StatusCode.Should()
                .NotBe(
                    HttpStatusCode.OK,
                    "the snapshot database is offline, so the open against it must fail"
                );
        }
        finally
        {
            await SetDatabaseOfflineAsync(snapshotDatabaseName, offline: false);
        }

        // The very next request, at the same effective connection string, must reach the server rather
        // than be refused from SqlClient's blocking period.
        using HttpResponseMessage recovered = await DerivativeRoutingSupport.SendAsync(
            Harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        string body = await recovered.Content.ReadAsStringAsync();
        recovered
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                $"a restored derivative must be reached on the next request, not after a blocking period: {body}"
            );
    }

    private static Task SetDatabaseOfflineAsync(string databaseName, bool offline)
    {
        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);

        return MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            offline
                ? $"ALTER DATABASE {quotedDatabaseName} SET OFFLINE WITH ROLLBACK IMMEDIATE;"
                : $"ALTER DATABASE {quotedDatabaseName} SET ONLINE;"
        );
    }
}
