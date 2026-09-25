// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Proves A10: request-time datastore independence on an initialized, identity-enabled host. After one
/// resource request has served normally, the CMS data-store surface is poisoned (<c>LoadDataStores</c>
/// throws, connection-string decryption throws) and the identity tenant snapshot's 60-second freshness
/// window is advanced past expiry, yet an authorized identity request still reaches the
/// operation-unsupported capability gate - because the identity pipeline never resolves a physical data
/// store at all (design.md D9; <see cref="Identity.IdentityTenantSnapshotTests" /> A9 proves the same
/// independence at the unit level) - while the poisoned <c>LoadDataStores</c> is never called during
/// that request. Follows <see cref="Given_Postgresql_ApplicationContextIntegration" />'s
/// MultiTenancy/BypassAuthorization shape.
/// </summary>
public sealed class Given_Postgresql_IdentityDatastoreIndependence : PostgresqlApiIntegrationTestBase
{
    private const string Tenant = "identity-datastore-independence-tenant";

    private readonly PoisonableRecordingDataStoreProvider _dataStoreProvider = new(Tenant);
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.UtcNow);
    private readonly IConnectionStringDecryptionService _connectionStringDecryptionService =
        CreateThrowingConnectionStringDecryptionService();

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

    protected override IConnectionStringDecryptionService ConnectionStringDecryptionServiceOverride =>
        _connectionStringDecryptionService;

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
            _timeProvider,
            _connectionStringDecryptionService
        );

    private static IConnectionStringDecryptionService CreateThrowingConnectionStringDecryptionService()
    {
        var fake = A.Fake<IConnectionStringDecryptionService>();
        A.CallTo(() => fake.DecryptFromBase64(A<string?>._))
            .Throws(() =>
                new InvalidOperationException(
                    "IConnectionStringDecryptionService was poisoned for this test and must never be called "
                        + "by the identity pipeline."
                )
            );
        return fake;
    }
}

/// <summary>
/// Wraps <see cref="AllowAllClaimSetProvider" />'s CRUD-on-every-fixture-resource claim set and adds the
/// CMS-seeded identity service claim (<c>http://ed-fi.org/identity/claims/services/identity</c>),
/// granting <c>Create</c> and <c>Read</c> under <c>NoFurtherAuthorizationRequired</c>, under the same
/// claim set name the default fake JWT issues (<see cref="ExternalDoublesConstants.SmokeClaimSetName" />)
/// so one authorized client can serve both the warmup resource request and the identity request.
/// </summary>
internal sealed class IdentityGrantingClaimSetProvider(FixtureContext fixture) : IClaimSetProvider
{
    private readonly AllowAllClaimSetProvider _inner = new(fixture);

    public async Task<IList<ClaimSet>> GetAllClaimSets(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        IList<ClaimSet> claimSets = await _inner.GetAllClaimSets(tenant, cancellationToken);

        return
        [
            .. claimSets.Select(claimSet =>
                claimSet with
                {
                    ResourceClaims =
                    [
                        .. claimSet.ResourceClaims,
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                            "Create",
                            [
                                new AuthorizationStrategy(
                                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                ),
                            ]
                        ),
                        new ResourceClaim(
                            $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                            "Read",
                            [
                                new AuthorizationStrategy(
                                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                ),
                            ]
                        ),
                    ],
                }
            ),
        ];
    }
}

/// <summary>
/// An <see cref="IDataStoreProvider" /> serving one configured data store normally until
/// <see cref="PoisonLoadDataStores" /> is called, after which <see cref="LoadDataStores" /> always
/// throws. <see cref="LoadTenants" /> always succeeds regardless of poisoning, matching the design's
/// separation between tenant-name lookup and datastore configuration (design.md D4, Finding 7).
/// <see cref="LoadDataStoresCallCount" /> lets a scenario prove a request never called it.
/// </summary>
internal sealed class PoisonableRecordingDataStoreProvider(string tenant) : IDataStoreProvider
{
    private DataStore? _dataStore;
    private volatile bool _shouldThrowOnLoadDataStores;
    private int _loadDataStoresCallCount;

    /// <summary>How many times LoadDataStores has been called, across the lifetime of this instance.</summary>
    public int LoadDataStoresCallCount => Volatile.Read(ref _loadDataStoresCallCount);

    public void Configure(long id, string connectionString) =>
        _dataStore = new DataStore(
            id,
            "default",
            "identity-datastore-independence",
            connectionString,
            new Dictionary<RouteQualifierName, RouteQualifierValue>()
        );

    /// <summary>Makes every later call to LoadDataStores throw.</summary>
    public void PoisonLoadDataStores() => _shouldThrowOnLoadDataStores = true;

    public Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _loadDataStoresCallCount);

        if (_shouldThrowOnLoadDataStores)
        {
            throw new InvalidOperationException(
                "PoisonableRecordingDataStoreProvider.LoadDataStores was poisoned for this test and must "
                    + "never be called by the identity pipeline."
            );
        }

        return Task.FromResult<IList<DataStore>>(_dataStore is null ? [] : [_dataStore]);
    }

    public Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) => _dataStore is null ? [] : [_dataStore];

    public DataStore? GetById(long id, string? tenant = null) =>
        _dataStore is { } dataStore && dataStore.Id == id ? dataStore : null;

    public bool IsLoaded(string? tenant = null) => _dataStore is not null;

    public Task<IList<string>> LoadTenants(CancellationToken cancellationToken = default) =>
        Task.FromResult<IList<string>>([tenant]);

    public bool TenantExists(string tenant) => true;

    public IReadOnlyList<string> GetLoadedTenantKeys() => [tenant];
}
