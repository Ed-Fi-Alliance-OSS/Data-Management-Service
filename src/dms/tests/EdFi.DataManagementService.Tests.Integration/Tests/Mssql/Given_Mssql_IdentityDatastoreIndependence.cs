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
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Proves request-time datastore independence on an initialized, identity-enabled host. After one
/// resource request and one identity request have served normally, the CMS data-store surface is
/// poisoned (every datastore member but <c>LoadTenants</c> throws) and the identity tenant snapshot's
/// 60-second freshness window is advanced past expiry, yet an authorized identity request still
/// refreshes the snapshot through <c>LoadTenants</c> and reaches the operation-unsupported capability
/// gate - because the identity pipeline never resolves a physical data store at all
/// (<see cref="Identity.IdentityTenantSnapshotTests" /> proves the same independence at the unit
/// level) - while no poisoned member is called during that request. Follows <see cref="Given_Mssql_ApplicationContextIntegration" />'s
/// MultiTenancy/BypassAuthorization shape.
/// </summary>
public sealed class Given_Mssql_IdentityDatastoreIndependence : MssqlApiIntegrationTestBase
{
    private const string Tenant = "identity-datastore-independence-tenant";

    private readonly PoisonableRecordingDataStoreProvider _dataStoreProvider = new(Tenant);
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.UtcNow);

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    protected override bool MultiTenancy => true;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string> { ["AppSettings:EnableIdentityManagement"] = "true" };

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new IdentityGrantingClaimSetProvider(fixture);

    protected override IDataStoreProvider CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    )
    {
        _dataStoreProvider.Configure(ExternalDoublesConstants.StableDataStoreId, primaryConnectionString);
        return _dataStoreProvider;
    }

    protected override TimeProvider TimeProviderOverride => _timeProvider;

    /// <summary>
    /// Keeps the production, scoped CachedApplicationContextProvider real while swapping only its
    /// CMS-facing dependency, so the client-to-tenant binding middleware observes a genuine Success
    /// result with an empty DataStoreIds list - identity operations are datastore-independent, so the
    /// binding step never inspects it.
    /// </summary>
    protected override IConfigurationServiceApplicationProvider? ApplicationContextConfigurationProviderOverride =>
        new RecordingConfigurationServiceApplicationProvider(
            (_, _) =>
                new ApplicationContextResult.Success(
                    new ApplicationContext(
                        Id: 1,
                        ApplicationId: 1,
                        ClientId: ExternalDoublesConstants.SmokeClientId,
                        ClientUuid: ExternalDoublesConstants.StableClientUuid,
                        DataStoreIds: [],
                        CreatorOwnershipTokenId: null,
                        OwnershipTokenIds: []
                    )
                )
        );

    [Test]
    public async Task It_serves_an_identity_request_after_the_datastore_surface_is_poisoned() =>
        await IdentityDatastoreIndependenceScenario.It_serves_an_identity_request_after_the_datastore_surface_is_poisoned(
            Harness,
            Tenant,
            _dataStoreProvider,
            _timeProvider
        );
}
