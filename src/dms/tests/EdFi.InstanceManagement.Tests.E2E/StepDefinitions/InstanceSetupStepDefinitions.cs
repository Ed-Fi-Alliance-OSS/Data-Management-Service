// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Configuration;
using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.Models;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class InstanceSetupStepDefinitions(InstanceManagementContext context)
{
    public ConfigServiceClient? _configClient;
    private InstanceResponse? _lastCreatedInstance;

    [Given("I am authenticated to the Configuration Service as system admin")]
    public async Task GivenIAmAuthenticatedToTheConfigurationServiceAsSystemAdmin()
    {
        var tokenUrl = $"{TestConfiguration.ConfigServiceUrl}/connect/token";
        context.ConfigToken = await TokenHelper.GetConfigServiceTokenAsync(
            tokenUrl,
            "DmsConfigurationService",
            "ValidClientSecret1234567890!Abcd"
        );

        // Create a client for the current tenant (if one is set)
        _configClient = new ConfigServiceClient(
            TestConfiguration.ConfigServiceUrl,
            context.ConfigToken,
            context.CurrentTenant
        );

        // Ensure the tenant exists before any other operations
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            await _configClient.EnsureTenantExistsAsync(context.CurrentTenant);
        }
    }

    [Given("I am working with tenant {string}")]
    public async Task GivenIAmWorkingWithTenant(string tenantName)
    {
        context.CurrentTenant = tenantName;

        // Ensure we have a config token
        if (context.ConfigToken == null)
        {
            var tokenUrl = $"{TestConfiguration.ConfigServiceUrl}/connect/token";
            context.ConfigToken = await TokenHelper.GetConfigServiceTokenAsync(
                tokenUrl,
                "DmsConfigurationService",
                "ValidClientSecret1234567890!Abcd"
            );
        }

        // Ensure the tenant exists
        var systemClient = new ConfigServiceClient(TestConfiguration.ConfigServiceUrl, context.ConfigToken);
        await systemClient.EnsureTenantExistsAsync(tenantName);

        if (!context.TenantNames.Contains(tenantName))
        {
            context.TenantNames.Add(tenantName);
        }

        // Create/update the config client for this tenant
        _configClient = new ConfigServiceClient(
            TestConfiguration.ConfigServiceUrl,
            context.ConfigToken,
            tenantName
        );

        context.ConfigClientsByTenant[tenantName] = _configClient;
    }

    [When("I create a vendor with the following details:")]
    public async Task WhenICreateAVendorWithTheFollowingDetails(Table table)
    {
        var data = ParseKeyValueTable(table);

        var request = new VendorRequest(
            Company: data["Company"],
            ContactName: data["ContactName"],
            ContactEmailAddress: data["ContactEmailAddress"],
            NamespacePrefixes: data["NamespacePrefixes"]
        );

        var (vendor, _) = await _configClient!.CreateVendorAsync(request);
        context.VendorId = vendor.Id;
        context.MarkVendorScenarioOwned(context.CurrentTenant ?? "", vendor.Id);

        // Track vendor by tenant if working with explicit tenant
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            context.VendorIdsByTenant[context.CurrentTenant] = vendor.Id;
        }
    }

    [Then("the vendor should be created successfully")]
    public void ThenTheVendorShouldBeCreatedSuccessfully()
    {
        context.VendorId.Should().NotBeNull();
    }

    [Then("the vendor ID should be stored")]
    public void ThenTheVendorIdShouldBeStored()
    {
        context.VendorId.Should().NotBeNull();
    }

    [Given("a vendor exists")]
    public async Task GivenAVendorExists()
    {
        // Check if vendor exists for current tenant
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            if (context.VendorIdsByTenant.TryGetValue(context.CurrentTenant, out var vendorId))
            {
                context.VendorId = vendorId;
                return;
            }
        }
        else if (context.VendorId != null)
        {
            return;
        }

        var request = new VendorRequest(
            Company: $"Test Vendor for {context.CurrentTenant ?? "default"}",
            ContactName: "Test Admin",
            ContactEmailAddress: "admin@testdistrict.edu",
            NamespacePrefixes: "uri://ed-fi.org,uri://testdistrict.edu"
        );

        var (vendor, _) = await _configClient!.CreateVendorAsync(request);
        context.VendorId = vendor.Id;
        context.MarkVendorScenarioOwned(context.CurrentTenant ?? "", vendor.Id);

        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            context.VendorIdsByTenant[context.CurrentTenant] = vendor.Id;
        }
    }

    [When("I create an instance with the following details:")]
    public async Task WhenICreateAnInstanceWithTheFollowingDetails(Table table)
    {
        var data = ParseKeyValueTable(table);

        var request = new InstanceRequest(
            DataStoreType: data["DataStoreType"],
            Name: data["Name"],
            ConnectionString: ResolveConnectionString(data["ConnectionString"])
        );

        _lastCreatedInstance = await _configClient!.CreateInstanceAsync(request);
        context.DataStoreIds.Add(_lastCreatedInstance.Id);
        context.MarkDataStoreScenarioOwned(context.CurrentTenant ?? "", _lastCreatedInstance.Id);

        // Track instance by tenant if working with explicit tenant
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            context.DataStoreIdToTenant[_lastCreatedInstance.Id] = context.CurrentTenant;
        }
    }

    [When("I add route context {string} with value {string} to the instance")]
    public async Task WhenIAddRouteContextWithValueToTheInstance(string contextKey, string contextValue)
    {
        _lastCreatedInstance.Should().NotBeNull("An instance must be created before adding route context");

        var request = new RouteContextRequest(
            DataStoreId: _lastCreatedInstance!.Id,
            ContextKey: contextKey,
            ContextValue: contextValue
        );

        await _configClient!.CreateRouteContextAsync(request);
    }

    [Then("the instance should be created successfully")]
    public void ThenTheInstanceShouldBeCreatedSuccessfully()
    {
        _lastCreatedInstance.Should().NotBeNull();
        context.DataStoreIds.Should().Contain(_lastCreatedInstance!.Id);
    }

    [Then("{int} instances should be created")]
    public void ThenInstancesShouldBeCreated(int expectedCount)
    {
        // Count only instances for current tenant if one is set
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            var tenantInstanceCount = context.DataStoreIdToTenant.Count(kvp =>
                kvp.Value == context.CurrentTenant
            );
            tenantInstanceCount.Should().Be(expectedCount);
        }
        else
        {
            context.DataStoreIds.Should().HaveCount(expectedCount);
        }
    }

    /// <summary>
    /// Get or create a ConfigServiceClient for the specified tenant
    /// </summary>
    private async Task<ConfigServiceClient> GetOrCreateTenantClientAsync(string tenantName)
    {
        if (context.ConfigClientsByTenant.TryGetValue(tenantName, out var existingClient))
        {
            return existingClient;
        }

        // Ensure we have a config token
        if (context.ConfigToken == null)
        {
            var tokenUrl = $"{TestConfiguration.ConfigServiceUrl}/connect/token";
            context.ConfigToken = await TokenHelper.GetConfigServiceTokenAsync(
                tokenUrl,
                "DmsConfigurationService",
                "ValidClientSecret1234567890!Abcd"
            );
        }

        // Create the tenant
        var systemClient = new ConfigServiceClient(TestConfiguration.ConfigServiceUrl, context.ConfigToken);
        await systemClient.EnsureTenantExistsAsync(tenantName);

        if (!context.TenantNames.Contains(tenantName))
        {
            context.TenantNames.Add(tenantName);
        }

        // Create client for this tenant
        var tenantClient = new ConfigServiceClient(
            TestConfiguration.ConfigServiceUrl,
            context.ConfigToken,
            tenantName
        );

        context.ConfigClientsByTenant[tenantName] = tenantClient;
        return tenantClient;
    }

    [When("I create an application with the following details:")]
    public async Task WhenICreateAnApplicationWithTheFollowingDetails(Table table)
    {
        context.VendorId.Should().NotBeNull("Vendor must exist before creating application");

        var data = ParseKeyValueTable(table);

        var edOrgIds = data["EducationOrganizationIds"].Split(',').Select(int.Parse).ToArray();

        // Get instances for current tenant if one is set, otherwise use all instances
        List<int> dataStoreIds;
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            dataStoreIds = context
                .DataStoreIdToTenant.Where(kvp => kvp.Value == context.CurrentTenant)
                .Select(kvp => kvp.Key)
                .ToList();
        }
        else
        {
            dataStoreIds = context.DataStoreIds;
        }

        dataStoreIds.Should().NotBeEmpty("Data stores must exist before creating application");

        var request = new ApplicationRequest(
            VendorId: context.VendorId!.Value,
            ApplicationName: data["ApplicationName"],
            ClaimSetName: data["ClaimSetName"],
            EducationOrganizationIds: edOrgIds,
            DataStoreIds: [.. dataStoreIds]
        );

        var application = await _configClient!.CreateApplicationAsync(request);
        context.ApplicationId = application.Id;
        context.ClientKey = application.Key;
        context.ClientSecret = application.Secret;
        context.MarkApplicationScenarioOwned(context.CurrentTenant ?? "", application.Id);

        // Track application by tenant if working with explicit tenant
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            context.ApplicationIdsByTenant[context.CurrentTenant] = application.Id;
        }
    }

    [Then("the application should be created successfully")]
    public void ThenTheApplicationShouldBeCreatedSuccessfully()
    {
        context.ApplicationId.Should().NotBeNull();
    }

    [Then("the application credentials should be stored")]
    public void ThenTheApplicationCredentialsShouldBeStored()
    {
        context.ClientKey.Should().NotBeNullOrEmpty();
        context.ClientSecret.Should().NotBeNullOrEmpty();
    }

    [Given("tenant {string} is set up with a vendor and instances:")]
    public async Task GivenTenantIsSetUpWithVendorAndInstances(string tenantName, Table table)
    {
        var requestedRoutes = table.Rows.Select(row => row["Route"]).ToList();

        // Fixture-aware dual mode: for a canonical pre-registered tenant, validate the requested
        // tenant/routes against run state and hydrate context with zero CMS creation.
        if (InstanceFixtureState.IsAvailable && InstanceFixtureState.Current.IsFixtureTenant(tenantName))
        {
            HydrateFixtureTenantSetup(InstanceFixtureState.Current, tenantName, requestedRoutes);
            return;
        }

        await CreateTenantSetupViaCmsAsync(tenantName, requestedRoutes);
    }

    internal void HydrateFixtureTenantSetup(
        InstanceFixtureState fixture,
        string tenantName,
        IReadOnlyList<string> requestedRoutes
    )
    {
        foreach (var route in requestedRoutes)
        {
            if (
                !fixture.TryGetRoute(route, out var fixtureRoute)
                || !string.Equals(fixtureRoute.TenantName, tenantName, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new InvalidOperationException(
                    $"Route '{route}' is not a pre-registered fixture route owned by tenant '{tenantName}'."
                );
            }
        }

        InstanceFixtureHydrator.HydrateAll(context, fixture);
        context.CurrentTenant = tenantName;
    }

    private async Task CreateTenantSetupViaCmsAsync(string tenantName, IReadOnlyList<string> requestedRoutes)
    {
        // Get or create the tenant client
        var tenantClient = await GetOrCreateTenantClientAsync(tenantName);

        // Set the current tenant and update _configClient for subsequent steps
        context.CurrentTenant = tenantName;
        _configClient = tenantClient;

        // Derive a clean vendor identity from the first requested route rather than the (possibly renamed)
        // tenant name so the generated contact email stays valid.
        var vendorDistrictId = requestedRoutes.Count > 0 ? requestedRoutes[0].Split('/')[0] : tenantName;

        // Create vendor for this tenant if not exists
        if (!context.VendorIdsByTenant.ContainsKey(tenantName))
        {
            var vendorRequest = new VendorRequest(
                Company: $"District {vendorDistrictId} Vendor",
                ContactName: "Test Admin",
                ContactEmailAddress: $"admin@district{vendorDistrictId}.edu",
                NamespacePrefixes: $"uri://ed-fi.org,uri://district{vendorDistrictId}.edu"
            );

            var (vendor, _) = await tenantClient.CreateVendorAsync(vendorRequest);
            context.VendorIdsByTenant[tenantName] = vendor.Id;
            context.VendorId = vendor.Id;
            context.MarkVendorScenarioOwned(tenantName, vendor.Id);
        }

        // Create instances from the table
        foreach (var route in requestedRoutes)
        {
            var parts = route.Split('/');
            var districtId = parts[0];
            var schoolYear = parts[1];

            var dbIndex = GetDatabaseIndexForRoute(districtId, schoolYear);
            var connectionString = TestConstants.GetConnectionString(dbIndex);

            var instance = await tenantClient.CreateInstanceAsync(
                new InstanceRequest(
                    DataStoreType: "District",
                    Name: $"District {districtId} - School Year {schoolYear}",
                    ConnectionString: connectionString
                )
            );

            context.DataStoreIds.Add(instance.Id);
            context.DataStoreIdToTenant[instance.Id] = tenantName;
            context.RouteQualifierToDataStoreId[route] = instance.Id;
            context.MarkDataStoreScenarioOwned(tenantName, instance.Id);

            // Add route contexts
            await tenantClient.CreateRouteContextAsync(
                new RouteContextRequest(
                    DataStoreId: instance.Id,
                    ContextKey: "districtId",
                    ContextValue: districtId
                )
            );
            await tenantClient.CreateRouteContextAsync(
                new RouteContextRequest(
                    DataStoreId: instance.Id,
                    ContextKey: "schoolYear",
                    ContextValue: schoolYear
                )
            );
        }
    }

    /// <summary>
    /// Resolves a logical database token (Database1/Database2/Database3) to the engine-correct opaque
    /// connection string published by the fixture; any other value is returned verbatim.
    /// </summary>
    private static string ResolveConnectionString(string connectionStringOrToken) =>
        connectionStringOrToken switch
        {
            "Database1" => TestConstants.GetConnectionString(1),
            "Database2" => TestConstants.GetConnectionString(2),
            "Database3" => TestConstants.GetConnectionString(3),
            _ => connectionStringOrToken,
        };

    [Given("tenant {string} has an application for district {string}")]
    public async Task GivenTenantHasApplicationForDistrict(string tenantName, string districtId)
    {
        // Fixture-aware dual mode: for a canonical pre-registered tenant, validate the district against run
        // state and hydrate the pre-registered application credentials with zero CMS creation.
        if (InstanceFixtureState.IsAvailable && InstanceFixtureState.Current.IsFixtureTenant(tenantName))
        {
            HydrateFixtureApplication(InstanceFixtureState.Current, tenantName, districtId);
            return;
        }

        var tenantClient = await GetOrCreateTenantClientAsync(tenantName);
        var vendorId = context.VendorIdsByTenant[tenantName];

        // Get instance IDs for this tenant
        var tenantDataStoreIds = context
            .DataStoreIdToTenant.Where(kvp => kvp.Value == tenantName)
            .Select(kvp => kvp.Key)
            .ToList();

        var edOrgIds = new[] { int.Parse(districtId) };

        var application = await tenantClient.CreateApplicationAsync(
            new ApplicationRequest(
                vendorId,
                $"District {districtId} Test App",
                "E2E-NoFurtherAuthRequiredClaimSet",
                edOrgIds,
                [.. tenantDataStoreIds]
            )
        );

        context.ApplicationIdsByTenant[tenantName] = application.Id;
        context.CredentialsByTenant[tenantName] = (application.Key, application.Secret);
        context.MarkApplicationScenarioOwned(tenantName, application.Id);

        // Store first application's credentials for DMS authentication (legacy support)
        if (context.ClientKey == null)
        {
            context.ApplicationId = application.Id;
            context.ClientKey = application.Key;
            context.ClientSecret = application.Secret;
        }
    }

    internal void HydrateFixtureApplication(
        InstanceFixtureState fixture,
        string tenantName,
        string districtId
    )
    {
        if (
            !fixture
                .RoutesForTenant(tenantName)
                .Any(r => string.Equals(r.DistrictId, districtId, StringComparison.Ordinal))
        )
        {
            throw new InvalidOperationException(
                $"Fixture tenant '{tenantName}' owns no route for district '{districtId}'."
            );
        }

        InstanceFixtureHydrator.HydrateAll(context, fixture);
        var fixtureTenant = fixture.GetTenant(tenantName);
        context.ApplicationId = fixtureTenant.ApplicationId;
        context.ClientKey = fixtureTenant.ClientKey;
        context.ClientSecret = fixtureTenant.ClientSecret;
    }

    [Given("tenant {string} has an application for district {string} with claim set {string}")]
    [When("tenant {string} has an application for district {string} with claim set {string}")]
    public async Task GivenTenantHasApplicationForDistrictWithClaimSet(
        string tenantName,
        string districtId,
        string claimSetName
    )
    {
        // This overload always creates a real, scenario-owned application (even under a fixture tenant) so
        // authorization coverage stays real. Authenticate lazily at this CMS boundary.
        var tenantClient = await GetOrCreateTenantClientAsync(tenantName);
        var vendorId = context.VendorIdsByTenant[tenantName];

        var tenantDataStoreIds = context
            .DataStoreIdToTenant.Where(kvp => kvp.Value == tenantName)
            .Select(kvp => kvp.Key)
            .ToList();

        var edOrgIds = new[] { int.Parse(districtId) };

        var application = await tenantClient.CreateApplicationAsync(
            new ApplicationRequest(
                vendorId,
                $"District {districtId} {claimSetName} App",
                claimSetName,
                edOrgIds,
                [.. tenantDataStoreIds]
            )
        );

        // Track the replacement application separately from the immutable fixture application so
        // per-scenario cleanup deletes only this scenario-owned application, never the fixture app.
        context.MarkApplicationScenarioOwned(tenantName, application.Id);

        // Overwrite this tenant's stored credentials so a subsequent
        // "authenticated to DMS with credentials for tenant" picks up this claim set.
        context.ApplicationIdsByTenant[tenantName] = application.Id;
        context.CredentialsByTenant[tenantName] = (application.Key, application.Secret);
    }

    /// <summary>
    /// Maps route qualifiers to database index based on known test data configuration.
    /// </summary>
    private static int GetDatabaseIndexForRoute(string districtId, string schoolYear) =>
        (districtId, schoolYear) switch
        {
            ("255901", "2024") => 1,
            ("255901", "2025") => 2,
            ("255902", "2024") => 3,
            _ => throw new ArgumentException($"Unknown route: {districtId}/{schoolYear}"),
        };

    /// <summary>
    /// Parse a Reqnroll table as key-value pairs.
    /// In Reqnroll, a 2-column table without explicit headers treats the first row as header.
    /// This method extracts all rows (including header) as key-value pairs.
    /// </summary>
    private static Dictionary<string, string> ParseKeyValueTable(Table table)
    {
        var headers = table.Header.ToList();
        var data = new Dictionary<string, string> { { headers[0], headers[1] } };
        foreach (var row in table.Rows)
        {
            data[row[0]] = row[1];
        }
        return data;
    }
}
