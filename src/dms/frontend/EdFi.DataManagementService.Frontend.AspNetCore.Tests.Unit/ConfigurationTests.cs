// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
public class ConfigurationTests
{
    [TestFixture]
    public class Given_A_Configuration_With_Invalid_App_Settings
    {
        private WebApplicationFactory<Program>? _factory;

        [SetUp]
        public void Setup()
        {
            var securityMetadataService = A.Fake<ISecurityMetadataService>();
            A.CallTo(() => securityMetadataService.GetClaimSets()).Returns([]);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?> { ["AppSettings:AuthenticationService"] = null }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => securityMetadataService);
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();
        }

        [TestFixture]
        public class When_Requesting_Any_Endpoint_Should_Return_InternalServerError
            : Given_A_Configuration_With_Invalid_App_Settings
        {
            [Test]
            public async Task When_no_authentication_service()
            {
                // Arrange
                using var client = _factory!.CreateClient();

                // Act
                var response = await client.GetAsync("/");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_Connection_Strings
    {
        private WebApplicationFactory<Program>? _factory;

        [SetUp]
        public void Setup()
        {
            var securityMetadataService = A.Fake<ISecurityMetadataService>();
            A.CallTo(() => securityMetadataService.GetClaimSets()).Returns([]);
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
                        collection.AddTransient((x) => securityMetadataService);
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();
        }

        [TestFixture]
        public class When_Requesting_Any_Endpoint_Should_Return_InternalServerError
            : Given_A_Configuration_With_Invalid_Connection_Strings
        {
            [Test]
            public async Task When_no_valid_connection_strings()
            {
                // Arrange
                using var client = _factory!.CreateClient();

                // Act
                var response = await client.GetAsync("/");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_Identity_Settings
    {
        private WebApplicationFactory<Program>? _factoryWithAuthorization;
        private WebApplicationFactory<Program>? _factoryWithoutAuthorization;

        [SetUp]
        public void Setup()
        {
            var securityMetadataService = A.Fake<ISecurityMetadataService>();
            A.CallTo(() => securityMetadataService.GetClaimSets()).Returns([]);
            _factoryWithAuthorization = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["IdentitySettings:EnforceAuthorization"] = "true",
                                ["IdentitySettings:Authority"] = "",
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => securityMetadataService);
                    }
                );
            });

            _factoryWithoutAuthorization = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["IdentitySettings:EnforceAuthorization"] = "false",
                                ["IdentitySettings:Authority"] = "",
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => securityMetadataService);
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factoryWithAuthorization!.Dispose();
            _factoryWithoutAuthorization!.Dispose();
        }

        [TestFixture]
        public class When_Requesting_Any_Endpoint_Should_Return_InternalServerError
            : Given_A_Configuration_With_Invalid_Identity_Settings
        {
            [Test]
            public void When_authorization_enabled_and_no_authority()
            {
                // Act
                Func<HttpClient> createClient = () => _factoryWithAuthorization!.CreateClient();

                // Assert
                createClient
                    .Should()
                    .Throw<OptionsValidationException>()
                    .WithMessage("Missing required IdentitySettings value: Authority");
            }
        }

        [TestFixture]
        public class When_Requesting_Any_Endpoint_Should_Return_Ok
            : Given_A_Configuration_With_Invalid_Identity_Settings
        {
            [Test]
            public async Task When_authorization_disabled_and_no_authority()
            {
                // Arrange
                using var client = _factoryWithoutAuthorization!.CreateClient();

                // Act
                var response = await client.GetAsync("/");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                content.Should().NotBeEmpty();
            }
        }
    }
}
