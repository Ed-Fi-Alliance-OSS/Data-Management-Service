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

        _configClient = new ConfigServiceClient(
            TestConfiguration.ConfigServiceUrl,
            context.ConfigToken,
            TestConfiguration.TenantName
        );

        // Ensure the tenant exists before any other operations
        if (!string.IsNullOrEmpty(TestConfiguration.TenantName))
        {
            await _configClient.EnsureTenantExistsAsync(TestConfiguration.TenantName);
        }
    }

    [When("I create a vendor with the following details:")]
    public async Task WhenICreateAVendorWithTheFollowingDetails(Table table)
    {
        // Convert vertical table (key-value pairs) to dictionary
        // Reqnroll treats first row as header for tables without explicit headers
        var headers = table.Header.ToList();
        var data = new Dictionary<string, string> { { headers[0], headers[1] } };
        foreach (var row in table.Rows)
        {
            data[row[0]] = row[1];
        }

        var request = new VendorRequest(
            Company: data["Company"],
            ContactName: data["ContactName"],
            ContactEmailAddress: data["ContactEmailAddress"],
            NamespacePrefixes: data["NamespacePrefixes"]
        );

        var (vendor, _) = await _configClient!.CreateVendorAsync(request);
        context.VendorId = vendor.Id;
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
        if (context.VendorId == null)
        {
            var request = new VendorRequest(
                Company: "Multi-District Test Vendor",
                ContactName: "Test Admin",
                ContactEmailAddress: "admin@testdistrict.edu",
                NamespacePrefixes: "uri://ed-fi.org,uri://testdistrict.edu"
            );

            var (vendor, _) = await _configClient!.CreateVendorAsync(request);
            context.VendorId = vendor.Id;
        }
    }

    [When("I create an instance with the following details:")]
    public async Task WhenICreateAnInstanceWithTheFollowingDetails(Table table)
    {
        // Convert vertical table (key-value pairs) to dictionary
        // Reqnroll treats first row as header for tables without explicit headers
        var headers = table.Header.ToList();
        var data = new Dictionary<string, string> { { headers[0], headers[1] } };
        foreach (var row in table.Rows)
        {
            data[row[0]] = row[1];
        }

        var request = new InstanceRequest(
            InstanceType: data["InstanceType"],
            InstanceName: data["InstanceName"],
            ConnectionString: data["ConnectionString"]
        );

        _lastCreatedInstance = await _configClient!.CreateInstanceAsync(request);
        context.InstanceIds.Add(_lastCreatedInstance.Id);
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
        context.InstanceIds.Should().HaveCount(expectedCount);
    }

    [Given("{int} instances exist with route contexts")]
    public async Task GivenInstancesExistWithRouteContexts(int count)
    {
        if (context.InstanceIds.Count >= count)
        {
            return;
        }

        // Create instances dynamically with their Kafka infrastructure
        await GivenAVendorExists();

        // Store the infrastructure manager in context for cleanup
        context.InfrastructureManager = infrastructureManager;

        var instanceConfigs = new[]
        {
            (
                Name: "District 255901 - School Year 2024",
                DbIndex: 1,
                DistrictId: "255901",
                SchoolYear: "2024"
            ),
            (
                Name: "District 255901 - School Year 2025",
                DbIndex: 2,
                DistrictId: "255901",
                SchoolYear: "2025"
            ),
            (
                Name: "District 255902 - School Year 2024",
                DbIndex: 3,
                DistrictId: "255902",
                SchoolYear: "2024"
            ),
        };

        foreach (var config in instanceConfigs.Take(count))
        {
            var connectionString = TestConstants.GetConnectionString(config.DbIndex);
            var databaseName = TestConstants.GetDatabaseName(config.DbIndex);

            // Create instance via Config Service
            var instance = await _configClient!.CreateInstanceAsync(
                new InstanceRequest(
                    InstanceType: "District",
                    InstanceName: config.Name,
                    ConnectionString: connectionString
                )
            );

            context.InstanceIds.Add(instance.Id);
            context.InstanceIdToDatabaseName[instance.Id] = databaseName;
            context.RouteQualifierToInstanceId[$"{config.DistrictId}/{config.SchoolYear}"] = instance.Id;

            // Add route contexts
            await _configClient.CreateRouteContextAsync(
                new RouteContextRequest(
                    InstanceId: instance.Id,
                    ContextKey: "districtId",
                    ContextValue: config.DistrictId
                )
            );
            await _configClient.CreateRouteContextAsync(
                new RouteContextRequest(
                    InstanceId: instance.Id,
                    ContextKey: "schoolYear",
                    ContextValue: config.SchoolYear
                )
            );

            // Setup Kafka topic and Debezium connector
            await infrastructureManager.SetupInstanceInfrastructureAsync(instance.Id, databaseName);
        }
    }

    [When("I create an application with the following details:")]
    public async Task WhenICreateAnApplicationWithTheFollowingDetails(Table table)
    {
        context.VendorId.Should().NotBeNull("Vendor must exist before creating application");
        context.InstanceIds.Should().NotBeEmpty("Instances must exist before creating application");

        // Feature file tables without explicit headers are parsed as vertical key-value pairs
        // where the first row becomes the header in Reqnroll
        // Convert to dictionary: treat header as keys, first data row as values
        var headers = table.Header.ToList();
        Dictionary<string, string> data;

        if (table.Rows.Count == 0)
        {
            // No data rows, the header row contains key-value pairs
            // This shouldn't happen in our case, but handle it
            data = new Dictionary<string, string>();
        }
        else if (headers.Contains("ApplicationName") && table.Rows.Count == 1)
        {
            // Horizontal table with actual headers
            var row = table.Rows[0];
            data = headers.ToDictionary(header => header, header => row[header]);
        }
        else
        {
            // Vertical key-value table where Reqnroll treats first row as header
            // and subsequent rows as data
            data = new Dictionary<string, string> { { headers[0], headers[1] } };
            foreach (var row in table.Rows)
            {
                data[row[0]] = row[1];
            }
        }

        var edOrgIds = data["EducationOrganizationIds"].Split(',').Select(int.Parse).ToArray();

        var request = new ApplicationRequest(
            VendorId: context.VendorId!.Value,
            ApplicationName: data["ApplicationName"],
            ClaimSetName: data["ClaimSetName"],
            EducationOrganizationIds: edOrgIds,
            DmsInstanceIds: [.. context.InstanceIds]
        );

        var application = await _configClient!.CreateApplicationAsync(request);
        context.ApplicationId = application.Id;
        context.ClientKey = application.Key;
        context.ClientSecret = application.Secret;
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
}
