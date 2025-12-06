// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Configuration;
using EdFi.InstanceManagement.Tests.E2E.Infrastructure;
using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.Models;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class InstanceSetupStepDefinitions(
    InstanceManagementContext context,
    InstanceInfrastructureManager infrastructureManager
)
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
            "s3creT@09"
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
                "s3creT@09"
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
            InstanceType: data["InstanceType"],
            InstanceName: data["InstanceName"],
            ConnectionString: data["ConnectionString"]
        );

        _lastCreatedInstance = await _configClient!.CreateInstanceAsync(request);
        context.InstanceIds.Add(_lastCreatedInstance.Id);

        // Track instance by tenant if working with explicit tenant
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            context.InstanceIdToTenant[_lastCreatedInstance.Id] = context.CurrentTenant;
        }
    }

    [When("I add route context {string} with value {string} to the instance")]
    public async Task WhenIAddRouteContextWithValueToTheInstance(string contextKey, string contextValue)
    {
        _lastCreatedInstance.Should().NotBeNull("An instance must be created before adding route context");

        var request = new RouteContextRequest(
            InstanceId: _lastCreatedInstance!.Id,
            ContextKey: contextKey,
            ContextValue: contextValue
        );

        await _configClient!.CreateRouteContextAsync(request);
    }

    [Then("the instance should be created successfully")]
    public void ThenTheInstanceShouldBeCreatedSuccessfully()
    {
        _lastCreatedInstance.Should().NotBeNull();
        context.InstanceIds.Should().Contain(_lastCreatedInstance!.Id);
    }

    [Then("{int} instances should be created")]
    public void ThenInstancesShouldBeCreated(int expectedCount)
    {
        // Count only instances for current tenant if one is set
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            var tenantInstanceCount = context.InstanceIdToTenant.Count(kvp =>
                kvp.Value == context.CurrentTenant
            );
            tenantInstanceCount.Should().Be(expectedCount);
        }
        else
        {
            context.InstanceIds.Should().HaveCount(expectedCount);
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
                "s3creT@09"
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
        List<int> instanceIds;
        if (!string.IsNullOrEmpty(context.CurrentTenant))
        {
            instanceIds = context
                .InstanceIdToTenant.Where(kvp => kvp.Value == context.CurrentTenant)
                .Select(kvp => kvp.Key)
                .ToList();
        }
        else
        {
            instanceIds = context.InstanceIds;
        }

        instanceIds.Should().NotBeEmpty("Instances must exist before creating application");

        var request = new ApplicationRequest(
            VendorId: context.VendorId!.Value,
            ApplicationName: data["ApplicationName"],
            ClaimSetName: data["ClaimSetName"],
            EducationOrganizationIds: edOrgIds,
            DmsInstanceIds: [.. instanceIds]
        );

        var application = await _configClient!.CreateApplicationAsync(request);
        context.ApplicationId = application.Id;
        context.ClientKey = application.Key;
        context.ClientSecret = application.Secret;

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
        // Store the infrastructure manager in context for cleanup
        context.InfrastructureManager = infrastructureManager;

        // Get or create the tenant client
        var tenantClient = await GetOrCreateTenantClientAsync(tenantName);

        // Set the current tenant and update _configClient for subsequent steps
        context.CurrentTenant = tenantName;
        _configClient = tenantClient;

        // Create vendor for this tenant if not exists
        if (!context.VendorIdsByTenant.ContainsKey(tenantName))
        {
            var districtId = tenantName.Replace("Tenant_", "");
            var vendorRequest = new VendorRequest(
                Company: $"District {districtId} Vendor",
                ContactName: "Test Admin",
                ContactEmailAddress: $"admin@district{districtId}.edu",
                NamespacePrefixes: $"uri://ed-fi.org,uri://district{districtId}.edu"
            );

            var (vendor, _) = await tenantClient.CreateVendorAsync(vendorRequest);
            context.VendorIdsByTenant[tenantName] = vendor.Id;
            context.VendorId = vendor.Id;
        }

        // Create instances from the table
        foreach (var row in table.Rows)
        {
            var route = row["Route"];
            var parts = route.Split('/');
            var districtId = parts[0];
            var schoolYear = parts[1];

            var dbIndex = GetDatabaseIndexForRoute(districtId, schoolYear);
            var connectionString = TestConstants.GetConnectionString(dbIndex);
            var databaseName = TestConstants.GetDatabaseName(dbIndex);

            var instance = await tenantClient.CreateInstanceAsync(
                new InstanceRequest(
                    InstanceType: "District",
                    InstanceName: $"District {districtId} - School Year {schoolYear}",
                    ConnectionString: connectionString
                )
            );

            context.InstanceIds.Add(instance.Id);
            context.InstanceIdToDatabaseName[instance.Id] = databaseName;
            context.InstanceIdToTenant[instance.Id] = tenantName;
            context.RouteQualifierToInstanceId[route] = instance.Id;

            // Add route contexts
            await tenantClient.CreateRouteContextAsync(
                new RouteContextRequest(
                    InstanceId: instance.Id,
                    ContextKey: "districtId",
                    ContextValue: districtId
                )
            );
            await tenantClient.CreateRouteContextAsync(
                new RouteContextRequest(
                    InstanceId: instance.Id,
                    ContextKey: "schoolYear",
                    ContextValue: schoolYear
                )
            );

            // Setup Kafka topic and Debezium connector
            await infrastructureManager.SetupInstanceInfrastructureAsync(instance.Id, databaseName);
        }
    }

    [Given("tenant {string} has an application for district {string}")]
    public async Task GivenTenantHasApplicationForDistrict(string tenantName, string districtId)
    {
        var tenantClient = context.ConfigClientsByTenant[tenantName];
        var vendorId = context.VendorIdsByTenant[tenantName];

        // Get instance IDs for this tenant
        var tenantInstanceIds = context
            .InstanceIdToTenant.Where(kvp => kvp.Value == tenantName)
            .Select(kvp => kvp.Key)
            .ToList();

        var edOrgIds = new[] { int.Parse(districtId) };

        var application = await tenantClient.CreateApplicationAsync(
            new ApplicationRequest(
                vendorId,
                $"District {districtId} Test App",
                "E2E-NoFurtherAuthRequiredClaimSet",
                edOrgIds,
                [.. tenantInstanceIds]
            )
        );

        context.ApplicationIdsByTenant[tenantName] = application.Id;
        context.CredentialsByTenant[tenantName] = (application.Key, application.Secret);

        // Store first application's credentials for DMS authentication (legacy support)
        if (context.ClientKey == null)
        {
            context.ApplicationId = application.Id;
            context.ClientKey = application.Key;
            context.ClientSecret = application.Secret;
        }
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
