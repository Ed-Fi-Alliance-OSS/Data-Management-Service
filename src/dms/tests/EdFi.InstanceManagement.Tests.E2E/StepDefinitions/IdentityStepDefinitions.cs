// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.Models;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// Real-CMS identity lifecycle, authorization, and revocation step definitions (DMS-1515 Task 16).
/// These steps drive the identity surface (D9/D10) through Configuration Service and DMS instances
/// started with the toggle on, proving A20 and the E2E half of C1/C9 against a real CMS rather than
/// a fake provider double.
/// </summary>
[Binding]
public class IdentityStepDefinitions(InstanceManagementContext context)
{
    private string? _applicationTenant;
    private int? _applicationId;
    private readonly Dictionary<string, (int Id, string Key, string Secret)> _clientsByRole = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, string> _tokensByRole = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _scenarioOwnedClaimSetIds = [];

    /// <summary>
    /// Deletes any dedicated claim set this scenario imported for a revocation proof. Runs before
    /// <see cref="Hooks.InstanceManagementCleanupHooks.CleanupInstanceResources"/> (Order 1000), which
    /// resets <see cref="InstanceManagementContext"/> including <c>ConfigToken</c>, so the token this
    /// hook needs is still live.
    /// </summary>
    [AfterScenario(Order = 500)]
    public async Task CleanupScenarioOwnedClaimSetsAsync()
    {
        if (_scenarioOwnedClaimSetIds.Count == 0 || string.IsNullOrEmpty(context.ConfigToken))
        {
            return;
        }

        // Claim sets are tenant-scoped in CMS, so the delete call needs the same Tenant header the
        // import call used.
        var client = new ConfigServiceClient(
            TestConfiguration.ConfigServiceUrl,
            context.ConfigToken,
            _applicationTenant
        );
        foreach (var claimSetId in _scenarioOwnedClaimSetIds)
        {
            try
            {
                await client.DeleteClaimSetAsync(claimSetId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete scenario-owned claim set {claimSetId}: {ex.Message}");
            }
        }
    }

    [Given("tenant {string} has an identity-only application with claim set {string}")]
    public async Task GivenTenantHasIdentityOnlyApplicationWithClaimSet(
        string tenantName,
        string claimSetName
    )
    {
        await CreateIdentityOnlyApplicationAsync(tenantName, claimSetName);
    }

    [Given(
        "tenant {string} has an identity-only application with claim set {string} granting identity actions {string}"
    )]
    public async Task GivenTenantHasIdentityOnlyApplicationWithDedicatedClaimSet(
        string tenantName,
        string claimSetName,
        string actionsCsv
    )
    {
        string[] actions = actionsCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        var client = GetTenantConfigClient(tenantName);
        int claimSetId = await client.ImportIdentityOnlyClaimSetAsync(claimSetName, actions);
        _scenarioOwnedClaimSetIds.Add(claimSetId);

        await CreateIdentityOnlyApplicationAsync(tenantName, claimSetName);
    }

    [When("the identity claim is removed from claim set {string}")]
    public async Task WhenTheIdentityClaimIsRemovedFromClaimSet(string claimSetName)
    {
        _applicationTenant.Should().NotBeNull("an identity-only application must be created first");

        var client = GetTenantConfigClient(_applicationTenant!);
        // No actions => an empty resourceClaims array, which replaces the claim set's grants with
        // nothing (the import endpoint's own upsert-by-name contract), simulating an administrator
        // removing the identity claim from a claim set through CMS.
        await client.ImportIdentityOnlyClaimSetAsync(claimSetName);
    }

    [When("claim sets are reloaded for tenant {string}")]
    public async Task WhenClaimSetsAreReloadedForTenant(string tenantName)
    {
        string managementToken = await GetManagementTokenAsync();
        using var dmsClient = new DmsApiClient(TestConfiguration.DmsApiUrl, managementToken);

        var response = await dmsClient.PostReloadClaimsetsAsync(tenantName);
        response.EnsureSuccessStatusCode();
    }

    [When("{int} seconds elapse for the claim set cache to expire")]
    public async Task WhenSecondsElapseForTheClaimSetCacheToExpire(int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }

    [Given("a second API client is added to the application with an empty Data Store assignment")]
    [When("a second API client is added to the application with an empty Data Store assignment")]
    public async Task WhenASecondApiClientIsAddedWithEmptyDataStoreAssignment()
    {
        _applicationId.Should().NotBeNull("an identity-only application must be created first");
        _applicationTenant.Should().NotBeNull();

        var client = GetTenantConfigClient(_applicationTenant!);
        (ApiClientCredentialsResponse credentials, _) = await client.CreateApiClientAsync(
            new ApiClientRequest(_applicationId!.Value, "Second Client", true, [])
        );

        _clientsByRole["second"] = (credentials.Id, credentials.Key, credentials.Secret);
    }

    [Then("the {string} client should have an empty Data Store assignment")]
    public async Task ThenTheClientShouldHaveAnEmptyDataStoreAssignment(string role)
    {
        (int id, _, _) = ResolveClient(role);
        var client = GetTenantConfigClient(_applicationTenant!);

        var apiClient = await client.GetApiClientAsync(id.ToString(CultureInfo.InvariantCulture));

        apiClient
            .DataStoreIds.Should()
            .BeEmpty($"the {role} client must keep an empty Data Store assignment");
    }

    [When("the {string} client is updated retaining its empty Data Store assignment")]
    public async Task WhenTheClientIsUpdatedRetainingItsEmptyDataStoreAssignment(string role)
    {
        (int id, string key, string secret) = ResolveClient(role);
        var client = GetTenantConfigClient(_applicationTenant!);

        var current = await client.GetApiClientAsync(id.ToString(CultureInfo.InvariantCulture));

        await client.UpdateApiClientAsync(
            id,
            new ApiClientRequest(current.ApplicationId, current.Name, current.IsApproved, [])
        );

        _clientsByRole[role] = (id, key, secret);
    }

    [When("the {string} client's credentials are reset")]
    public async Task WhenTheClientsCredentialsAreReset(string role)
    {
        (int id, string key, _) = ResolveClient(role);
        var client = GetTenantConfigClient(_applicationTenant!);

        var reset = await client.ResetApiClientCredentialsAsync(id);

        reset.Key.Should().Be(key, "reset-credential must not change the OAuth client key");
        _clientsByRole[role] = (id, reset.Key, reset.Secret);
    }

    [When("the {string} client is deleted")]
    public async Task WhenTheClientIsDeleted(string role)
    {
        (int id, _, _) = ResolveClient(role);
        var client = GetTenantConfigClient(_applicationTenant!);

        await client.DeleteApiClientAsync(id);
    }

    [Given("a token is minted with the {string} client's current credentials")]
    [When("a token is minted with the {string} client's current credentials")]
    public async Task WhenATokenIsMintedWithTheClientsCurrentCredentials(string role)
    {
        (_, string key, string secret) = ResolveClient(role);

        string token = await TokenHelper.GetDmsTokenAsync(
            $"{TestConfiguration.ConfigServiceUrl}/connect/token",
            key,
            secret
        );

        _tokensByRole[role] = token;
        // The generic "identity route" steps (used for cross-tenant and single-client scenarios)
        // read the shared context token rather than a role, so the most recently minted token wins.
        context.DmsToken = token;
    }

    [Given(
        "every identity operation for tenant {string} instance {string} using the {string} client's token responds with {int} and problem type {string}"
    )]
    [Then(
        "every identity operation for tenant {string} instance {string} using the {string} client's token responds with {int} and problem type {string}"
    )]
    public async Task ThenEveryIdentityOperationRespondsWithAndProblemType(
        string tenantName,
        string instanceRoute,
        string role,
        int expectedStatus,
        string expectedType
    ) =>
        await AssertEveryIdentityOperationAsync(
            tenantName,
            instanceRoute,
            ResolveToken(role),
            expectedStatus,
            expectedType
        );

    [Then(
        "every identity operation for tenant {string} instance {string} using the {string} client's token responds with {int}"
    )]
    public async Task ThenEveryIdentityOperationRespondsWith(
        string tenantName,
        string instanceRoute,
        string role,
        int expectedStatus
    ) =>
        await AssertEveryIdentityOperationAsync(
            tenantName,
            instanceRoute,
            ResolveToken(role),
            expectedStatus,
            expectedType: null
        );

    [Then(
        "a GET request for resource {string} at tenant {string} instance {string} using the {string} client's token responds with {int}"
    )]
    public async Task ThenAGetRequestForResourceRespondsWith(
        string resource,
        string tenantName,
        string instanceRoute,
        string role,
        int expectedStatus
    )
    {
        string token = ResolveToken(role);
        (string districtId, string schoolYear) = SplitInstanceRoute(instanceRoute);

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, token, tenantName);
        var response = await client.GetResourceAsync(districtId, schoolYear, resource);

        ((int)response.StatusCode).Should().Be(expectedStatus);
    }

