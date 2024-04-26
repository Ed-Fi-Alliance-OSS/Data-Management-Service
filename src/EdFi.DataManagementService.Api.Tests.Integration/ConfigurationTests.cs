// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Integration;

[TestFixture]
public class ConfigurationTests
{
    [TestFixture]
    public class Given_a_configuration_with_invalid_app_settings
    {
        private WebApplicationFactory<Program> _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:AuthenticationService"] = null,
                        }
                    );
                });
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory.Dispose();
        }

        [TestFixture]
        public class Should_respond_with_internal_server_error_when_missing_authentication_service
            : Given_a_configuration_with_invalid_app_settings
        {
            [Test]
            public async Task When_no_authentication_service()
            {
                // Arrange
                using var client = _factory.CreateClient();

                // Act
                var response = await client.GetAsync("/");

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }
        }
    }

    [TestFixture]
    public class Given_a_configuration_with_invalid_connection_strings
    {
        private WebApplicationFactory<Program> _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DatabaseConnection"] = null,
                        }
                    );
                });
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory.Dispose();
        }

        [TestFixture]
        public class Should_respond_with_internal_server_error_when_missing_database_connection
            : Given_a_configuration_with_invalid_connection_strings
        {
            [Test]
            public async Task When_no_authentication_service()
            {
                // Arrange
                using var client = _factory.CreateClient();

                // Act
                var response = await client.GetAsync("/");

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }
        }
    }
}
