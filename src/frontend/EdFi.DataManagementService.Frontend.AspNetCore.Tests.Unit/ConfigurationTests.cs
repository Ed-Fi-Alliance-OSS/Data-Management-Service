// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?> { ["AppSettings:AuthenticationService"] = null, }
                        );
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
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
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
}
