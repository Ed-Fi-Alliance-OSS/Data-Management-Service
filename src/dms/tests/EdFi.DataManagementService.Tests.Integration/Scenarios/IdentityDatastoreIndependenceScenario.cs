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

        int loadTenantsCallCountBeforeTheIdentityRequest = dataStoreProvider.LoadTenantsCallCount;

        // Poison the datastore configuration surface, then push the identity tenant snapshot's
        // 60-second freshness window into the past so the next identity request must refresh it.
        dataStoreProvider.PoisonDatastoreSurface();
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        await AssertOperationNotSupportedAsync(harness, tenant);

        // The expired snapshot was refreshed through LoadTenants during the poisoned request.
        dataStoreProvider
            .LoadTenantsCallCount.Should()
            .Be(
                loadTenantsCallCountBeforeTheIdentityRequest + 1,
                "the expired identity tenant snapshot must refresh through LoadTenants"
            );

        // No datastore member was reached by the identity request: the identity pipeline maps no
        // ResolveDataStoreMiddleware step and resolves tenant existence through LoadTenants alone.
        dataStoreProvider
            .PoisonedCallCount.Should()
            .Be(0, "the identity pipeline must never read or report datastore configuration");
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
/// <see cref="PoisonDatastoreSurface" /> is called, after which every member that reads or reports
/// datastore configuration throws and is counted in <see cref="PoisonedCallCount" />.
/// <see cref="LoadTenants" /> always succeeds regardless of poisoning, matching the design's
/// separation between tenant-name lookup and datastore configuration, and
/// <see cref="LoadTenantsCallCount" /> lets a scenario prove a request did refresh the tenant snapshot.
/// </summary>
internal sealed class PoisonableRecordingDataStoreProvider(string tenant) : IDataStoreProvider
{
    private DataStore? _dataStore;
    private volatile bool _poisoned;
    private int _poisonedCallCount;
    private int _loadTenantsCallCount;

    /// <summary>How many datastore members were called after <see cref="PoisonDatastoreSurface" />.</summary>
    public int PoisonedCallCount => Volatile.Read(ref _poisonedCallCount);

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

    /// <summary>Makes every later call to a member other than LoadTenants throw.</summary>
    public void PoisonDatastoreSurface() => _poisoned = true;

    public Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfPoisoned(nameof(LoadDataStores));
        return Task.FromResult<IList<DataStore>>(_dataStore is null ? [] : [_dataStore]);
    }

    public Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfPoisoned(nameof(RefreshInstancesIfExpiredAsync));
        return Task.CompletedTask;
    }

    public IReadOnlyList<DataStore> GetAll(string? tenant = null)
    {
        ThrowIfPoisoned(nameof(GetAll));
        return _dataStore is null ? [] : [_dataStore];
    }

    public DataStore? GetById(long id, string? tenant = null)
    {
        ThrowIfPoisoned(nameof(GetById));
        return _dataStore is { } dataStore && dataStore.Id == id ? dataStore : null;
    }

    public bool IsLoaded(string? tenant = null)
    {
        ThrowIfPoisoned(nameof(IsLoaded));
        return _dataStore is not null;
    }

    public Task<IList<string>> LoadTenants(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loadTenantsCallCount);
        return Task.FromResult<IList<string>>([tenant]);
    }

    public bool TenantExists(string tenant)
    {
        ThrowIfPoisoned(nameof(TenantExists));
        return true;
    }

    public IReadOnlyList<string> GetLoadedTenantKeys()
    {
        ThrowIfPoisoned(nameof(GetLoadedTenantKeys));
        return [tenant];
    }

    /// <summary>
    /// Counts and throws once poisoned. The count still catches a caller that swallows the exception.
    /// </summary>
    private void ThrowIfPoisoned(string memberName)
    {
        if (!_poisoned)
        {
            return;
        }

        Interlocked.Increment(ref _poisonedCallCount);
        throw new InvalidOperationException(
            $"PoisonableRecordingDataStoreProvider.{memberName} was poisoned for this test and must never be "
                + "called by the identity pipeline."
        );
    }
}
