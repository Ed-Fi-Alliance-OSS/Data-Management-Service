// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.TestBase;

/// <summary>
/// Base class for frontend tests that provides common test setup and configuration
/// </summary>
public abstract class FrontendTestBase
{
    /// <summary>
    /// Creates a test WebApplicationFactory with minimal configuration
    /// </summary>
    protected static WebApplicationFactory<Program> CreateTestFactory(
        Action<IServiceCollection>? configureServices = null,
        Dictionary<string, string?>? additionalConfiguration = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");

            // Provide minimal required configuration
            builder.ConfigureAppConfiguration(
                (context, config) =>
                {
                    // Clear existing sources to avoid loading appsettings.json with rate limiting
                    config.Sources.Clear();
                    var testConfig = new Dictionary<string, string?>
                    {
                        ["AppSettings:Datastore"] = "postgresql",
                        ["AppSettings:QueryHandler"] = "postgresql",
                        ["AppSettings:MaskRequestBodyInLogs"] = "false",
                        ["AppSettings:DeployDatabaseOnStartup"] = "false",
                        ["ConnectionStrings:DatabaseConnection"] =
                            "Host=localhost;Database=test;Username=test;Password=test",
                        ["ConfigurationServiceSettings:BaseUrl"] = "http://localhost/config",
                        ["ConfigurationServiceSettings:ClientId"] = "test-client",
                        ["ConfigurationServiceSettings:ClientSecret"] = "test-secret",
                        ["ConfigurationServiceSettings:Scope"] = "test-scope",
                        ["ConfigurationServiceSettings:CacheExpirationMinutes"] = "5",
                        // Disable JWT by default in tests
                        ["JwtAuthentication:Enabled"] = "false",
                        // Note: Don't add rate limiting configuration here unless specifically needed
                        // If a test needs rate limiting, it should provide it via additionalConfiguration
                    };

                    // Add any additional configuration provided
                    if (additionalConfiguration != null)
                    {
                        foreach (var kvp in additionalConfiguration)
                        {
                            testConfig[kvp.Key] = kvp.Value;
                        }
                    }

                    config.AddInMemoryCollection(testConfig);
                }
            );

            if (configureServices != null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
    }
}
