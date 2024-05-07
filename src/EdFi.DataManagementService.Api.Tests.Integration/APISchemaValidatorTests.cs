// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Integration;

[TestFixture]
public class APISchemaValidatorTests
{
    [TestFixture]
    public class Given_a_invalid_api_schema_file
    {
        private WebApplicationFactory<Program> _factory;
        private IApiSchemaProvider _apiSchemaProvider;
        private IApiSchemaValidator _apiSchemaValidator;
        private Action<IWebHostBuilder> _webHostBuilder;

        [SetUp]
        public void Setup()
        {
            var apiSchemaRootNode =
                JsonNode.Parse(
                    "{\"projectSchemas\": { \"ed-fi\": {\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
                ) ?? new JsonObject();

            _apiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _apiSchemaProvider.ApiSchemaRootNode).Returns(apiSchemaRootNode!);

            var logger = A.Fake<ILogger<ApiSchemaSchemaProvider>>();

            _apiSchemaValidator = new ApiSchemaValidator(new ApiSchemaSchemaProvider(logger));

            _webHostBuilder = (builder) =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => _apiSchemaProvider);
                        collection.AddTransient((x) => _apiSchemaValidator);
                    }
                );
            };
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(_webHostBuilder);
        }

        [TearDown]
        public void Teardown()
        {
            _factory.Dispose();
        }

        [TestFixture]
        public class When_requesting_discovery_endpoint_Should_return_InternalServerError
            : Given_a_invalid_api_schema_file
        {
            [Test]
            public async Task When_api_schema_with_validation_errors()
            {
                // Arrange
                using var client = _factory.CreateClient();

                // Act
                var response = await client.GetAsync("/");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }
        }

        [TestFixture]
        public class When_requesting_metadata_endpoint_Should_return_InternalServerError
            : Given_a_invalid_api_schema_file
        {
            [Test]
            public async Task When_api_schema_with_validation_errors()
            {
                // Arrange
                using var client = _factory.CreateClient();

                // Act
                var response = await client.GetAsync("/metadata");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }
        }

        [TestFixture]
        public class When_requesting_students_endpoint_Should_return_InternalServerError
            : Given_a_invalid_api_schema_file
        {
            [Test]
            public async Task When_api_schema_with_validation_errors()
            {
                // Arrange
                using var client = _factory.CreateClient();

                // Act
                var response = await client.GetAsync("/data/ed-fi/students");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }
        }

        [TestFixture]
        public class When_requesting_schools_endpoint_Should_return_InternalServerError
            : Given_a_invalid_api_schema_file
        {
            [Test]
            public async Task When_api_schema_with_validation_errors()
            {
                // Arrange
                using var client = _factory.CreateClient();

                // Act
                var response = await client.GetAsync("/data/ed-fi/schools");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }
        }
    }
}
