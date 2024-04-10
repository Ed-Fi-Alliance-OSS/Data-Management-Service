// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Integration;

[TestFixture]
public class APISchemaFileTests
{
    [TestFixture]
    public class Given_an_ApiSchema_file_with_invalid_resourceschemas
    {
        private JsonNode? _schemaContent;
        private IApiSchemaProvider _apiSchemaProvider;
        private StringContent _jsonContent;
        private Action<IWebHostBuilder> _webHostBuilder;

        [SetUp]
        public void Setup()
        {
            _schemaContent = JsonContentProvider.ReadContent("InvalidResourceSchemas.json");
            _apiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => _apiSchemaProvider.ApiSchemaRootNode).Returns(_schemaContent!);

            _jsonContent = new(
                JsonSerializer.Serialize(new { property1 = "property1" }),
                Encoding.UTF8,
                "application/json"
            );
            _webHostBuilder = (builder) =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => _apiSchemaProvider);
                    }
                );
            };
        }

        [TestFixture]
        public class Should_respond_with_internal_server_error_for_a_GET_request
            : Given_an_ApiSchema_file_with_invalid_resourceschemas
        {
            [Test]
            public async Task When_no_resourcename_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using var client = factory.CreateClient();

                // Act
                var response = await client.GetAsync("/data/ed-fi/noresourcenames");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_isdescriptor_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using var client = factory.CreateClient();

                // Act
                var response = await client.GetAsync("/data/ed-fi/noIsDescriptors");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_allowidentityupdates_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using var client = factory.CreateClient();

                // Act
                var response = await client.GetAsync("/data/ed-fi/noallowidentityupdates");
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }
        }

        [TestFixture]
        public class Should_respond_with_internal_server_error_for_a_POST_request
            : Given_an_ApiSchema_file_with_invalid_resourceschemas
        {
            [Test]
            public async Task When_no_isshoolyearenumeration_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/noIsSchoolYearEnumerations", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_jsonschemaforinsert_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/noJsonSchemaForInserts", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_identityfullnames_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/noidentityfullnames", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_identitypathorder_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/noidentitypathorders", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_issubclass_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/noIsSubclasses", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_nosubclasstype_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/noSubClassTypes", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_superclassresourcename_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/nosuperclassresourcenames", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_superclassprojectnames_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/nosuperclassprojectnames", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_superclassidentitydocumentkey_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync(
                    "/data/ed-fi/nosuperclassidentitydocumentkeys",
                    jsonContent
                );
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_subclassidentitydocumentkey_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync(
                    "/data/ed-fi/noSubclassIdentityDocumentKeys",
                    jsonContent
                );
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }

            [Test]
            public async Task When_no_documentpathsmapping_element()
            {
                // Arrange
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                    _webHostBuilder
                );
                using StringContent jsonContent = _jsonContent;
                using var client = factory.CreateClient();

                // Act
                var response = await client.PostAsync("/data/ed-fi/nodocumentpathsmappings", jsonContent);
                var content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }
        }
    }
}
