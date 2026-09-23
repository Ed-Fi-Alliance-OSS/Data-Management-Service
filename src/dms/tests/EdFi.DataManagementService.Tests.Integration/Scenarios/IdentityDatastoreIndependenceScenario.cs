// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Real-HTTP-pipeline coverage for A10: request-time datastore independence of the identity pipeline on
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
        FakeTimeProvider timeProvider,
        IConnectionStringDecryptionService connectionStringDecryptionService
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

        int loadDataStoresCallCountBeforeTheIdentityRequest = dataStoreProvider.LoadDataStoresCallCount;

        // Poison the datastore configuration surface, then push the identity tenant snapshot's
        // 60-second freshness window into the past so the next identity request must refresh it.
        dataStoreProvider.PoisonLoadDataStores();
        timeProvider.Advance(TimeSpan.FromSeconds(61));

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

        // The poisoned LoadDataStores was never reached by the identity request: the identity pipeline
        // maps no ResolveDataStoreMiddleware step and resolves tenant existence through LoadTenants
        // alone (design.md D4, D9).
        dataStoreProvider
            .LoadDataStoresCallCount.Should()
            .Be(
                loadDataStoresCallCountBeforeTheIdentityRequest,
                "the identity pipeline must never call LoadDataStores"
            );
        A.CallTo(connectionStringDecryptionService).MustNotHaveHappened();
    }
}
