// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// DocumentCache read acceleration is enabled and the parent is a registered cache target, so a
/// derivative read that still bypassed the cache did so because of the derivative guard.
/// </summary>
public sealed class Given_Postgresql_DerivativeDocumentCache : PostgresqlApiIntegrationTestBase
{
    private MutableInstanceProvider _provider = null!;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool RecordDocumentCacheReadTelemetry => true;

    protected override IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives =>
        [DataStoreDerivativeType.ReadReplica, DataStoreDerivativeType.Snapshot];

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
    public Task It_bypasses_the_cache_and_leaves_the_derivative_cache_untouched() =>
        DerivativeDocumentCacheScenario.It_bypasses_the_cache_and_leaves_the_derivative_cache_untouched(
            Harness,
            () => OpenAssertionConnectionAsync(DerivativeConnectionString(DataStoreDerivativeType.Snapshot)),
            "dms.\"DocumentCache\""
        );

    [Test]
    public async Task It_still_uses_the_cache_for_a_parent_read()
    {
        // Every derivative removed, so this same request selects the parent.
        _provider.Publish([
            DerivativeRoutingSupport.ParentOnly(
                ExternalDoublesConstants.StableDataStoreId,
                PrimaryConnectionString,
                RelationalProviderToken.Postgresql
            ),
        ]);

        await DerivativeDocumentCacheScenario.It_still_uses_the_cache_for_a_parent_read(Harness);
    }

    private new Task<DbConnection> OpenAssertionConnectionAsync(string leasedConnectionString) =>
        base.OpenAssertionConnectionAsync(leasedConnectionString);
}
