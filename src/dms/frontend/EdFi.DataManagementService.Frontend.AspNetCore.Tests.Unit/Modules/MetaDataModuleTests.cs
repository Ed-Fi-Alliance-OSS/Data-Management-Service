// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class MetadataModuleTests
{
    [TestFixture]
    public class When_Getting_Profiles_Endpoint
    {
        [Test]
        public async Task It_returns_profile_names_list()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>(["StudentProfile", "SchoolProfile"]));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/profiles");
            var content = await response.Content.ReadAsStringAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonArray.Should().NotBeNull();
            jsonArray!.Count.Should().Be(2);
            jsonArray[0]!.GetValue<string>().Should().Be("StudentProfile");
            jsonArray[1]!.GetValue<string>().Should().Be("SchoolProfile");
        }

        [Test]
        public async Task It_returns_empty_array_when_no_profiles()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>([]));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/profiles");
            var content = await response.Content.ReadAsStringAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonArray.Should().NotBeNull();
            jsonArray!.Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class When_Getting_Profile_OpenApi_Specification
    {
        [Test]
        public async Task It_returns_OpenApi_spec_for_valid_profile()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "StudentProfile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .Returns(
                    Task.FromResult<JsonNode?>(
                        JsonNode.Parse(
                            """
                            {
                              "openapi": "3.0.0",
                              "info": { "title": "StudentProfile Resources" },
                              "servers": [{ "url": "http://localhost/data" }]
                            }
                            """
                        )
                    )
                );

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/profiles/StudentProfile/resources-spec.json");
            var content = await response.Content.ReadAsStringAsync();
            var jsonContent = JsonNode.Parse(content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonContent.Should().NotBeNull();
            jsonContent!["openapi"]!.GetValue<string>().Should().Be("3.0.0");
            jsonContent["info"]!["title"]!.GetValue<string>().Should().Be("StudentProfile Resources");
        }

        [Test]
        public async Task It_returns_404_when_profile_not_found()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "NonExistentProfile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .Returns(Task.FromResult<JsonNode?>(null));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/profiles/NonExistentProfile/resources-spec.json");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_handles_case_insensitive_profile_names()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(A<string>._, A<string?>._, A<JsonArray>._)
                )
                .Returns(Task.FromResult<JsonNode?>(JsonNode.Parse("""{"openapi": "3.0.0"}""")));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/profiles/studentprofile/resources-spec.json");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "studentprofile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class When_Getting_The_Base_Metadata_Endpoint
    {
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        [SetUp]
        public void SetUp()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => apiService);
                    }
                );
            });
            using var client = factory.CreateClient();

            // Act
            _response = client.GetAsync("/metadata").GetAwaiter().GetResult();
            var content = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _jsonContent = JsonNode.Parse(content) ?? throw new Exception("JSON parsing failed");
        }

        [TearDownAttribute]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_OK()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_the_body_contains_the_dependencies_url()
        {
            _jsonContent?["dependencies"]?.ToString().Should().Be("http://localhost/metadata/dependencies");
        }

        [Test]
        public void Then_the_body_contains_the_specifications_url()
        {
            _jsonContent
                ?["specifications"]?.ToString()
                .Should()
                .Be("http://localhost/metadata/specifications");
        }

        [Test]
        public void Then_the_body_contains_the_xsdFiles_url()
        {
            _jsonContent?["discovery"]?.ToString().Should().Be("http://localhost/metadata/xsdFiles");
        }
    }

    [TestFixture]
    public class MetadataSpecificationsListTests
    {
        private WebApplicationFactory<Program> _factory;
        private HttpClient _client;
        private JsonArray? _specificationsJsonArray;

        [SetUp]
        public void SetUp()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetResourceOpenApiSpecification(A<JsonArray>._))
                .Returns(
                    JsonNode.Parse(
                        """
                        {
                          "openapi": "3.0.0",
                          "servers": [
                            {
                              "url": "http://localhost/data"
                            }
                          ]
                        }
                        """
                    )!
                );
            A.CallTo(() => apiService.GetDescriptorOpenApiSpecification(A<JsonArray>._))
                .Returns(
                    JsonNode.Parse(
                        """
                        {
                          "openapi": "3.0.0",
                          "servers": [
                            {
                              "url": "http://localhost/data"
                            }
                          ]
                        }
                        """
                    )!
                );

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");

                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => apiService);
                    }
                );
            });
            _client = _factory.CreateClient();

            // Act
            var response = _client.GetAsync("/metadata/specifications").GetAwaiter().GetResult();
            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var jsonContent = JsonNode.Parse(content);
            _specificationsJsonArray = jsonContent as JsonArray;
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void Metadata_Endpoint_Returns_Specifications_List()
        {
            // Assert
            _specificationsJsonArray.Should().NotBeNull();
            _specificationsJsonArray!.Count.Should().BeGreaterOrEqualTo(3);
            _specificationsJsonArray[0]!["name"]?.GetValue<string>().Should().Be("Resources");
            _specificationsJsonArray[1]!["name"]?.GetValue<string>().Should().Be("Descriptors");
            _specificationsJsonArray[2]!["name"]?.GetValue<string>().Should().Be("Discovery");
        }

        [Test]
        public async Task Api_Spec_Contains_Servers_Array()
        {
            // Assert
            _specificationsJsonArray.Should().NotBeNull();
            foreach (var item in _specificationsJsonArray!)
            {
                var endpointUri = item?["endpointUri"]?.GetValue<string>();
                endpointUri.Should().NotBeNullOrEmpty();
                var response = await _client.GetAsync(endpointUri);
                var content = await response.Content.ReadAsStringAsync();
                var jsonContent = JsonNode.Parse(content);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                jsonContent.Should().NotBeNull();
                var servers = jsonContent?["servers"];
                servers.Should().NotBeNull();
                servers.Should().BeOfType<JsonArray>();
                servers!.AsArray().Count.Should().Be(1);
                var server = servers[0];
                server.Should().NotBeNull();
                server?["url"]?.GetValue<string>().Should().Be("http://localhost/data");
            }
        }
    }

    [Test]
    public async Task Metadata_Returns_Descriptors_Content()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();

        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetDescriptorOpenApiSpecification(A<JsonArray>._))
            .Returns(
                JsonNode.Parse(
                    """
                    {
                      "openapi": "3.0.0",
                      "servers": [
                        {
                          "url": "http://localhost/data"
                        }
                      ],
                      "paths": {
                        "/ed-fi/absenceEventCategoryDescriptors": {}
                      }
                    }
                    """
                )!
            );

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => contentProvider);
                    collection.AddTransient((x) => apiService);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/specifications/descriptors-spec.json");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var openapiVersion = jsonContent?["openapi"]?.GetValue<string>();
        var paths = jsonContent?["paths"]?.AsObject();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        openapiVersion.Should().Be("3.0.0");
        paths.Should().NotBeNull();
        paths?["/ed-fi/absenceEventCategoryDescriptors"].Should().NotBeNull();
    }

    [Test]
    public async Task Metadata_Returns_Invalid_Resource_Error()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/swagger.json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Metadata_Returns_Dependencies()
    {
        // Arrange
        var httpContext = A.Fake<HttpContext>();

        var apiService = A.Fake<IApiService>();
        var dependenciesJson = JsonNode
            .Parse(
                """
                [
                  {
                    "resource": "/ed-fi/absenceEventCategoryDescriptors",
                    "order": 1
                  }
                ]
                """
            )!
            .AsArray();
        A.CallTo(() => apiService.GetDependencies()).Returns(dependenciesJson);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => httpContext);
                    collection.AddTransient((x) => apiService);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/dependencies");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        jsonContent
            ?[0]!["resource"]
            ?.GetValue<string>()
            .Should()
            .Be("/ed-fi/absenceEventCategoryDescriptors");
        jsonContent?[0]!["order"]?.GetValue<int>().Should().Be(1);
    }
}
