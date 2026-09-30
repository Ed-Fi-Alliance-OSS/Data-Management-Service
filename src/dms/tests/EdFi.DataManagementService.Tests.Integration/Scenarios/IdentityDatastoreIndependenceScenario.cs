// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Real-HTTP-pipeline coverage for request-time datastore independence of the identity pipeline on
/// an initialized host. Hard-coded for the ProfileRootOnlyMerge fixture's Student shape, matching
/// <see cref="CrudRoundTripScenario" /> and <see cref="ApplicationContextIntegrationScenario" />.
/// </summary>
internal static class IdentityDatastoreIndependenceScenario
{
    private const string StudentsEndpointFormat = "/{0}/data/ed-fi/students";
    private const string IdentityGetByIdEndpointFormat = "/{0}/identity/v2/identities/605943412";

    public static async Task It_serves_an_identity_request_after_the_datastore_surface_is_poisoned(
        ApiIntegrationHarness harness,
        string tenant,
        PoisonableRecordingDataStoreProvider dataStoreProvider,
        FakeTimeProvider timeProvider
    )
    {
        // One resource request through the fully initialized host, proving the leased data store is
        // reachable before anything is poisoned.
        var payload = new JsonObject { ["studentUniqueId"] = "identity-ds-indep-001", ["firstName"] = "Ada" };
        using var createContent = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage warmupResponse = await harness.HttpClient.PostAsync(
            string.Format(StudentsEndpointFormat, tenant),
            createContent
        );
        string warmupBody = await warmupResponse.Content.ReadAsStringAsync();
        warmupResponse.StatusCode.Should().Be(HttpStatusCode.Created, warmupBody);

        // One identity request fills the identity tenant snapshot, so the clock advance below
        // genuinely expires it.
        await AssertOperationNotSupportedAsync(harness, tenant);

        int loadDataStoresCallCountBeforeTheIdentityRequest = dataStoreProvider.LoadDataStoresCallCount;
        int loadTenantsCallCountBeforeTheIdentityRequest = dataStoreProvider.LoadTenantsCallCount;

        // Poison the datastore configuration surface, then push the identity tenant snapshot's
        // 60-second freshness window into the past so the next identity request must refresh it.
        dataStoreProvider.PoisonLoadDataStores();
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        await AssertOperationNotSupportedAsync(harness, tenant);

        // The expired snapshot was refreshed through LoadTenants during the poisoned request.
        dataStoreProvider
            .LoadTenantsCallCount.Should()
            .Be(
                loadTenantsCallCountBeforeTheIdentityRequest + 1,
                "the expired identity tenant snapshot must refresh through LoadTenants"
            );

        // The poisoned LoadDataStores was never reached by the identity request: the identity pipeline
        // maps no ResolveDataStoreMiddleware step and resolves tenant existence through LoadTenants
        // alone.
        dataStoreProvider
            .LoadDataStoresCallCount.Should()
            .Be(
                loadDataStoresCallCountBeforeTheIdentityRequest,
                "the identity pipeline must never call LoadDataStores"
            );
    }

    private static async Task AssertOperationNotSupportedAsync(ApiIntegrationHarness harness, string tenant)
    {
        using HttpResponseMessage identityResponse = await harness.HttpClient.GetAsync(
            string.Format(IdentityGetByIdEndpointFormat, tenant)
        );
        string identityBody = await identityResponse.Content.ReadAsStringAsync();

        identityResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, identityBody);

        JsonNode identityJson = JsonNode.Parse(identityBody)!;
        identityJson["type"]!
            .GetValue<string>()
            .Should()
            .Be(IdentityFailureResponse.OperationNotSupportedType);
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
/// separation between tenant-name lookup and datastore configuration.
/// <see cref="LoadDataStoresCallCount" /> lets a scenario prove a request never called it, and
/// <see cref="LoadTenantsCallCount" /> lets it prove a request did refresh the tenant snapshot.
/// </summary>
internal sealed class PoisonableRecordingDataStoreProvider(string tenant) : IDataStoreProvider
{
    private DataStore? _dataStore;
    private volatile bool _shouldThrowOnLoadDataStores;
    private int _loadDataStoresCallCount;
    private int _loadTenantsCallCount;

    /// <summary>How many times LoadDataStores has been called, across the lifetime of this instance.</summary>
    public int LoadDataStoresCallCount => Volatile.Read(ref _loadDataStoresCallCount);

    /// <summary>How many times LoadTenants has been called, across the lifetime of this instance.</summary>
    public int LoadTenantsCallCount => Volatile.Read(ref _loadTenantsCallCount);

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

    public Task<IList<string>> LoadTenants(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loadTenantsCallCount);
        return Task.FromResult<IList<string>>([tenant]);
    }

    public bool TenantExists(string tenant) => true;

    public IReadOnlyList<string> GetLoadedTenantKeys() => [tenant];
}
