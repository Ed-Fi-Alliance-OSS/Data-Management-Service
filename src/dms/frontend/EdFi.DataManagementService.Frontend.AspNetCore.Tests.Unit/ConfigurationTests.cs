// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class ConfigurationTests
{
    [TestFixture]
    public class Given_A_Configuration_With_Invalid_App_Settings
    {
        protected WebApplicationFactory<Program>? Factory;
        protected string StatusDirectory = null!;
        protected string StatusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            StatusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            StatusFilePath = Path.Combine(StatusDirectory, "dms-startup-status.json");

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:AuthenticationService"] = null,
                                ["AppSettings:StartupStatusFilePath"] = StatusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        // Add validators to trigger ReportInvalidConfigurationMiddleware
                        collection.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            Factory!.Dispose();

            if (Directory.Exists(StatusDirectory))
            {
                Directory.Delete(StatusDirectory, recursive: true);
            }
        }

        [TestFixture]
        public class When_Requesting_Any_Endpoint_Should_Return_InternalServerError
            : Given_A_Configuration_With_Invalid_App_Settings
        {
            [Test]
            public async Task When_no_authentication_service()
            {
                // Arrange
                using var client = Factory!.CreateClient();

                // Act
                var response = await client.GetAsync("/");
                string content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }

            [Test]
            public async Task It_writes_failed_startup_status_instead_of_completed()
            {
                // Arrange
                using var client = Factory!.CreateClient();

                // Act
                var response = await client.GetAsync("/");

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                File.Exists(StatusFilePath).Should().BeTrue();

                var startupStatus = JsonNode.Parse(await File.ReadAllTextAsync(StatusFilePath))!.AsObject();

                startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
                startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureEndpoints);
                startupStatus["Summary"]!
                    .GetValue<string>()
                    .Should()
                    .Contain("Configuration validation failed");
                startupStatus["ErrorType"]!
                    .GetValue<string>()
                    .Should()
                    .Be(nameof(OptionsValidationException));
                startupStatus["ErrorMessage"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Default_Max_Request_Body_Size
    {
        private WebApplicationFactory<Program>? _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection => TestMockHelper.AddEssentialMocks(collection));
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();
        }

        [Test]
        public void It_uses_the_configured_default_request_body_size_for_host_limits()
        {
            using var client = _factory!.CreateClient();

            var formOptions = _factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;
            var kestrelOptions = _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
            var appSettings = _factory.Services.GetRequiredService<IOptions<AppSettings>>().Value;

            appSettings
                .MaxRequestBodySizeMegabytes.Should()
                .Be(AppSettings.DefaultMaxRequestBodySizeMegabytes);

            long maxRequestBodySizeBytes =
                (long)appSettings.MaxRequestBodySizeMegabytes * AppSettings.BytesPerMegabyte;
            maxRequestBodySizeBytes.Should().Be(formOptions.ValueLengthLimit);
            maxRequestBodySizeBytes.Should().Be(formOptions.MultipartBodyLengthLimit);
            maxRequestBodySizeBytes.Should().Be(kestrelOptions.Limits.MaxRequestBodySize);
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_Max_Request_Body_Size
    {
        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:AuthenticationService"] = "http://localhost:5126/connect/token",
                                ["AppSettings:MaxRequestBodySizeMegabytes"] = "0",
                                ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        // Add validators to trigger ReportInvalidConfigurationMiddleware
                        collection.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public async Task It_returns_internal_server_error_when_max_request_body_size_is_invalid()
        {
            // Arrange
            using var client = _factory!.CreateClient();

            // Act
            var response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            content.Should().Be(string.Empty);
            File.Exists(_statusFilePath).Should().BeTrue();

            var startupStatus = JsonNode.Parse(await File.ReadAllTextAsync(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
            startupStatus["ErrorMessage"]!.GetValue<string>().Should().Contain("MaxRequestBodySizeMegabytes");
        }
    }

    [TestFixture]
    public class Given_A_Bound_App_Settings_Without_Max_Request_Body_Size
    {
        [Test]
        public void It_uses_the_default_request_body_size_and_validates_successfully()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSettings:AuthenticationService"] = "http://localhost:5126/connect/token",
                        ["AppSettings:Datastore"] = "postgresql",
                        ["AppSettings:CorrelationIdHeader"] = "correlationid",
                    }
                )
                .Build();

            var appSettings = new AppSettings
            {
                AuthenticationService = "placeholder",
                Datastore = "postgresql",
                CorrelationIdHeader = "correlationid",
            };
            configuration.GetSection("AppSettings").Bind(appSettings);

            appSettings
                .MaxRequestBodySizeMegabytes.Should()
                .Be(AppSettings.DefaultMaxRequestBodySizeMegabytes);

            var validator = new AppSettingsValidator();
            validator.Validate(null, appSettings).Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_Connection_Strings
    {
        private WebApplicationFactory<Program>? _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:DatabaseConnection"] = null,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        // Add validators to trigger ReportInvalidConfigurationMiddleware
                        collection.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();
        }
    }
}