    [When("a GET request is made to identity route {string}")]
    public async Task WhenAGetRequestIsMadeToIdentityRoute(string route)
    {
        context.DmsToken.Should().NotBeNullOrEmpty("must be authenticated to DMS first");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken!);
        context.LastResponse = await client.GetByLocationAsync($"/{route}");
    }

    [When("a POST request is made to identity route {string} with body:")]
    public async Task WhenAPostRequestIsMadeToIdentityRouteWithBody(string route, string jsonBody)
    {
        context.DmsToken.Should().NotBeNullOrEmpty("must be authenticated to DMS first");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken!);
        var body = JsonSerializer.Deserialize<JsonElement>(jsonBody);
        context.LastResponse = await client.PostByLocationAsync($"/{route}", body);
    }

    [When("a GET request is made to identity swagger for tenant {string} instance {string}")]
    public async Task WhenAGetRequestIsMadeToIdentitySwaggerForTenantInstance(
        string tenantName,
        string instanceRoute
    )
    {
        (string districtId, string schoolYear) = SplitInstanceRoute(instanceRoute);
        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        context.LastResponse = await client.GetIdentitySwaggerAsync(tenantName, districtId, schoolYear);
    }

    [When("a GET request is made to metadata specifications for tenant {string} instance {string}")]
    public async Task WhenAGetRequestIsMadeToMetadataSpecificationsForTenantInstance(
        string tenantName,
        string instanceRoute
    )
    {
        (string districtId, string schoolYear) = SplitInstanceRoute(instanceRoute);
        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        context.LastResponse = await client.GetMetadataSpecificationsAsync(
            tenantName,
            districtId,
            schoolYear
        );
    }

    [Then("the discovery response should list an identity URL ending with {string}")]
    public async Task ThenTheDiscoveryResponseShouldListAnIdentityUrlEndingWith(string suffix)
    {
        context.LastResponse.Should().NotBeNull();
        string body = await context.LastResponse!.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        document
            .RootElement.GetProperty("urls")
            .TryGetProperty("identity", out var identityUrl)
            .Should()
            .BeTrue($"expected 'urls.identity' in: {body}");

        identityUrl.GetString().Should().EndWith(suffix);
    }

    [Then("the metadata specifications response lists {string} under {string}")]
    public async Task ThenTheMetadataSpecificationsResponseListsUnder(string name, string prefix)
    {
        context.LastResponse.Should().NotBeNull();
        string body = await context.LastResponse!.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        bool matches = document
            .RootElement.EnumerateArray()
            .Any(entry =>
                entry.GetProperty("name").GetString() == name
                && entry.GetProperty("prefix").GetString() == prefix
            );

        matches.Should().BeTrue($"expected an entry named '{name}' listed under '{prefix}' in: {body}");
    }

    [Then("the swagger document's first server URL ends with {string}")]
    public async Task ThenTheSwaggerDocumentsFirstServerUrlEndsWith(string suffix)
    {
        context.LastResponse.Should().NotBeNull();
        string body = await context.LastResponse!.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        string? firstServerUrl = document
            .RootElement.GetProperty("servers")[0]
            .GetProperty("url")
            .GetString();

        firstServerUrl.Should().NotBeNullOrEmpty();
        firstServerUrl.Should().EndWith(suffix);
    }

    [Then("the response body {string} should be {string}")]
    public async Task ThenTheResponseBodyPropertyShouldBe(string propertyName, string expectedValue)
    {
        context.LastResponse.Should().NotBeNull();
        string body = await context.LastResponse!.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        document
            .RootElement.GetProperty(propertyName)
            .GetString()
            .Should()
            .Be(expectedValue, $"response body was: {body}");
    }

    [Then("the response header {string} should be {string}")]
    public void ThenTheResponseHeaderShouldBe(string headerName, string expectedValue)
    {
        context.LastResponse.Should().NotBeNull();
        context
            .LastResponse!.Headers.TryGetValues(headerName, out var values)
            .Should()
            .BeTrue($"expected header '{headerName}' to be present");

        values!.Should().ContainSingle().Which.Should().Be(expectedValue);
    }

    private async Task CreateIdentityOnlyApplicationAsync(string tenantName, string claimSetName)
    {
        context
            .VendorIdsByTenant.Should()
            .ContainKey(tenantName, $"a vendor for tenant {tenantName} must be hydrated first");

        int vendorId = context.VendorIdsByTenant[tenantName];
        string districtId = tenantName.Split('_')[^1];

        var client = GetTenantConfigClient(tenantName);
        var application = await client.CreateApplicationAsync(
            new ApplicationRequest(
                VendorId: vendorId,
                // The Application's first API client row reuses this name verbatim in a column that
                // is only 50 characters wide (dmscs.ApiClient.Name), so a long claim set name is
                // truncated here rather than risking an unhandled 500 from the database.
                ApplicationName: $"Identity E2E {tenantName} {ShortClaimSetTag(claimSetName)}",
                ClaimSetName: claimSetName,
                EducationOrganizationIds: [int.Parse(districtId, CultureInfo.InvariantCulture)],
                DataStoreIds: []
            )
        );

        _applicationTenant = tenantName;
        _applicationId = application.Id;
        _clientsByRole.Clear();
        _tokensByRole.Clear();
        context.MarkApplicationScenarioOwned(tenantName, application.Id);

        var initialClient = await client.GetApiClientAsync(application.Key);
        _clientsByRole["initial"] = (initialClient.Id, application.Key, application.Secret);
    }

    private static async Task AssertEveryIdentityOperationAsync(
        string tenantName,
        string instanceRoute,
        string token,
        int expectedStatus,
        string? expectedType
    )
    {
        (string districtId, string schoolYear) = SplitInstanceRoute(instanceRoute);
        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, token);

        await AssertIdentityResponseAsync(
            await client.PostIdentityCreateAsync(tenantName, districtId, schoolYear, new { }),
            "create",
            expectedStatus,
            expectedType
        );
        await AssertIdentityResponseAsync(
            await client.GetIdentityByIdAsync(tenantName, districtId, schoolYear, "605943412"),
            "get-by-id",
            expectedStatus,
            expectedType
        );
        await AssertIdentityResponseAsync(
            await client.PostIdentityFindAsync(tenantName, districtId, schoolYear, Array.Empty<string>()),
            "find",
            expectedStatus,
            expectedType
        );
        await AssertIdentityResponseAsync(
            await client.PostIdentitySearchAsync(tenantName, districtId, schoolYear, Array.Empty<object>()),
            "search",
            expectedStatus,
            expectedType
        );
        await AssertIdentityResponseAsync(
            await client.GetIdentityResultsAsync(tenantName, districtId, schoolYear, "SAMPLE-REQUEST-TOKEN"),
            "results",
            expectedStatus,
            expectedType
        );
    }

    private static async Task AssertIdentityResponseAsync(
        HttpResponseMessage response,
        string operation,
        int expectedStatus,
        string? expectedType
    )
    {
        string body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(expectedStatus, $"identity {operation} response was: {body}");

        if (expectedType is not null)
        {
            using var document = JsonDocument.Parse(body);
            document
                .RootElement.GetProperty("type")
                .GetString()
                .Should()
                .Be(expectedType, $"identity {operation} response was: {body}");
        }
    }

    private (int Id, string Key, string Secret) ResolveClient(string role)
    {
        _clientsByRole.Should().ContainKey(role, $"the {role} client must be created first");
        return _clientsByRole[role];
    }

    private string ResolveToken(string role)
    {
        _tokensByRole.Should().ContainKey(role, $"a token must be minted for the {role} client first");
        return _tokensByRole[role];
    }

    private static (string DistrictId, string SchoolYear) SplitInstanceRoute(string instanceRoute)
    {
        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "instance route must be in format districtId/schoolYear");
        return (parts[0], parts[1]);
    }

    private static string ShortClaimSetTag(string claimSetName) =>
        claimSetName.Length <= 15 ? claimSetName : claimSetName[..15];

    private ConfigServiceClient GetTenantConfigClient(string tenantName)
    {
        if (context.ConfigClientsByTenant.TryGetValue(tenantName, out var existing))
        {
            return existing;
        }

        context
            .ConfigToken.Should()
            .NotBeNullOrEmpty("the Configuration Service system admin token must be established first");

        var client = new ConfigServiceClient(
            TestConfiguration.ConfigServiceUrl,
            context.ConfigToken!,
            tenantName
        );
        context.ConfigClientsByTenant[tenantName] = client;
        return client;
    }

    private static async Task<string> GetManagementTokenAsync() =>
        await TokenHelper.GetConfigServiceTokenAsync(
            $"{TestConfiguration.ConfigServiceUrl}/connect/token",
            "DmsConfigurationService",
            "ValidClientSecret1234567890!Abcd"
        );
}
